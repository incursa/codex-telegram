using System.Text.Json.Nodes;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Incursa.Codex.Telegram.Tests.TestSupport;
using Incursa.OpenAI.Codex;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexGatewayTests
{
    [Fact]
    public async Task ListThreadsAsync_UsesInjectedRuntimeClientFactory()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        using TemporaryDirectory workspaceRoot = TemporaryDirectory.Create();

        ScriptedCodexRuntimeClient runtimeClient = new();
        runtimeClient.QueueListThreadsResult(new CodexThreadListResult
        {
            Threads = [
                CreateSummary(
                    "thread-1",
                    "Alpha",
                    "Preview from the scripted client",
                    workspaceRoot.Path),
            ],
            NextCursor = "cursor-1",
        });

        RecordingRuntimeClientFactory runtimeClientFactory = new(runtimeClient);
        IOptions<CodexTelegramOptions> telegramOptions = CreateTelegramOptions(dataRoot.Path, workspaceRoot.Path);

        await using CodexSessionRuntimeRegistry registry = CreateRegistry(runtimeClientFactory);
        CodexGateway gateway = CreateGateway(telegramOptions, registry);

        Assert.Equal(1, runtimeClientFactory.CreateCalls);

        IReadOnlyList<CodexThreadListItemVm> threads = await gateway.ListThreadsAsync(
            new CodexThreadListQuery(null, "all", "updatedAt", false, null, 25),
            CancellationToken.None);

        Assert.Single(threads);
        CodexThreadListItemVm thread = threads[0];
        Assert.Equal("thread-1", thread.Id);
        Assert.Equal("Alpha", thread.Name);
        Assert.Equal("Preview from the scripted client", thread.Preview);
        Assert.Equal("idle", thread.Status);
    }

    [Fact]
    public async Task GetThreadGoalAsync_UsesInjectedRuntimeClientFactory()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        using TemporaryDirectory workspaceRoot = TemporaryDirectory.Create();

        ScriptedCodexRuntimeClient runtimeClient = new();
        ICodexThreadHandle thread = runtimeClient.CreateThreadHandle("thread-1");
        await thread.SetGoalAsync("Keep the gateway seam injectable", 42, CancellationToken.None);

        RecordingRuntimeClientFactory runtimeClientFactory = new(runtimeClient);
        IOptions<CodexTelegramOptions> telegramOptions = CreateTelegramOptions(dataRoot.Path, workspaceRoot.Path);

        await using CodexSessionRuntimeRegistry registry = CreateRegistry(runtimeClientFactory);
        CodexGateway gateway = CreateGateway(telegramOptions, registry);

        Assert.Equal(1, runtimeClientFactory.CreateCalls);

        CodexThreadGoalVm? goal = await gateway.GetThreadGoalAsync("thread-1", CancellationToken.None);

        Assert.NotNull(goal);
        Assert.Equal("thread-1", goal!.ThreadId);
        Assert.Equal("Keep the gateway seam injectable", goal.Objective);
        Assert.Equal(CodexThreadGoalStatus.Active, goal.Status);
        Assert.Equal(42, goal.TokenBudget);
    }

    [Fact]
    public async Task CreateThreadAsync_UsesInjectedRuntimeClientFactoryForDedicatedSlot()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        using TemporaryDirectory workspaceRoot = TemporaryDirectory.Create();

        ScriptedCodexRuntimeClient runtimeClient = new();
        runtimeClient.QueueStartThreadId("thread-1");
        runtimeClient.QueueTurn("thread-1", "turn-1").Complete("Hello from the scripted runtime.");

        RecordingRuntimeClientFactory runtimeClientFactory = new(runtimeClient);
        IOptions<CodexTelegramOptions> telegramOptions = CreateTelegramOptions(dataRoot.Path, workspaceRoot.Path);

        await using CodexSessionRuntimeRegistry registry = CreateRegistry(runtimeClientFactory);
        CodexGateway gateway = CreateGateway(telegramOptions, registry);

        Assert.Equal(1, runtimeClientFactory.CreateCalls);

        CodexThreadExecutionVm execution = await gateway.CreateThreadAsync(
            new CodexTurnSubmission
            {
                Prompt = "Say hello",
                ThreadName = "Alpha",
                WorkingDirectory = workspaceRoot.Path,
            },
            CancellationToken.None);

        Assert.Equal(2, runtimeClientFactory.CreateCalls);
        Assert.Equal("thread-1", execution.ThreadId);
        Assert.Equal("turn-1", execution.TurnId);
        Assert.Equal("running", execution.Status);
    }

    [Fact]
    public async Task RuntimeRegistryStopAsync_PersistsInterruptedTurnMarker()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        using TemporaryDirectory workspaceRoot = TemporaryDirectory.Create();

        ScriptedCodexRuntimeClient runtimeClient = new();
        ScriptedCodexTurnScript script = runtimeClient.QueueTurn("thread-1", "turn-1");
        script.AddDelta("still working").HoldCompletion().Complete("done");

        RecordingRuntimeClientFactory runtimeClientFactory = new(runtimeClient);
        IOptions<CodexTelegramOptions> telegramOptions = CreateTelegramOptions(dataRoot.Path, workspaceRoot.Path);
        CodexThreadManifestStore manifestStore = new(telegramOptions, TimeProvider.System);
        await manifestStore.GetOrCreateAsync("thread-1", CancellationToken.None);

        await using CodexSessionRuntimeRegistry registry = CreateRegistry(runtimeClientFactory, manifestStore: manifestStore);
        CodexRuntimeSlot slot = await registry.GetOrCreateForThreadAsync("thread-1", CancellationToken.None);
        ICodexThreadHandle thread = await slot.Client.ResumeThreadAsync("thread-1", null, CancellationToken.None);
        await slot.TurnCoordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await registry.StopAsync(CancellationToken.None);
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));

        CodexThreadManifestRecord? manifest = await manifestStore.ReadAsync("thread-1", CancellationToken.None);
        Assert.NotNull(manifest?.InterruptedTurn);
        Assert.Equal("turn-1", manifest!.InterruptedTurn!.TurnId);
        Assert.Equal("turn-1", manifest.LastTurnId);
        Assert.Equal("application_shutdown", manifest.InterruptedTurn.Reason);
        Assert.Contains("shut down", manifest.InterruptedTurn.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, script.InterruptCount);
    }

    [Fact]
    public async Task RuntimeRegistryReattachPersistedTurnsAsync_AttachesAndConsumesLastTurn()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        using TemporaryDirectory workspaceRoot = TemporaryDirectory.Create();

        ScriptedCodexRuntimeClient runtimeClient = new();
        ScriptedCodexTurnScript script = runtimeClient.QueueTurn("thread-1", "turn-1");
        script.AddDelta("reattached output").Complete("reattached output");

        RecordingRuntimeClientFactory runtimeClientFactory = new(runtimeClient);
        IOptions<CodexTelegramOptions> telegramOptions = CreateTelegramOptions(dataRoot.Path, workspaceRoot.Path);
        CodexThreadManifestStore manifestStore = new(telegramOptions, TimeProvider.System);
        await manifestStore.UpdateAsync("thread-1", manifest =>
        {
            manifest.LastTurnId = "turn-1";
            manifest.InterruptedTurn = CreateInterruptedTurnRecord("turn-1");
            return manifest;
        }, CancellationToken.None);

        await using CodexSessionRuntimeRegistry registry = CreateRegistry(runtimeClientFactory, manifestStore: manifestStore);

        IReadOnlyCollection<CodexThreadExecutionVm> executions = await registry.ReattachPersistedTurnsAsync(["thread-1"], CancellationToken.None);

        CodexThreadExecutionVm execution = Assert.Single(executions);
        Assert.Equal("thread-1", execution.ThreadId);
        Assert.Equal("turn-1", execution.TurnId);
        Assert.Equal("running", execution.Status);
        await script.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));

        CodexThreadManifestRecord? manifest = await manifestStore.ReadAsync("thread-1", CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.Equal("turn-1", manifest!.LastTurnId);
        Assert.Null(manifest.InterruptedTurn);
        Assert.True(script.NormalizedObservableSubscriptionCount > 0);
    }

    [Fact]
    public async Task RuntimeRegistryReattachPersistedTurnsAsync_LeavesMarkerWhenAttachFails()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        using TemporaryDirectory workspaceRoot = TemporaryDirectory.Create();

        ScriptedCodexRuntimeClient runtimeClient = new();
        RecordingRuntimeClientFactory runtimeClientFactory = new(runtimeClient);
        IOptions<CodexTelegramOptions> telegramOptions = CreateTelegramOptions(dataRoot.Path, workspaceRoot.Path);
        CodexThreadManifestStore manifestStore = new(telegramOptions, TimeProvider.System);
        await manifestStore.UpdateAsync("thread-1", manifest =>
        {
            manifest.LastTurnId = "turn-1";
            manifest.InterruptedTurn = CreateInterruptedTurnRecord("turn-1");
            return manifest;
        }, CancellationToken.None);

        await using CodexSessionRuntimeRegistry registry = CreateRegistry(runtimeClientFactory, manifestStore: manifestStore);

        IReadOnlyCollection<CodexThreadExecutionVm> executions = await registry.ReattachPersistedTurnsAsync(["thread-1"], CancellationToken.None);

        Assert.Empty(executions);
        Assert.Null(registry.TryGetActiveTurnState("thread-1"));
        CodexThreadManifestRecord? manifest = await manifestStore.ReadAsync("thread-1", CancellationToken.None);
        Assert.NotNull(manifest?.InterruptedTurn);
        Assert.Equal("turn-1", manifest!.InterruptedTurn!.TurnId);
    }

    [Fact]
    public async Task SendTurnAsync_ClearsPersistedInterruptedTurnMarker()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        using TemporaryDirectory workspaceRoot = TemporaryDirectory.Create();

        ScriptedCodexRuntimeClient runtimeClient = new();
        runtimeClient.QueueTurn("thread-1", "turn-2").Complete("continued");
        RecordingRuntimeClientFactory runtimeClientFactory = new(runtimeClient);
        IOptions<CodexTelegramOptions> telegramOptions = CreateTelegramOptions(dataRoot.Path, workspaceRoot.Path);
        CodexThreadManifestStore manifestStore = new(telegramOptions, TimeProvider.System);
        await manifestStore.UpdateAsync("thread-1", manifest =>
        {
            manifest.InterruptedTurn = CreateInterruptedTurnRecord("turn-1");
            return manifest;
        }, CancellationToken.None);

        await using CodexSessionRuntimeRegistry registry = CreateRegistry(runtimeClientFactory, manifestStore: manifestStore);
        CodexGateway gateway = CreateGateway(telegramOptions, registry);

        CodexThreadExecutionVm execution = await gateway.SendTurnAsync(
            "thread-1",
            [new CodexTextInput { Text = "continue" }],
            CancellationToken.None);

        CodexThreadManifestRecord? manifest = await manifestStore.ReadAsync("thread-1", CancellationToken.None);
        Assert.Equal("turn-2", execution.TurnId);
        Assert.NotNull(manifest);
        Assert.Null(manifest!.InterruptedTurn);
        Assert.Equal("turn-2", manifest.LastTurnId);
    }

    [Fact]
    public async Task ListSessionsAsync_UsesCloseoutProjectionForLastActivityAndOrdering()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        using TemporaryDirectory workspaceRoot = TemporaryDirectory.Create();

        DateTimeOffset olderActivity = new(2026, 5, 13, 1, 0, 0, TimeSpan.Zero);
        DateTimeOffset normalActivity = new(2026, 5, 13, 2, 0, 0, TimeSpan.Zero);
        DateTimeOffset closeoutActivity = new(2026, 5, 13, 3, 0, 0, TimeSpan.Zero);
        CodexTurnCloseoutSummary closeout = new(
            "turn-1",
            "completed",
            closeoutActivity,
            AssistantTextSeen: true,
            FinalResponseSeen: false,
            Warning: true,
            "Codex streamed assistant text but ended the turn without a final response item.");

        ScriptedCodexRuntimeClient runtimeClient = new();
        runtimeClient.QueueListThreadsResult(new CodexThreadListResult
        {
            Threads =
            [
                CreateSummary("thread-normal", "Normal", "Normal preview", workspaceRoot.Path, updatedAt: normalActivity),
                CreateSummary("thread-closeout", "Closeout", "Closeout preview", workspaceRoot.Path, updatedAt: olderActivity),
            ],
        });

        CodexSessionEventLog eventLog = new();
        eventLog.Record(new CodexSessionEventRecord(
            "thread-closeout",
            "turn-1",
            closeoutActivity,
            "turn.completed",
            CodexSessionEventKind.TerminalSuccess,
            CodexSessionEventLane.State,
            "Turn completed",
            closeout.Message,
            closeout));

        RecordingRuntimeClientFactory runtimeClientFactory = new(runtimeClient);
        IOptions<CodexTelegramOptions> telegramOptions = CreateTelegramOptions(dataRoot.Path, workspaceRoot.Path);

        await using CodexSessionRuntimeRegistry registry = CreateRegistry(runtimeClientFactory, eventLog);
        CodexGateway gateway = CreateGateway(telegramOptions, registry);
        CodexGatewaySessionManager manager = CreateSessionManager(gateway, registry, telegramOptions, eventLog);

        CodexSessionSummary[] sessions = (await manager.ListSessionsAsync(CancellationToken.None)).ToArray();

        Assert.Collection(
            sessions,
            session =>
            {
                Assert.Equal("thread-closeout", session.Id);
                Assert.Equal(closeoutActivity, session.LastActivityUtc);
                Assert.Same(closeout, session.LastTurnCloseout);
            },
            session =>
            {
                Assert.Equal("thread-normal", session.Id);
                Assert.Equal(normalActivity, session.LastActivityUtc);
                Assert.Null(session.LastTurnCloseout);
            });
    }

    [Fact]
    public async Task ListSessionsAsync_UsesPersistedInterruptedTurnMarkerAsCloseout()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        using TemporaryDirectory workspaceRoot = TemporaryDirectory.Create();

        DateTimeOffset threadActivity = new(2026, 5, 13, 1, 0, 0, TimeSpan.Zero);
        DateTimeOffset interruptedAt = new(2026, 5, 13, 2, 0, 0, TimeSpan.Zero);

        ScriptedCodexRuntimeClient runtimeClient = new();
        runtimeClient.QueueListThreadsResult(new CodexThreadListResult
        {
            Threads =
            [
                CreateSummary("thread-1", "Interrupted", "Interrupted preview", workspaceRoot.Path, updatedAt: threadActivity),
            ],
        });

        RecordingRuntimeClientFactory runtimeClientFactory = new(runtimeClient);
        IOptions<CodexTelegramOptions> telegramOptions = CreateTelegramOptions(dataRoot.Path, workspaceRoot.Path);
        CodexThreadManifestStore manifestStore = new(telegramOptions, TimeProvider.System);
        await manifestStore.UpdateAsync("thread-1", manifest =>
        {
            manifest.InterruptedTurn = CreateInterruptedTurnRecord("turn-1", interruptedAt);
            return manifest;
        }, CancellationToken.None);

        await using CodexSessionRuntimeRegistry registry = CreateRegistry(runtimeClientFactory, manifestStore: manifestStore);
        CodexGateway gateway = CreateGateway(telegramOptions, registry);
        CodexGatewaySessionManager manager = CreateSessionManager(gateway, registry, telegramOptions, NullCodexSessionEventLog.Instance);

        CodexSessionSummary session = Assert.Single(await manager.ListSessionsAsync(CancellationToken.None));

        Assert.Equal(interruptedAt, session.LastActivityUtc);
        Assert.NotNull(session.LastTurnCloseout);
        Assert.Equal("turn-1", session.LastTurnCloseout!.TurnId);
        Assert.Equal("interrupted", session.LastTurnCloseout.Status);
        Assert.True(session.LastTurnCloseout.Warning);
        Assert.Contains("shut down", session.LastTurnCloseout.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TailAsync_AppendsRecentSessionEvents()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        using TemporaryDirectory workspaceRoot = TemporaryDirectory.Create();

        ScriptedCodexRuntimeClient runtimeClient = new();
        runtimeClient.QueueListThreadsResult(new CodexThreadListResult
        {
            Threads =
            [
                CreateSummary("thread-1", "Tail", "Tail preview", workspaceRoot.Path),
            ],
        });
        runtimeClient.QueueReadThreadSnapshot("thread-1", CreateSnapshot("thread-1", "Tail", workspaceRoot.Path));

        CodexSessionEventLog eventLog = new();
        eventLog.Record(new CodexSessionEventRecord(
            "thread-1",
            "turn-1",
            new DateTimeOffset(2026, 5, 13, 4, 0, 0, TimeSpan.Zero),
            "turn.closeout.warning",
            CodexSessionEventKind.CloseoutWarning,
            CodexSessionEventLane.Timeline,
            "Turn completed without a final response",
            "Codex streamed assistant text but ended the turn without a final response item."));

        RecordingRuntimeClientFactory runtimeClientFactory = new(runtimeClient);
        IOptions<CodexTelegramOptions> telegramOptions = CreateTelegramOptions(dataRoot.Path, workspaceRoot.Path);

        await using CodexSessionRuntimeRegistry registry = CreateRegistry(runtimeClientFactory, eventLog);
        CodexGateway gateway = CreateGateway(telegramOptions, registry);
        CodexGatewaySessionManager manager = CreateSessionManager(gateway, registry, telegramOptions, eventLog);

        string tail = await manager.TailAsync("thread-1", 20, CancellationToken.None);

        Assert.Contains("recent events:", tail);
        Assert.Contains("[Timeline/CloseoutWarning]", tail);
        Assert.Contains("Turn completed without a final response", tail);
    }

    private static CodexGateway CreateGateway(IOptions<CodexTelegramOptions> options, CodexSessionRuntimeRegistry registry)
        => new(
            options,
            new CodexThreadManifestStore(options, TimeProvider.System),
            new CodexWorkspaceBrowser(options),
            registry);

    private static CodexGatewaySessionManager CreateSessionManager(
        CodexGateway gateway,
        CodexSessionRuntimeRegistry registry,
        IOptions<CodexTelegramOptions> telegramOptions,
        ICodexSessionEventLog eventLog)
        => new(
            gateway,
            registry,
            new CodexThreadManifestStore(telegramOptions, TimeProvider.System),
            new TelegramBotStateStore(telegramOptions),
            eventLog,
            Microsoft.Extensions.Options.Options.Create(new TelegramBotOptions()),
            telegramOptions);

    private static CodexSessionRuntimeRegistry CreateRegistry(
        ICodexRuntimeClientFactory runtimeClientFactory,
        ICodexSessionEventLog? eventLog = null,
        ICodexThreadManifestStore? manifestStore = null)
        => new(
            Microsoft.Extensions.Options.Options.Create(new CodexClientOptions
            {
                BackendSelection = CodexBackendSelection.AppServer,
                ClientName = "Incursa.Codex.Telegram.Tests",
                ClientTitle = "Incursa Codex Telegram Tests",
            }),
            Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions()),
            new NoopPlanInputCoordinator(),
            new NoopRealtimeBroadcaster(),
            new NoopTurnOutputRelay(),
            eventLog ?? NullCodexSessionEventLog.Instance,
            new NoopApplicationLifetime(),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            runtimeClientFactory,
            manifestStore);

    private static CodexInterruptedTurnRecord CreateInterruptedTurnRecord(
        string turnId,
        DateTimeOffset? recordedAt = null)
    {
        DateTimeOffset timestamp = recordedAt ?? new DateTimeOffset(2026, 5, 13, 0, 0, 0, TimeSpan.Zero);
        return new CodexInterruptedTurnRecord
        {
            TurnId = turnId,
            StartedAt = timestamp.AddMinutes(-5),
            UpdatedAt = timestamp.AddMinutes(-1),
            RecordedAt = timestamp,
            Reason = "application_shutdown",
            Message = "The app shut down while this turn was active. On restart the app will try to reattach if Codex is still running.",
        };
    }

    private static IOptions<CodexTelegramOptions> CreateTelegramOptions(string dataRoot, string workspaceRoot)
        => Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Context = new CodexContextOptions
            {
                WorkingDirectory = workspaceRoot,
            },
            Workspace = new CodexWorkspaceOptions
            {
                DataRoot = dataRoot,
                WorkspaceRoots = [workspaceRoot],
            },
        });

    private static CodexThreadSummary CreateSummary(
        string threadId,
        string name,
        string preview,
        string path,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
        => new()
        {
            Id = threadId,
            Name = name,
            Preview = preview,
            Status = new CodexIdleThreadStatus
            {
                Type = "idle",
            },
            ModelProvider = "test",
            CreatedAt = createdAt ?? new DateTimeOffset(2026, 5, 13, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = updatedAt ?? new DateTimeOffset(2026, 5, 13, 0, 0, 0, TimeSpan.Zero),
            Ephemeral = false,
            Path = path,
            Source = new CodexSubAgentSessionSource(new CodexOtherSubAgentSource("test")),
        };

    private static CodexThreadSnapshot CreateSnapshot(string threadId, string name, string path)
        => new()
        {
            Id = threadId,
            Name = name,
            Preview = string.Empty,
            Status = new CodexIdleThreadStatus
            {
                Type = "idle",
            },
            ModelProvider = "test",
            CreatedAt = new DateTimeOffset(2026, 5, 13, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 5, 13, 0, 0, 0, TimeSpan.Zero),
            Ephemeral = false,
            Path = path,
            Source = new CodexSubAgentSessionSource(new CodexOtherSubAgentSource("test")),
            Turns = [],
        };

    private sealed class RecordingRuntimeClientFactory : ICodexRuntimeClientFactory
    {
        private readonly ICodexRuntimeClient _runtimeClient;

        public RecordingRuntimeClientFactory(ICodexRuntimeClient runtimeClient)
        {
            _runtimeClient = runtimeClient;
        }

        public int CreateCalls { get; private set; }

        public ICodexRuntimeClient Create(CodexClientOptions options)
        {
            _ = options;
            CreateCalls++;
            return _runtimeClient;
        }
    }

    private sealed class NoopPlanInputCoordinator : ITelegramPlanInputCoordinator
    {
        public JsonObject? HandleApprovalRequest(string action, JsonObject? request)
        {
            _ = action;
            _ = request;
            return null;
        }

        public Task<bool> TryAnswerPendingAsync(TelegramConversationScope conversation, string text, CancellationToken cancellationToken)
        {
            _ = conversation;
            _ = text;
            _ = cancellationToken;
            return Task.FromResult(false);
        }

        public Task<bool> TryAnswerCallbackAsync(string token, TelegramConversationScope conversation, string callbackQueryId, CancellationToken cancellationToken)
        {
            _ = token;
            _ = conversation;
            _ = callbackQueryId;
            _ = cancellationToken;
            return Task.FromResult(false);
        }
    }

    private sealed class NoopRealtimeBroadcaster : ICodexRealtimeBroadcaster
    {
        public Task BroadcastRuntimeStateAsync(CodexRuntimeStateVm runtime, CancellationToken cancellationToken)
        {
            _ = runtime;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task BroadcastThreadEventAsync(string threadId, CodexTimelineEntryVm entry, CancellationToken cancellationToken)
        {
            _ = threadId;
            _ = entry;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class NoopTurnOutputRelay : ITelegramTurnOutputRelay
    {
        public Task PublishTurnEventAsync(CodexTimelineEntryVm entry, CancellationToken cancellationToken)
        {
            _ = entry;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class NoopApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
