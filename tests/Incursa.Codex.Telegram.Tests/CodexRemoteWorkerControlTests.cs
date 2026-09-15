using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexRemoteWorkerControlTests
{
    [Fact]
    public async Task WorkerControlEndpointRequiresAuthenticationAndExplicitConfirmation()
    {
        RecordingWorkerRegistry registry = new();
        IOptions<CodexTelegramOptions> options = CreateOptions();

        DefaultHttpContext unauthorized = CreateContext("Bearer wrong-token", new CodexRemoteWorkerControlRequest("worker:local", "drain", true));
        IResult unauthorizedResult = await CodexRemoteWorkerControlEndpoints.HandleAsync(unauthorized, options, registry, CancellationToken.None);
        await unauthorizedResult.ExecuteAsync(unauthorized);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.Response.StatusCode);

        DefaultHttpContext unconfirmed = CreateContext(
            "Bearer coordinator-token-that-is-long-enough-1234",
            new CodexRemoteWorkerControlRequest("worker:local", "drain", false));
        IResult unconfirmedResult = await CodexRemoteWorkerControlEndpoints.HandleAsync(unconfirmed, options, registry, CancellationToken.None);
        await unconfirmedResult.ExecuteAsync(unconfirmed);
        Assert.Equal(StatusCodes.Status400BadRequest, unconfirmed.Response.StatusCode);
        Assert.False(registry.Draining);
    }

    [Fact]
    public async Task WorkerControlEndpointDrainsAndResumesWithoutTouchingExistingLeases()
    {
        RecordingWorkerRegistry registry = new();
        registry.ActiveLeaseCount = 2;
        IOptions<CodexTelegramOptions> options = CreateOptions();

        DefaultHttpContext drain = CreateContext(
            "Bearer coordinator-token-that-is-long-enough-1234",
            new CodexRemoteWorkerControlRequest("worker:local", "drain", true));
        IResult drainResult = await CodexRemoteWorkerControlEndpoints.HandleAsync(drain, options, registry, CancellationToken.None);
        await drainResult.ExecuteAsync(drain);
        Assert.Equal(StatusCodes.Status200OK, drain.Response.StatusCode);
        Assert.True(registry.Draining);
        Assert.Equal(2, registry.ActiveLeaseCount);
        Assert.Contains("worker_drained", await ReadResponseAsync(drain), StringComparison.Ordinal);

        DefaultHttpContext resume = CreateContext(
            "Bearer coordinator-token-that-is-long-enough-1234",
            new CodexRemoteWorkerControlRequest("worker:local", "resume", true));
        IResult resumeResult = await CodexRemoteWorkerControlEndpoints.HandleAsync(resume, options, registry, CancellationToken.None);
        await resumeResult.ExecuteAsync(resume);
        Assert.Equal(StatusCodes.Status200OK, resume.Response.StatusCode);
        Assert.False(registry.Draining);
        Assert.Equal(2, registry.ActiveLeaseCount);
    }

    [Fact]
    public async Task CoordinatorWorkerControlRelayUsesAdmittedWorkerAndExactAction()
    {
        CapturingHandler handler = new(new CodexRemoteWorkerControlResponse(
            true,
            CreateWorker(CodexWorkerState.Draining),
            "worker_drained"));
        using HttpClient client = new(handler);
        CodexRemoteWorkerControlRelay relay = new(
            new StaticHttpClientFactory(client),
            new FakeCoordinatorWorkerStore(),
            CreateOptions());

        CodexWorkerSnapshot worker = CreateWorker(CodexWorkerState.Online) with { IsRemote = true };
        CodexWorkerSnapshot result = await relay.SetDrainingAsync(worker, true, CancellationToken.None);

        Assert.True(result.IsRemote);
        Assert.Equal(CodexWorkerState.Draining, result.State);
        Assert.NotNull(handler.Request);
        Assert.Equal("Bearer coordinator-token-that-is-long-enough-1234", handler.Request!.Headers.Authorization!.ToString());
        Assert.EndsWith("/api/worker/v1/worker/control", handler.Request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        CodexRemoteWorkerControlRequest request = JsonSerializer.Deserialize<CodexRemoteWorkerControlRequest>(
            handler.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("worker:remote", request.WorkerId);
        Assert.Equal("drain", request.Action);
        Assert.True(request.Confirm);
    }

    private static IOptions<CodexTelegramOptions> CreateOptions()
        => Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Coordinator = new CodexCoordinatorOptions
            {
                Enabled = true,
                WorkerRegistrationEnabled = true,
                AuthenticationToken = "coordinator-token-that-is-long-enough-1234",
                RequestTimeoutSeconds = 10,
            },
        });

    private static CodexWorkerSnapshot CreateWorker(CodexWorkerState state)
        => new(
            "worker:remote",
            "Remote worker",
            state,
            state == CodexWorkerState.Draining ? "draining" : "ready",
            "1.0.47",
            2,
            4,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ["codex-execution"],
            [],
            IsRemote: false,
            ControlPlaneUrl: "https://worker.example.test/control");

    private static DefaultHttpContext CreateContext(string authorization, object payload)
    {
        DefaultHttpContext context = new();
        context.RequestServices = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();
        context.Request.Headers.Authorization = authorization;
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using StreamReader reader = new(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class RecordingWorkerRegistry : ICodexWorkerRegistry
    {
        public bool Draining { get; private set; }
        public int ActiveLeaseCount { get; set; }

        public Task<CodexWorkerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(new CodexWorkerSnapshot(
                "worker:local",
                "Local worker",
                Draining ? CodexWorkerState.Draining : CodexWorkerState.Online,
                Draining ? "draining" : "ready",
                "1.0.47",
                ActiveLeaseCount,
                4,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                ["codex-execution"],
                []));

        public Task<CodexWorkerLease?> TryAcquireLeaseAsync(string taskId, TimeSpan lifetime, CancellationToken cancellationToken)
            => Task.FromResult<CodexWorkerLease?>(null);

        public Task<bool> ReleaseLeaseAsync(string taskId, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> SetDrainingAsync(bool draining, CancellationToken cancellationToken)
        {
            bool changed = Draining != draining;
            Draining = draining;
            return Task.FromResult(changed);
        }
    }

    private sealed class FakeCoordinatorWorkerStore : ICodexCoordinatorWorkerStore
    {
        public Task<bool> UpsertAsync(CodexWorkerSnapshot worker, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<IReadOnlyList<CodexWorkerSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CodexWorkerSnapshot>>([CreateWorker(CodexWorkerState.Online) with { IsRemote = true }]);
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler(CodexRemoteWorkerControlResponse response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(response),
            };
        }
    }
}
