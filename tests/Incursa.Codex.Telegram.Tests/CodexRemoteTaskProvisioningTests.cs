using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexRemoteTaskProvisioningTests
{
    [Fact]
    public async Task ProvisioningUsesLeaseThenPersistsBoundedCoordinatorProjection()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        IOptions<CodexTelegramOptions> options = Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot.Path },
            Coordinator = new CodexCoordinatorOptions
            {
                Enabled = true,
                WorkerRegistrationEnabled = true,
                AuthenticationToken = "coordinator-token-that-is-long-enough-1234",
                RequestTimeoutSeconds = 5,
            },
        });
        using CodexCoordinatorWorkerStore workerStore = new(options, clock, dataRoot.Path);
        using CodexCoordinatorLeaseStore leaseStore = new(options, dataRoot.Path);
        using CodexCoordinatorTaskStore taskStore = new(options, dataRoot.Path);
        using CodexSupervisionLedger ledger = new(options, clock, dataRoot.Path);
        using StaticClientFactory clients = new();
        CodexCoordinatorLeaseHandoffService handoff = new(clients, workerStore, leaseStore, options, clock);
        CodexRemoteTaskProvisioningService provisioner = new(clients, workerStore, handoff, taskStore, ledger, options, clock);
        await workerStore.UpsertAsync(
            new CodexWorkerSnapshot(
                "worker:linux",
                "Linux worker",
                CodexWorkerState.Online,
                "ready",
                "1.0.40",
                0,
                2,
                clock.GetUtcNow().AddMinutes(-1),
                clock.GetUtcNow(),
                ["codex-execution", "task-workspaces"],
                [],
                true,
                "http://127.0.0.1:5287"),
            CancellationToken.None);

        CodexRemoteTaskProvisionResponse result = await provisioner.ProvisionAsync(
            new CodexRemoteTaskProvisionRequest(
                "task:remote-1",
                42,
                42,
                null,
                "Remote task",
                "C:\\repo",
                "main",
                null,
                "worker:linux"),
            CancellationToken.None);

        Assert.True(result.Provisioned);
        Assert.Equal("worker:linux", result.WorkerId);
        Assert.Equal("lease:remote", result.LeaseId);
        Assert.Equal(2, clients.RequestCount);
        CodexCoordinatorTaskSnapshot? stored = await taskStore.GetAsync(42, "task:remote-1", CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(result.WorkspaceId, stored.WorkspaceId);
        Assert.DoesNotContain("C:\\repo", JsonSerializer.Serialize(stored), StringComparison.Ordinal);
        CodexSupervisionTaskSnapshot task = Assert.Single(await ledger.ListTasksAsync(42, CancellationToken.None));
        Assert.Equal("worker:linux", task.WorkerId);
        Assert.Equal("lease:remote", task.LeaseId);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StaticClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;

        public StaticClientFactory()
        {
            _client = new HttpClient(new DelegateHandler(this));
        }

        public int RequestCount { get; private set; }

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();

        private sealed class DelegateHandler(StaticClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.RequestCount++;
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("coordinator-token-that-is-long-enough-1234", request.Headers.Authorization?.Parameter);
                if (request.RequestUri?.AbsolutePath.EndsWith("worker-leases/accept", StringComparison.Ordinal) == true)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new CodexWorkerLeaseAcceptance(true, "worker:linux", "lease:remote", "lease_accepted")),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new CodexRemoteTaskProvisionResponse(
                        true,
                        "task:remote-1",
                        "worker:linux",
                        "lease:remote",
                        "workspace:remote-1",
                        "thread:remote-1",
                        "Remote task",
                        "codex/task/remote-1",
                        45100,
                        "codex_task_remote_1",
                        "task_provisioned")),
                });
            }
        }
    }
}
