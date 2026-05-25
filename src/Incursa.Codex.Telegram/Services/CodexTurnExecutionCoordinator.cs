using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Threading.Channels;
using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Incursa.Codex.Telegram.Services;

internal interface ICodexTurnExecutionCoordinator
{
    bool HasActiveTurn { get; }

    IReadOnlyCollection<string> GetActiveThreadIds();

    bool HasActiveTurnForThread(string threadId);

    string? GetActiveTurnId(string threadId);

    CodexActiveTurnStateVm? TryGetActiveTurnState(string threadId);

    void RegisterActiveTurn(string threadId, string turnId, ICodexTurnHandle? turn = null, CodexTimelineEntryVm? lastEvent = null);

    void UpdateActiveTurnState(string threadId, string turnId, CodexTimelineEntryVm? lastEvent = null);

    bool TryClearActiveTurn(string threadId, string turnId);

    Task SteerAsync(string threadId, string turnId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken);

    Task InterruptAsync(string threadId, string turnId, CancellationToken cancellationToken);
}

internal sealed class CodexTurnExecutionCoordinator
    : ICodexTurnExecutionCoordinator
{
    private const string TurnCompletionMarker = "~~ turn complete ~~";
    private const string LegacyTurnFinishedMarker = "~~ fin ~~";
    private static readonly TimeSpan ObservableBufferWindow = TimeSpan.FromMilliseconds(10);
    private const int ObservableBufferMaxCount = 32;
    private static readonly TimeSpan[] DefaultCapacityRetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

    private readonly ConcurrentDictionary<string, ActiveTurnState> _activeTurns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _startingThreads = new(StringComparer.Ordinal);
    private readonly ICodexRealtimeBroadcaster _broadcaster;
    private readonly ITelegramTurnOutputRelay _telegramTurnOutputRelay;
    private readonly ICodexSessionEventLog _eventLog;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _terminalEventHoldDuration;
    private readonly IReadOnlyList<TimeSpan> _capacityRetryDelays;
    private readonly ILogger<CodexTurnExecutionCoordinator> _logger;

    public CodexTurnExecutionCoordinator(
        ICodexRealtimeBroadcaster broadcaster,
        ITelegramTurnOutputRelay telegramTurnOutputRelay,
        IHostApplicationLifetime applicationLifetime,
        TimeProvider timeProvider,
        TimeSpan terminalEventHoldDuration,
        ILogger<CodexTurnExecutionCoordinator> logger,
        IReadOnlyList<TimeSpan>? capacityRetryDelays = null,
        ICodexSessionEventLog? eventLog = null)
    {
        _broadcaster = broadcaster;
        _telegramTurnOutputRelay = telegramTurnOutputRelay;
        _eventLog = eventLog ?? NullCodexSessionEventLog.Instance;
        _applicationLifetime = applicationLifetime;
        _timeProvider = timeProvider;
        _terminalEventHoldDuration = terminalEventHoldDuration < TimeSpan.Zero ? TimeSpan.Zero : terminalEventHoldDuration;
        _capacityRetryDelays = capacityRetryDelays is { Count: > 0 }
            ? capacityRetryDelays.Select(delay => delay < TimeSpan.Zero ? TimeSpan.Zero : delay).ToArray()
            : DefaultCapacityRetryDelays;
        _logger = logger;
    }

    public bool HasActiveTurn
        => !_activeTurns.IsEmpty || !_startingThreads.IsEmpty;

    public IReadOnlyCollection<string> GetActiveThreadIds()
        => _activeTurns.Keys
            .Concat(_startingThreads.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public bool HasActiveTurnForThread(string threadId)
        => !string.IsNullOrWhiteSpace(threadId)
            && (_activeTurns.ContainsKey(threadId) || _startingThreads.ContainsKey(threadId));

    public string? GetActiveTurnId(string threadId)
        => _activeTurns.TryGetValue(threadId, out ActiveTurnState? state) ? state.TurnId : null;

    public CodexActiveTurnStateVm? TryGetActiveTurnState(string threadId)
    {
        if (!_activeTurns.TryGetValue(threadId, out ActiveTurnState? state))
        {
            return null;
        }

        return state.ToViewModel();
    }

    public void RegisterActiveTurn(string threadId, string turnId, ICodexTurnHandle? turn = null, CodexTimelineEntryVm? lastEvent = null)
    {
        ActiveTurnState state = new(threadId, turnId, turn, lastEvent);
        if (!_activeTurns.TryAdd(threadId, state))
        {
            throw new InvalidOperationException($"A Codex turn is already active for thread '{threadId}'.");
        }
    }

    public void UpdateActiveTurnState(string threadId, string turnId, CodexTimelineEntryVm? lastEvent = null)
    {
        ActiveTurnState state = GetRequiredState(threadId, turnId);
        state.Update(lastEvent);
    }

    public bool TryClearActiveTurn(string threadId, string turnId)
    {
        if (!_activeTurns.TryGetValue(threadId, out ActiveTurnState? state))
        {
            return false;
        }

        if (!string.Equals(state.TurnId, turnId, StringComparison.Ordinal))
        {
            return false;
        }

        return _activeTurns.TryRemove(threadId, out _);
    }

    public async Task<CodexThreadExecutionVm> StartAsync(
        ICodexThreadHandle thread,
        IReadOnlyList<CodexInputItem> input,
        CodexTurnOptions turnOptions,
        CancellationToken cancellationToken)
    {
        string startingThreadId = thread.Id ?? string.Empty;
        bool trackedStartingThread = false;
        if (!string.IsNullOrWhiteSpace(startingThreadId))
        {
            if (HasActiveTurnForThread(startingThreadId) || !_startingThreads.TryAdd(startingThreadId, 0))
            {
                throw new InvalidOperationException($"A Codex turn is already active for thread '{startingThreadId}'.");
            }

            trackedStartingThread = true;
        }

        try
        {
            TurnStartResult startResult = await StartTurnWithCapacityRetriesAsync(
                thread,
                input,
                turnOptions,
                startingThreadId,
                cancellationToken).ConfigureAwait(false);
            ICodexTurnHandle turn = startResult.Turn;
            string threadId = string.IsNullOrWhiteSpace(turn.ThreadId) ? thread.Id ?? string.Empty : turn.ThreadId;
            ActiveTurnState state = new(
                threadId,
                turn.Id,
                turn,
                lastEvent: null,
                thread,
                input,
                turnOptions,
                startResult.CapacityRetryAttempts);
            if (!_activeTurns.TryAdd(threadId, state))
            {
                throw new InvalidOperationException($"A Codex turn is already active for thread '{threadId}'.");
            }

            await PublishTurnAcceptedAsync(state, cancellationToken).ConfigureAwait(false);
            _ = Task.Run(() => ConsumeTurnAsync(state), _applicationLifetime.ApplicationStopping);
            return new CodexThreadExecutionVm(threadId, turn.Id, "running", null);
        }
        finally
        {
            if (trackedStartingThread)
            {
                _startingThreads.TryRemove(startingThreadId, out _);
            }
        }
    }

    private async Task PublishTurnAcceptedAsync(ActiveTurnState state, CancellationToken cancellationToken)
    {
        try
        {
            await _telegramTurnOutputRelay.PublishTurnAcceptedAsync(state.ThreadId, state.TurnId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to publish initial Telegram live card for turn {TurnId} on thread {ThreadId}.", state.TurnId, state.ThreadId);
        }
    }

    public async Task SteerAsync(string threadId, string turnId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
    {
        ActiveTurnState state = GetRequiredState(threadId, turnId);
        if (state.Turn is null)
        {
            throw new InvalidOperationException($"Active turn '{turnId}' on thread '{threadId}' does not have a live Codex turn.");
        }

        await state.Turn.SteerAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task InterruptAsync(string threadId, string turnId, CancellationToken cancellationToken)
    {
        ActiveTurnState state = GetRequiredState(threadId, turnId);
        if (state.Turn is null)
        {
            throw new InvalidOperationException($"Active turn '{turnId}' on thread '{threadId}' does not have a live Codex turn.");
        }

        await state.Turn.InterruptAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ConsumeTurnAsync(ActiveTurnState state)
    {
        if (state.Turn is null)
        {
            _logger.LogDebug("Synthetic active turn {TurnId} on thread {ThreadId} has no live Codex turn to consume.", state.TurnId, state.ThreadId);
            return;
        }

        CodexTimelineEntryVm? pendingTerminalEntry = null;
        CodexTimelineEntryVm? pendingFinalResponseEntry = null;
        int lateEventCount = 0;
        bool terminalHoldTimedOut = false;
        bool terminalFailurePublished = false;
        bool retryScheduled = false;
        Task terminalHoldTask = Task.CompletedTask;
        CancellationTokenSource? terminalHoldCancellation = null;
        using CancellationTokenSource streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationLifetime.ApplicationStopping);
        try
        {
            await using ObservableTimelineSubscription subscription = ObservableTimelineSubscription.Subscribe(
                state.Turn
                    .ObserveNormalizedEventsAsync()
                    .Select(evt => CodexViewModelMapper.ToTimelineEntryVm(evt, state.ThreadId))
                    .Buffer(ObservableBufferWindow, ObservableBufferMaxCount)
                    .Where(batch => batch.Count > 0));
            await using IAsyncEnumerator<CodexTimelineEntryVm> enumerator = subscription
                .ReadAllAsync(streamCancellation.Token)
                .GetAsyncEnumerator(streamCancellation.Token);

            while (true)
            {
                if (pendingTerminalEntry is null)
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    CodexTimelineEntryVm streamEntry = enumerator.Current;
                    CapacityRetryResult capacityRetryResult = await TryHandleCapacityRetryAsync(state, streamEntry, streamCancellation.Token).ConfigureAwait(false);
                    if (capacityRetryResult == CapacityRetryResult.Scheduled)
                    {
                        retryScheduled = true;
                        return;
                    }

                    if (capacityRetryResult == CapacityRetryResult.Exhausted)
                    {
                        terminalFailurePublished = true;
                        return;
                    }

                    if (IsFinalResponse(streamEntry))
                    {
                        pendingFinalResponseEntry = streamEntry;
                        state.RecordVisibleOutputIfNeeded(streamEntry);
                        RecordEvent(streamEntry);
                        UpdateActiveTurnState(state.ThreadId, state.TurnId, streamEntry);
                        continue;
                    }

                    if (IsTerminalTurnEvent(streamEntry))
                    {
                        pendingTerminalEntry = streamEntry;
                        if (_terminalEventHoldDuration <= TimeSpan.Zero)
                        {
                            terminalHoldTask = Task.CompletedTask;
                        }
                        else
                        {
                            terminalHoldCancellation ??= new CancellationTokenSource();
                            terminalHoldTask = Task.Delay(_terminalEventHoldDuration, _timeProvider, terminalHoldCancellation.Token);
                        }
                        continue;
                    }

                    state.RecordVisibleOutputIfNeeded(streamEntry);
                    RecordEvent(streamEntry);
                    UpdateActiveTurnState(state.ThreadId, state.TurnId, streamEntry);
                    await _broadcaster.BroadcastThreadEventAsync(state.ThreadId, streamEntry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
                    await _telegramTurnOutputRelay.PublishTurnEventAsync(streamEntry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
                    continue;
                }

                Task<bool> moveNextTask = enumerator.MoveNextAsync().AsTask();
                Task completedTask = await Task.WhenAny(moveNextTask, terminalHoldTask).ConfigureAwait(false);
                if (completedTask == terminalHoldTask)
                {
                    terminalHoldTimedOut = true;
                    terminalHoldCancellation?.Cancel();
                    streamCancellation.Cancel();
                    break;
                }

                if (!await moveNextTask.ConfigureAwait(false))
                {
                    break;
                }

                CodexTimelineEntryVm postTerminalEntry = enumerator.Current;
                CapacityRetryResult postTerminalCapacityRetryResult = await TryHandleCapacityRetryAsync(state, postTerminalEntry, streamCancellation.Token).ConfigureAwait(false);
                if (postTerminalCapacityRetryResult == CapacityRetryResult.Scheduled)
                {
                    retryScheduled = true;
                    return;
                }

                if (postTerminalCapacityRetryResult == CapacityRetryResult.Exhausted)
                {
                    terminalFailurePublished = true;
                    return;
                }

                if (IsFinalResponse(postTerminalEntry))
                {
                    pendingFinalResponseEntry = postTerminalEntry;
                    state.RecordVisibleOutputIfNeeded(postTerminalEntry);
                    RecordEvent(postTerminalEntry);
                    UpdateActiveTurnState(state.ThreadId, state.TurnId, postTerminalEntry);
                    continue;
                }

                if (IsTerminalTurnEvent(postTerminalEntry))
                {
                    pendingTerminalEntry = postTerminalEntry;
                    continue;
                }

                lateEventCount++;
                state.RecordVisibleOutputIfNeeded(postTerminalEntry);
                RecordEvent(postTerminalEntry);
                UpdateActiveTurnState(state.ThreadId, state.TurnId, postTerminalEntry);
                await _broadcaster.BroadcastThreadEventAsync(state.ThreadId, postTerminalEntry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
                await _telegramTurnOutputRelay.PublishTurnEventAsync(postTerminalEntry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
            }

            if (pendingTerminalEntry is not null)
            {
                EmptyOutputRetryResult emptyOutputRetryResult = await TryHandleEmptyOutputRetryAsync(
                    state,
                    pendingTerminalEntry,
                    _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
                if (emptyOutputRetryResult == EmptyOutputRetryResult.Scheduled)
                {
                    retryScheduled = true;
                    return;
                }

                if (emptyOutputRetryResult == EmptyOutputRetryResult.Exhausted)
                {
                    terminalFailurePublished = true;
                    return;
                }

                if (terminalHoldTimedOut)
                {
                    if (lateEventCount > 0)
                    {
                        _logger.LogWarning(
                            "Turn {TurnId} on thread {ThreadId} reported a terminal event and the {TerminalHoldDuration} hold elapsed after {LateEventCount} later event(s); published completion.",
                            state.TurnId,
                            state.ThreadId,
                            _terminalEventHoldDuration,
                            lateEventCount);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Turn {TurnId} on thread {ThreadId} reported a terminal event and the {TerminalHoldDuration} hold elapsed before the stream ended; published completion.",
                            state.TurnId,
                            state.ThreadId,
                            _terminalEventHoldDuration);
                    }
                }
                else if (lateEventCount > 0)
                {
                    _logger.LogWarning(
                        "Turn {TurnId} on thread {ThreadId} reported a terminal event before the stream ended; published completion after {LateEventCount} later event(s).",
                        state.TurnId,
                        state.ThreadId,
                        lateEventCount);
                }

                await PublishPendingFinalResponseAsync(state, pendingFinalResponseEntry).ConfigureAwait(false);
                await PublishTerminalEventAsync(state, pendingTerminalEntry).ConfigureAwait(false);
            }

        }
        catch (OperationCanceledException) when (!_applicationLifetime.ApplicationStopping.IsCancellationRequested && !streamCancellation.IsCancellationRequested)
        {
            _logger.LogDebug("Turn {TurnId} on thread {ThreadId} was cancelled.", state.TurnId, state.ThreadId);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            terminalFailurePublished = true;
            _logger.LogError(exception, "Turn {TurnId} on thread {ThreadId} failed during stream consumption.", state.TurnId, state.ThreadId);
            await PublishFailureAsync(state, exception).ConfigureAwait(false);
        }

        finally
        {
            terminalHoldCancellation?.Cancel();
            terminalHoldCancellation?.Dispose();

            if (pendingTerminalEntry is null && !terminalFailurePublished && !retryScheduled && !_applicationLifetime.ApplicationStopping.IsCancellationRequested)
            {
                pendingTerminalEntry = new CodexTimelineEntryVm(
                    "turn.stream.ended",
                    "Turn stream ended without a terminal event",
                    "The SDK did not observe turn.completed or turn.failed.",
                    null,
                    "danger",
                    _timeProvider.GetUtcNow(),
                    state.ThreadId,
                    state.TurnId,
                    new Dictionary<string, string?>
                    {
                        ["terminal"] = true.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["terminalState"] = CodexTurnTerminalState.Incomplete.ToString(),
                    },
                    false);

                EmptyOutputRetryResult emptyOutputRetryResult = await TryHandleEmptyOutputRetryAsync(
                    state,
                    pendingTerminalEntry,
                    _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
                if (emptyOutputRetryResult == EmptyOutputRetryResult.Scheduled)
                {
                    retryScheduled = true;
                }
                else if (emptyOutputRetryResult == EmptyOutputRetryResult.Exhausted)
                {
                    terminalFailurePublished = true;
                }
                else
                {
                    await PublishPendingFinalResponseAsync(state, pendingFinalResponseEntry).ConfigureAwait(false);
                    await PublishTerminalEventAsync(state, pendingTerminalEntry).ConfigureAwait(false);
                }
            }

            if (!retryScheduled)
            {
                TryClearActiveTurn(state.ThreadId, state.TurnId);
            }
        }
    }

    private async Task<TurnStartResult> StartTurnWithCapacityRetriesAsync(
        ICodexThreadHandle thread,
        IReadOnlyList<CodexInputItem> input,
        CodexTurnOptions turnOptions,
        string threadId,
        CancellationToken cancellationToken)
    {
        int capacityRetryAttempts = 0;
        while (true)
        {
            try
            {
                ICodexTurnHandle turn = await thread.StartTurnAsync(input, turnOptions, cancellationToken).ConfigureAwait(false);
                return new TurnStartResult(turn, capacityRetryAttempts);
            }
            catch (Exception exception) when (IsCapacityException(exception) && capacityRetryAttempts < _capacityRetryDelays.Count)
            {
                capacityRetryAttempts++;
                TimeSpan delay = _capacityRetryDelays[capacityRetryAttempts - 1];
                await PublishCapacityRetryNoticeAsync(threadId, null, capacityRetryAttempts, delay).ConfigureAwait(false);
                await DelayCapacityRetryAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsCapacityException(exception))
            {
                throw new InvalidOperationException(CreateCapacityExhaustedMessage(), exception);
            }
        }
    }

    private async Task<CapacityRetryResult> TryHandleCapacityRetryAsync(
        ActiveTurnState state,
        CodexTimelineEntryVm entry,
        CancellationToken cancellationToken)
    {
        if (!IsCapacityThreadError(entry))
        {
            return CapacityRetryResult.NotHandled;
        }

        if (state.Thread is null || state.Input is null || state.TurnOptions is null)
        {
            return CapacityRetryResult.NotHandled;
        }

        while (state.CapacityRetryAttempts < _capacityRetryDelays.Count)
        {
            int retryAttempt = state.IncrementCapacityRetryAttempts();
            TimeSpan delay = _capacityRetryDelays[retryAttempt - 1];
            await PublishCapacityRetryNoticeAsync(state.ThreadId, state.TurnId, retryAttempt, delay).ConfigureAwait(false);
            await DelayCapacityRetryAsync(delay, cancellationToken).ConfigureAwait(false);

            try
            {
                ICodexTurnHandle retryTurn = await state.Thread
                    .StartTurnAsync(state.Input, state.TurnOptions, cancellationToken)
                    .ConfigureAwait(false);
                state.ReplaceTurn(retryTurn);
                _ = Task.Run(() => ConsumeTurnAsync(state), _applicationLifetime.ApplicationStopping);
                return CapacityRetryResult.Scheduled;
            }
            catch (Exception exception) when (IsCapacityException(exception))
            {
                _logger.LogWarning(
                    exception,
                    "Capacity retry {RetryAttempt}/{RetryCount} failed to start for thread {ThreadId}.",
                    retryAttempt,
                    _capacityRetryDelays.Count,
                    state.ThreadId);
            }
        }

        await PublishCapacityExhaustedAsync(state).ConfigureAwait(false);
        return CapacityRetryResult.Exhausted;
    }

    private async Task<EmptyOutputRetryResult> TryHandleEmptyOutputRetryAsync(
        ActiveTurnState state,
        CodexTimelineEntryVm terminalEntry,
        CancellationToken cancellationToken)
    {
        if (!IsEmptySuccessfulCompletion(state, terminalEntry))
        {
            return EmptyOutputRetryResult.NotHandled;
        }

        if (state.Thread is null || state.Input is null || state.TurnOptions is null)
        {
            return EmptyOutputRetryResult.NotHandled;
        }

        while (state.EmptyOutputRetryAttempts < _capacityRetryDelays.Count)
        {
            int retryAttempt = state.IncrementEmptyOutputRetryAttempts();
            TimeSpan delay = _capacityRetryDelays[retryAttempt - 1];
            await PublishEmptyOutputRetryNoticeAsync(state.ThreadId, state.TurnId, retryAttempt, delay).ConfigureAwait(false);
            await DelayCapacityRetryAsync(delay, cancellationToken).ConfigureAwait(false);

            try
            {
                ICodexTurnHandle retryTurn = await state.Thread
                    .StartTurnAsync(state.Input, state.TurnOptions, cancellationToken)
                    .ConfigureAwait(false);
                state.ReplaceTurn(retryTurn);
                _ = Task.Run(() => ConsumeTurnAsync(state), _applicationLifetime.ApplicationStopping);
                return EmptyOutputRetryResult.Scheduled;
            }
            catch (Exception exception) when (IsCapacityException(exception))
            {
                _logger.LogWarning(
                    exception,
                    "Empty-output retry {RetryAttempt}/{RetryCount} hit capacity while starting for thread {ThreadId}.",
                    retryAttempt,
                    _capacityRetryDelays.Count,
                    state.ThreadId);
            }
        }

        await PublishEmptyOutputExhaustedAsync(state).ConfigureAwait(false);
        return EmptyOutputRetryResult.Exhausted;
    }

    private async Task DelayCapacityRetryAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishCapacityRetryNoticeAsync(string threadId, string? turnId, int retryAttempt, TimeSpan delay)
    {
        CodexTimelineEntryVm entry = new(
            "turn.retry",
            "Selected model is at capacity",
            $"Retrying in {FormatDelay(delay)} ({retryAttempt}/{_capacityRetryDelays.Count}).",
            "The original message is still pending.",
            "warning",
            _timeProvider.GetUtcNow(),
            threadId,
            turnId,
            new Dictionary<string, string?>
            {
                ["retryAttempt"] = retryAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["retryCount"] = _capacityRetryDelays.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reason"] = "capacity",
            },
            false);

        await PublishNonTerminalEventAsync(entry).ConfigureAwait(false);
    }

    private async Task PublishEmptyOutputRetryNoticeAsync(string threadId, string? turnId, int retryAttempt, TimeSpan delay)
    {
        CodexTimelineEntryVm entry = new(
            "turn.retry",
            "Codex completed without visible output",
            $"Retrying in {FormatDelay(delay)} ({retryAttempt}/{_capacityRetryDelays.Count}).",
            "The original message is still pending.",
            "warning",
            _timeProvider.GetUtcNow(),
            threadId,
            turnId,
            new Dictionary<string, string?>
            {
                ["retryAttempt"] = retryAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["retryCount"] = _capacityRetryDelays.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reason"] = "empty-output",
            },
            false);

        await PublishNonTerminalEventAsync(entry).ConfigureAwait(false);
    }

    private async Task PublishCapacityExhaustedAsync(ActiveTurnState state)
    {
        CodexTimelineEntryVm entry = new(
            "turn.failed",
            "Selected model is still at capacity",
            $"Stopped after {_capacityRetryDelays.Count} retries.",
            "Please try again later or choose another model.",
            "danger",
            _timeProvider.GetUtcNow(),
            state.ThreadId,
            state.TurnId,
            new Dictionary<string, string?>
            {
                ["reason"] = "capacity",
                ["retryCount"] = _capacityRetryDelays.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            false);

        await PublishTerminalEventAsync(state, entry).ConfigureAwait(false);
    }

    private async Task PublishEmptyOutputExhaustedAsync(ActiveTurnState state)
    {
        CodexTimelineEntryVm entry = new(
            "turn.failed",
            "Codex completed without visible output",
            $"Stopped after {_capacityRetryDelays.Count} retries.",
            "No assistant text was received. Please try again or choose another model.",
            "danger",
            _timeProvider.GetUtcNow(),
            state.ThreadId,
            state.TurnId,
            new Dictionary<string, string?>
            {
                ["reason"] = "empty-output",
                ["retryCount"] = _capacityRetryDelays.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            false);

        await PublishTerminalEventAsync(state, entry).ConfigureAwait(false);
    }

    private async Task PublishNonTerminalEventAsync(CodexTimelineEntryVm entry)
    {
        RecordEvent(entry);

        try
        {
            if (!string.IsNullOrWhiteSpace(entry.ThreadId))
            {
                await _broadcaster.BroadcastThreadEventAsync(entry.ThreadId, entry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
            }

            await _telegramTurnOutputRelay.PublishTurnEventAsync(entry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to publish Codex capacity retry notice for thread {ThreadId}.", entry.ThreadId);
        }
    }

    private async Task PublishTerminalEventAsync(ActiveTurnState state, CodexTimelineEntryVm entry)
    {
        CodexTurnCloseoutSummary closeout = BuildTurnCloseoutSummary(state, entry);
        RecordEvent(entry, closeout);
        UpdateActiveTurnState(state.ThreadId, state.TurnId, entry);

        try
        {
            await _broadcaster.BroadcastThreadEventAsync(state.ThreadId, entry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
            await _telegramTurnOutputRelay.PublishTurnEventAsync(entry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
            await PublishMissingFinalResponseWarningIfNeededAsync(state, entry, closeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to publish terminal event for turn {TurnId} on thread {ThreadId}.", state.TurnId, state.ThreadId);
        }
    }

    private async Task PublishPendingFinalResponseAsync(ActiveTurnState state, CodexTimelineEntryVm? entry)
    {
        if (entry is null)
        {
            return;
        }

        try
        {
            await _broadcaster.BroadcastThreadEventAsync(state.ThreadId, entry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
            await _telegramTurnOutputRelay.PublishTurnEventAsync(entry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to publish final response for turn {TurnId} on thread {ThreadId}.", state.TurnId, state.ThreadId);
        }
    }

    private async Task PublishMissingFinalResponseWarningIfNeededAsync(
        ActiveTurnState state,
        CodexTimelineEntryVm terminalEntry,
        CodexTurnCloseoutSummary closeout)
    {
        if (!closeout.Warning)
        {
            return;
        }

        if (!_eventLog.TryMarkCloseoutWarningPublished(state.ThreadId, state.TurnId))
        {
            return;
        }

        await PublishRecoveredAssistantOutputIfNeededAsync(state).ConfigureAwait(false);

        CodexTimelineEntryVm warning = new(
            "turn.closeout.warning",
            "Turn completed without a final response",
            "The session is idle.",
            closeout.Message,
            "warning",
            _timeProvider.GetUtcNow(),
            state.ThreadId,
            state.TurnId,
            new Dictionary<string, string?>
            {
                ["terminalEventType"] = terminalEntry.Type,
                ["assistantTextSeen"] = closeout.AssistantTextSeen.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["finalResponseSeen"] = closeout.FinalResponseSeen.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            false);

        RecordEvent(warning);
        await _broadcaster.BroadcastThreadEventAsync(state.ThreadId, warning, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
        await _telegramTurnOutputRelay.PublishTurnEventAsync(warning, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
    }

    private async Task PublishRecoveredAssistantOutputIfNeededAsync(ActiveTurnState state)
    {
        if (_eventLog.HasVisibleAssistantOutput(state.ThreadId, state.TurnId)
            || string.IsNullOrWhiteSpace(state.AssistantOutputText))
        {
            return;
        }

        CodexTimelineEntryVm recovered = new(
            "turn.assistant.recovered",
            "Recovered assistant text",
            null,
            state.AssistantOutputText,
            "warning",
            _timeProvider.GetUtcNow(),
            state.ThreadId,
            state.TurnId,
            new Dictionary<string, string?>
            {
                ["reason"] = "terminal-without-final-response",
            },
            false);

        RecordEvent(recovered);
        await _broadcaster.BroadcastThreadEventAsync(state.ThreadId, recovered, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
        await _telegramTurnOutputRelay.PublishTurnEventAsync(recovered, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
    }

    private static bool IsTerminalTurnEvent(CodexTimelineEntryVm entry)
        => string.Equals(entry.Type, "turn.completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Type, "turn.failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Type, "turn.interrupted", StringComparison.OrdinalIgnoreCase)
            || IsMetadataFlagSet(entry, "terminal");

    private void RecordEvent(CodexTimelineEntryVm entry, CodexTurnCloseoutSummary? closeout = null)
        => _eventLog.Record(CodexSessionEventRecord.FromTimelineEntry(entry, closeout));

    private CodexTurnCloseoutSummary BuildTurnCloseoutSummary(ActiveTurnState state, CodexTimelineEntryVm entry)
    {
        bool completed = string.Equals(entry.Type, "turn.completed", StringComparison.OrdinalIgnoreCase);
        bool interrupted = string.Equals(entry.Type, "turn.interrupted", StringComparison.OrdinalIgnoreCase);
        bool terminalIncomplete = IsTerminalState(entry, CodexTurnTerminalState.Incomplete);
        bool finalResponseSeen = state.FinalResponseSeen || (completed && !IsEmptyCompletionBody(entry.Body));
        bool assistantTextSeen = state.HasVisibleAssistantOutput || _eventLog.HasVisibleAssistantOutput(state.ThreadId, state.TurnId);
        bool warning = completed && assistantTextSeen && !finalResponseSeen;
        string status = terminalIncomplete ? "incomplete" : completed ? "completed" : interrupted ? "interrupted" : "failed";
        string message = warning
            ? "Codex streamed assistant text but ended the turn without a final response item."
            : ResolveCloseoutMessage(status, finalResponseSeen, entry.Body);

        return new CodexTurnCloseoutSummary(
            state.TurnId,
            status,
            entry.Timestamp,
            assistantTextSeen,
            finalResponseSeen,
            warning,
            message);
    }

    private static string ResolveCloseoutMessage(string status, bool finalResponseSeen, string? body)
    {
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(body)
                ? "Turn failed without an error message."
                : body.Trim();
        }

        if (string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase))
        {
            return finalResponseSeen
                ? "Turn was interrupted after capturing a final response."
                : string.IsNullOrWhiteSpace(body)
                    ? "Turn was interrupted."
                    : body.Trim();
        }

        if (string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(body)
                ? "Codex stream ended before the SDK observed a terminal event."
                : body.Trim();
        }

        return finalResponseSeen
            ? "Turn completed with a final response."
            : "Turn completed without assistant response text.";
    }

    private static bool IsEmptySuccessfulCompletion(ActiveTurnState state, CodexTimelineEntryVm entry)
        => string.Equals(entry.Type, "turn.completed", StringComparison.OrdinalIgnoreCase)
            && IsEmptyCompletionBody(entry.Body)
            && !state.HasVisibleAssistantOutput
            && !state.FinalResponseSeen;

    private static bool IsEmptyCompletionBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return true;
        }

        string normalized = body.Trim();
        if (normalized.EndsWith(TurnCompletionMarker, StringComparison.Ordinal))
        {
            normalized = normalized[..^TurnCompletionMarker.Length].TrimEnd();
        }

        if (normalized.EndsWith(LegacyTurnFinishedMarker, StringComparison.Ordinal))
        {
            normalized = normalized[..^LegacyTurnFinishedMarker.Length].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(normalized);
    }

    private static bool IsVisibleCodexOutput(CodexTimelineEntryVm entry)
    {
        if (IsTerminalTurnEvent(entry))
        {
            return !string.IsNullOrWhiteSpace(entry.Body);
        }

        if (string.Equals(entry.Type, "item.agentMessage.delta", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(entry.Body);
        }

        if (string.Equals(entry.Type, "turn.finalResponse", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(entry.Body);
        }

        return !entry.IsInternal
            && (!string.IsNullOrWhiteSpace(entry.Body) || !string.IsNullOrWhiteSpace(entry.Subtitle));
    }

    private static bool IsVisibleAssistantOutput(CodexTimelineEntryVm entry)
        => (string.Equals(entry.Type, "item.agentMessage.delta", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Type, "turn.finalResponse", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(entry.Body);

    private static bool IsFinalResponse(CodexTimelineEntryVm entry)
        => string.Equals(entry.Type, "turn.finalResponse", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entry.Body);

    private static bool IsMetadataFlagSet(CodexTimelineEntryVm entry, string key)
        => entry.Metadata.TryGetValue(key, out string? value)
            && bool.TryParse(value, out bool parsed)
            && parsed;

    private static bool IsTerminalState(CodexTimelineEntryVm entry, CodexTurnTerminalState state)
        => entry.Metadata.TryGetValue("terminalState", out string? value)
            && string.Equals(value, state.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool IsCapacityThreadError(CodexTimelineEntryVm entry)
        => string.Equals(entry.Type, "thread.error", StringComparison.OrdinalIgnoreCase)
            && (IsCapacityMessage(entry.Subtitle) || IsCapacityMessage(entry.Body));

    private static bool IsCapacityException(Exception exception)
    {
        if (IsCapacityMessage(exception.Message))
        {
            return true;
        }

        return exception is AggregateException aggregateException
            ? aggregateException.Flatten().InnerExceptions.Any(IsCapacityException)
            : exception.InnerException is not null && IsCapacityException(exception.InnerException);
    }

    private static bool IsCapacityMessage(string? message)
        => !string.IsNullOrWhiteSpace(message)
            && message.Contains("model", StringComparison.OrdinalIgnoreCase)
            && message.Contains("at capacity", StringComparison.OrdinalIgnoreCase);

    private string CreateCapacityExhaustedMessage()
        => $"Selected model is still at capacity after {_capacityRetryDelays.Count} retries. Please try again later or choose another model.";

    private static string FormatDelay(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return "now";
        }

        if (delay.TotalSeconds < 60)
        {
            return $"{Math.Ceiling(delay.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)}s";
        }

        return $"{Math.Ceiling(delay.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture)}m";
    }

    private async Task PublishFailureAsync(ActiveTurnState state, Exception exception)
    {
        CodexTimelineEntryVm entry = new(
            "turn.failed",
            "Turn failed",
            state.TurnId,
            FormatTurnFailureMessage(exception),
            "danger",
            _timeProvider.GetUtcNow(),
            state.ThreadId,
            state.TurnId,
            new Dictionary<string, string?>
            {
                ["exceptionType"] = exception.GetType().Name,
                ["message"] = exception.Message,
            },
            false);

        await PublishTerminalEventAsync(state, entry).ConfigureAwait(false);
    }

    private static string FormatTurnFailureMessage(Exception exception)
    {
        string message = exception.Message;
        if (exception is UnauthorizedAccessException)
        {
            return $"Permission denied while Codex was running: {message}";
        }

        if (message.Contains("sandbox", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || message.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return $"Codex hit a permission or sandbox block: {message}";
        }

        return $"Codex stopped while streaming turn output: {message}";
    }

    private ActiveTurnState GetRequiredState(string threadId, string turnId)
    {
        if (_activeTurns.TryGetValue(threadId, out ActiveTurnState? state) && string.Equals(state.TurnId, turnId, StringComparison.Ordinal))
        {
            return state;
        }

        throw new InvalidOperationException($"Active turn '{turnId}' on thread '{threadId}' was not found.");
    }

    private sealed class ObservableTimelineSubscription : IObserver<IList<CodexTimelineEntryVm>>, IAsyncDisposable
    {
        private readonly Channel<CodexTimelineEntryVm> _channel = Channel.CreateUnbounded<CodexTimelineEntryVm>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        private IDisposable? _subscription;

        private ObservableTimelineSubscription()
        {
        }

        public static ObservableTimelineSubscription Subscribe(IObservable<IList<CodexTimelineEntryVm>> source)
        {
            ObservableTimelineSubscription observer = new();
            observer._subscription = source.Subscribe(observer);
            return observer;
        }

        public IAsyncEnumerable<CodexTimelineEntryVm> ReadAllAsync(CancellationToken cancellationToken)
            => _channel.Reader.ReadAllAsync(cancellationToken);

        public void OnNext(IList<CodexTimelineEntryVm> value)
        {
            foreach (CodexTimelineEntryVm item in value)
            {
                _channel.Writer.TryWrite(item);
            }
        }

        public void OnError(Exception error)
            => _channel.Writer.TryComplete(error);

        public void OnCompleted()
            => _channel.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            _subscription?.Dispose();
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ActiveTurnState
    {
        public ActiveTurnState(
            string threadId,
            string turnId,
            ICodexTurnHandle? turn,
            CodexTimelineEntryVm? lastEvent,
            ICodexThreadHandle? thread = null,
            IReadOnlyList<CodexInputItem>? input = null,
            CodexTurnOptions? turnOptions = null,
            int capacityRetryAttempts = 0)
        {
            ThreadId = threadId;
            TurnId = turnId;
            Turn = turn;
            Thread = thread;
            Input = input;
            TurnOptions = turnOptions;
            CapacityRetryAttempts = capacityRetryAttempts;
            StartedAt = DateTimeOffset.UtcNow;
            UpdatedAt = lastEvent?.Timestamp ?? StartedAt;
            LastEvent = lastEvent;
        }

        public string ThreadId { get; }

        public string TurnId { get; private set; }

        public ICodexTurnHandle? Turn { get; private set; }

        public ICodexThreadHandle? Thread { get; }

        public IReadOnlyList<CodexInputItem>? Input { get; }

        public CodexTurnOptions? TurnOptions { get; }

        public int CapacityRetryAttempts { get; private set; }

        public int EmptyOutputRetryAttempts { get; private set; }

        public bool HasVisibleCodexOutput { get; private set; }

        public bool HasVisibleAssistantOutput { get; private set; }

        public bool FinalResponseSeen { get; private set; }

        public string? AssistantOutputText { get; private set; }

        public DateTimeOffset StartedAt { get; }

        public DateTimeOffset UpdatedAt { get; private set; }

        public CodexTimelineEntryVm? LastEvent { get; private set; }

        public void Update(CodexTimelineEntryVm? entry)
        {
            if (!string.IsNullOrWhiteSpace(entry?.TurnId))
            {
                TurnId = entry.TurnId;
            }

            LastEvent = entry;
            UpdatedAt = entry?.Timestamp ?? DateTimeOffset.UtcNow;
        }

        public int IncrementCapacityRetryAttempts()
            => ++CapacityRetryAttempts;

        public int IncrementEmptyOutputRetryAttempts()
            => ++EmptyOutputRetryAttempts;

        public void RecordVisibleOutputIfNeeded(CodexTimelineEntryVm entry)
        {
            if (IsVisibleCodexOutput(entry))
            {
                HasVisibleCodexOutput = true;
            }

            if (IsVisibleAssistantOutput(entry))
            {
                HasVisibleAssistantOutput = true;
                AssistantOutputText = string.Concat(AssistantOutputText, entry.Body);
            }

            if (IsFinalResponse(entry))
            {
                FinalResponseSeen = true;
            }
        }

        public void ReplaceTurn(ICodexTurnHandle turn)
        {
            Turn = turn;
            TurnId = turn.Id;
            HasVisibleCodexOutput = false;
            HasVisibleAssistantOutput = false;
            FinalResponseSeen = false;
            AssistantOutputText = null;
            UpdatedAt = DateTimeOffset.UtcNow;
        }

        public CodexActiveTurnStateVm ToViewModel()
        {
            return new CodexActiveTurnStateVm(
                ThreadId,
                TurnId,
                StartedAt,
                UpdatedAt,
                LastEvent);
        }
    }

    private sealed record TurnStartResult(ICodexTurnHandle Turn, int CapacityRetryAttempts);

    private enum CapacityRetryResult
    {
        NotHandled,
        Scheduled,
        Exhausted,
    }

    private enum EmptyOutputRetryResult
    {
        NotHandled,
        Scheduled,
        Exhausted,
    }
}
