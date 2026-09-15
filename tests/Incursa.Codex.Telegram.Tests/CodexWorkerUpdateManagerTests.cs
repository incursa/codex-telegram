using System.Security.Cryptography;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexWorkerUpdateManagerTests
{
    [Fact]
    public async Task StageVerifiesPackageAndPreservesRollbackArtifact()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        string packagePath = Path.Combine(dataRoot.Path, "candidate.dll");
        File.Copy(typeof(CodexWorkerUpdateManager).Assembly.Location, packagePath);
        string stageRoot = Path.Combine(dataRoot.Path, "staged");
        string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))).ToLowerInvariant();
        IOptions<CodexTelegramOptions> options = Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot.Path },
            Worker = new CodexWorkerOptions { WorkerId = "worker:test", MaxConcurrentTasks = 1 },
            Updates = new CodexWorkerUpdateOptions
            {
                Enabled = true,
                PackagePath = packagePath,
                TargetVersion = typeof(CodexWorkerUpdateManager).Assembly.GetName().Version!.ToString(),
                ExpectedSha256 = sha256,
                StageRoot = stageRoot,
                RequiredCapabilities = ["codex-execution"],
            },
        });
        using CodexWorkerUpdateManager manager = new(options, new FakeWorkerRegistry(), TimeProvider.System, dataRoot.Path);

        CodexWorkerUpdateSnapshot staged = await manager.StageAsync(CancellationToken.None);

        Assert.Equal(CodexWorkerUpdateState.Staged, staged.State);
        Assert.Equal("package_verified_and_staged", staged.OutcomeCode);
        Assert.True(File.Exists(Path.Combine(stageRoot, staged.StagedPackageName!)));
        Assert.True(File.Exists(Path.Combine(stageRoot, staged.RollbackPackageName!)));
        Assert.DoesNotContain(packagePath, File.ReadAllText(Path.Combine(dataRoot.Path, "codex-worker-update-state.json")), StringComparison.OrdinalIgnoreCase);

        CodexWorkerUpdateSnapshot rollback = await manager.StageRollbackAsync(CancellationToken.None);
        Assert.Equal(CodexWorkerUpdateState.RollbackStaged, rollback.State);
        Assert.Equal("rollback_package_staged", rollback.OutcomeCode);
    }

    [Fact]
    public async Task StageFailsClosedUntilWorkerIsDrainedAndIdle()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        string packagePath = Path.Combine(dataRoot.Path, "candidate.dll");
        File.Copy(typeof(CodexWorkerUpdateManager).Assembly.Location, packagePath);
        string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))).ToLowerInvariant();
        IOptions<CodexTelegramOptions> options = Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot.Path },
            Updates = new CodexWorkerUpdateOptions
            {
                Enabled = true,
                PackagePath = packagePath,
                TargetVersion = typeof(CodexWorkerUpdateManager).Assembly.GetName().Version!.ToString(),
                ExpectedSha256 = sha256,
            },
        });
        FakeWorkerRegistry worker = new() { ActiveLeaseCount = 1 };
        using CodexWorkerUpdateManager manager = new(options, worker, TimeProvider.System, dataRoot.Path);

        CodexWorkerUpdateSnapshot result = await manager.StageAsync(CancellationToken.None);

        Assert.Equal(CodexWorkerUpdateState.Rejected, result.State);
        Assert.Equal("worker_must_be_drained", result.OutcomeCode);
        Assert.False(File.Exists(Path.Combine(dataRoot.Path, "worker-updates", result.StagedPackageName ?? string.Empty)));
    }

    private sealed class FakeWorkerRegistry : ICodexWorkerRegistry
    {
        public int ActiveLeaseCount { get; set; }

        public Task<CodexWorkerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(new CodexWorkerSnapshot(
                "worker:test",
                "Test worker",
                CodexWorkerState.Draining,
                "ready",
                "test",
                ActiveLeaseCount,
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
