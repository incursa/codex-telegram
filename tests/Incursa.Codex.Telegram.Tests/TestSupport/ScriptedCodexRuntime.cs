using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Incursa.Codex.Telegram.Services;
using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Tests.TestSupport;

internal sealed class ScriptedCodexRuntime : IDisposable
{
    public ScriptedCodexRuntimeClient Client { get; } = new();

    public ScriptedCodexTurnScript QueueTurn(string threadId, string? turnId = null)
        => Client.QueueTurn(threadId, turnId);

    public ICodexThreadHandle CreateThread(string threadId, bool started = true, CodexThreadOptions? defaults = null)
        => Client.CreateThreadHandle(threadId, started, defaults ?? new CodexThreadOptions());

    public void Dispose()
    {
    }
}

internal sealed class ScriptedCodexThreadHandle : ICodexThreadHandle
{
    private readonly ScriptedCodexThreadState _state;
    private readonly ScriptedCodexTurnScriptQueue _turnScripts;

    public ScriptedCodexThreadHandle(
        ScriptedCodexThreadState state,
        ScriptedCodexTurnScriptQueue turnScripts)
    {
        _state = state;
        _turnScripts = turnScripts;
    }

    public string? Id => _state.ThreadId;

    public bool Started => _state.Started;

    public CodexThreadOptions Defaults => _state.Defaults;

    public Task<ICodexTurnHandle> StartTurnAsync(
        IReadOnlyList<CodexInputItem> input,
        CodexTurnOptions turnOptions,
        CancellationToken cancellationToken)
    {
        _ = input;
        _ = turnOptions;
        _ = cancellationToken;

        ScriptedCodexTurnScript script = _turnScripts.Dequeue(Id ?? string.Empty);
        script.Started.TrySetResult(true);
        return Task.FromResult<ICodexTurnHandle>(new ScriptedCodexTurnHandle(script));
    }

    public Task SetNameAsync(string name, CancellationToken cancellationToken)
    {
        _ = name;
        _ = cancellationToken;
        _state.Name = name;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public Task CompactAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public Task<CodexThreadGoal?> GetGoalAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(_state.Goal);
    }

    public Task<CodexThreadGoal> SetGoalAsync(string objective, long? tokenBudget, CancellationToken cancellationToken)
    {
        _ = objective;
        _ = cancellationToken;
        CodexThreadGoal goal = (_state.Goal ?? new CodexThreadGoal
        {
            ThreadId = _state.ThreadId,
            CreatedAt = _state.CreatedAt,
        }) with
        {
            ThreadId = _state.ThreadId,
            Objective = objective,
            Status = CodexThreadGoalStatus.Active,
            TokenBudget = tokenBudget,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _state.Goal = goal;
        _state.UpdatedAt = goal.UpdatedAt;
        return Task.FromResult(goal);
    }

    public Task<CodexThreadGoal> SetGoalStatusAsync(CodexThreadGoalStatus status, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        CodexThreadGoal goal = (_state.Goal ?? new CodexThreadGoal
        {
            ThreadId = _state.ThreadId,
            Objective = "Existing goal",
            CreatedAt = _state.CreatedAt,
        }) with
        {
            ThreadId = _state.ThreadId,
            Status = status,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _state.Goal = goal;
        _state.UpdatedAt = goal.UpdatedAt;
        return Task.FromResult(goal);
    }

    public Task<bool> ClearGoalAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        bool removed = _state.Goal is not null;
        _state.Goal = null;
        _state.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.FromResult(removed);
    }
}

internal sealed class ScriptedCodexTurnHandle : ICodexTurnHandle
{
    private readonly ScriptedCodexTurnScript _script;

    public ScriptedCodexTurnHandle(ScriptedCodexTurnScript script)
    {
        _script = script;
    }

    public string Id => _script.TurnId;

    public string? ThreadId => _script.ThreadId;

    public IAsyncEnumerable<CodexThreadEvent> StreamAsync(CancellationToken cancellationToken)
    {
        _script.RecordRawStreamEnumeration();
        return EnumerateRawEventsAsync(cancellationToken);
    }

    public IObservable<CodexThreadEvent> ObserveEventsAsync()
    {
        _script.RecordRawObservableSubscription();
        return new ScriptedObservable<CodexThreadEvent>(EnumerateRawEventsAsync);
    }

    private async IAsyncEnumerable<CodexThreadEvent> EnumerateRawEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            if (_script.StartDelay is { } startDelay)
            {
                await Task.Delay(startDelay, cancellationToken).ConfigureAwait(false);
            }

            foreach (CodexThreadEvent evt in _script.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return evt;

                if (_script.InterEventDelay is { } interEventDelay)
                {
                    await Task.Delay(interEventDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            if (_script.CompletionGate is not null)
            {
                await _script.CompletionGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_script.FaultException is not null)
            {
                throw _script.FaultException;
            }

            if (_script.EmitAutoCompletion)
            {
                yield return _script.ToCompletedEvent();
            }
        }
        finally
        {
            _script.Finished.TrySetResult(true);
        }
    }

    public IAsyncEnumerable<CodexTurnEvent> StreamNormalizedAsync(CancellationToken cancellationToken)
    {
        _script.RecordNormalizedStreamEnumeration();
        return EnumerateNormalizedEventsAsync(cancellationToken);
    }

    public IObservable<CodexTurnEvent> ObserveNormalizedEventsAsync()
    {
        _script.RecordNormalizedObservableSubscription();
        return new ScriptedObservable<CodexTurnEvent>(EnumerateNormalizedEventsAsync);
    }

    private async IAsyncEnumerable<CodexTurnEvent> EnumerateNormalizedEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int sequenceNumber = 0;
        ScriptedNormalizationState normalizationState = new();
        StringBuilder assistantDeltas = new();

        CodexTurnEvent CreateEvent(
            string rawEventType,
            CodexTurnEventKind kind,
            string? title,
            string? text,
            IReadOnlyDictionary<string, string?>? metadata = null,
            CodexTurnEventImportance importance = CodexTurnEventImportance.Normal,
            bool isTerminal = false,
            CodexTurnTerminalState terminalState = CodexTurnTerminalState.None,
            bool contributesToFinalOutput = false,
            bool isUserVisibleByDefault = false)
            => new()
            {
                SequenceNumber = ++sequenceNumber,
                ThreadId = _script.ThreadId,
                TurnId = _script.TurnId,
                RawEventType = rawEventType,
                Kind = kind,
                Importance = importance,
                Timestamp = DateTimeOffset.UtcNow,
                Title = title,
                Text = text,
                Metadata = metadata ?? new Dictionary<string, string?>(StringComparer.Ordinal),
                IsTerminal = isTerminal,
                TerminalState = terminalState,
                ContributesToFinalOutput = contributesToFinalOutput,
                IsUserVisibleByDefault = isUserVisibleByDefault,
            };

        try
        {
            if (_script.StartDelay is { } startDelay)
            {
                await Task.Delay(startDelay, cancellationToken).ConfigureAwait(false);
            }

            foreach (CodexThreadEvent evt in _script.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (CodexTurnEvent normalized in Normalize(evt, CreateEvent, assistantDeltas, normalizationState))
                {
                    yield return normalized;
                }

                if (_script.InterEventDelay is { } interEventDelay)
                {
                    await Task.Delay(interEventDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            if (_script.CompletionGate is not null)
            {
                await _script.CompletionGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_script.FaultException is not null)
            {
                throw _script.FaultException;
            }

            if (_script.EmitAutoCompletion)
            {
                foreach (CodexTurnEvent normalized in Normalize(_script.ToCompletedEvent(), CreateEvent, assistantDeltas, normalizationState))
                {
                    yield return normalized;
                }
            }
        }
        finally
        {
            _script.Finished.TrySetResult(true);
        }

        if (!normalizationState.TerminalSeen)
        {
            if (assistantDeltas.Length > 0)
            {
                yield return CreateEvent(
                    "turn.stream.ended",
                    CodexTurnEventKind.FinalResponse,
                    "Final response",
                    assistantDeltas.ToString(),
                    new Dictionary<string, string?>
                    {
                        ["source"] = CodexFinalResponseSource.AssistantDelta.ToString(),
                        ["complete"] = false.ToString(),
                    },
                    CodexTurnEventImportance.High,
                    contributesToFinalOutput: true,
                    isUserVisibleByDefault: true);
            }

            yield return CreateEvent(
                "turn.stream.ended",
                CodexTurnEventKind.Terminal,
                "Turn stream ended without a terminal event",
                "The SDK did not observe turn.completed or turn.failed.",
                new Dictionary<string, string?>
                {
                    ["status"] = CodexTurnTerminalState.Incomplete.ToString(),
                },
                CodexTurnEventImportance.Critical,
                isTerminal: true,
                terminalState: CodexTurnTerminalState.Incomplete,
                isUserVisibleByDefault: true);
        }
    }

    public async Task<CodexTurnResult> RunToResultAsync(CancellationToken cancellationToken)
    {
        CodexTurnResult result = new()
        {
            ThreadId = ThreadId ?? string.Empty,
            TurnId = Id,
            TerminalState = CodexTurnTerminalState.Incomplete,
            StartedUtc = DateTimeOffset.UtcNow,
        };

        await foreach (CodexTurnEvent evt in StreamNormalizedAsync(cancellationToken).ConfigureAwait(false))
        {
            if (evt.Kind == CodexTurnEventKind.FinalResponse)
            {
                result = result with
                {
                    FinalResponseText = evt.Text,
                    FinalResponseSource = CodexFinalResponseSource.CompletedItem,
                    FinalResponseComplete = evt.Metadata.TryGetValue("complete", out string? complete)
                        && bool.TryParse(complete, out bool completeValue)
                        && completeValue,
                    FinalResponseCharCount = evt.Text?.Length ?? 0,
                };
            }

            if (evt.IsTerminal)
            {
                result = result with
                {
                    TerminalState = evt.TerminalState,
                    TerminalEventSeen = evt.TerminalState != CodexTurnTerminalState.Incomplete,
                    TerminalEventType = evt.RawEventType,
                    CompletedUtc = evt.Timestamp,
                };
            }
        }

        return result;
    }

    public Task SteerAsync(IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        _script.RecordSteer(input);
        return Task.CompletedTask;
    }

    public Task InterruptAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        _script.RecordInterrupt();
        return Task.CompletedTask;
    }

    private static IEnumerable<CodexTurnEvent> Normalize(
        CodexThreadEvent evt,
        Func<string, CodexTurnEventKind, string?, string?, IReadOnlyDictionary<string, string?>?, CodexTurnEventImportance, bool, CodexTurnTerminalState, bool, bool, CodexTurnEvent> createEvent,
        StringBuilder assistantDeltas,
        ScriptedNormalizationState state)
    {
        switch (evt)
        {
            case CodexTurnStartedEvent started:
                yield return createEvent(
                    evt.Type,
                    CodexTurnEventKind.Activity,
                    "Turn started",
                    started.Turn.Id,
                    new Dictionary<string, string?> { ["status"] = started.Turn.Status.ToString() },
                    CodexTurnEventImportance.Normal,
                    false,
                    CodexTurnTerminalState.None,
                    false,
                    false);
                break;

            case CodexUnknownThreadEvent unknown when string.Equals(unknown.UnknownType, "item.agentMessage.delta", StringComparison.OrdinalIgnoreCase):
                string? delta = GetString(unknown.RawPayload, "delta")
                    ?? GetString(unknown.RawPayload, "text")
                    ?? GetString(unknown.RawPayload, "content");
                if (!string.IsNullOrEmpty(delta))
                {
                    assistantDeltas.Append(delta);
                    yield return createEvent(
                        unknown.UnknownType,
                        CodexTurnEventKind.AssistantDelta,
                        "Assistant response delta",
                        delta,
                        new Dictionary<string, string?>(),
                        CodexTurnEventImportance.High,
                        false,
                        CodexTurnTerminalState.None,
                        true,
                        true);
                }

                break;

            case CodexThreadErrorEvent threadError:
                yield return createEvent(
                    evt.Type,
                    CodexTurnEventKind.Error,
                    "Thread error",
                    threadError.Error.Message,
                    new Dictionary<string, string?> { ["willRetry"] = threadError.WillRetry.ToString() },
                    CodexTurnEventImportance.Critical,
                    false,
                    CodexTurnTerminalState.None,
                    false,
                    true);
                break;

            case CodexTurnPlanUpdatedEvent plan:
                yield return createEvent(
                    evt.Type,
                    CodexTurnEventKind.Activity,
                    "Plan updated",
                    plan.Explanation,
                    new Dictionary<string, string?> { ["planStepCount"] = plan.Plan.Count.ToString() },
                    CodexTurnEventImportance.Normal,
                    false,
                    CodexTurnTerminalState.None,
                    false,
                    true);
                break;

            case CodexPlanDeltaEvent planDelta:
                yield return createEvent(
                    evt.Type,
                    CodexTurnEventKind.Activity,
                    "Plan delta",
                    planDelta.Delta,
                    new Dictionary<string, string?> { ["itemId"] = planDelta.ItemId },
                    CodexTurnEventImportance.Normal,
                    false,
                    CodexTurnTerminalState.None,
                    false,
                    false);
                break;

            case CodexThreadCompactedEvent:
                yield return createEvent(
                    evt.Type,
                    CodexTurnEventKind.Activity,
                    "Thread compacted",
                    null,
                    new Dictionary<string, string?>(),
                    CodexTurnEventImportance.High,
                    false,
                    CodexTurnTerminalState.None,
                    false,
                    true);
                break;

            case CodexUnknownThreadEvent unknown when unknown.UnknownType.Contains("requestApproval", StringComparison.OrdinalIgnoreCase)
                || unknown.UnknownType.Contains("approval.required", StringComparison.OrdinalIgnoreCase):
                yield return createEvent(
                    unknown.UnknownType,
                    CodexTurnEventKind.ApprovalNeeded,
                    "Approval needed",
                    null,
                    new Dictionary<string, string?>(),
                    CodexTurnEventImportance.Critical,
                    false,
                    CodexTurnTerminalState.None,
                    false,
                    true);
                break;

            case CodexItemStartedEvent itemStarted:
                yield return NormalizeItemEvent(evt.Type, itemStarted.Item, createEvent);
                break;

            case CodexItemUpdatedEvent itemUpdated:
                yield return NormalizeItemEvent(evt.Type, itemUpdated.Item, createEvent);
                break;

            case CodexItemCompletedEvent itemCompleted:
                if (itemCompleted.Item is CodexAgentMessageItem agentMessage && !string.IsNullOrWhiteSpace(agentMessage.Text))
                {
                    if (agentMessage.Phase == CodexMessagePhase.FinalAnswer)
                    {
                        yield return CreateFinalResponse(agentMessage.Text, CodexFinalResponseSource.CompletedItem, complete: false, createEvent);
                    }
                    else
                    {
                        yield return createEvent(
                            evt.Type,
                            CodexTurnEventKind.AssistantMessage,
                            "Assistant message",
                            agentMessage.Text,
                            new Dictionary<string, string?> { ["phase"] = agentMessage.Phase?.ToString() },
                            CodexTurnEventImportance.Normal,
                            false,
                            CodexTurnTerminalState.None,
                            agentMessage.Phase is null,
                            agentMessage.Phase is null);
                    }
                }
                else
                {
                    yield return NormalizeItemEvent(evt.Type, itemCompleted.Item, createEvent);
                }

                break;

            case CodexTurnCompletedEvent completed:
                state.TerminalSeen = true;
                string? finalResponse = SelectFinalResponse(completed.Turn.Items);
                if (string.IsNullOrWhiteSpace(finalResponse) && assistantDeltas.Length > 0)
                {
                    finalResponse = assistantDeltas.ToString();
                }

                if (!string.IsNullOrWhiteSpace(finalResponse))
                {
                    yield return CreateFinalResponse(finalResponse, CodexFinalResponseSource.TerminalEvent, complete: true, createEvent);
                }

                yield return createEvent(
                    evt.Type,
                    CodexTurnEventKind.Terminal,
                    "Turn completed",
                    completed.Turn.Error?.Message,
                    new Dictionary<string, string?> { ["status"] = completed.Turn.Status.ToString() },
                    CodexTurnEventImportance.High,
                    true,
                    CodexTurnTerminalState.Completed,
                    false,
                    true);
                break;

            case CodexTurnFailedEvent failed:
                state.TerminalSeen = true;
                yield return createEvent(
                    evt.Type,
                    CodexTurnEventKind.Error,
                    "Turn failed",
                    failed.Turn.Error?.Message,
                    new Dictionary<string, string?>(),
                    CodexTurnEventImportance.Critical,
                    false,
                    CodexTurnTerminalState.None,
                    false,
                    true);
                yield return createEvent(
                    evt.Type,
                    CodexTurnEventKind.Terminal,
                    "Turn failed",
                    failed.Turn.Error?.Message,
                    new Dictionary<string, string?> { ["status"] = failed.Turn.Status.ToString() },
                    CodexTurnEventImportance.Critical,
                    true,
                    CodexTurnTerminalState.Failed,
                    false,
                    true);
                break;

            default:
                yield return createEvent(
                    evt.Type,
                    CodexTurnEventKind.Progress,
                    evt.Type,
                    null,
                    new Dictionary<string, string?>(),
                    CodexTurnEventImportance.Low,
                    false,
                    CodexTurnTerminalState.None,
                    false,
                    false);
                break;
        }
    }

    private static CodexTurnEvent NormalizeItemEvent(
        string rawType,
        CodexThreadItem item,
        Func<string, CodexTurnEventKind, string?, string?, IReadOnlyDictionary<string, string?>?, CodexTurnEventImportance, bool, CodexTurnTerminalState, bool, bool, CodexTurnEvent> createEvent)
        => createEvent(
            rawType,
            item is CodexImageViewItem or CodexImageGenerationItem ? CodexTurnEventKind.Artifact : CodexTurnEventKind.Progress,
            $"Item: {item.Type}",
            item switch
            {
                CodexCommandExecutionItem command => command.Command,
                CodexContextCompactionItem => "Context compaction",
                CodexAgentMessageItem message => message.Text,
                _ => null,
            },
            item switch
            {
                CodexCommandExecutionItem command => new Dictionary<string, string?>
                {
                    ["command"] = command.Command,
                    ["status"] = command.Status.ToString(),
                    ["exitCode"] = command.ExitCode?.ToString(),
                    ["durationMs"] = command.DurationMs?.ToString(),
                },
                _ => new Dictionary<string, string?>(),
            },
            CodexTurnEventImportance.Low,
            false,
            CodexTurnTerminalState.None,
            false,
            false);

    private static CodexTurnEvent CreateFinalResponse(
        string text,
        CodexFinalResponseSource source,
        bool complete,
        Func<string, CodexTurnEventKind, string?, string?, IReadOnlyDictionary<string, string?>?, CodexTurnEventImportance, bool, CodexTurnTerminalState, bool, bool, CodexTurnEvent> createEvent)
        => createEvent(
            "turn.finalResponse",
            CodexTurnEventKind.FinalResponse,
            "Final response",
            text,
            new Dictionary<string, string?>
            {
                ["source"] = source.ToString(),
                ["complete"] = complete.ToString(),
            },
            CodexTurnEventImportance.High,
            false,
            CodexTurnTerminalState.None,
            true,
            true);

    private static string? SelectFinalResponse(IReadOnlyList<CodexThreadItem> items)
        => items.OfType<CodexAgentMessageItem>().LastOrDefault(item => item.Phase == CodexMessagePhase.FinalAnswer && !string.IsNullOrWhiteSpace(item.Text))?.Text
            ?? items.OfType<CodexAgentMessageItem>().LastOrDefault(item => item.Phase is null && !string.IsNullOrWhiteSpace(item.Text))?.Text;

    private static string? GetString(JsonObject? payload, string name)
        => payload is not null
            && payload.TryGetPropertyValue(name, out JsonNode? node)
            && node is JsonValue value
            && value.TryGetValue(out string? text)
            && !string.IsNullOrEmpty(text)
                ? text
                : null;

    private sealed class ScriptedNormalizationState
    {
        public bool TerminalSeen { get; set; }
    }
}

internal sealed class ScriptedObservable<T> : IObservable<T>
{
    private readonly Func<CancellationToken, IAsyncEnumerable<T>> _sourceFactory;

    public ScriptedObservable(Func<CancellationToken, IAsyncEnumerable<T>> sourceFactory)
    {
        _sourceFactory = sourceFactory;
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        CancellationTokenSource cancellation = new();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (T item in _sourceFactory(cancellation.Token).ConfigureAwait(false))
                {
                    observer.OnNext(item);
                }

                observer.OnCompleted();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                observer.OnCompleted();
            }
            catch (Exception exception)
            {
                observer.OnError(exception);
            }
        });

        return new Subscription(cancellation);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private int _disposed;

        public Subscription(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cancellation.Cancel();
            }
        }
    }
}

internal sealed class ScriptedCodexTurnScriptQueue
{
    private readonly Dictionary<string, Queue<ScriptedCodexTurnScript>> _turnScriptsByThread = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _nextTurnSequence = 1;

    public ScriptedCodexTurnScript QueueTurn(string threadId, string? turnId = null)
    {
        ScriptedCodexTurnScript script = new(threadId, turnId ?? NextTurnId());
        lock (_gate)
        {
            if (!_turnScriptsByThread.TryGetValue(threadId, out Queue<ScriptedCodexTurnScript>? queue))
            {
                queue = new Queue<ScriptedCodexTurnScript>();
                _turnScriptsByThread[threadId] = queue;
            }

            queue.Enqueue(script);
        }

        return script;
    }

    public ScriptedCodexTurnScript Dequeue(string threadId)
    {
        lock (_gate)
        {
            if (_turnScriptsByThread.TryGetValue(threadId, out Queue<ScriptedCodexTurnScript>? queue) && queue.Count > 0)
            {
                return queue.Dequeue();
            }
        }

        throw new InvalidOperationException($"No scripted Codex turn was queued for thread '{threadId}'.");
    }

    private string NextTurnId()
        => $"turn-{_nextTurnSequence++}";
}

internal sealed class ScriptedCodexTurnScript
{
    private readonly List<CodexThreadEvent> _events = [];
    private readonly List<IReadOnlyList<CodexInputItem>> _steeredInputs = [];
    private TimeSpan? _startDelay;
    private TimeSpan? _interEventDelay;
    private TaskCompletionSource<bool>? _completionGate;
    private Exception? _faultException;
    private IReadOnlyList<CodexThreadItem>? _terminalItems;
    private CodexUsage? _usage;
    private int _interruptCount;
    private int _rawObservableSubscriptionCount;
    private int _normalizedObservableSubscriptionCount;
    private int _rawStreamEnumerationCount;
    private int _normalizedStreamEnumerationCount;
    private bool _emitAutoCompletion = true;

    public ScriptedCodexTurnScript(string threadId, string turnId)
    {
        ThreadId = threadId;
        TurnId = turnId;
        AddEvent(ScriptedCodexTurnEvents.TurnStarted(threadId, turnId));
    }

    public string ThreadId { get; }

    public string TurnId { get; }

    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<CodexThreadEvent> Events => _events;

    public TimeSpan? StartDelay => _startDelay;

    public TimeSpan? InterEventDelay => _interEventDelay;

    public TaskCompletionSource<bool>? CompletionGate => _completionGate;

    public Exception? FaultException => _faultException;

    public Exception? UnhandledException { get; internal set; }

    internal bool EmitAutoCompletion => _emitAutoCompletion;

    public IReadOnlyList<IReadOnlyList<CodexInputItem>> SteeredInputs => _steeredInputs;

    public int InterruptCount => _interruptCount;

    public int RawObservableSubscriptionCount => _rawObservableSubscriptionCount;

    public int NormalizedObservableSubscriptionCount => _normalizedObservableSubscriptionCount;

    public int RawStreamEnumerationCount => _rawStreamEnumerationCount;

    public int NormalizedStreamEnumerationCount => _normalizedStreamEnumerationCount;

    public ScriptedCodexTurnScript AddEvent(CodexThreadEvent evt)
    {
        _events.Add(evt);
        return this;
    }

    public ScriptedCodexTurnScript AddCompletionEvent(
        string? finalResponse = null,
        IReadOnlyList<CodexThreadItem>? terminalItems = null,
        CodexUsage? usage = null)
    {
        _events.Add(ScriptedCodexTurnEvents.TurnCompleted(ThreadId, TurnId, finalResponse, terminalItems, usage));
        _emitAutoCompletion = false;
        return this;
    }

    public ScriptedCodexTurnScript AddDelta(string delta, string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.AssistantDelta(threadId ?? ThreadId, TurnId, delta));

    public ScriptedCodexTurnScript AddPlanUpdate(
        string explanation,
        IReadOnlyList<CodexTurnPlanStep> plan,
        string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.PlanUpdated(threadId ?? ThreadId, TurnId, explanation, plan));

    public ScriptedCodexTurnScript AddPlanDelta(string delta, string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.PlanDelta(threadId ?? ThreadId, TurnId, delta));

    public ScriptedCodexTurnScript AddContextCompaction(string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.ContextCompaction(threadId ?? ThreadId, TurnId));

    public ScriptedCodexTurnScript AddThreadCompacted(string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.ThreadCompacted(threadId ?? ThreadId, TurnId));

    public ScriptedCodexTurnScript AddUserInputRequest(string prompt, string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.UserInputRequest(threadId ?? ThreadId, TurnId, prompt));

    public ScriptedCodexTurnScript AddApprovalRequest(string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.ApprovalRequest(threadId ?? ThreadId, TurnId));

    public ScriptedCodexTurnScript AddUnknownProgress(string type, string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.UnknownProgress(type, threadId ?? ThreadId, TurnId));

    public ScriptedCodexTurnScript AddAssistantMessage(string text, string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.AssistantMessage(threadId ?? ThreadId, TurnId, text));

    public ScriptedCodexTurnScript AddImageArtifact(string path, string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.ImageArtifact(threadId ?? ThreadId, TurnId, path));

    public ScriptedCodexTurnScript AddThreadError(string message, bool willRetry = false, string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.ThreadError(threadId ?? ThreadId, TurnId, message, willRetry));

    public ScriptedCodexTurnScript AddCommandProgress(
        string command,
        CodexCommandExecutionStatus status,
        string? cwd = null,
        int? exitCode = null,
        int? durationMs = null,
        string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.CommandExecution(threadId ?? ThreadId, TurnId, command, status, cwd, exitCode, durationMs));

    public ScriptedCodexTurnScript WithStartDelay(TimeSpan delay)
    {
        _startDelay = delay;
        return this;
    }

    public ScriptedCodexTurnScript WithInterEventDelay(TimeSpan delay)
    {
        _interEventDelay = delay;
        return this;
    }

    public ScriptedCodexTurnScript HoldCompletion()
    {
        _completionGate ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return this;
    }

    public ScriptedCodexTurnScript ReleaseCompletion()
    {
        _completionGate?.TrySetResult(true);
        return this;
    }

    public ScriptedCodexTurnScript Fault(Exception exception)
    {
        _faultException = exception;
        return this;
    }

    public ScriptedCodexTurnScript Complete(
        string? finalResponse = null,
        IReadOnlyList<CodexThreadItem>? terminalItems = null,
        CodexUsage? usage = null)
    {
        _terminalItems = terminalItems;
        if (finalResponse is not null && terminalItems is null)
        {
            _terminalItems = ScriptedCodexTurnEvents.FinalResponseItems(finalResponse);
        }

        _usage = usage;
        return this;
    }

    public ScriptedCodexTurnScript CompleteWithLongResponse(string finalResponse, int chunkSize = 12)
    {
        string[] chunks = finalResponse
            .Chunk(Math.Max(1, chunkSize))
            .Select(chunk => new string(chunk))
            .ToArray();

        foreach (string chunk in chunks)
        {
            AddDelta(chunk);
        }

        return Complete(finalResponse);
    }

    internal void RecordSteer(IReadOnlyList<CodexInputItem> input)
        => _steeredInputs.Add(input);

    internal void RecordInterrupt()
        => _interruptCount++;

    internal void RecordRawObservableSubscription()
        => _rawObservableSubscriptionCount++;

    internal void RecordNormalizedObservableSubscription()
        => _normalizedObservableSubscriptionCount++;

    internal void RecordRawStreamEnumeration()
        => _rawStreamEnumerationCount++;

    internal void RecordNormalizedStreamEnumeration()
        => _normalizedStreamEnumerationCount++;

    internal CodexTurnCompletedEvent ToCompletedEvent()
        => ScriptedCodexTurnEvents.TurnCompleted(ThreadId, TurnId, FinalResponseText, _terminalItems, _usage);

    private string? FinalResponseText
        => _terminalItems?.OfType<CodexAgentMessageItem>().LastOrDefault(item => item.Phase == CodexMessagePhase.FinalAnswer && !string.IsNullOrWhiteSpace(item.Text))?.Text
            ?? _terminalItems?.OfType<CodexAgentMessageItem>().LastOrDefault(item => item.Phase is null && !string.IsNullOrWhiteSpace(item.Text))?.Text;
}

internal static class ScriptedCodexTurnEvents
{
    public static CodexTurnStartedEvent TurnStarted(string threadId, string turnId)
        => new()
        {
            Type = "turn.started",
            Turn = new CodexTurnRecord
            {
                Id = turnId,
                Status = CodexTurnStatus.InProgress,
            },
        };

    public static CodexUnknownThreadEvent AssistantDelta(string threadId, string turnId, string delta)
        => new("item.agentMessage.delta")
        {
            RawPayload = new JsonObject
            {
                ["threadId"] = threadId,
                ["turnId"] = turnId,
                ["delta"] = delta,
            },
        };

    public static CodexTurnPlanUpdatedEvent PlanUpdated(
        string threadId,
        string turnId,
        string explanation,
        IReadOnlyList<CodexTurnPlanStep> plan)
        => new()
        {
            Type = "turn.plan.updated",
            ThreadId = threadId,
            TurnId = turnId,
            Explanation = explanation,
            Plan = plan,
        };

    public static CodexPlanDeltaEvent PlanDelta(string threadId, string turnId, string delta)
        => new()
        {
            Type = "item.plan.delta",
            ThreadId = threadId,
            TurnId = turnId,
            ItemId = "plan-item-1",
            Delta = delta,
        };

    public static CodexItemStartedEvent ContextCompaction(string threadId, string turnId)
        => new()
        {
            Type = "item.compaction",
            ThreadId = threadId,
            TurnId = turnId,
            Item = new CodexContextCompactionItem(),
        };

    public static CodexThreadCompactedEvent ThreadCompacted(string threadId, string turnId)
        => new()
        {
            Type = "thread.compacted",
            ThreadId = threadId,
            TurnId = turnId,
        };

    public static CodexUnknownThreadEvent UserInputRequest(string threadId, string turnId, string prompt)
        => new("item.userInput.request")
        {
            RawPayload = new JsonObject
            {
                ["threadId"] = threadId,
                ["turnId"] = turnId,
                ["prompt"] = prompt,
            },
        };

    public static CodexUnknownThreadEvent ApprovalRequest(string threadId, string turnId)
        => new("requestApproval")
        {
            RawPayload = new JsonObject
            {
                ["threadId"] = threadId,
                ["turnId"] = turnId,
            },
        };

    public static CodexUnknownThreadEvent UnknownProgress(string type, string threadId, string turnId)
        => new(type)
        {
            RawPayload = new JsonObject
            {
                ["threadId"] = threadId,
                ["turnId"] = turnId,
            },
        };

    public static CodexItemCompletedEvent AssistantMessage(string threadId, string turnId, string text)
        => new()
        {
            Type = "item.completed",
            ThreadId = threadId,
            TurnId = turnId,
            Item = new CodexAgentMessageItem
            {
                Text = text,
            },
        };

    public static CodexItemCompletedEvent ImageArtifact(string threadId, string turnId, string path)
        => new()
        {
            Type = "item.completed",
            ThreadId = threadId,
            TurnId = turnId,
            Item = new CodexImageViewItem
            {
                Path = path,
            },
        };

    public static CodexThreadErrorEvent ThreadError(string threadId, string turnId, string message, bool willRetry)
        => new()
        {
            Type = "thread.error",
            ThreadId = threadId,
            TurnId = turnId,
            WillRetry = willRetry,
            Error = new CodexTurnError
            {
                Message = message,
            },
        };

    public static CodexItemStartedEvent CommandExecution(
        string threadId,
        string turnId,
        string command,
        CodexCommandExecutionStatus status,
        string? cwd = null,
        int? exitCode = null,
        int? durationMs = null)
        => new()
        {
            Type = "item.command",
            ThreadId = threadId,
            TurnId = turnId,
            Item = new CodexCommandExecutionItem
            {
                Type = "item.command",
                Command = command,
                Status = status,
                Cwd = cwd ?? string.Empty,
                ExitCode = exitCode,
                DurationMs = durationMs,
            },
        };

    public static CodexTurnCompletedEvent TurnCompleted(
        string threadId,
        string turnId,
        string? finalResponse,
        IReadOnlyList<CodexThreadItem>? terminalItems,
        CodexUsage? usage)
        => new()
        {
            Type = "turn.completed",
            Turn = CreateTurnRecord(threadId, turnId, CodexTurnStatus.Completed, finalResponse, terminalItems, usage, null),
        };

    public static CodexTurnFailedEvent TurnFailed(
        string threadId,
        string turnId,
        string message,
        IReadOnlyList<CodexThreadItem>? terminalItems = null,
        CodexUsage? usage = null)
        => new()
        {
            Type = "turn.failed",
            Turn = CreateTurnRecord(threadId, turnId, CodexTurnStatus.Failed, null, terminalItems, usage, new InvalidOperationException(message)),
        };

    public static IReadOnlyList<CodexThreadItem> FinalResponseItems(string finalResponse)
        => [new CodexAgentMessageItem
        {
            Type = "agentMessage",
            Phase = CodexMessagePhase.FinalAnswer,
            Text = finalResponse,
        }];

    public static CodexTurnRecord CreateTurnRecord(
        string threadId,
        string turnId,
        CodexTurnStatus status,
        string? finalResponse,
        IReadOnlyList<CodexThreadItem>? items,
        CodexUsage? usage,
        Exception? exception = null)
        => new()
        {
            Id = turnId,
            Status = status,
            Error = exception is null
                ? null
                : new CodexTurnError
                {
                    Message = exception.Message,
                },
            Items = items ?? (finalResponse is null ? [] : FinalResponseItems(finalResponse)),
            Usage = usage ?? CreateUsage(),
        };

    public static CodexUsage CreateUsage(int totalTokens = 0)
        => new()
        {
            Total = new CodexTokenUsageBreakdown
            {
                CachedInputTokens = 0,
                InputTokens = 0,
                OutputTokens = 0,
                ReasoningOutputTokens = 0,
                TotalTokens = totalTokens,
            },
            ModelContextWindow = null,
        };
}
