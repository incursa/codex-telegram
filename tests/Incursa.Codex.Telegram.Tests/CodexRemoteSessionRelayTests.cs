using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Incursa.Codex.Telegram.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexRemoteSessionRelayTests
{
    [Fact]
    public async Task CoordinatorRelaySendsExplicitCallbackAndCommandIdentityToAssignedWorker()
    {
        CapturingHandler handler = new();
        using HttpClient client = new(handler);
        StaticHttpClientFactory clientFactory = new(client);
        IOptions<CodexTelegramOptions> options = CreateOptions();
        CodexRemoteSessionRelay relay = new(clientFactory, new FakeCoordinatorWorkerStore(), options);
        CodexSupervisionTaskSnapshot task = new(
            "task:1",
            "thread:1",
            "Remote task",
            new TelegramConversationScope(42, null),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            WorkerId: "worker:remote",
            LeaseId: "lease:1",
            WorkspaceId: "workspace:1");

        CodexThreadExecutionVm execution = await relay.SendAsync(
            task,
            42,
            42,
            null,
            "command:1",
            "inspect the repository",
            planMode: true,
            CancellationToken.None);

        Assert.Equal("thread:1", execution.ThreadId);
        Assert.NotNull(handler.Request);
        Assert.Equal("Bearer coordinator-token-that-is-long-enough-1234", handler.Request!.Headers.Authorization!.ToString());
        CodexRemoteSessionSendRequest body = JsonSerializer.Deserialize<CodexRemoteSessionSendRequest>(handler.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("command:1", body.CommandId);
        Assert.True(body.PlanMode);
        Assert.Equal("https://coordinator.example/api/coordinator/v1/worker-events", body.CallbackUrl);
    }

    [Fact]
    public async Task WorkerSessionEndpointRequiresCoordinatorAuthentication()
    {
        IOptions<CodexTelegramOptions> options = CreateOptions();
        DefaultHttpContext context = CreateContext("Bearer wrong-token", new { TaskId = "task:1" });

        IResult result = await CodexRemoteSessionEndpoints.SendAsync(
            context,
            options,
            workerRegistry: null!,
            supervisionLedger: null!,
            sessionManager: null!,
            eventForwarder: null!,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task WorkerSessionEndpointExecutesAndRejectsDuplicateCommandWithoutSecondSend()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        FakeWorkerRegistry workerRegistry = new("worker:remote");
        ScriptedCodexSessionManager sessionManager = new();
        RecordingEventForwarder eventForwarder = new();
        using CodexSupervisionLedger ledger = new(options, TimeProvider.System, dataRoot.Path);
        await ledger.RegisterTaskAsync(
            "task:1",
            "thread:1",
            "Remote task",
            new TelegramConversationScope(42, null),
            42,
            CancellationToken.None);
        await ledger.BindTaskWorkerAsync("task:1", 42, "worker:remote", "lease:1", "workspace:1", CancellationToken.None);

        CodexRemoteSessionSendRequest request = new(
            "task:1",
            42,
            "worker:remote",
            "thread:1",
            "command:1",
            42,
            null,
            "inspect the repository",
            false,
            "https://coordinator.example/api/coordinator/v1/worker-events");

        DefaultHttpContext firstContext = CreateContext("Bearer coordinator-token-that-is-long-enough-1234", request);
        IResult firstResult = await CodexRemoteSessionEndpoints.SendAsync(
            firstContext,
            options,
            workerRegistry,
            ledger,
            sessionManager,
            eventForwarder,
            CancellationToken.None);
        await firstResult.ExecuteAsync(firstContext);

        DefaultHttpContext duplicateContext = CreateContext("Bearer coordinator-token-that-is-long-enough-1234", request);
        IResult duplicateResult = await CodexRemoteSessionEndpoints.SendAsync(
            duplicateContext,
            options,
            workerRegistry,
            ledger,
            sessionManager,
            eventForwarder,
            CancellationToken.None);
        await duplicateResult.ExecuteAsync(duplicateContext);

        Assert.Equal(StatusCodes.Status200OK, firstContext.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, duplicateContext.Response.StatusCode);
        Assert.Single(sessionManager.TextSends);
        Assert.Equal("thread:1", Assert.Single(eventForwarder.Registered).ThreadId);
    }

    private static IOptions<CodexTelegramOptions> CreateOptions(string? dataRoot = null)
        => Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot ?? Path.GetTempPath() },
            Coordinator = new CodexCoordinatorOptions
            {
                Enabled = true,
                WorkerRegistrationEnabled = true,
                AuthenticationToken = "coordinator-token-that-is-long-enough-1234",
                Url = "https://coordinator.example",
            },
        });

    private static DefaultHttpContext CreateContext(string authorization, object payload)
    {
        DefaultHttpContext context = new();
        context.RequestServices = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();
        context.Request.Headers.Authorization = authorization;
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FakeWorkerRegistry(string workerId) : ICodexWorkerRegistry
    {
        public Task<CodexWorkerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(new CodexWorkerSnapshot(
                workerId,
                "Remote worker",
                CodexWorkerState.Online,
                "ready",
                "1.0.41",
                1,
                4,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                ["codex-execution", "task-workspaces"],
                [],
                IsRemote: true,
                ControlPlaneUrl: "https://worker.example"));

        public Task<CodexWorkerLease?> TryAcquireLeaseAsync(string taskId, TimeSpan lifetime, CancellationToken cancellationToken)
            => Task.FromResult<CodexWorkerLease?>(null);

        public Task<bool> ReleaseLeaseAsync(string taskId, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> SetDrainingAsync(bool draining, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    private sealed class RecordingEventForwarder : ICodexRemoteTurnEventForwarder
    {
        public List<(string ThreadId, string WorkerId, string CallbackUrl)> Registered { get; } = [];

        public void RegisterPending(string threadId, string workerId, string callbackUrl)
            => Registered.Add((threadId, workerId, callbackUrl));

        public Task ForwardAsync(string threadId, CodexTimelineEntryVm entry, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeCoordinatorWorkerStore : ICodexCoordinatorWorkerStore
    {
        public Task<bool> UpsertAsync(CodexWorkerSnapshot worker, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<IReadOnlyList<CodexWorkerSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CodexWorkerSnapshot>>([
                new CodexWorkerSnapshot(
                    "worker:remote",
                    "Remote worker",
                    CodexWorkerState.Online,
                    "ready",
                    "1.0.41",
                    0,
                    4,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    ["codex-execution"],
                    [],
                    IsRemote: true,
                    ControlPlaneUrl: "https://worker.example"),
            ]);
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CodexThreadExecutionVm("thread:1", "turn:1", "running", null)),
            };
        }
    }
}

public sealed class CodexCoordinatorTurnEventEndpointTests
{
    [Fact]
    public async Task EventEndpointRequiresCoordinatorAuthentication()
    {
        IOptions<CodexTelegramOptions> options = Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Coordinator = new CodexCoordinatorOptions
            {
                Enabled = true,
                AuthenticationToken = "coordinator-token-that-is-long-enough-1234",
            },
        });
        DefaultHttpContext context = CreateContext("Bearer wrong-token", new { WorkerId = "worker:remote", ThreadId = "thread:1" });

        IResult result = await CodexCoordinatorTurnEventEndpoints.ReceiveAsync(
            context,
            options,
            workerStore: null!,
            turnOutputRelay: null!,
            supervisionLedger: null!,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task EventEndpointPublishesBoundedEventAndCompletesMatchingRun()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        IOptions<CodexTelegramOptions> options = Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot.Path },
            Coordinator = new CodexCoordinatorOptions
            {
                Enabled = true,
                AuthenticationToken = "coordinator-token-that-is-long-enough-1234",
            },
        });
        FakeCoordinatorWorkerStore workerStore = new();
        RecordingTurnOutputRelay relay = new();
        using CodexSupervisionLedger ledger = new(options, TimeProvider.System, dataRoot.Path);
        CodexSupervisionCommandStart command = await ledger.StartCommandAsync(
            "command:1",
            "thread:1",
            "Remote task",
            new TelegramConversationScope(42, null),
            42,
            "remote_prompt",
            CancellationToken.None);
        await ledger.UpdateRunAsync(command.Run!.RunId, CodexSupervisionRunState.Running, "thread:1", "turn:1", null, CancellationToken.None);

        CodexTimelineEntryVm entry = new(
            "turn.completed",
            "Turn completed",
            null,
            "done",
            "info",
            DateTimeOffset.UtcNow,
            "thread:1",
            "turn:1",
            new Dictionary<string, string?>(),
            false);
        DefaultHttpContext context = CreateContext(
            "Bearer coordinator-token-that-is-long-enough-1234",
            new CodexRemoteTurnEvent("worker:remote", "thread:1", entry));

        IResult result = await CodexCoordinatorTurnEventEndpoints.ReceiveAsync(
            context,
            options,
            workerStore,
            relay,
            ledger,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        CodexTimelineEntryVm received = Assert.Single(relay.Entries);
        Assert.Equal(entry.Type, received.Type);
        Assert.Equal(entry.ThreadId, received.ThreadId);
        Assert.Equal(entry.TurnId, received.TurnId);
        Assert.Equal(entry.Body, received.Body);
        CodexSupervisionTaskSnapshot task = Assert.Single(await ledger.ListTasksAsync(42, CancellationToken.None));
        Assert.Equal(CodexSupervisionRunState.Completed, task.LatestRun!.State);
    }

    private static DefaultHttpContext CreateContext(string authorization, object payload)
    {
        DefaultHttpContext context = new();
        context.RequestServices = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();
        context.Request.Headers.Authorization = authorization;
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FakeCoordinatorWorkerStore : ICodexCoordinatorWorkerStore
    {
        public Task<bool> UpsertAsync(CodexWorkerSnapshot worker, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<IReadOnlyList<CodexWorkerSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CodexWorkerSnapshot>>([
                new CodexWorkerSnapshot(
                    "worker:remote",
                    "Remote worker",
                    CodexWorkerState.Online,
                    "ready",
                    "1.0.41",
                    1,
                    4,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    ["codex-execution"],
                    [],
                    IsRemote: true,
                    ControlPlaneUrl: "https://worker.example"),
            ]);
    }

    private sealed class RecordingTurnOutputRelay : ITelegramTurnOutputRelay
    {
        public List<CodexTimelineEntryVm> Entries { get; } = [];

        public Task PublishTurnEventAsync(CodexTimelineEntryVm entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
