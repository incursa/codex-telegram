using System.Globalization;
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

public sealed class CodexCoordinatorLeaseHandoffTests
{
    [Fact]
    public async Task HandoffSelectsEligibleRemoteWorkerAndIsIdempotentByTask()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z", CultureInfo.InvariantCulture));
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        using CodexCoordinatorWorkerStore workerStore = new(options, clock, dataRoot.Path);
        using CodexCoordinatorLeaseStore leaseStore = new(options, dataRoot.Path);
        await workerStore.UpsertAsync(CreateWorker("worker:wrong", "http://127.0.0.1:5288", ["other-capability"]), CancellationToken.None);
        await workerStore.UpsertAsync(CreateWorker("worker:right", "http://127.0.0.1:5289", ["codex-execution"]), CancellationToken.None);

        int requestCount = 0;
        using StaticClientFactory clients = new(() =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CodexWorkerLeaseAcceptance(true, "worker:right", "lease:remote", "lease_accepted")),
            };
        });
        CodexCoordinatorLeaseHandoffService service = new(clients, workerStore, leaseStore, options, clock);
        CodexCoordinatorLeaseRequest request = new("task:123", "workspace:123", ["codex-execution"]);

        CodexCoordinatorLeaseSnapshot first = await service.HandoffAsync(request, CancellationToken.None);
        CodexCoordinatorLeaseSnapshot second = await service.HandoffAsync(request, CancellationToken.None);

        Assert.Equal(CodexCoordinatorLeaseState.Accepted, first.State);
        Assert.Equal("worker:right", first.WorkerId);
        Assert.Equal("lease:remote", first.LeaseId);
        Assert.Equal(first.GrantId, second.GrantId);
        Assert.Equal(1, requestCount);
        Assert.Contains("lease_accepted", File.ReadAllText(Path.Combine(dataRoot.Path, "codex-coordinator-leases.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandoffFailsClosedWhenNoRemoteWorkerIsEligible()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z", CultureInfo.InvariantCulture));
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        using CodexCoordinatorWorkerStore workerStore = new(options, clock, dataRoot.Path);
        using CodexCoordinatorLeaseStore leaseStore = new(options, dataRoot.Path);
        await workerStore.UpsertAsync(CreateWorker("worker:missing-endpoint", null, ["codex-execution"]), CancellationToken.None);
        using StaticClientFactory clients = new(() => throw new InvalidOperationException("No request should be sent."));

        CodexCoordinatorLeaseHandoffService service = new(clients, workerStore, leaseStore, options, clock);
        CodexCoordinatorLeaseSnapshot result = await service.HandoffAsync(
            new CodexCoordinatorLeaseRequest("task:123", null, ["codex-execution"]),
            CancellationToken.None);

        Assert.Equal(CodexCoordinatorLeaseState.Rejected, result.State);
        Assert.Equal("no_eligible_worker", result.OutcomeCode);
        Assert.Equal("grant:none", result.GrantId);
    }

    [Fact]
    public async Task WorkerAcceptEndpointBindsGrantToLocalReadyWorker()
    {
        IOptions<CodexTelegramOptions> options = Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Coordinator = new CodexCoordinatorOptions
            {
                WorkerRegistrationEnabled = true,
                AuthenticationToken = "coordinator-token-that-is-long-enough-1234",
            },
        });
        FakeWorkerRegistry workerRegistry = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CodexWorkerLeaseGrant grant = new(
            "grant:123",
            "task:123",
            "workspace:123",
            "worker:local",
            ["codex-execution"],
            now,
            now.AddMinutes(5));
        DefaultHttpContext context = new();
        context.RequestServices = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();
        context.Request.Headers.Authorization = "Bearer coordinator-token-that-is-long-enough-1234";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(grant));
        context.Response.Body = new MemoryStream();

        IResult result = await CodexCoordinatorWorkerEndpoints.AcceptLeaseAsync(context, options, workerRegistry, CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("task:123", workerRegistry.AcquiredTaskId);
        context.Response.Body.Position = 0;
        CodexWorkerLeaseAcceptance? response = await JsonSerializer.DeserializeAsync<CodexWorkerLeaseAcceptance>(context.Response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(response);
        Assert.True(response.Accepted);
        Assert.Equal("lease:local", response.LeaseId);
    }

    private static IOptions<CodexTelegramOptions> CreateOptions(string dataRoot)
        => Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot },
            Coordinator = new CodexCoordinatorOptions
            {
                Enabled = true,
                AuthenticationToken = "coordinator-token-that-is-long-enough-1234",
                MaximumRegisteredWorkers = 4,
                RequestTimeoutSeconds = 5,
                HeartbeatIntervalSeconds = 15,
            },
        });

    private static CodexWorkerSnapshot CreateWorker(string workerId, string? controlPlaneUrl, IReadOnlyList<string> capabilities)
        => new(
            workerId,
            workerId,
            CodexWorkerState.Online,
            "ready",
            "1.0",
            0,
            2,
            DateTimeOffset.Parse("2026-09-15T11:59:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-15T12:00:00Z", CultureInfo.InvariantCulture),
            capabilities,
            [],
            true,
            controlPlaneUrl);

    private sealed class FixedTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => initialUtcNow;
    }

    private sealed class StaticClientFactory(Func<HttpResponseMessage> responseFactory) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new DelegateHandler(responseFactory));

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }

    private sealed class DelegateHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responseFactory());
    }

    private sealed class FakeWorkerRegistry : ICodexWorkerRegistry
    {
        public string? AcquiredTaskId { get; private set; }

        public Task<CodexWorkerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(new CodexWorkerSnapshot(
                "worker:local",
                "Local worker",
                CodexWorkerState.Online,
                "ready",
                "1.0",
                0,
                4,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                ["codex-execution"],
                []));

        public Task<CodexWorkerLease?> TryAcquireLeaseAsync(string taskId, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            AcquiredTaskId = taskId;
            return Task.FromResult<CodexWorkerLease?>(new CodexWorkerLease(
                "lease:local",
                taskId,
                "worker:local",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.Add(lifetime)));
        }

        public Task<bool> ReleaseLeaseAsync(string taskId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> SetDrainingAsync(bool draining, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
