using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexFleetRolloutTests
{
    [Fact]
    public async Task RolloutStorePersistsOwnerScopedWorkerEvidence()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        using CodexFleetRolloutStore store = new(CreateOptions(dataRoot.Path), clock, dataRoot.Path);

        CodexFleetRolloutSnapshot created = await store.CreateAsync(
            42,
            "1.0.48",
            new string('a', 64),
            ["codex-execution"],
            ["worker:one", "worker:two"],
            CancellationToken.None);
        CodexFleetRolloutSnapshot? updated = await store.UpdateWorkerAsync(
            42,
            created.RolloutId,
            "worker:one",
            CodexFleetRolloutWorkerState.Active,
            "health_verified",
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(CodexFleetRolloutState.Staging, updated.State);
        Assert.Equal(CodexFleetRolloutWorkerState.Active, updated.Workers[0].State);
        Assert.Null(await store.GetAsync(99, created.RolloutId, CancellationToken.None));
        Assert.StartsWith("rollout:", created.RolloutId, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dataRoot.Path, "codex-fleet-rollouts.json")));

        using CodexFleetRolloutStore reloaded = new(CreateOptions(dataRoot.Path), clock, dataRoot.Path);
        CodexFleetRolloutSnapshot persisted = Assert.Single(await reloaded.ListAsync(42, CancellationToken.None));
        Assert.Equal(updated.RolloutId, persisted.RolloutId);
        Assert.Equal(updated.State, persisted.State);
        Assert.Equal(updated.TargetVersion, persisted.TargetVersion);
        Assert.Equal(updated.ExpectedSha256, persisted.ExpectedSha256);
        Assert.Equal(updated.Workers.Count, persisted.Workers.Count);
        Assert.Equal(updated.Workers[0].State, persisted.Workers[0].State);
        Assert.Equal(updated.Workers[0].OutcomeCode, persisted.Workers[0].OutcomeCode);
    }

    [Fact]
    public async Task CoordinatorStagesOneWorkerOnlyAfterDrainAndFinalizesAfterHealth()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        FakeWorkerRegistry worker = new();
        FakeUpdateManager updates = new();
        using CodexFleetRolloutStore store = new(CreateOptions(dataRoot.Path), TimeProvider.System, dataRoot.Path);
        CodexFleetRolloutCoordinator coordinator = new(
            store,
            worker,
            new EmptyCoordinatorWorkerStore(),
            updates,
            remoteUpdateRelay: null!);

        CodexFleetRolloutActionResult planned = await coordinator.StartAsync(
            42,
            "1.0.48",
            new string('a', 64),
            ["codex-execution"],
            ["worker:test"],
            CancellationToken.None);
        Assert.True(planned.Accepted);
        Assert.Equal("rollout_planned", planned.OutcomeCode);

        CodexFleetRolloutActionResult blocked = await coordinator.AdvanceAsync(42, planned.Rollout!.RolloutId, CancellationToken.None);
        Assert.Equal("worker_must_be_drained", blocked.OutcomeCode);
        Assert.Equal(0, updates.StageCount);

        worker.State = CodexWorkerState.Draining;
        CodexFleetRolloutActionResult staged = await coordinator.AdvanceAsync(42, planned.Rollout.RolloutId, CancellationToken.None);
        Assert.Equal("worker_stage_requested", staged.OutcomeCode);
        Assert.Equal(1, updates.StageCount);

        CodexFleetRolloutActionResult waiting = await coordinator.AdvanceAsync(42, planned.Rollout.RolloutId, CancellationToken.None);
        Assert.Equal("awaiting_installer", waiting.OutcomeCode);
        Assert.Equal(1, updates.StageCount);
        CodexFleetRolloutActionResult notYetFinal = await coordinator.FinalizeAsync(42, planned.Rollout.RolloutId, CancellationToken.None);
        Assert.Equal("awaiting_installer", notYetFinal.OutcomeCode);

        updates.State = CodexWorkerUpdateState.Active;
        CodexFleetRolloutActionResult ready = await coordinator.AdvanceAsync(42, planned.Rollout.RolloutId, CancellationToken.None);
        Assert.Equal("rollout_ready_to_finalize", ready.OutcomeCode);

        CodexFleetRolloutActionResult completed = await coordinator.FinalizeAsync(42, planned.Rollout.RolloutId, CancellationToken.None);
        Assert.Equal("rollout_completed", completed.OutcomeCode);
        Assert.Equal(CodexFleetRolloutState.Completed, completed.Rollout!.State);

        updates.State = CodexWorkerUpdateState.HealthFailed;
        CodexFleetRolloutActionResult rollbackStage = await coordinator.RollbackAsync(42, planned.Rollout.RolloutId, CancellationToken.None);
        Assert.Equal("rollback_stage_requested", rollbackStage.OutcomeCode);
        CodexFleetRolloutActionResult rollbackWaiting = await coordinator.RollbackAsync(42, planned.Rollout.RolloutId, CancellationToken.None);
        Assert.Equal("rollback_awaiting_installer", rollbackWaiting.OutcomeCode);

        updates.State = CodexWorkerUpdateState.RollbackActive;
        CodexFleetRolloutActionResult rolledBack = await coordinator.FinalizeAsync(42, planned.Rollout.RolloutId, CancellationToken.None);
        Assert.Equal("rollout_rolled_back", rolledBack.OutcomeCode);
    }

    private static IOptions<CodexTelegramOptions> CreateOptions(string dataRoot)
        => Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot },
        });

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class EmptyCoordinatorWorkerStore : ICodexCoordinatorWorkerStore
    {
        public Task<bool> UpsertAsync(CodexWorkerSnapshot worker, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<IReadOnlyList<CodexWorkerSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CodexWorkerSnapshot>>([]);
    }

    private sealed class FakeWorkerRegistry : ICodexWorkerRegistry
    {
        public CodexWorkerState State { get; set; } = CodexWorkerState.Online;

        public Task<CodexWorkerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(new CodexWorkerSnapshot(
                "worker:test",
                "Test worker",
                State,
                State == CodexWorkerState.Draining ? "draining" : "ready",
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

    private sealed class FakeUpdateManager : ICodexWorkerUpdateManager
    {
        public CodexWorkerUpdateState State { get; set; } = CodexWorkerUpdateState.None;
        public int StageCount { get; private set; }

        public Task<CodexWorkerUpdateSnapshot> GetStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(Snapshot());

        public Task<CodexWorkerUpdateSnapshot> StageAsync(CancellationToken cancellationToken)
        {
            StageCount++;
            State = CodexWorkerUpdateState.Staged;
            return Task.FromResult(Snapshot());
        }

        public Task<CodexWorkerUpdateSnapshot> StageRollbackAsync(CancellationToken cancellationToken)
        {
            State = CodexWorkerUpdateState.RollbackStaged;
            return Task.FromResult(Snapshot());
        }

        public Task<CodexWorkerUpdateSnapshot> CompleteAsync(CodexWorkerUpdateCompletion completion, CancellationToken cancellationToken)
            => Task.FromResult(Snapshot());

        private CodexWorkerUpdateSnapshot Snapshot()
            => new(State, "1.0.48", new string('a', 64), "candidate.staged", "rollback.bin", DateTimeOffset.UtcNow, State.ToString());
    }
}
