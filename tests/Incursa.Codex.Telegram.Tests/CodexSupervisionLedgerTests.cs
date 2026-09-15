using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexSupervisionLedgerTests
{
    [Fact]
    public async Task StartCommandCreatesSeparateTaskRunAndCommandIdentities()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        TelegramConversationScope conversation = new(1234, 55);
        using CodexSupervisionLedger ledger = CreateLedger(temp.Path);

        CodexSupervisionCommandStart started = await ledger.StartCommandAsync(
            "telegram:100",
            "thread-1",
            "Build worker",
            conversation,
            1234,
            "prompt",
            CancellationToken.None);

        Assert.True(started.Accepted);
        Assert.NotNull(started.Task);
        Assert.NotNull(started.Run);
        Assert.NotEqual(started.Task!.TaskId, started.Run!.RunId);
        Assert.NotEqual(started.Run.CommandId, started.Run.RunId);
        Assert.Equal(CodexSupervisionRunState.Accepted, started.Run.State);
    }

    [Fact]
    public async Task ReplayedCommandIsRejectedWithoutCreatingAnotherRun()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        using CodexSupervisionLedger ledger = CreateLedger(temp.Path);

        CodexSupervisionCommandStart first = await StartAsync(ledger, "command:replay");
        CodexSupervisionCommandStart replay = await StartAsync(ledger, "command:replay");

        Assert.True(first.Accepted);
        Assert.False(replay.Accepted);
        Assert.Equal(first.Task, replay.Task);
        Assert.Equal(first.Run, replay.Run);
        Assert.Single(await ledger.ListTasksAsync(1234, CancellationToken.None));
        Assert.Equal(first.Run!.RunId, (await ledger.ListTasksAsync(1234, CancellationToken.None)).Single().LatestRun!.RunId);
    }

    [Fact]
    public async Task SameCodexThreadInDifferentConversationCannotShareTask()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        using CodexSupervisionLedger ledger = CreateLedger(temp.Path);

        CodexSupervisionCommandStart first = await ledger.StartCommandAsync(
            "command:one",
            "thread-shared",
            "Session",
            new TelegramConversationScope(1234, null),
            1234,
            "prompt",
            CancellationToken.None);
        CodexSupervisionCommandStart second = await ledger.StartCommandAsync(
            "command:two",
            "thread-shared",
            "Session",
            new TelegramConversationScope(9999, null),
            9999,
            "prompt",
            CancellationToken.None);

        Assert.NotEqual(first.Task!.TaskId, second.Task!.TaskId);
        Assert.Single(await ledger.ListTasksAsync(1234, CancellationToken.None));
        Assert.Single(await ledger.ListTasksAsync(9999, CancellationToken.None));
    }

    [Fact]
    public async Task RunStatePersistsAcrossReloadAndDoesNotPersistPromptText()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        using (CodexSupervisionLedger ledger = CreateLedger(temp.Path))
        {
            CodexSupervisionCommandStart started = await StartAsync(ledger, "command:persist");
            await ledger.UpdateRunAsync(
                started.Run!.RunId,
                CodexSupervisionRunState.Running,
                "thread-1",
                "turn-7",
                null,
                CancellationToken.None);
        }

        string persisted = await File.ReadAllTextAsync(Path.Combine(temp.Path, "codex-supervision-state.json"));
        Assert.DoesNotContain("private prompt text", persisted, StringComparison.Ordinal);

        using CodexSupervisionLedger reloaded = CreateLedger(temp.Path);
        CodexSupervisionTaskSnapshot task = Assert.Single(await reloaded.ListTasksAsync(1234, CancellationToken.None));
        Assert.Equal("thread-1", task.CodexThreadId);
        Assert.Equal(CodexSupervisionRunState.Running, task.LatestRun!.State);
        Assert.Equal("turn-7", task.LatestRun.TurnId);
    }

    [Fact]
    public async Task TerminalTurnStateUpdatesEveryRunBoundToTurn()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        using CodexSupervisionLedger ledger = CreateLedger(temp.Path);
        CodexSupervisionCommandStart started = await StartAsync(ledger, "command:terminal");
        await ledger.UpdateRunAsync(
            started.Run!.RunId,
            CodexSupervisionRunState.Running,
            "thread-1",
            "turn-8",
            null,
            CancellationToken.None);

        int updated = await ledger.UpdateRunsForTurnAsync(
            "turn-8",
            CodexSupervisionRunState.Completed,
            "thread-1",
            "turn_completed",
            CancellationToken.None);

        Assert.Equal(1, updated);
        CodexSupervisionTaskSnapshot task = Assert.Single(await ledger.ListTasksAsync(1234, CancellationToken.None));
        Assert.Equal(CodexSupervisionRunState.Completed, task.LatestRun!.State);
        Assert.Equal("turn_completed", task.LatestRun.OutcomeCode);
        Assert.NotNull(task.LatestRun.CompletedAt);
    }

    [Fact]
    public async Task UnknownOutcomeRemainsOpenForExplicitReconciliation()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        using CodexSupervisionLedger ledger = CreateLedger(temp.Path);
        CodexSupervisionCommandStart started = await StartAsync(ledger, "command:unknown");

        CodexSupervisionRunRecord? updated = await ledger.UpdateRunAsync(
            started.Run!.RunId,
            CodexSupervisionRunState.Unknown,
            "thread-1",
            null,
            "external_outcome_unknown",
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Null(updated.CompletedAt);
        Assert.Equal(CodexSupervisionRunState.Unknown, Assert.Single((await ledger.ListTasksAsync(1234, CancellationToken.None))).LatestRun!.State);
    }

    [Fact]
    public async Task RestartReconciliationMarksInFlightRunsUnknownWithoutClaimingCompletion()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        using (CodexSupervisionLedger ledger = CreateLedger(temp.Path))
        {
            CodexSupervisionCommandStart started = await StartAsync(ledger, "command:restart");
            await ledger.UpdateRunAsync(
                started.Run!.RunId,
                CodexSupervisionRunState.Running,
                "thread-restart",
                "turn-restart",
                null,
                CancellationToken.None);
        }

        using CodexSupervisionLedger reloaded = CreateLedger(temp.Path);
        Assert.Equal(1, await reloaded.ReconcileAfterRestartAsync(CancellationToken.None));

        CodexSupervisionRunSnapshot run = Assert.Single((await reloaded.ListTasksAsync(1234, CancellationToken.None))).LatestRun!;
        Assert.Equal(CodexSupervisionRunState.Unknown, run.State);
        Assert.Equal("process_restart_reconciliation", run.OutcomeCode);
        Assert.Null(run.CompletedAt);
        Assert.Equal(0, await reloaded.ReconcileAfterRestartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeliveryStateIsSeparateFromRunCompletionAndScopedToOwner()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        using CodexSupervisionLedger ledger = CreateLedger(temp.Path);
        CodexSupervisionCommandStart started = await StartAsync(ledger, "command:delivery");

        CodexSupervisionDeliveryRecord? delivery = await ledger.StartDeliveryAsync(
            started.Run!.RunId,
            "queue:item-1",
            new TelegramConversationScope(1234, 9),
            "completion",
            CancellationToken.None);
        Assert.NotNull(delivery);
        Assert.Equal(CodexSupervisionDeliveryState.Queued, delivery.State);

        CodexSupervisionDeliveryRecord? sending = await ledger.UpdateDeliveryAsync(
            delivery.DeliveryId,
            CodexSupervisionDeliveryState.Sending,
            null,
            CancellationToken.None);
        Assert.NotNull(sending);
        CodexSupervisionDeliveryRecord? delivered = await ledger.UpdateDeliveryAsync(
            sending.DeliveryId,
            CodexSupervisionDeliveryState.Delivered,
            "telegram_accepted",
            CancellationToken.None);
        Assert.NotNull(delivered);

        Assert.Equal(1, delivered.AttemptCount);
        Assert.Equal(CodexSupervisionDeliveryState.Delivered, delivered.State);
        Assert.Single(await ledger.ListDeliveriesAsync(1234, CancellationToken.None));
        Assert.Empty(await ledger.ListDeliveriesAsync(9876, CancellationToken.None));
    }

    [Fact]
    public async Task ApprovalClaimAndRecoveryRecordsAreBoundedAndFailClosed()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        using CodexSupervisionLedger ledger = CreateLedger(temp.Path);
        CodexSupervisionCommandStart started = await StartAsync(ledger, "command:actions");
        await ledger.UpdateRunAsync(
            started.Run!.RunId,
            CodexSupervisionRunState.Running,
            "thread-1",
            "turn-actions",
            null,
            CancellationToken.None);

        CodexSupervisionApprovalRecord? requested = await ledger.RecordApprovalRequestAsync(
            "turn-actions",
            "thread-1",
            "item/commandExecution/requestApproval",
            TimeSpan.FromMinutes(30),
            CancellationToken.None);
        Assert.NotNull(requested);
        CodexSupervisionApprovalRecord? denied = await ledger.RecordApprovalDecisionAsync(
            "turn-actions",
            "thread-1",
            "item/commandExecution/requestApproval",
            "denied_by_default",
            1234,
            CancellationToken.None);
        Assert.NotNull(denied);
        Assert.Equal(CodexSupervisionApprovalState.Denied, denied.State);
        Assert.Equal("denied_by_default", denied.OutcomeCode);

        CodexSupervisionClaimRecord? claim = await ledger.ClaimTaskAsync(
            started.Task!.TaskId,
            1234,
            TimeSpan.FromMinutes(10),
            CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Null(await ledger.ClaimTaskAsync(started.Task.TaskId, 9876, TimeSpan.FromMinutes(10), CancellationToken.None));

        CodexSupervisionRecoveryRecord? recovery = await ledger.RecordRecoveryActionAsync(
            started.Run.RunId,
            "replace_unreadable_thread",
            CodexSupervisionRecoveryState.Applied,
            "child_command_created",
            CancellationToken.None);
        Assert.NotNull(recovery);
        Assert.Equal(CodexSupervisionRecoveryState.Applied, recovery.State);
        Assert.Single(await ledger.ListApprovalsAsync(1234, CancellationToken.None));
        Assert.Single(await ledger.ListClaimsAsync(1234, CancellationToken.None));
        Assert.Single(await ledger.ListRecoveryActionsAsync(1234, CancellationToken.None));

        Assert.True(await ledger.ReleaseTaskClaimAsync(started.Task.TaskId, 1234, CancellationToken.None));
        Assert.False(await ledger.ReleaseTaskClaimAsync(started.Task.TaskId, 1234, CancellationToken.None));
    }

    private static async Task<CodexSupervisionCommandStart> StartAsync(
        CodexSupervisionLedger ledger,
        string commandId)
        => await ledger.StartCommandAsync(
            commandId,
            "thread-1",
            "Build worker",
            new TelegramConversationScope(1234, null),
            1234,
            "prompt",
            CancellationToken.None);

    private static CodexSupervisionLedger CreateLedger(string dataRoot)
        => new(
            Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
            {
                Workspace = new CodexWorkspaceOptions
                {
                    DataRoot = dataRoot,
                },
            }),
            TimeProvider.System,
            dataRoot);
}
