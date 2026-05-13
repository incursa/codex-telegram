using System.Runtime.CompilerServices;
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

    public async IAsyncEnumerable<CodexThreadEvent> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken)
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

            yield return _script.ToCompletedEvent();
        }
        finally
        {
            _script.Finished.TrySetResult(true);
        }
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

    public IReadOnlyList<IReadOnlyList<CodexInputItem>> SteeredInputs => _steeredInputs;

    public int InterruptCount => _interruptCount;

    public ScriptedCodexTurnScript AddEvent(CodexThreadEvent evt)
    {
        _events.Add(evt);
        return this;
    }

    public ScriptedCodexTurnScript AddDelta(string delta, string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.AssistantDelta(threadId ?? ThreadId, TurnId, delta));

    public ScriptedCodexTurnScript AddPlanUpdate(
        string explanation,
        IReadOnlyList<CodexTurnPlanStep> plan,
        string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.PlanUpdated(threadId ?? ThreadId, TurnId, explanation, plan));

    public ScriptedCodexTurnScript AddContextCompaction(string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.ContextCompaction(threadId ?? ThreadId, TurnId));

    public ScriptedCodexTurnScript AddUserInputRequest(string prompt, string? threadId = null)
        => AddEvent(ScriptedCodexTurnEvents.UserInputRequest(threadId ?? ThreadId, TurnId, prompt));

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

    public static CodexItemStartedEvent ContextCompaction(string threadId, string turnId)
        => new()
        {
            Type = "item.compaction",
            ThreadId = threadId,
            TurnId = turnId,
            Item = new CodexContextCompactionItem(),
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
