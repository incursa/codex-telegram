using System.Collections.Concurrent;
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

    void RegisterActiveTurn(string threadId, string turnId, CodexTurn? turn = null, CodexTimelineEntryVm? lastEvent = null);

    void UpdateActiveTurnState(string threadId, string turnId, CodexTimelineEntryVm? lastEvent = null);

    bool TryClearActiveTurn(string threadId, string turnId);

    Task SteerAsync(string threadId, string turnId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken);

    Task InterruptAsync(string threadId, string turnId, CancellationToken cancellationToken);
}

internal sealed class CodexTurnExecutionCoordinator
    : ICodexTurnExecutionCoordinator
{
    private readonly ConcurrentDictionary<string, ActiveTurnState> _activeTurns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _startingThreads = new(StringComparer.Ordinal);
    private readonly ICodexRealtimeBroadcaster _broadcaster;
    private readonly ITelegramTurnOutputRelay _telegramTurnOutputRelay;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<CodexTurnExecutionCoordinator> _logger;

    public CodexTurnExecutionCoordinator(
        ICodexRealtimeBroadcaster broadcaster,
        ITelegramTurnOutputRelay telegramTurnOutputRelay,
        IHostApplicationLifetime applicationLifetime,
        ILogger<CodexTurnExecutionCoordinator> logger)
    {
        _broadcaster = broadcaster;
        _telegramTurnOutputRelay = telegramTurnOutputRelay;
        _applicationLifetime = applicationLifetime;
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

    public void RegisterActiveTurn(string threadId, string turnId, CodexTurn? turn = null, CodexTimelineEntryVm? lastEvent = null)
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
        CodexThread thread,
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
            CodexTurn turn = await thread.StartTurnAsync(input, turnOptions, cancellationToken).ConfigureAwait(false);
            string threadId = string.IsNullOrWhiteSpace(turn.ThreadId) ? thread.Id ?? string.Empty : turn.ThreadId;
            RegisterActiveTurn(threadId, turn.Id, turn);
            ActiveTurnState state = GetRequiredState(threadId, turn.Id);

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

        try
        {
            await foreach (CodexThreadEvent evt in state.Turn.StreamAsync(_applicationLifetime.ApplicationStopping).ConfigureAwait(false))
            {
                CodexTimelineEntryVm entry = CodexViewModelMapper.ToTimelineEntryVm(evt, state.ThreadId);
                UpdateActiveTurnState(state.ThreadId, state.TurnId, entry);
                await _broadcaster.BroadcastThreadEventAsync(state.ThreadId, entry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
                await _telegramTurnOutputRelay.PublishTurnEventAsync(entry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Turn {TurnId} on thread {ThreadId} was cancelled.", state.TurnId, state.ThreadId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Turn {TurnId} on thread {ThreadId} failed during stream consumption.", state.TurnId, state.ThreadId);
            await PublishFailureAsync(state, exception).ConfigureAwait(false);
        }
        finally
        {
            TryClearActiveTurn(state.ThreadId, state.TurnId);
        }
    }

    private async Task PublishFailureAsync(ActiveTurnState state, Exception exception)
    {
        CodexTimelineEntryVm entry = new(
            "turn.failed",
            "Turn failed",
            state.TurnId,
            FormatTurnFailureMessage(exception),
            "danger",
            DateTimeOffset.UtcNow,
            state.ThreadId,
            state.TurnId,
            new Dictionary<string, string?>
            {
                ["exceptionType"] = exception.GetType().Name,
                ["message"] = exception.Message,
            },
            false);

        UpdateActiveTurnState(state.ThreadId, state.TurnId, entry);

        try
        {
            await _broadcaster.BroadcastThreadEventAsync(state.ThreadId, entry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
            await _telegramTurnOutputRelay.PublishTurnEventAsync(entry, _applicationLifetime.ApplicationStopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
        }
        catch (Exception publishException)
        {
            _logger.LogWarning(publishException, "Failed to publish failure event for turn {TurnId} on thread {ThreadId}.", state.TurnId, state.ThreadId);
        }
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

    private sealed class ActiveTurnState
    {
        public ActiveTurnState(string threadId, string turnId, CodexTurn? turn, CodexTimelineEntryVm? lastEvent)
        {
            ThreadId = threadId;
            TurnId = turnId;
            Turn = turn;
            StartedAt = DateTimeOffset.UtcNow;
            UpdatedAt = lastEvent?.Timestamp ?? StartedAt;
            LastEvent = lastEvent;
        }

        public string ThreadId { get; }

        public string TurnId { get; private set; }

        public CodexTurn? Turn { get; }

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
}
