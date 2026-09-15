using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Incursa.OpenAI.Codex;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal sealed class CodexSessionRuntimeRegistry : ICodexTurnExecutionCoordinator, IHostedService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<CodexRuntimeSlot>> _threadSlots = new(StringComparer.Ordinal);
    private readonly CodexRuntimeSlot _defaultSlot;
    private readonly IOptions<CodexClientOptions> _clientOptions;
    private readonly ITelegramPlanInputCoordinator _planInputCoordinator;
    private readonly ICodexRealtimeBroadcaster _broadcaster;
    private readonly ITelegramTurnOutputRelay _telegramTurnOutputRelay;
    private readonly ICodexSessionEventLog _eventLog;
    private readonly ICodexSupervisionLedger _supervisionLedger;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CodexSessionRuntimeRegistry> _logger;
    private readonly ICodexRuntimeClientFactory _runtimeClientFactory;
    private readonly ICodexThreadManifestStore? _manifestStore;
    private readonly CodexTelegramOptions _telegramOptions;
    private readonly TimeSpan _terminalEventHoldDuration;

    public CodexSessionRuntimeRegistry(
        IOptions<CodexClientOptions> clientOptions,
        IOptions<CodexTelegramOptions> telegramOptions,
        ITelegramPlanInputCoordinator planInputCoordinator,
        ICodexRealtimeBroadcaster broadcaster,
        ITelegramTurnOutputRelay telegramTurnOutputRelay,
        ICodexSessionEventLog eventLog,
        IHostApplicationLifetime applicationLifetime,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        ICodexRuntimeClientFactory runtimeClientFactory,
        ICodexThreadManifestStore? manifestStore = null,
        ICodexSupervisionLedger? supervisionLedger = null)
    {
        _clientOptions = clientOptions;
        _planInputCoordinator = planInputCoordinator;
        _broadcaster = broadcaster;
        _telegramTurnOutputRelay = telegramTurnOutputRelay;
        _eventLog = eventLog;
        _applicationLifetime = applicationLifetime;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CodexSessionRuntimeRegistry>();
        _runtimeClientFactory = runtimeClientFactory;
        _manifestStore = manifestStore;
        _supervisionLedger = supervisionLedger ?? new NullCodexSupervisionLedger();
        _telegramOptions = telegramOptions.Value;
        int holdMilliseconds = Math.Clamp(
            _telegramOptions.TerminalEventHoldMilliseconds,
            CodexTurnStreamingDefaults.MinTerminalEventHoldMilliseconds,
            CodexTurnStreamingDefaults.MaxTerminalEventHoldMilliseconds);
        _terminalEventHoldDuration = TimeSpan.FromMilliseconds(holdMilliseconds);
        _defaultSlot = CreateSlot(broadcastRuntimeState: true);
    }

    public bool HasActiveTurn
        => EnumerateSlots().Any(slot => slot.TurnCoordinator.HasActiveTurn);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        int reconciled = await _supervisionLedger.ReconcileAfterRestartAsync(cancellationToken).ConfigureAwait(false);
        if (reconciled > 0)
        {
            _logger.LogWarning(
                "Marked {RunCount} non-terminal supervision run(s) as unknown after process restart; explicit reconciliation is required.",
                reconciled);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CodexRuntimeSlot[] slots = EnumerateSlots().ToArray();
        CodexActiveTurnStateVm[] activeTurns = slots
            .SelectMany(slot => slot.TurnCoordinator.GetActiveTurnStates())
            .GroupBy(state => state.ThreadId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        try
        {
            await PersistInterruptedTurnsAsync(activeTurns, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(slots.Select(slot => slot.TurnCoordinator.StopAsync(cancellationToken))).ConfigureAwait(false);
        }
    }

    public async Task<CodexRuntimeSlot> GetDefaultAsync(CancellationToken cancellationToken)
    {
        await _defaultSlot.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return _defaultSlot;
    }

    public async Task<CodexRuntimeSlot> CreateDedicatedSlotAsync(CancellationToken cancellationToken)
    {
        CodexRuntimeSlot slot = CreateSlot(broadcastRuntimeState: false);
        await slot.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return slot;
    }

    public async Task<CodexRuntimeSlot> GetOrCreateForThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return await GetDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        Lazy<CodexRuntimeSlot> lazySlot = _threadSlots.GetOrAdd(threadId, _ => new Lazy<CodexRuntimeSlot>(() => CreateSlot(broadcastRuntimeState: false)));
        CodexRuntimeSlot slot = lazySlot.Value;
        await slot.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return slot;
    }

    public async Task<CodexRuntimeSlot> GetBestForThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(threadId) && _threadSlots.TryGetValue(threadId, out Lazy<CodexRuntimeSlot>? lazySlot))
        {
            CodexRuntimeSlot slot = lazySlot.Value;
            await slot.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return slot;
        }

        return await GetDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<CodexThreadExecutionVm>> ReattachPersistedTurnsAsync(
        IEnumerable<string> threadIds,
        CancellationToken cancellationToken)
    {
        if (_manifestStore is null)
        {
            return [];
        }

        List<CodexThreadExecutionVm> attached = [];
        foreach (string threadId in threadIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (HasActiveTurnForThread(threadId))
            {
                continue;
            }

            CodexThreadManifestRecord? manifest = await _manifestStore.ReadAsync(threadId, cancellationToken).ConfigureAwait(false);
            string? turnId = manifest?.InterruptedTurn?.TurnId;
            if (manifest is null || string.IsNullOrWhiteSpace(turnId))
            {
                continue;
            }

            try
            {
                CodexRuntimeSlot slot = await GetOrCreateForThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
                CodexThreadOptions threadOptions = CodexOptionMapper.BuildThreadOptions(_telegramOptions, new CodexThreadContextSubmission(), manifest);
                ICodexThreadHandle thread = await slot.Client.ResumeThreadAsync(threadId, threadOptions, cancellationToken).ConfigureAwait(false);
                ICodexTurnHandle turn = await thread.AttachTurnAsync(
                    turnId,
                    new CodexTurnAttachOptions
                    {
                        ResumeOptions = threadOptions,
                    },
                    cancellationToken).ConfigureAwait(false);
                CodexThreadExecutionVm execution = await slot.TurnCoordinator.AttachAsync(thread, turn, cancellationToken).ConfigureAwait(false);
                await _manifestStore.UpdateAsync(
                    threadId,
                    current =>
                    {
                        current.LastTurnId = execution.TurnId;
                        current.InterruptedTurn = null;
                        return current;
                    },
                    cancellationToken).ConfigureAwait(false);
                await UpdateSupervisionStateAsync(
                    execution.TurnId,
                    CodexSupervisionRunState.Running,
                    threadId,
                    "turn_reattached",
                    cancellationToken).ConfigureAwait(false);
                attached.Add(execution);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (CodexCapabilityNotSupportedException exception)
            {
                _logger.LogDebug(exception, "Codex runtime does not support reattaching turn {TurnId} on thread {ThreadId}.", turnId, threadId);
            }
            catch (CodexInvalidRequestException exception)
            {
                await ClearStaleInterruptedTurnMarkerAsync(threadId, turnId, exception, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                await ClearStaleInterruptedTurnMarkerAsync(threadId, turnId, exception, cancellationToken).ConfigureAwait(false);
            }
        }

        return attached;
    }

    public void BindThread(string threadId, CodexRuntimeSlot slot)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread id cannot be empty.", nameof(threadId));
        }

        ArgumentNullException.ThrowIfNull(slot);
        _threadSlots[threadId] = new Lazy<CodexRuntimeSlot>(() => slot);
    }

    public IReadOnlyCollection<string> GetActiveThreadIds()
        => EnumerateSlots()
            .SelectMany(slot => slot.TurnCoordinator.GetActiveThreadIds())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public bool HasActiveTurnForThread(string threadId)
        => !string.IsNullOrWhiteSpace(threadId)
            && EnumerateSlots().Any(slot => slot.TurnCoordinator.HasActiveTurnForThread(threadId));

    public string? GetActiveTurnId(string threadId)
        => EnumerateSlots()
            .Select(slot => slot.TurnCoordinator.GetActiveTurnId(threadId))
            .FirstOrDefault(turnId => !string.IsNullOrWhiteSpace(turnId));

    public CodexActiveTurnStateVm? TryGetActiveTurnState(string threadId)
        => EnumerateSlots()
            .Select(slot => slot.TurnCoordinator.TryGetActiveTurnState(threadId))
            .FirstOrDefault(state => state is not null);

    public void RegisterActiveTurn(string threadId, string turnId, ICodexTurnHandle? turn = null, CodexTimelineEntryVm? lastEvent = null)
    {
        CodexRuntimeSlot slot = GetKnownSlotForThread(threadId);
        slot.TurnCoordinator.RegisterActiveTurn(threadId, turnId, turn, lastEvent);
    }

    public void UpdateActiveTurnState(string threadId, string turnId, CodexTimelineEntryVm? lastEvent = null)
    {
        CodexRuntimeSlot slot = GetKnownSlotForThread(threadId);
        slot.TurnCoordinator.UpdateActiveTurnState(threadId, turnId, lastEvent);
    }

    public bool TryClearActiveTurn(string threadId, string turnId)
        => EnumerateSlots().Any(slot => slot.TurnCoordinator.TryClearActiveTurn(threadId, turnId));

    public async Task SteerAsync(string threadId, string turnId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
    {
        CodexRuntimeSlot slot = GetKnownSlotForThread(threadId);
        await slot.TurnCoordinator.SteerAsync(threadId, turnId, input, cancellationToken).ConfigureAwait(false);
    }

    public async Task InterruptAsync(string threadId, string turnId, CancellationToken cancellationToken)
    {
        CodexRuntimeSlot slot = GetKnownSlotForThread(threadId);
        await slot.TurnCoordinator.InterruptAsync(threadId, turnId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (CodexRuntimeSlot slot in EnumerateSlots())
        {
            await slot.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task PersistInterruptedTurnsAsync(
        IReadOnlyCollection<CodexActiveTurnStateVm> activeTurns,
        CancellationToken cancellationToken)
    {
        if (_manifestStore is null || activeTurns.Count == 0)
        {
            return;
        }

        DateTimeOffset recordedAt = _timeProvider.GetUtcNow();
        foreach (CodexActiveTurnStateVm activeTurn in activeTurns)
        {
            try
            {
                await _manifestStore.UpdateAsync(
                    activeTurn.ThreadId,
                    manifest =>
                    {
                        manifest.LastTurnId = activeTurn.TurnId;
                        manifest.InterruptedTurn = CreateInterruptedTurnRecord(activeTurn, recordedAt);
                        return manifest;
                    },
                    cancellationToken).ConfigureAwait(false);
                await UpdateSupervisionStateAsync(
                    activeTurn.TurnId,
                    CodexSupervisionRunState.Interrupted,
                    activeTurn.ThreadId,
                    "application_shutdown",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to persist interrupted turn marker for turn {TurnId} on thread {ThreadId}.", activeTurn.TurnId, activeTurn.ThreadId);
            }
        }
    }

    private async Task ClearStaleInterruptedTurnMarkerAsync(
        string threadId,
        string turnId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (_manifestStore is null)
        {
            return;
        }

        _logger.LogWarning(exception, "Codex turn {TurnId} on thread {ThreadId} is no longer attachable. Clearing the interrupted-turn marker.", turnId, threadId);
        await _manifestStore.UpdateAsync(
            threadId,
            manifest =>
            {
                if (string.Equals(manifest.InterruptedTurn?.TurnId, turnId, StringComparison.Ordinal))
                {
                    manifest.InterruptedTurn = null;
                }

                return manifest;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static CodexInterruptedTurnRecord CreateInterruptedTurnRecord(CodexActiveTurnStateVm activeTurn, DateTimeOffset recordedAt)
    {
        string? summary = activeTurn.LastEvent?.Body ?? activeTurn.LastEvent?.Subtitle;
        return new CodexInterruptedTurnRecord
        {
            TurnId = activeTurn.TurnId,
            StartedAt = activeTurn.StartedAt,
            UpdatedAt = activeTurn.UpdatedAt,
            RecordedAt = recordedAt,
            Reason = "application_shutdown",
            Message = "The app shut down while this turn was active. On restart the app will try to reattach if Codex is still running; otherwise send a new message to continue on the resumed thread.",
            LastEventType = activeTurn.LastEvent?.Type,
            LastEventTitle = activeTurn.LastEvent?.Title,
            LastEventSummary = string.IsNullOrWhiteSpace(summary) ? null : CodexTextFormatting.TruncatePreview(summary),
        };
    }

    private async Task UpdateSupervisionStateAsync(
        string turnId,
        CodexSupervisionRunState state,
        string? sessionId,
        string outcomeCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await _supervisionLedger.UpdateRunsForTurnAsync(
                turnId,
                state,
                sessionId,
                outcomeCode,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to persist supervision state {State} for turn {TurnId}.", state, turnId);
        }
    }

    private CodexRuntimeSlot CreateSlot(bool broadcastRuntimeState)
        => new(
            _runtimeClientFactory.Create(CreateClientOptions()),
            new CodexRuntimeState(),
            new CodexTurnExecutionCoordinator(
                _broadcaster,
                _telegramTurnOutputRelay,
                _applicationLifetime,
                _timeProvider,
                _terminalEventHoldDuration,
                _loggerFactory.CreateLogger<CodexTurnExecutionCoordinator>(),
                eventLog: _eventLog,
                supervisionLedger: _supervisionLedger),
            _broadcaster,
            broadcastRuntimeState);

    private CodexClientOptions CreateClientOptions()
    {
        CodexClientOptions source = _clientOptions.Value;
        CodexApprovalHandler? configuredHandler = source.ApprovalHandler;
        CodexClientOptions destination = new CodexClientOptions
        {
            BackendSelection = source.BackendSelection,
            CodexPathOverride = source.CodexPathOverride,
            BaseUrl = source.BaseUrl,
            ApiKey = source.ApiKey,
            Config = source.Config,
            Environment = source.Environment,
            ClientName = source.ClientName,
            ClientTitle = source.ClientTitle,
            ClientVersion = source.ClientVersion,
            ApprovalHandler = (action, request) => HandleApprovalRequest(action, request, configuredHandler),
        };

        CodexClientOptionsPlanModeBridge.CopyPlanMode(source, destination);
        return destination;
    }

    private JsonObject? HandleApprovalRequest(
        string action,
        JsonObject? request,
        CodexApprovalHandler? configuredHandler)
    {
        JsonObject? response = _planInputCoordinator.HandleApprovalRequest(action, request)
            ?? configuredHandler?.Invoke(action, request)
            ?? CreateDefaultApprovalDenialResponse(action);
        if (!IsApprovalAction(action))
        {
            return response;
        }

        string? turnId = GetRequestString(request, "turnId", "turn_id");
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return response;
        }

        try
        {
            _supervisionLedger.RecordApprovalDecisionAsync(
                    turnId,
                    GetRequestString(request, "threadId", "thread_id"),
                    action,
                    GetResponseDecision(response) ?? "denied_by_default",
                    null,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to persist Codex approval decision for turn {TurnId}.", turnId);
        }

        return response;
    }

    private static bool IsApprovalAction(string action)
        => action.Contains("requestApproval", StringComparison.OrdinalIgnoreCase)
            || action.Contains("approval.required", StringComparison.OrdinalIgnoreCase);

    private static string? GetRequestString(JsonObject? request, params string[] names)
    {
        JsonObject? payload = request?["params"] as JsonObject ?? request;
        if (payload is null)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (payload[name] is JsonValue value && value.TryGetValue<string>(out string? result) && !string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        return null;
    }

    private static string? GetResponseDecision(JsonObject? response)
    {
        if (response is null)
        {
            return null;
        }

        foreach (string name in new[] { "decision", "approval", "result" })
        {
            if (response[name] is JsonValue value && value.TryGetValue<string>(out string? result) && !string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        return null;
    }

    // A missing Telegram approval is a safety failure, never consent. Plan-mode
    // user input is handled by TelegramPlanInputCoordinator above; command and
    // file-change approvals must be rejected when no explicit decision exists.
    internal static JsonObject? CreateDefaultApprovalDenialResponse(string action)
        => action switch
        {
            "item/commandExecution/requestApproval" => new JsonObject { ["decision"] = "reject" },
            "item/fileChange/requestApproval" => new JsonObject { ["decision"] = "reject" },
            _ => null,
        };

    private CodexRuntimeSlot GetKnownSlotForThread(string threadId)
    {
        if (!string.IsNullOrWhiteSpace(threadId) && _threadSlots.TryGetValue(threadId, out Lazy<CodexRuntimeSlot>? lazySlot))
        {
            return lazySlot.Value;
        }

        CodexRuntimeSlot? activeSlot = EnumerateSlots()
            .FirstOrDefault(slot => slot.TurnCoordinator.HasActiveTurnForThread(threadId));
        return activeSlot ?? _defaultSlot;
    }

    private IReadOnlyCollection<CodexRuntimeSlot> EnumerateSlots()
        => _threadSlots.Values
            .Where(lazy => lazy.IsValueCreated)
            .Select(lazy => lazy.Value)
            .Prepend(_defaultSlot)
            .Distinct(SlotReferenceComparer.Instance)
            .ToArray();

    private sealed class SlotReferenceComparer : IEqualityComparer<CodexRuntimeSlot>
    {
        public static SlotReferenceComparer Instance { get; } = new();

        public bool Equals(CodexRuntimeSlot? x, CodexRuntimeSlot? y)
            => ReferenceEquals(x, y);

        public int GetHashCode(CodexRuntimeSlot obj)
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

internal sealed class CodexRuntimeSlot : IAsyncDisposable
{
    private readonly ICodexRealtimeBroadcaster _broadcaster;
    private readonly bool _broadcastRuntimeState;
    private readonly SemaphoreSlim _runtimeInitGate = new(1, 1);

    public CodexRuntimeSlot(
        ICodexRuntimeClient client,
        CodexRuntimeState runtimeState,
        CodexTurnExecutionCoordinator turnCoordinator,
        ICodexRealtimeBroadcaster broadcaster,
        bool broadcastRuntimeState)
    {
        Client = client;
        RuntimeState = runtimeState;
        TurnCoordinator = turnCoordinator;
        _broadcaster = broadcaster;
        _broadcastRuntimeState = broadcastRuntimeState;
    }

    public ICodexRuntimeClient Client { get; }

    public CodexRuntimeState RuntimeState { get; }

    public CodexTurnExecutionCoordinator TurnCoordinator { get; }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (RuntimeState.Initialized)
        {
            return;
        }

        await _runtimeInitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (RuntimeState.Initialized)
            {
                return;
            }

            CodexRuntimeMetadata metadata = await Client.InitializeAsync(cancellationToken).ConfigureAwait(false);
            CodexRuntimeCapabilities capabilities = Client.Capabilities ?? throw new InvalidOperationException("Codex runtime initialized without capabilities.");
            IReadOnlyList<CodexModel> models = [];

            try
            {
                if (capabilities.SupportsListModels)
                {
                    CodexModelListResult result = await Client.ListModelsAsync(new CodexModelListOptions(), cancellationToken).ConfigureAwait(false);
                    models = result.Models;
                }
            }
            catch (CodexCapabilityNotSupportedException)
            {
                models = [];
            }

            RuntimeState.SetReady(metadata, capabilities, models, $"Codex runtime ready: {metadata.ServerInfo?.Name ?? "Codex"} {metadata.ServerInfo?.Version ?? string.Empty}".Trim());
            if (_broadcastRuntimeState)
            {
                await _broadcaster.BroadcastRuntimeStateAsync(RuntimeState.ToViewModel(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            RuntimeState.SetError(exception);
            throw;
        }
        finally
        {
            _runtimeInitGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        _runtimeInitGate.Dispose();
    }
}
