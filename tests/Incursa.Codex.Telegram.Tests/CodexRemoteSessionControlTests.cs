using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Incursa.Codex.Telegram.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexRemoteSessionControlTests
{
    [Fact]
    public async Task WorkerControlEndpointRequiresCoordinatorAuthentication()
    {
        IOptions<CodexTelegramOptions> options = CreateOptions();
        DefaultHttpContext context = CreateContext("Bearer wrong-token", new { TaskId = "task:1" });

        IResult result = await CodexRemoteSessionControlEndpoints.SendAsync(
            context,
            options,
            workerRegistry: null!,
            supervisionLedger: null!,
            sessionManager: null!,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task WorkerControlEndpointExecutesSteerAndDeduplicatesCommand()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        FakeWorkerRegistry workerRegistry = new(CodexWorkerState.Online);
        ScriptedCodexSessionManager sessionManager = new();
        using CodexSupervisionLedger ledger = new(options, TimeProvider.System, dataRoot.Path);
        await ledger.RegisterTaskAsync(
            "task:1",
            "thread:1",
            "Remote task",
            new TelegramConversationScope(42, null),
            42,
            CancellationToken.None);
        await ledger.BindTaskWorkerAsync("task:1", 42, "worker:remote", "lease:1", "workspace:1", CancellationToken.None);

        CodexRemoteSessionControlRequest request = new(
            "task:1",
            42,
            "worker:remote",
            "thread:1",
            "command:steer-1",
            42,
            null,
            "steer",
            "focus on the failing test");

        DefaultHttpContext firstContext = CreateContext("Bearer coordinator-token-that-is-long-enough-1234", request);
        IResult firstResult = await CodexRemoteSessionControlEndpoints.SendAsync(
            firstContext,
            options,
            workerRegistry,
            ledger,
            sessionManager,
            CancellationToken.None);
        await firstResult.ExecuteAsync(firstContext);

        DefaultHttpContext duplicateContext = CreateContext("Bearer coordinator-token-that-is-long-enough-1234", request);
        IResult duplicateResult = await CodexRemoteSessionControlEndpoints.SendAsync(
            duplicateContext,
            options,
            workerRegistry,
            ledger,
            sessionManager,
            CancellationToken.None);
        await duplicateResult.ExecuteAsync(duplicateContext);

        Assert.Equal(StatusCodes.Status200OK, firstContext.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, duplicateContext.Response.StatusCode);
        Assert.Single(sessionManager.SteerRequests);
        CodexSupervisionTaskSnapshot task = Assert.Single(await ledger.ListTasksAsync(42, CancellationToken.None));
        Assert.Equal(CodexSupervisionRunState.Completed, task.LatestRun!.State);
    }

    [Fact]
    public async Task CoordinatorControlRelaySendsAuthenticatedStopToDrainingWorker()
    {
        CapturingHandler handler = new();
        using HttpClient client = new(handler);
        IOptions<CodexTelegramOptions> options = CreateOptions();
        CodexRemoteSessionControlRelay relay = new(new StaticHttpClientFactory(client), new FakeCoordinatorWorkerStore(CodexWorkerState.Draining), options);
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

        CodexRemoteSessionControlResponse result = await relay.SendAsync(
            task,
            42,
            42,
            null,
            "command:stop-1",
            "stop",
            null,
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal("Bearer coordinator-token-that-is-long-enough-1234", handler.Request!.Headers.Authorization!.ToString());
        CodexRemoteSessionControlRequest body = JsonSerializer.Deserialize<CodexRemoteSessionControlRequest>(handler.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("stop", body.Action);
        Assert.Null(body.Input);
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

    private sealed class FakeWorkerRegistry(CodexWorkerState state) : ICodexWorkerRegistry
    {
        public Task<CodexWorkerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(CreateWorker(state));

        public Task<CodexWorkerLease?> TryAcquireLeaseAsync(string taskId, TimeSpan lifetime, CancellationToken cancellationToken)
            => Task.FromResult<CodexWorkerLease?>(null);

        public Task<bool> ReleaseLeaseAsync(string taskId, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> SetDrainingAsync(bool draining, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    private sealed class FakeCoordinatorWorkerStore(CodexWorkerState state) : ICodexCoordinatorWorkerStore
    {
        public Task<bool> UpsertAsync(CodexWorkerSnapshot worker, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<IReadOnlyList<CodexWorkerSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CodexWorkerSnapshot>>([CreateWorker(state, isRemote: true)]);
    }

    private static CodexWorkerSnapshot CreateWorker(CodexWorkerState state, bool isRemote = false)
        => new(
            "worker:remote",
            "Remote worker",
            state,
            "ready",
            "1.0.42",
            1,
            4,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ["codex-execution"],
            [],
            IsRemote: isRemote,
            ControlPlaneUrl: "https://worker.example");

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
                Content = JsonContent.Create(new CodexRemoteSessionControlResponse(true, "stop requested")),
            };
        }
    }
}
