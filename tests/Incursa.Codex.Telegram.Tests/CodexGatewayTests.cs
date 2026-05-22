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

    private static CodexGateway CreateGateway(IOptions<CodexTelegramOptions> options, CodexSessionRuntimeRegistry registry)
        => new(
            options,
            new CodexThreadManifestStore(options, TimeProvider.System),
            new CodexWorkspaceBrowser(options),
            registry);

    private static CodexSessionRuntimeRegistry CreateRegistry(ICodexRuntimeClientFactory runtimeClientFactory)
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
            new NoopApplicationLifetime(),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            runtimeClientFactory);

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

    private static CodexThreadSummary CreateSummary(string threadId, string name, string preview, string path)
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
            CreatedAt = new DateTimeOffset(2026, 5, 13, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 5, 13, 0, 0, 0, TimeSpan.Zero),
            Ephemeral = false,
            Path = path,
            Source = new CodexSubAgentSessionSource(new CodexOtherSubAgentSource("test")),
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
