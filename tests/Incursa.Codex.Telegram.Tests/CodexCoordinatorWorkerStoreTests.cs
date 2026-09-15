using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexCoordinatorWorkerStoreTests
{
    [Fact]
    public async Task UpsertPersistsRemoteWorkerAndReloadsIt()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z", CultureInfo.InvariantCulture));
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path, maxWorkers: 2);
        CodexWorkerSnapshot worker = CreateWorker("worker:a", "Worker A");

        using (CodexCoordinatorWorkerStore store = new(options, clock, dataRoot.Path))
        {
            Assert.True(await store.UpsertAsync(worker, CancellationToken.None));
        }

        using (CodexCoordinatorWorkerStore reloaded = new(options, clock, dataRoot.Path))
        {
            CodexWorkerSnapshot result = Assert.Single(await reloaded.ListAsync(CancellationToken.None));
            Assert.Equal("worker:a", result.WorkerId);
            Assert.True(result.IsRemote);
            Assert.Equal("ready", result.Readiness);
        }
    }

    [Fact]
    public async Task StaleHeartbeatIsProjectedUnavailableAndCapacityIsBounded()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z", CultureInfo.InvariantCulture));
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path, maxWorkers: 1);
        using CodexCoordinatorWorkerStore store = new(options, clock, dataRoot.Path);

        Assert.True(await store.UpsertAsync(CreateWorker("worker:a", "Worker A"), CancellationToken.None));
        Assert.False(await store.UpsertAsync(CreateWorker("worker:b", "Worker B"), CancellationToken.None));
        clock.UtcNow = clock.UtcNow.AddMinutes(1);

        CodexWorkerSnapshot result = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal("unavailable", result.Readiness);
        Assert.Contains("stale", string.Join("; ", result.Issues), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoordinatorHeartbeatRequiresExactBearerToken()
    {
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "correct-token").ToString();

        Assert.False(CodexCoordinatorWorkerEndpoints.TryAuthorize(context.Request, "wrong-token"));
        Assert.True(CodexCoordinatorWorkerEndpoints.TryAuthorize(context.Request, "correct-token"));
        context.Request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("correct-token"))).ToString();
        Assert.False(CodexCoordinatorWorkerEndpoints.TryAuthorize(context.Request, "correct-token"));
    }

    private static IOptions<CodexTelegramOptions> CreateOptions(string dataRoot, int maxWorkers)
        => Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot },
            Coordinator = new CodexCoordinatorOptions
            {
                MaximumRegisteredWorkers = maxWorkers,
                HeartbeatIntervalSeconds = 15,
            },
        });

    private static CodexWorkerSnapshot CreateWorker(string id, string name)
        => new(
            id,
            name,
            CodexWorkerState.Online,
            "ready",
            "1.0",
            0,
            2,
            DateTimeOffset.Parse("2026-09-15T11:59:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-09-15T12:00:00Z", CultureInfo.InvariantCulture),
            ["codex-execution"],
            []);

    private sealed class FixedTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
