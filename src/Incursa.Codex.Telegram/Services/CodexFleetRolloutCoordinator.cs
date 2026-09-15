namespace Incursa.Codex.Telegram.Services;

internal interface ICodexFleetRolloutCoordinator
{
    Task<CodexFleetRolloutActionResult> StartAsync(
        long ownerUserId,
        string targetVersion,
        string expectedSha256,
        IReadOnlyList<string> requiredCapabilities,
        IReadOnlyList<string> workerIds,
        CancellationToken cancellationToken);

    Task<CodexFleetRolloutActionResult> AdvanceAsync(long ownerUserId, string rolloutId, CancellationToken cancellationToken);

    Task<CodexFleetRolloutActionResult> RollbackAsync(long ownerUserId, string rolloutId, CancellationToken cancellationToken);

    Task<CodexFleetRolloutActionResult> FinalizeAsync(long ownerUserId, string rolloutId, CancellationToken cancellationToken);
}

internal sealed record CodexFleetRolloutActionResult(
    bool Accepted,
    string OutcomeCode,
    CodexFleetRolloutSnapshot? Rollout);

/// <summary>
/// Advances one worker at a time through a drain-aware rollout. The coordinator
/// records intent and evidence, while the external installer owns activation.
/// </summary>
internal sealed class CodexFleetRolloutCoordinator : ICodexFleetRolloutCoordinator
{
    private readonly ICodexFleetRolloutStore _rolloutStore;
    private readonly ICodexWorkerRegistry _workerRegistry;
    private readonly ICodexCoordinatorWorkerStore _coordinatorWorkerStore;
    private readonly ICodexWorkerUpdateManager _localUpdateManager;
    private readonly CodexRemoteWorkerUpdateRelay _remoteUpdateRelay;

    public CodexFleetRolloutCoordinator(
        ICodexFleetRolloutStore rolloutStore,
        ICodexWorkerRegistry workerRegistry,
        ICodexCoordinatorWorkerStore coordinatorWorkerStore,
        ICodexWorkerUpdateManager localUpdateManager,
        CodexRemoteWorkerUpdateRelay remoteUpdateRelay)
    {
        _rolloutStore = rolloutStore;
        _workerRegistry = workerRegistry;
        _coordinatorWorkerStore = coordinatorWorkerStore;
        _localUpdateManager = localUpdateManager;
        _remoteUpdateRelay = remoteUpdateRelay;
    }

    public async Task<CodexFleetRolloutActionResult> StartAsync(
        long ownerUserId,
        string targetVersion,
        string expectedSha256,
        IReadOnlyList<string> requiredCapabilities,
        IReadOnlyList<string> workerIds,
        CancellationToken cancellationToken)
    {
        if (ownerUserId == 0
            || !IsSafeVersion(targetVersion)
            || !IsSha256(expectedSha256)
            || requiredCapabilities.Count > 24
            || requiredCapabilities.Any(capability => !IsSafeToken(capability, 120))
            || workerIds.Count is < 1 or > 32
            || workerIds.Any(workerId => !IsSafeToken(workerId, 120))
            || workerIds.Distinct(StringComparer.Ordinal).Count() != workerIds.Count)
        {
            return Failure("rollout_metadata_invalid");
        }

        foreach (string workerId in workerIds)
        {
            CodexWorkerSnapshot? worker = await FindWorkerAsync(workerId, cancellationToken).ConfigureAwait(false);
            string? compatibilityFailure = CheckCompatibility(worker, requiredCapabilities);
            if (compatibilityFailure is not null)
            {
                return Failure(compatibilityFailure);
            }
        }

        CodexFleetRolloutSnapshot rollout = await _rolloutStore.CreateAsync(
            ownerUserId,
            targetVersion,
            expectedSha256,
            requiredCapabilities,
            workerIds,
            cancellationToken).ConfigureAwait(false);
        return Success("rollout_planned", rollout);
    }

    public async Task<CodexFleetRolloutActionResult> AdvanceAsync(long ownerUserId, string rolloutId, CancellationToken cancellationToken)
    {
        CodexFleetRolloutSnapshot? rollout = await LoadAsync(ownerUserId, rolloutId, cancellationToken).ConfigureAwait(false);
        if (rollout is null)
        {
            return Failure("rollout_not_found");
        }

        rollout = await RefreshAsync(rollout, cancellationToken).ConfigureAwait(false);
        if (rollout.Workers.Any(worker => worker.State == CodexFleetRolloutWorkerState.HealthFailed))
        {
            return Success("rollback_required", rollout);
        }

        foreach (CodexFleetRolloutWorkerSnapshot workerState in rollout.Workers.OrderBy(worker => worker.Sequence))
        {
            if (workerState.State == CodexFleetRolloutWorkerState.Active)
            {
                continue;
            }

            if (workerState.State == CodexFleetRolloutWorkerState.Staged)
            {
                return Success("awaiting_installer", rollout);
            }

            if (workerState.State != CodexFleetRolloutWorkerState.Pending
                && workerState.State != CodexFleetRolloutWorkerState.Blocked)
            {
                return Success("rollout_waiting", rollout);
            }

            if (rollout.Workers.Any(previous => previous.Sequence < workerState.Sequence
                && previous.State != CodexFleetRolloutWorkerState.Active))
            {
                return Success("awaiting_previous_worker", rollout);
            }

            CodexWorkerSnapshot? worker = await FindWorkerAsync(workerState.WorkerId, cancellationToken).ConfigureAwait(false);
            string? compatibilityFailure = CheckCompatibility(worker, rollout.RequiredCapabilities);
            if (compatibilityFailure is not null)
            {
                return await BlockAsync(rollout, workerState.WorkerId, compatibilityFailure, cancellationToken).ConfigureAwait(false);
            }

            if (worker!.State != CodexWorkerState.Draining || worker.ActiveLeaseCount != 0)
            {
                return await BlockAsync(rollout, worker.WorkerId, "worker_must_be_drained", cancellationToken).ConfigureAwait(false);
            }

            try
            {
                CodexWorkerUpdateSnapshot update;
                if (worker.IsRemote)
                {
                    update = (await _remoteUpdateRelay.ExecuteAsync(
                        worker,
                        rollout.RolloutId,
                        "stage",
                        rollout.TargetVersion,
                        rollout.ExpectedSha256,
                        rollout.RequiredCapabilities,
                        cancellationToken).ConfigureAwait(false)).Update;
                }
                else
                {
                    update = await _localUpdateManager.StageAsync(cancellationToken).ConfigureAwait(false);
                }
                if (update.State != CodexWorkerUpdateState.Staged)
                {
                    return await BlockAsync(rollout, worker.WorkerId, update.OutcomeCode ?? "stage_rejected", cancellationToken).ConfigureAwait(false);
                }

                CodexFleetRolloutSnapshot? updated = await _rolloutStore.UpdateWorkerAsync(
                    ownerUserId,
                    rollout.RolloutId,
                    worker.WorkerId,
                    CodexFleetRolloutWorkerState.Staged,
                    "worker_stage_requested",
                    cancellationToken).ConfigureAwait(false);
                return Success("worker_stage_requested", updated);
            }
            catch (InvalidOperationException exception)
            {
                return await BlockAsync(rollout, worker.WorkerId, ToOutcomeCode(exception), cancellationToken).ConfigureAwait(false);
            }
        }

        return Success("rollout_ready_to_finalize", rollout);
    }

    public async Task<CodexFleetRolloutActionResult> RollbackAsync(long ownerUserId, string rolloutId, CancellationToken cancellationToken)
    {
        CodexFleetRolloutSnapshot? rollout = await LoadAsync(ownerUserId, rolloutId, cancellationToken).ConfigureAwait(false);
        if (rollout is null)
        {
            return Failure("rollout_not_found");
        }

        rollout = await RefreshAsync(rollout, cancellationToken).ConfigureAwait(false);
        foreach (CodexFleetRolloutWorkerSnapshot workerState in rollout.Workers.OrderByDescending(worker => worker.Sequence))
        {
            if (workerState.State is CodexFleetRolloutWorkerState.Pending or CodexFleetRolloutWorkerState.Blocked or CodexFleetRolloutWorkerState.RollbackActive)
            {
                continue;
            }

            if (workerState.State == CodexFleetRolloutWorkerState.RollbackStaged)
            {
                return Success("rollback_awaiting_installer", rollout);
            }

            CodexWorkerSnapshot? worker = await FindWorkerAsync(workerState.WorkerId, cancellationToken).ConfigureAwait(false);
            string? compatibilityFailure = CheckCompatibility(worker, rollout.RequiredCapabilities);
            if (compatibilityFailure is not null)
            {
                return await BlockAsync(rollout, workerState.WorkerId, compatibilityFailure, cancellationToken).ConfigureAwait(false);
            }

            if (worker!.State != CodexWorkerState.Draining || worker.ActiveLeaseCount != 0)
            {
                return await BlockAsync(rollout, worker.WorkerId, "worker_must_be_drained", cancellationToken).ConfigureAwait(false);
            }

            try
            {
                CodexWorkerUpdateSnapshot update;
                if (worker.IsRemote)
                {
                    update = (await _remoteUpdateRelay.ExecuteAsync(worker, rollout.RolloutId, "rollback", null, null, [], cancellationToken).ConfigureAwait(false)).Update;
                }
                else
                {
                    update = await _localUpdateManager.StageRollbackAsync(cancellationToken).ConfigureAwait(false);
                }
                if (update.State != CodexWorkerUpdateState.RollbackStaged)
                {
                    return await BlockAsync(rollout, worker.WorkerId, update.OutcomeCode ?? "rollback_rejected", cancellationToken).ConfigureAwait(false);
                }

                CodexFleetRolloutSnapshot? updated = await _rolloutStore.UpdateWorkerAsync(
                    ownerUserId,
                    rollout.RolloutId,
                    worker.WorkerId,
                    CodexFleetRolloutWorkerState.RollbackStaged,
                    "rollback_stage_requested",
                    cancellationToken).ConfigureAwait(false);
                return Success("rollback_stage_requested", updated);
            }
            catch (InvalidOperationException exception)
            {
                return await BlockAsync(rollout, worker.WorkerId, ToOutcomeCode(exception), cancellationToken).ConfigureAwait(false);
            }
        }

        return Success("rollback_ready_to_finalize", rollout);
    }

    public async Task<CodexFleetRolloutActionResult> FinalizeAsync(long ownerUserId, string rolloutId, CancellationToken cancellationToken)
    {
        CodexFleetRolloutSnapshot? rollout = await LoadAsync(ownerUserId, rolloutId, cancellationToken).ConfigureAwait(false);
        if (rollout is null)
        {
            return Failure("rollout_not_found");
        }

        rollout = await RefreshAsync(rollout, cancellationToken).ConfigureAwait(false);
        if (rollout.Workers.All(worker => worker.State == CodexFleetRolloutWorkerState.Active))
        {
            return Success("rollout_completed", rollout);
        }

        if (rollout.Workers.Any(worker => worker.State == CodexFleetRolloutWorkerState.HealthFailed))
        {
            return Success("rollback_required", rollout);
        }

        if (rollout.Workers.Any(worker => worker.State is CodexFleetRolloutWorkerState.Staged or CodexFleetRolloutWorkerState.RollbackStaged))
        {
            return Success("awaiting_installer", rollout);
        }

        if (rollout.Workers.All(worker => worker.State is CodexFleetRolloutWorkerState.RollbackActive or CodexFleetRolloutWorkerState.Pending))
        {
            return Success("rollout_rolled_back", rollout);
        }

        return Success("rollout_incomplete", rollout);
    }

    private async Task<CodexFleetRolloutSnapshot?> LoadAsync(long ownerUserId, string rolloutId, CancellationToken cancellationToken)
        => await _rolloutStore.GetAsync(ownerUserId, rolloutId, cancellationToken).ConfigureAwait(false);

    private async Task<CodexFleetRolloutSnapshot> RefreshAsync(CodexFleetRolloutSnapshot rollout, CancellationToken cancellationToken)
    {
        foreach (CodexFleetRolloutWorkerSnapshot workerState in rollout.Workers.Where(worker => worker.State is not (CodexFleetRolloutWorkerState.Pending or CodexFleetRolloutWorkerState.Blocked)))
        {
            CodexWorkerSnapshot? worker = await FindWorkerAsync(workerState.WorkerId, cancellationToken).ConfigureAwait(false);
            if (worker is null || string.Equals(worker.Readiness, "unavailable", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                CodexWorkerUpdateSnapshot update = worker.IsRemote
                    ? await _remoteUpdateRelay.GetStatusAsync(worker, cancellationToken).ConfigureAwait(false)
                    : await _localUpdateManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                CodexFleetRolloutWorkerState? mapped = MapState(update.State);
                if (mapped is not null)
                {
                    rollout = await _rolloutStore.UpdateWorkerAsync(
                        rollout.OwnerUserId,
                        rollout.RolloutId,
                        worker.WorkerId,
                        mapped.Value,
                        update.OutcomeCode ?? "status_refreshed",
                        cancellationToken).ConfigureAwait(false) ?? rollout;
                }
            }
            catch (InvalidOperationException)
            {
                // A disconnected worker is not treated as healthy or failed; the installer boundary remains explicit.
            }
        }

        return await _rolloutStore.GetAsync(rollout.OwnerUserId, rollout.RolloutId, cancellationToken).ConfigureAwait(false) ?? rollout;
    }

    private async Task<CodexFleetRolloutActionResult> BlockAsync(
        CodexFleetRolloutSnapshot rollout,
        string workerId,
        string outcomeCode,
        CancellationToken cancellationToken)
    {
        CodexFleetRolloutSnapshot? updated = await _rolloutStore.UpdateWorkerAsync(
            rollout.OwnerUserId,
            rollout.RolloutId,
            workerId,
            CodexFleetRolloutWorkerState.Blocked,
            outcomeCode,
            cancellationToken).ConfigureAwait(false);
        return Success(outcomeCode, updated);
    }

    private async Task<CodexWorkerSnapshot?> FindWorkerAsync(string workerId, CancellationToken cancellationToken)
    {
        CodexWorkerSnapshot local = await _workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(local.WorkerId, workerId, StringComparison.Ordinal))
        {
            return local;
        }

        return (await _coordinatorWorkerStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(worker => worker.IsRemote && string.Equals(worker.WorkerId, workerId, StringComparison.Ordinal));
    }

    private static string? CheckCompatibility(CodexWorkerSnapshot? worker, IReadOnlyList<string> requiredCapabilities)
    {
        if (worker is null || worker.State is not (CodexWorkerState.Online or CodexWorkerState.Draining) || string.Equals(worker.Readiness, "unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "worker_unavailable";
        }

        return requiredCapabilities.Any(required => !worker.Capabilities.Contains(required, StringComparer.Ordinal))
            ? "worker_capability_mismatch"
            : null;
    }

    private static CodexFleetRolloutWorkerState? MapState(CodexWorkerUpdateState state)
        => state switch
        {
            CodexWorkerUpdateState.Staged => CodexFleetRolloutWorkerState.Staged,
            CodexWorkerUpdateState.Active => CodexFleetRolloutWorkerState.Active,
            CodexWorkerUpdateState.HealthFailed => CodexFleetRolloutWorkerState.HealthFailed,
            CodexWorkerUpdateState.RollbackStaged => CodexFleetRolloutWorkerState.RollbackStaged,
            CodexWorkerUpdateState.RollbackActive => CodexFleetRolloutWorkerState.RollbackActive,
            CodexWorkerUpdateState.Rejected => CodexFleetRolloutWorkerState.Blocked,
            _ => null,
        };

    private static CodexFleetRolloutActionResult Success(string outcomeCode, CodexFleetRolloutSnapshot? rollout)
        => new(true, outcomeCode, rollout);

    private static CodexFleetRolloutActionResult Failure(string outcomeCode)
        => new(false, outcomeCode, null);

    private static string ToOutcomeCode(InvalidOperationException exception)
        => exception.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            ? "worker_unavailable"
            : "worker_update_request_failed";

    private static bool IsSafeVersion(string? value)
        => IsSafeToken(value, 40) && Version.TryParse(value, out _);

    private static bool IsSha256(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length == 64 && value.Trim().All(char.IsAsciiHexDigit);

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or ':');
}
