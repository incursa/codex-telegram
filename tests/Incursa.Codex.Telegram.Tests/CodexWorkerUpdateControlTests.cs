using System.Text.Json;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexWorkerUpdateControlTests
{
    [Fact]
    public async Task CoordinatorControlRequiresConfirmationAndExactConfiguredTarget()
    {
        string sha256 = new('a', 64);
        IOptions<CodexTelegramOptions> options = CreateOptions(sha256);
        FakeUpdateManager updateManager = new();
        FakeWorkerRegistry workerRegistry = new();

        DefaultHttpContext unconfirmed = CreateContext(
            "Bearer coordinator-token-that-is-long-enough-1234",
            new
            {
                WorkerId = "worker:test",
                RolloutId = "rollout:1",
                Action = "stage",
                Confirm = false,
                TargetVersion = "1.0.48",
                ExpectedSha256 = sha256,
                RequiredCapabilities = new[] { "codex-execution" },
            });
        IResult unconfirmedResult = await CodexWorkerUpdateEndpoints.ControlAsync(
            unconfirmed,
            options,
            updateManager,
            workerRegistry,
            CancellationToken.None);
        await unconfirmedResult.ExecuteAsync(unconfirmed);
        Assert.Equal(StatusCodes.Status400BadRequest, unconfirmed.Response.StatusCode);
        Assert.False(updateManager.StageCalled);

        DefaultHttpContext mismatch = CreateContext(
            "Bearer coordinator-token-that-is-long-enough-1234",
            new
            {
                WorkerId = "worker:test",
                RolloutId = "rollout:1",
                Action = "stage",
                Confirm = true,
                TargetVersion = "1.0.47",
                ExpectedSha256 = sha256,
                RequiredCapabilities = new[] { "codex-execution" },
            });
        IResult mismatchResult = await CodexWorkerUpdateEndpoints.ControlAsync(
            mismatch,
            options,
            updateManager,
            workerRegistry,
            CancellationToken.None);
        await mismatchResult.ExecuteAsync(mismatch);
        Assert.Equal(StatusCodes.Status409Conflict, mismatch.Response.StatusCode);
        Assert.False(updateManager.StageCalled);

        DefaultHttpContext accepted = CreateContext(
            "Bearer coordinator-token-that-is-long-enough-1234",
            new
            {
                WorkerId = "worker:test",
                RolloutId = "rollout:1",
                Action = "stage",
                Confirm = true,
                TargetVersion = "1.0.48",
                ExpectedSha256 = sha256,
                RequiredCapabilities = new[] { "codex-execution" },
            });
        IResult acceptedResult = await CodexWorkerUpdateEndpoints.ControlAsync(
            accepted,
            options,
            updateManager,
            workerRegistry,
            CancellationToken.None);
        await acceptedResult.ExecuteAsync(accepted);
        Assert.Equal(StatusCodes.Status200OK, accepted.Response.StatusCode);
        Assert.True(updateManager.StageCalled);
    }

    private static IOptions<CodexTelegramOptions> CreateOptions(string sha256)
        => Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Worker = new CodexWorkerOptions { WorkerId = "worker:test" },
            Coordinator = new CodexCoordinatorOptions
            {
                Enabled = true,
                WorkerRegistrationEnabled = true,
                AuthenticationToken = "coordinator-token-that-is-long-enough-1234",
            },
            Updates = new CodexWorkerUpdateOptions
            {
                Enabled = true,
                TargetVersion = "1.0.48",
                ExpectedSha256 = sha256,
                RequiredCapabilities = ["codex-execution"],
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

    private sealed class FakeUpdateManager : ICodexWorkerUpdateManager
    {
        public bool StageCalled { get; private set; }

        public Task<CodexWorkerUpdateSnapshot> GetStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(new CodexWorkerUpdateSnapshot(CodexWorkerUpdateState.None, null, null, null, null, null, null));

        public Task<CodexWorkerUpdateSnapshot> StageAsync(CancellationToken cancellationToken)
        {
            StageCalled = true;
            return Task.FromResult(new CodexWorkerUpdateSnapshot(CodexWorkerUpdateState.Staged, "1.0.48", new('a', 64), "candidate", "rollback", DateTimeOffset.UtcNow, "package_verified_and_staged"));
        }

        public Task<CodexWorkerUpdateSnapshot> StageRollbackAsync(CancellationToken cancellationToken)
            => Task.FromResult(new CodexWorkerUpdateSnapshot(CodexWorkerUpdateState.RollbackStaged, null, null, null, null, DateTimeOffset.UtcNow, "rollback_package_staged"));

        public Task<CodexWorkerUpdateSnapshot> CompleteAsync(CodexWorkerUpdateCompletion completion, CancellationToken cancellationToken)
            => Task.FromResult(new CodexWorkerUpdateSnapshot(CodexWorkerUpdateState.Active, completion.Version, completion.Sha256, null, null, DateTimeOffset.UtcNow, "health_verified"));
    }

    private sealed class FakeWorkerRegistry : ICodexWorkerRegistry
    {
        public Task<CodexWorkerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(new CodexWorkerSnapshot(
                "worker:test",
                "Test worker",
                CodexWorkerState.Draining,
                "draining",
                "1.0.47",
                0,
                4,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                ["codex-execution"],
                []));

        public Task<CodexWorkerLease?> TryAcquireLeaseAsync(string taskId, TimeSpan lifetime, CancellationToken cancellationToken) => Task.FromResult<CodexWorkerLease?>(null);
        public Task<bool> ReleaseLeaseAsync(string taskId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> SetDrainingAsync(bool draining, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
