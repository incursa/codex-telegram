using Incursa.Codex.Telegram.Services;
using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Tests.TestSupport;

internal sealed class ScriptedCodexRuntimeClient : ICodexRuntimeClient
{
    private readonly ScriptedCodexTurnScriptQueue _turnScripts = new();
    private readonly Dictionary<string, ScriptedCodexThreadState> _threads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _queuedStartThreadIds = new();
    private readonly Queue<string> _queuedForkThreadIds = new();
    private readonly Queue<CodexRuntimeMetadata> _queuedInitializeResults = new();
    private readonly Queue<CodexThreadListResult> _queuedListThreadsResults = new();
    private readonly Dictionary<string, Queue<CodexThreadSnapshot>> _queuedReadThreadSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<CodexModelListResult> _queuedListModelsResults = new();
    private readonly object _gate = new();
    private int _nextThreadSequence = 1;
    private int _nextForkSequence = 1;

    public CodexRuntimeCapabilities? Capabilities { get; set; } = CreateCapabilities();

    public ScriptedCodexTurnScript QueueTurn(string threadId, string? turnId = null)
        => _turnScripts.QueueTurn(threadId, turnId);

    public ICodexThreadHandle CreateThreadHandle(string threadId, bool started = true, CodexThreadOptions? defaults = null)
    {
        lock (_gate)
        {
            return GetOrCreateThreadState(threadId, started, defaults ?? new CodexThreadOptions()).Handle;
        }
    }

    public void QueueStartThreadId(string threadId)
    {
        lock (_gate)
        {
            _queuedStartThreadIds.Enqueue(threadId);
        }
    }

    public void QueueForkThreadId(string threadId)
    {
        lock (_gate)
        {
            _queuedForkThreadIds.Enqueue(threadId);
        }
    }

    public void QueueInitializeResult(CodexRuntimeMetadata metadata)
    {
        lock (_gate)
        {
            _queuedInitializeResults.Enqueue(metadata);
        }
    }

    public void QueueListThreadsResult(CodexThreadListResult result)
    {
        lock (_gate)
        {
            _queuedListThreadsResults.Enqueue(result);
        }
    }

    public void QueueReadThreadSnapshot(string threadId, CodexThreadSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_queuedReadThreadSnapshots.TryGetValue(threadId, out Queue<CodexThreadSnapshot>? queue))
            {
                queue = new Queue<CodexThreadSnapshot>();
                _queuedReadThreadSnapshots[threadId] = queue;
            }

            queue.Enqueue(snapshot);
        }
    }

    public void QueueListModelsResult(CodexModelListResult result)
    {
        lock (_gate)
        {
            _queuedListModelsResults.Enqueue(result);
        }
    }

    public Task<CodexRuntimeMetadata> InitializeAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        lock (_gate)
        {
            if (_queuedInitializeResults.Count > 0)
            {
                return Task.FromResult(_queuedInitializeResults.Dequeue());
            }
        }

        return Task.FromResult(CreateRuntimeMetadata());
    }

    public Task<CodexThreadListResult> ListThreadsAsync(CodexThreadListOptions options, CancellationToken cancellationToken)
    {
        _ = options;
        _ = cancellationToken;
        lock (_gate)
        {
            if (_queuedListThreadsResults.Count > 0)
            {
                return Task.FromResult(_queuedListThreadsResults.Dequeue());
            }

            CodexThreadListResult result = new()
            {
                Threads = _threads.Values
                    .OrderBy(state => state.ThreadId, StringComparer.OrdinalIgnoreCase)
                    .Select(state => state.ToSummary())
                    .ToArray(),
                NextCursor = null,
            };

            return Task.FromResult(result);
        }
    }

    public Task<CodexThreadSnapshot> ReadThreadAsync(string threadId, CodexThreadReadOptions options, CancellationToken cancellationToken)
    {
        _ = options;
        _ = cancellationToken;
        lock (_gate)
        {
            if (_queuedReadThreadSnapshots.TryGetValue(threadId, out Queue<CodexThreadSnapshot>? queue) && queue.Count > 0)
            {
                return Task.FromResult(queue.Dequeue());
            }

            return Task.FromResult(GetOrCreateThreadState(threadId, started: true, defaults: new CodexThreadOptions()).ToSnapshot());
        }
    }

    public Task<ICodexThreadHandle> StartThreadAsync(CodexThreadOptions options, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        lock (_gate)
        {
            string threadId = _queuedStartThreadIds.Count > 0 ? _queuedStartThreadIds.Dequeue() : NextThreadId();
            ScriptedCodexThreadState state = GetOrCreateThreadState(threadId, started: true, defaults: options);
            state.ApplyThreadOptions(options);
            return Task.FromResult<ICodexThreadHandle>(state.Handle);
        }
    }

    public Task<ICodexThreadHandle> ResumeThreadAsync(string threadId, CodexThreadOptions? options, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        lock (_gate)
        {
            ScriptedCodexThreadState state = GetOrCreateThreadState(threadId, started: true, defaults: options ?? new CodexThreadOptions());
            if (options is not null)
            {
                state.ApplyThreadOptions(options);
            }

            return Task.FromResult<ICodexThreadHandle>(state.Handle);
        }
    }

    public Task<ICodexThreadHandle> ForkThreadAsync(string threadId, CodexThreadForkOptions options, CancellationToken cancellationToken)
    {
        _ = options;
        _ = cancellationToken;
        lock (_gate)
        {
            string forkedThreadId = _queuedForkThreadIds.Count > 0 ? _queuedForkThreadIds.Dequeue() : NextForkThreadId();
            ScriptedCodexThreadState source = GetOrCreateThreadState(threadId, started: true, defaults: new CodexThreadOptions());
            ScriptedCodexThreadState forked = GetOrCreateThreadState(forkedThreadId, started: true, defaults: source.Defaults);
            forked.CopyFrom(source);
            return Task.FromResult<ICodexThreadHandle>(forked.Handle);
        }
    }

    public Task ArchiveThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        lock (_gate)
        {
            ScriptedCodexThreadState state = GetOrCreateThreadState(threadId, started: true, defaults: new CodexThreadOptions());
            state.Status = CreateArchivedStatus();
            state.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task UnarchiveThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        lock (_gate)
        {
            ScriptedCodexThreadState state = GetOrCreateThreadState(threadId, started: true, defaults: new CodexThreadOptions());
            state.Status = CreateIdleStatus();
            state.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task<CodexModelListResult> ListModelsAsync(CodexModelListOptions options, CancellationToken cancellationToken)
    {
        _ = options;
        _ = cancellationToken;
        lock (_gate)
        {
            if (_queuedListModelsResults.Count > 0)
            {
                return Task.FromResult(_queuedListModelsResults.Dequeue());
            }
        }

        return Task.FromResult(new CodexModelListResult
        {
            Models = Array.Empty<CodexModel>(),
            NextCursor = null,
        });
    }

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;

    private ScriptedCodexThreadState GetOrCreateThreadState(string threadId, bool started, CodexThreadOptions defaults)
    {
        if (_threads.TryGetValue(threadId, out ScriptedCodexThreadState? existing))
        {
            existing.Started |= started;
            return existing;
        }

        ScriptedCodexThreadState state = new(threadId, started, defaults, _turnScripts);
        _threads[threadId] = state;
        return state;
    }

    private string NextThreadId()
        => $"thread-{_nextThreadSequence++}";

    private string NextForkThreadId()
        => $"fork-{_nextForkSequence++}";

    private static CodexRuntimeCapabilities CreateCapabilities()
        => new()
        {
            BackendSelection = CodexBackendSelection.Exec,
            ExperimentalApi = false,
            OptOutNotificationMethods = [],
            SupportsAccountRateLimits = true,
            SupportsArchiveThread = true,
            SupportsCompactThread = true,
            SupportsForkThread = true,
            SupportsListModels = true,
            SupportsListThreads = true,
            SupportsReadThread = true,
            SupportsResumeThread = true,
            SupportsSetThreadName = true,
            SupportsStartThread = true,
            SupportsThreadGoals = true,
            SupportsThreadStreaming = true,
            SupportsTurnInterruption = true,
            SupportsTurnSteering = true,
            SupportsUnarchiveThread = true,
        };

    private static CodexRuntimeMetadata CreateRuntimeMetadata()
        => new()
        {
            PlatformFamily = "test",
            PlatformOs = "test",
            UserAgent = "scripted-codex-runtime",
        };

    private static CodexThreadStatus CreateIdleStatus()
        => new CodexIdleThreadStatus
        {
            Type = "idle",
        };

    private static CodexThreadStatus CreateArchivedStatus()
        => new CodexIdleThreadStatus
        {
            Type = "archived",
        };

}

internal sealed class ScriptedCodexThreadState
{
    public ScriptedCodexThreadState(
        string threadId,
        bool started,
        CodexThreadOptions defaults,
        ScriptedCodexTurnScriptQueue turnScripts)
    {
        ThreadId = threadId;
        Started = started;
        Defaults = defaults;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
        Handle = new ScriptedCodexThreadHandle(this, turnScripts);
    }

    public string ThreadId { get; }

    public bool Started { get; set; }

    public CodexThreadOptions Defaults { get; }

    public ScriptedCodexThreadHandle Handle { get; }

    public string? Name { get; set; }

    public string Preview { get; set; } = string.Empty;

    public CodexThreadStatus Status { get; set; } = new CodexIdleThreadStatus
    {
        Type = "idle",
    };

    public string ModelProvider { get; set; } = "test";

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool Ephemeral { get; set; }

    public string? Path { get; set; }

    public CodexSessionSource? Source { get; set; } = new CodexSubAgentSessionSource(new CodexOtherSubAgentSource("test"));

    public string? AgentRole { get; set; }

    public string? AgentNickname { get; set; }

    public CodexGitInfo? GitInfo { get; set; }

    public CodexThreadGoal? Goal { get; set; }

    public CodexThreadSnapshot? SnapshotOverride { get; set; }

    public CodexThreadSummary? SummaryOverride { get; set; }

    public List<CodexTurnRecord> Turns { get; } = [];

    public void ApplyThreadOptions(CodexThreadOptions options)
    {
        Path = options.WorkingDirectory;
        ModelProvider = options.ModelProvider ?? ModelProvider;
        Ephemeral = options.Ephemeral ?? Ephemeral;
    }

    public void CopyFrom(ScriptedCodexThreadState source)
    {
        Name = source.Name;
        Preview = source.Preview;
        Status = source.Status;
        ModelProvider = source.ModelProvider;
        UpdatedAt = source.UpdatedAt;
        Ephemeral = source.Ephemeral;
        Path = source.Path;
        Source = source.Source;
        AgentRole = source.AgentRole;
        AgentNickname = source.AgentNickname;
        GitInfo = source.GitInfo;
        Goal = source.Goal is null ? null : source.Goal with { };
        SnapshotOverride = source.SnapshotOverride is null ? null : source.SnapshotOverride with { };
        SummaryOverride = source.SummaryOverride is null ? null : source.SummaryOverride with { };
        Turns.Clear();
        Turns.AddRange(source.Turns.Select(turn => turn with { }));
    }

    public CodexThreadSummary ToSummary()
        => SummaryOverride ?? new CodexThreadSummary
        {
            Id = ThreadId,
            Name = Name ?? ThreadId,
            Preview = Preview,
            Status = Status,
            ModelProvider = ModelProvider,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Ephemeral = Ephemeral,
            Path = Path ?? string.Empty,
            Source = Source ?? new CodexSubAgentSessionSource(new CodexOtherSubAgentSource("test")),
            AgentRole = AgentRole,
            AgentNickname = AgentNickname,
            GitInfo = GitInfo,
        };

    public CodexThreadSnapshot ToSnapshot()
        => SnapshotOverride ?? new CodexThreadSnapshot
        {
            Id = ThreadId,
            Name = Name ?? ThreadId,
            Preview = Preview,
            Status = Status,
            ModelProvider = ModelProvider,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Ephemeral = Ephemeral,
            Path = Path ?? string.Empty,
            Source = Source ?? new CodexSubAgentSessionSource(new CodexOtherSubAgentSource("test")),
            AgentRole = AgentRole,
            AgentNickname = AgentNickname,
            GitInfo = GitInfo,
            Turns = Turns.ToArray(),
        };
}
