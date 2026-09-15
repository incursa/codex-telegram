using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Durable application-owned identity and lifecycle projection for work submitted through Telegram.
/// Codex remains the execution authority; this ledger records enough state to recover and supervise it
/// without persisting prompt bodies or other private transcript content.
/// </summary>
internal interface ICodexSupervisionLedger
{
    Task<CodexSupervisionTaskRecord?> RegisterTaskAsync(
        string taskId,
        string codexThreadId,
        string sessionName,
        TelegramConversationScope conversation,
        long userId,
        CancellationToken cancellationToken);

    Task<CodexSupervisionCommandStart> StartCommandAsync(
        string? commandId,
        string codexThreadId,
        string sessionName,
        TelegramConversationScope conversation,
        long userId,
        string kind,
        CancellationToken cancellationToken);

    Task<CodexSupervisionRunRecord?> UpdateRunAsync(
        string runId,
        CodexSupervisionRunState state,
        string? codexThreadId,
        string? turnId,
        string? outcomeCode,
        CancellationToken cancellationToken);

    Task<int> UpdateRunsForTurnAsync(
        string turnId,
        CodexSupervisionRunState state,
        string? codexThreadId,
        string? outcomeCode,
        CancellationToken cancellationToken);

    Task<int> ReconcileAfterRestartAsync(CancellationToken cancellationToken);

    Task<string?> FindRunIdForTurnAsync(string turnId, CancellationToken cancellationToken);

    Task<CodexSupervisionDeliveryRecord?> StartDeliveryAsync(
        string runId,
        string queueItemId,
        TelegramConversationScope conversation,
        string kind,
        CancellationToken cancellationToken);

    Task<CodexSupervisionDeliveryRecord?> UpdateDeliveryAsync(
        string deliveryId,
        CodexSupervisionDeliveryState state,
        string? outcomeCode,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CodexSupervisionDeliverySnapshot>> ListDeliveriesAsync(
        long userId,
        CancellationToken cancellationToken);

    Task<CodexSupervisionApprovalRecord?> RecordApprovalRequestAsync(
        string turnId,
        string? codexThreadId,
        string actionKind,
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    Task<CodexSupervisionApprovalRecord?> RecordApprovalDecisionAsync(
        string turnId,
        string? codexThreadId,
        string actionKind,
        string decision,
        long? decidedByUserId,
        CancellationToken cancellationToken);

    Task<CodexSupervisionClaimRecord?> ClaimTaskAsync(
        string taskId,
        long userId,
        TimeSpan lease,
        CancellationToken cancellationToken);

    Task<bool> ReleaseTaskClaimAsync(
        string taskId,
        long userId,
        CancellationToken cancellationToken);

    Task<CodexSupervisionRecoveryRecord?> RecordRecoveryActionAsync(
        string runId,
        string actionKind,
        CodexSupervisionRecoveryState state,
        string? outcomeCode,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CodexSupervisionApprovalSnapshot>> ListApprovalsAsync(
        long userId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CodexSupervisionClaimSnapshot>> ListClaimsAsync(
        long userId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CodexSupervisionRecoverySnapshot>> ListRecoveryActionsAsync(
        long userId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CodexSupervisionTaskSnapshot>> ListTasksAsync(long userId, CancellationToken cancellationToken);

    Task<CodexSupervisionTaskSnapshot?> GetTaskForSessionAsync(long userId, string codexThreadId, CancellationToken cancellationToken);
}

internal enum CodexSupervisionRunState
{
    Accepted,
    Queued,
    Running,
    WaitingForInput,
    ReadyForReview,
    Completed,
    Failed,
    Interrupted,
    Unknown,
}

internal enum CodexSupervisionDeliveryState
{
    Queued,
    Sending,
    Delivered,
    Failed,
    Unknown,
}

internal enum CodexSupervisionApprovalState
{
    Requested,
    Granted,
    Denied,
    Expired,
}

internal enum CodexSupervisionClaimState
{
    Claimed,
    Released,
    Expired,
}

internal enum CodexSupervisionRecoveryState
{
    Requested,
    Applied,
    Rejected,
    Unknown,
}

internal sealed record CodexSupervisionCommandStart(
    bool Accepted,
    string CommandId,
    CodexSupervisionTaskRecord? Task,
    CodexSupervisionRunRecord? Run);

internal sealed record CodexSupervisionTaskRecord(
    string TaskId,
    string CodexThreadId,
    string SessionName,
    TelegramConversationScope Conversation,
    long OwnerUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record CodexSupervisionRunRecord(
    string RunId,
    string TaskId,
    string CommandId,
    string CodexThreadId,
    string Kind,
    CodexSupervisionRunState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? TurnId = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? OutcomeCode = null);

internal sealed record CodexSupervisionDeliveryRecord(
    string DeliveryId,
    string RunId,
    string QueueItemId,
    TelegramConversationScope Conversation,
    string Kind,
    CodexSupervisionDeliveryState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int AttemptCount,
    string? OutcomeCode = null);

internal sealed record CodexSupervisionDeliverySnapshot(
    string DeliveryId,
    string RunId,
    TelegramConversationScope Conversation,
    string Kind,
    CodexSupervisionDeliveryState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int AttemptCount,
    string? OutcomeCode);

internal sealed record CodexSupervisionApprovalRecord(
    string ApprovalId,
    string RunId,
    string TurnId,
    string ActionKind,
    CodexSupervisionApprovalState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    long? DecidedByUserId,
    string? OutcomeCode);

internal sealed record CodexSupervisionClaimRecord(
    string ClaimId,
    string TaskId,
    long UserId,
    CodexSupervisionClaimState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt);

internal sealed record CodexSupervisionRecoveryRecord(
    string RecoveryId,
    string RunId,
    string ActionKind,
    CodexSupervisionRecoveryState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? OutcomeCode);

internal sealed record CodexSupervisionApprovalSnapshot(
    string ApprovalId,
    string RunId,
    string TurnId,
    string ActionKind,
    CodexSupervisionApprovalState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    string? OutcomeCode);

internal sealed record CodexSupervisionClaimSnapshot(
    string ClaimId,
    string TaskId,
    long UserId,
    CodexSupervisionClaimState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt);

internal sealed record CodexSupervisionRecoverySnapshot(
    string RecoveryId,
    string RunId,
    string ActionKind,
    CodexSupervisionRecoveryState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? OutcomeCode);

/// <summary>
/// Redacted, read-only task projection intended for the Mini App.
/// </summary>
internal sealed record CodexSupervisionTaskSnapshot(
    string TaskId,
    string CodexThreadId,
    string SessionName,
    TelegramConversationScope Conversation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    CodexSupervisionRunSnapshot? LatestRun);

internal sealed record CodexSupervisionRunSnapshot(
    string RunId,
    string CommandId,
    string Kind,
    CodexSupervisionRunState State,
    string? TurnId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? OutcomeCode);

internal sealed class CodexSupervisionLedger : ICodexSupervisionLedger, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumTasks = 500;
    private const int MaximumRuns = 2_000;
    private const int MaximumCommands = 2_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _dataRoot;

    public CodexSupervisionLedger(
        IOptions<CodexTelegramOptions> options,
        TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        _dataRoot = GetDataRoot();
    }

    internal CodexSupervisionLedger(
        IOptions<CodexTelegramOptions> options,
        TimeProvider timeProvider,
        string? dataRootOverride = null)
    {
        _options = options;
        _timeProvider = timeProvider;
        _dataRoot = string.IsNullOrWhiteSpace(dataRootOverride)
            ? GetDataRoot()
            : Path.GetFullPath(dataRootOverride);
    }

    public async Task<CodexSupervisionTaskRecord?> RegisterTaskAsync(
        string taskId,
        string codexThreadId,
        string sessionName,
        TelegramConversationScope conversation,
        long userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(codexThreadId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            string conversationKey = conversation.ToStorageKey();
            LedgerTaskRecord? existing = state.Tasks.FirstOrDefault(task =>
                string.Equals(task.TaskId, taskId.Trim(), StringComparison.Ordinal));
            if (existing is not null)
            {
                return existing.OwnerUserId == userId
                    && string.Equals(existing.CodexThreadId, codexThreadId.Trim(), StringComparison.Ordinal)
                    && string.Equals(existing.ConversationKey, conversationKey, StringComparison.Ordinal)
                    ? existing.ToPublicRecord()
                    : null;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            LedgerTaskRecord taskRecord = new(
                taskId.Trim(),
                codexThreadId.Trim(),
                Limit(sessionName, 200) ?? "Codex task",
                conversationKey,
                userId,
                now,
                now);
            state.Tasks.Add(taskRecord);
            Trim(state);
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return taskRecord.ToPublicRecord();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexSupervisionCommandStart> StartCommandAsync(
        string? commandId,
        string codexThreadId,
        string sessionName,
        TelegramConversationScope conversation,
        long userId,
        string kind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(codexThreadId))
        {
            throw new ArgumentException("Codex thread id cannot be empty.", nameof(codexThreadId));
        }

        commandId = string.IsNullOrWhiteSpace(commandId)
            ? $"command:{Guid.NewGuid():N}"
            : commandId.Trim();
        if (commandId.Length > 256)
        {
            throw new ArgumentException("Command id is too long.", nameof(commandId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            LedgerCommandRecord? existingCommand = state.Commands.FirstOrDefault(command =>
                string.Equals(command.CommandId, commandId, StringComparison.Ordinal));
            if (existingCommand is not null)
            {
                LedgerTaskRecord? existingTask = state.Tasks.FirstOrDefault(task =>
                    string.Equals(task.TaskId, existingCommand.TaskId, StringComparison.Ordinal));
                LedgerRunRecord? existingRun = state.Runs.FirstOrDefault(run =>
                    string.Equals(run.RunId, existingCommand.RunId, StringComparison.Ordinal));
                return new CodexSupervisionCommandStart(
                    Accepted: false,
                    commandId,
                    existingTask?.ToPublicRecord(),
                    existingRun?.ToPublicRecord());
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            LedgerTaskRecord? task = state.Tasks.FirstOrDefault(candidate =>
                string.Equals(candidate.CodexThreadId, codexThreadId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.ConversationKey, conversation.ToStorageKey(), StringComparison.Ordinal)
                && candidate.OwnerUserId == userId);
            if (task is null)
            {
                task = new LedgerTaskRecord(
                    $"task:{Guid.NewGuid():N}",
                    codexThreadId,
                    Limit(sessionName, 200) ?? "Codex task",
                    conversation.ToStorageKey(),
                    userId,
                    now,
                    now);
                state.Tasks.Add(task);
            }
            else
            {
                task.SessionName = Limit(sessionName, 200) ?? task.SessionName;
                task.UpdatedAt = now;
            }

            LedgerRunRecord run = new(
                $"run:{Guid.NewGuid():N}",
                task.TaskId,
                commandId,
                codexThreadId,
                Limit(kind, 80) ?? "prompt",
                CodexSupervisionRunState.Accepted,
                now,
                now);
            state.Runs.Add(run);
            state.Commands.Add(new LedgerCommandRecord(commandId, task.TaskId, run.RunId, now, CodexSupervisionRunState.Accepted));
            Trim(state);
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new CodexSupervisionCommandStart(true, commandId, task.ToPublicRecord(), run.ToPublicRecord());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexSupervisionRunRecord?> UpdateRunAsync(
        string runId,
        CodexSupervisionRunState state,
        string? codexThreadId,
        string? turnId,
        string? outcomeCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            LedgerRunRecord? run = ledger.Runs.FirstOrDefault(candidate =>
                string.Equals(candidate.RunId, runId, StringComparison.Ordinal));
            if (run is null)
            {
                return null;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            run.State = state;
            run.CodexThreadId = string.IsNullOrWhiteSpace(codexThreadId) ? run.CodexThreadId : codexThreadId.Trim();
            run.TurnId = string.IsNullOrWhiteSpace(turnId) ? run.TurnId : turnId.Trim();
            run.OutcomeCode = Limit(outcomeCode, 120);
            run.UpdatedAt = now;
            if (state is CodexSupervisionRunState.Running or CodexSupervisionRunState.WaitingForInput)
            {
                run.StartedAt ??= now;
            }

            if (IsTerminal(state))
            {
                run.CompletedAt ??= now;
            }

            LedgerCommandRecord? command = ledger.Commands.FirstOrDefault(candidate =>
                string.Equals(candidate.CommandId, run.CommandId, StringComparison.Ordinal));
            if (command is not null)
            {
                command.State = state;
            }

            LedgerTaskRecord? task = ledger.Tasks.FirstOrDefault(candidate =>
                string.Equals(candidate.TaskId, run.TaskId, StringComparison.Ordinal));
            if (task is not null)
            {
                task.UpdatedAt = now;
            }

            await SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
            return run.ToPublicRecord();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> UpdateRunsForTurnAsync(
        string turnId,
        CodexSupervisionRunState state,
        string? codexThreadId,
        string? outcomeCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            LedgerRunRecord[] runs = ledger.Runs
                .Where(candidate => string.Equals(candidate.TurnId, turnId, StringComparison.Ordinal))
                .ToArray();
            if (runs.Length == 0)
            {
                return 0;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            foreach (LedgerRunRecord run in runs)
            {
                run.State = state;
                run.CodexThreadId = string.IsNullOrWhiteSpace(codexThreadId) ? run.CodexThreadId : codexThreadId.Trim();
                run.OutcomeCode = Limit(outcomeCode, 120);
                run.UpdatedAt = now;
                if (state is CodexSupervisionRunState.Running or CodexSupervisionRunState.WaitingForInput)
                {
                    run.StartedAt ??= now;
                }

                if (IsTerminal(state))
                {
                    run.CompletedAt ??= now;
                }

                LedgerCommandRecord? command = ledger.Commands.FirstOrDefault(candidate =>
                    string.Equals(candidate.CommandId, run.CommandId, StringComparison.Ordinal));
                if (command is not null)
                {
                    command.State = state;
                }

                LedgerTaskRecord? task = ledger.Tasks.FirstOrDefault(candidate =>
                    string.Equals(candidate.TaskId, run.TaskId, StringComparison.Ordinal));
                if (task is not null)
                {
                    task.UpdatedAt = now;
                }
            }

            await SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
            return runs.Length;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ReconcileAfterRestartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            LedgerRunRecord[] staleRuns = ledger.Runs
                .Where(run => run.State is CodexSupervisionRunState.Accepted
                    or CodexSupervisionRunState.Queued
                    or CodexSupervisionRunState.Running
                    or CodexSupervisionRunState.WaitingForInput)
                .ToArray();
            if (staleRuns.Length == 0)
            {
                return 0;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            foreach (LedgerRunRecord run in staleRuns)
            {
                run.State = CodexSupervisionRunState.Unknown;
                run.OutcomeCode = "process_restart_reconciliation";
                run.UpdatedAt = now;

                LedgerCommandRecord? command = ledger.Commands.FirstOrDefault(candidate =>
                    string.Equals(candidate.CommandId, run.CommandId, StringComparison.Ordinal));
                if (command is not null)
                {
                    command.State = CodexSupervisionRunState.Unknown;
                }

                LedgerTaskRecord? task = ledger.Tasks.FirstOrDefault(candidate =>
                    string.Equals(candidate.TaskId, run.TaskId, StringComparison.Ordinal));
                if (task is not null)
                {
                    task.UpdatedAt = now;
                }
            }

            await SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
            return staleRuns.Length;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> FindRunIdForTurnAsync(string turnId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return ledger.Runs
                .Where(run => string.Equals(run.TurnId, turnId, StringComparison.Ordinal))
                .OrderByDescending(run => run.UpdatedAt)
                .Select(run => run.RunId)
                .FirstOrDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexSupervisionDeliveryRecord?> StartDeliveryAsync(
        string runId,
        string queueItemId,
        TelegramConversationScope conversation,
        string kind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(queueItemId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!ledger.Runs.Any(run => string.Equals(run.RunId, runId, StringComparison.Ordinal)))
            {
                return null;
            }

            string conversationKey = conversation.ToStorageKey();
            LedgerDeliveryRecord? existing = ledger.Deliveries.FirstOrDefault(delivery =>
                string.Equals(delivery.RunId, runId, StringComparison.Ordinal)
                && string.Equals(delivery.QueueItemId, queueItemId, StringComparison.Ordinal)
                && string.Equals(delivery.ConversationKey, conversationKey, StringComparison.Ordinal));
            if (existing is not null)
            {
                return existing.ToPublicRecord();
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            LedgerDeliveryRecord delivery = new(
                $"delivery:{Guid.NewGuid():N}",
                runId,
                Limit(queueItemId, 256) ?? "queue-item",
                conversationKey,
                Limit(kind, 80) ?? "message",
                CodexSupervisionDeliveryState.Queued,
                now,
                now,
                0,
                null);
            ledger.Deliveries.Add(delivery);
            Trim(ledger);
            await SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
            return delivery.ToPublicRecord();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexSupervisionDeliveryRecord?> UpdateDeliveryAsync(
        string deliveryId,
        CodexSupervisionDeliveryState state,
        string? outcomeCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deliveryId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            LedgerDeliveryRecord? delivery = ledger.Deliveries.FirstOrDefault(candidate =>
                string.Equals(candidate.DeliveryId, deliveryId, StringComparison.Ordinal));
            if (delivery is null)
            {
                return null;
            }

            delivery.State = state;
            delivery.OutcomeCode = Limit(outcomeCode, 120);
            delivery.UpdatedAt = _timeProvider.GetUtcNow();
            if (state == CodexSupervisionDeliveryState.Sending)
            {
                delivery.AttemptCount++;
            }

            await SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
            return delivery.ToPublicRecord();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CodexSupervisionDeliverySnapshot>> ListDeliveriesAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            HashSet<string> ownedTaskIds = ledger.Tasks
                .Where(task => task.OwnerUserId == userId)
                .Select(task => task.TaskId)
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> ownedRunIds = ledger.Runs
                .Where(run => ownedTaskIds.Contains(run.TaskId))
                .Select(run => run.RunId)
                .ToHashSet(StringComparer.Ordinal);
            return ledger.Deliveries
                .Where(delivery => ownedRunIds.Contains(delivery.RunId))
                .OrderByDescending(delivery => delivery.UpdatedAt)
                .Select(delivery => delivery.ToSnapshot())
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexSupervisionApprovalRecord?> RecordApprovalRequestAsync(
        string turnId,
        string? codexThreadId,
        string actionKind,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            LedgerRunRecord? run = FindRun(ledger, turnId, codexThreadId);
            if (run is null)
            {
                return null;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            string normalizedAction = Limit(actionKind, 120) ?? "approval";
            LedgerApprovalRecord? existing = ledger.Approvals
                .Where(approval => string.Equals(approval.RunId, run.RunId, StringComparison.Ordinal)
                    && string.Equals(approval.TurnId, turnId, StringComparison.Ordinal)
                    && string.Equals(approval.ActionKind, normalizedAction, StringComparison.Ordinal))
                .OrderByDescending(approval => approval.UpdatedAt)
                .FirstOrDefault();
            if (existing is not null)
            {
                if (existing.State == CodexSupervisionApprovalState.Requested && existing.ExpiresAt <= now)
                {
                    existing.State = CodexSupervisionApprovalState.Expired;
                    existing.UpdatedAt = now;
                    await SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
                }

                return existing.ToPublicRecord();
            }

            LedgerApprovalRecord approval = new(
                $"approval:{Guid.NewGuid():N}",
                run.RunId,
                turnId.Trim(),
                normalizedAction,
                CodexSupervisionApprovalState.Requested,
                now,
                now,
                now.Add(NormalizeLifetime(lifetime)),
                null,
                null);
            ledger.Approvals.Add(approval);
            Trim(ledger);
            await SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
            return approval.ToPublicRecord();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexSupervisionApprovalRecord?> RecordApprovalDecisionAsync(
        string turnId,
        string? codexThreadId,
        string actionKind,
        string decision,
        long? decidedByUserId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            LedgerRunRecord? run = FindRun(ledger, turnId, codexThreadId);
            if (run is null)
            {
                return null;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            string normalizedAction = Limit(actionKind, 120) ?? "approval";
            LedgerApprovalRecord? approval = ledger.Approvals
                .Where(candidate => string.Equals(candidate.RunId, run.RunId, StringComparison.Ordinal)
                    && string.Equals(candidate.TurnId, turnId, StringComparison.Ordinal)
                    && string.Equals(candidate.ActionKind, normalizedAction, StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.UpdatedAt)
                .FirstOrDefault();
            if (approval is null)
            {
                approval = new LedgerApprovalRecord(
                    $"approval:{Guid.NewGuid():N}",
                    run.RunId,
                    turnId.Trim(),
                    normalizedAction,
                    CodexSupervisionApprovalState.Requested,
                    now,
                    now,
                    now,
                    null,
                    null);
                ledger.Approvals.Add(approval);
            }

            if (approval.State == CodexSupervisionApprovalState.Requested && approval.ExpiresAt > now)
            {
                approval.State = IsApprovalGranted(decision)
                    ? CodexSupervisionApprovalState.Granted
                    : CodexSupervisionApprovalState.Denied;
                approval.DecidedByUserId = decidedByUserId;
                approval.OutcomeCode = Limit(decision, 120) ?? "denied";
                approval.UpdatedAt = now;
            }
            else if (approval.State == CodexSupervisionApprovalState.Requested)
            {
                approval.State = CodexSupervisionApprovalState.Expired;
                approval.UpdatedAt = now;
            }

            Trim(ledger);
            await SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
            return approval.ToPublicRecord();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexSupervisionClaimRecord?> ClaimTaskAsync(
        string taskId,
        long userId,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId) || userId == 0)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!ledger.Tasks.Any(task => string.Equals(task.TaskId, taskId, StringComparison.Ordinal)))
            {
                return null;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            LedgerClaimRecord? existing = ledger.Claims.FirstOrDefault(claim =>
                string.Equals(claim.TaskId, taskId, StringComparison.Ordinal)
                && claim.State == CodexSupervisionClaimState.Claimed);
            if (existing is not null && existing.ExpiresAt > now && existing.UserId != userId)
            {
                return null;
            }

            if (existing is null || existing.ExpiresAt <= now)
            {
                if (existing is not null)
                {
                    existing.State = CodexSupervisionClaimState.Expired;
                    existing.UpdatedAt = now;
                }

                existing = new LedgerClaimRecord(
                    $"claim:{Guid.NewGuid():N}",
                    taskId.Trim(),
                    userId,
                    CodexSupervisionClaimState.Claimed,
                    now,
                    now,
                    now.Add(NormalizeLifetime(lease)));
                ledger.Claims.Add(existing);
            }
            else
            {
                existing.UpdatedAt = now;
                existing.ExpiresAt = now.Add(NormalizeLifetime(lease));
            }

            Trim(ledger);
            await SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
            return existing.ToPublicRecord();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ReleaseTaskClaimAsync(
        string taskId,
        long userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId) || userId == 0)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            LedgerClaimRecord? claim = ledger.Claims.FirstOrDefault(candidate =>
                string.Equals(candidate.TaskId, taskId, StringComparison.Ordinal)
                && candidate.State == CodexSupervisionClaimState.Claimed
                && candidate.UserId == userId);
            if (claim is null)
            {
                return false;
            }

            claim.State = CodexSupervisionClaimState.Released;
            claim.UpdatedAt = _timeProvider.GetUtcNow();
            await SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexSupervisionRecoveryRecord?> RecordRecoveryActionAsync(
        string runId,
        string actionKind,
        CodexSupervisionRecoveryState state,
        string? outcomeCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!ledger.Runs.Any(run => string.Equals(run.RunId, runId, StringComparison.Ordinal)))
            {
                return null;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            LedgerRecoveryRecord recovery = new(
                $"recovery:{Guid.NewGuid():N}",
                runId.Trim(),
                Limit(actionKind, 120) ?? "recovery",
                state,
                now,
                now,
                Limit(outcomeCode, 120));
            ledger.Recoveries.Add(recovery);
            Trim(ledger);
            await SaveAsync(ledger, cancellationToken).ConfigureAwait(false);
            return recovery.ToPublicRecord();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CodexSupervisionApprovalSnapshot>> ListApprovalsAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            HashSet<string> ownedRunIds = GetOwnedRunIds(ledger, userId);
            return ledger.Approvals
                .Where(approval => ownedRunIds.Contains(approval.RunId))
                .OrderByDescending(approval => approval.UpdatedAt)
                .Select(approval => approval.ToSnapshot())
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CodexSupervisionClaimSnapshot>> ListClaimsAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return ledger.Claims
                .Where(claim => claim.UserId == userId)
                .OrderByDescending(claim => claim.UpdatedAt)
                .Select(claim => claim.ToSnapshot())
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CodexSupervisionRecoverySnapshot>> ListRecoveryActionsAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState ledger = await LoadAsync(cancellationToken).ConfigureAwait(false);
            HashSet<string> ownedRunIds = GetOwnedRunIds(ledger, userId);
            return ledger.Recoveries
                .Where(recovery => ownedRunIds.Contains(recovery.RunId))
                .OrderByDescending(recovery => recovery.UpdatedAt)
                .Select(recovery => recovery.ToSnapshot())
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CodexSupervisionTaskSnapshot>> ListTasksAsync(long userId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LedgerState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return BuildSnapshots(state, userId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexSupervisionTaskSnapshot?> GetTaskForSessionAsync(
        long userId,
        string codexThreadId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(codexThreadId))
        {
            return null;
        }

        return (await ListTasksAsync(userId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(task => string.Equals(task.CodexThreadId, codexThreadId, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task<LedgerState> LoadAsync(CancellationToken cancellationToken)
    {
        string path = GetStatePath();
        if (!File.Exists(path))
        {
            return new LedgerState();
        }

        await using FileStream stream = File.OpenRead(path);
        LedgerState? state = await JsonSerializer.DeserializeAsync<LedgerState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        state ??= new LedgerState();
        state.Tasks ??= [];
        state.Runs ??= [];
        state.Commands ??= [];
        state.Deliveries ??= [];
        state.Approvals ??= [];
        state.Claims ??= [];
        state.Recoveries ??= [];
        return state;
    }

    private async Task SaveAsync(LedgerState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataRoot);
        string targetPath = GetStatePath();
        string tempPath = Path.Combine(_dataRoot, $"{Guid.NewGuid():N}.json.tmp");
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }

    private string GetStatePath()
        => Path.Combine(_dataRoot, "codex-supervision-state.json");

    private string GetDataRoot()
    {
        string? configuredRoot = _options.Value.Workspace.DataRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        return string.IsNullOrWhiteSpace(_options.Value.InstanceId)
            ? Path.Combine(AppContext.BaseDirectory, "App_Data", "codex-telegram")
            : CodexTelegramDataRoot.GetDefaultDataRoot(_options.Value.InstanceId);
    }

    private static IReadOnlyList<CodexSupervisionTaskSnapshot> BuildSnapshots(LedgerState state, long userId)
        => state.Tasks
            .Where(task => task.OwnerUserId == userId)
            .Select(task => new CodexSupervisionTaskSnapshot(
                task.TaskId,
                task.CodexThreadId,
                task.SessionName,
                TelegramConversationScope.TryParseStorageKey(task.ConversationKey, out TelegramConversationScope conversation)
                    ? conversation
                    : new TelegramConversationScope(task.OwnerUserId, null),
                task.CreatedAt,
                task.UpdatedAt,
                state.Runs
                    .Where(run => string.Equals(run.TaskId, task.TaskId, StringComparison.Ordinal))
                    .OrderByDescending(run => run.UpdatedAt)
                    .Select(run => run.ToSnapshot())
                    .FirstOrDefault()))
            .OrderByDescending(task => task.UpdatedAt)
            .ToArray();

    private static LedgerRunRecord? FindRun(LedgerState state, string turnId, string? codexThreadId)
        => state.Runs
            .Where(run => string.Equals(run.TurnId, turnId, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(codexThreadId)
                    || string.Equals(run.CodexThreadId, codexThreadId, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(run => run.UpdatedAt)
            .FirstOrDefault();

    private static HashSet<string> GetOwnedRunIds(LedgerState state, long userId)
    {
        HashSet<string> ownedTaskIds = state.Tasks
            .Where(task => task.OwnerUserId == userId)
            .Select(task => task.TaskId)
            .ToHashSet(StringComparer.Ordinal);
        return state.Runs
            .Where(run => ownedTaskIds.Contains(run.TaskId))
            .Select(run => run.RunId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsApprovalGranted(string decision)
        => decision.Equals("approve", StringComparison.OrdinalIgnoreCase)
            || decision.Equals("approved", StringComparison.OrdinalIgnoreCase)
            || decision.Equals("allow", StringComparison.OrdinalIgnoreCase)
            || decision.Equals("allowed", StringComparison.OrdinalIgnoreCase)
            || decision.Equals("accept", StringComparison.OrdinalIgnoreCase)
            || decision.Equals("accepted", StringComparison.OrdinalIgnoreCase)
            || decision.Equals("grant", StringComparison.OrdinalIgnoreCase)
            || decision.Equals("granted", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan NormalizeLifetime(TimeSpan value)
    {
        if (value < TimeSpan.FromMinutes(1))
        {
            return TimeSpan.FromMinutes(1);
        }

        return value > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : value;
    }

    private static void Trim(LedgerState state)
    {
        state.Tasks = state.Tasks
            .OrderByDescending(task => task.UpdatedAt)
            .Take(MaximumTasks)
            .ToList();
        state.Runs = state.Runs
            .OrderByDescending(run => run.UpdatedAt)
            .Take(MaximumRuns)
            .ToList();
        HashSet<string> retainedRunIds = state.Runs.Select(run => run.RunId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> retainedTaskIds = state.Tasks.Select(task => task.TaskId).ToHashSet(StringComparer.Ordinal);
        state.Commands = state.Commands
            .Where(command => retainedRunIds.Contains(command.RunId) && retainedTaskIds.Contains(command.TaskId))
            .OrderByDescending(command => command.AcceptedAt)
            .Take(MaximumCommands)
            .ToList();
        state.Deliveries = state.Deliveries
            .Where(delivery => retainedRunIds.Contains(delivery.RunId))
            .OrderByDescending(delivery => delivery.UpdatedAt)
            .Take(MaximumCommands)
            .ToList();
        state.Approvals = state.Approvals
            .Where(approval => retainedRunIds.Contains(approval.RunId))
            .OrderByDescending(approval => approval.UpdatedAt)
            .Take(MaximumCommands)
            .ToList();
        state.Recoveries = state.Recoveries
            .Where(recovery => retainedRunIds.Contains(recovery.RunId))
            .OrderByDescending(recovery => recovery.UpdatedAt)
            .Take(MaximumCommands)
            .ToList();
        state.Claims = state.Claims
            .Where(claim => retainedTaskIds.Contains(claim.TaskId))
            .OrderByDescending(claim => claim.UpdatedAt)
            .Take(MaximumTasks)
            .ToList();
    }

    private static bool IsTerminal(CodexSupervisionRunState state)
        => state is CodexSupervisionRunState.Completed
            or CodexSupervisionRunState.Failed
            or CodexSupervisionRunState.Interrupted;

    private static string? Limit(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        return value.Length <= maximum ? value : value[..maximum];
    }

    private sealed class LedgerState
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<LedgerTaskRecord> Tasks { get; set; } = [];

        public List<LedgerRunRecord> Runs { get; set; } = [];

        public List<LedgerCommandRecord> Commands { get; set; } = [];

        public List<LedgerDeliveryRecord> Deliveries { get; set; } = [];

        public List<LedgerApprovalRecord> Approvals { get; set; } = [];

        public List<LedgerClaimRecord> Claims { get; set; } = [];

        public List<LedgerRecoveryRecord> Recoveries { get; set; } = [];
    }

    private sealed class LedgerTaskRecord
    {
        public LedgerTaskRecord() { }

        public LedgerTaskRecord(
            string taskId,
            string codexThreadId,
            string sessionName,
            string conversationKey,
            long ownerUserId,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt)
        {
            TaskId = taskId;
            CodexThreadId = codexThreadId;
            SessionName = sessionName;
            ConversationKey = conversationKey;
            OwnerUserId = ownerUserId;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
        }

        public string TaskId { get; set; } = string.Empty;
        public string CodexThreadId { get; set; } = string.Empty;
        public string SessionName { get; set; } = string.Empty;
        public string ConversationKey { get; set; } = string.Empty;
        public long OwnerUserId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public CodexSupervisionTaskRecord ToPublicRecord()
            => TelegramConversationScope.TryParseStorageKey(ConversationKey, out TelegramConversationScope conversation)
                ? new(TaskId, CodexThreadId, SessionName, conversation, OwnerUserId, CreatedAt, UpdatedAt)
                : new(TaskId, CodexThreadId, SessionName, new TelegramConversationScope(OwnerUserId, null), OwnerUserId, CreatedAt, UpdatedAt);
    }

    private sealed class LedgerRunRecord
    {
        public LedgerRunRecord() { }

        public LedgerRunRecord(
            string runId,
            string taskId,
            string commandId,
            string codexThreadId,
            string kind,
            CodexSupervisionRunState state,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt)
        {
            RunId = runId;
            TaskId = taskId;
            CommandId = commandId;
            CodexThreadId = codexThreadId;
            Kind = kind;
            State = state;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
        }

        public string RunId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string CommandId { get; set; } = string.Empty;
        public string CodexThreadId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public CodexSupervisionRunState State { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? TurnId { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? OutcomeCode { get; set; }

        public CodexSupervisionRunRecord ToPublicRecord()
            => new(RunId, TaskId, CommandId, CodexThreadId, Kind, State, CreatedAt, UpdatedAt, TurnId, StartedAt, CompletedAt, OutcomeCode);

        public CodexSupervisionRunSnapshot ToSnapshot()
            => new(RunId, CommandId, Kind, State, TurnId, CreatedAt, UpdatedAt, StartedAt, CompletedAt, OutcomeCode);
    }

    private sealed class LedgerCommandRecord
    {
        public LedgerCommandRecord() { }

        public LedgerCommandRecord(
            string commandId,
            string taskId,
            string runId,
            DateTimeOffset acceptedAt,
            CodexSupervisionRunState state)
        {
            CommandId = commandId;
            TaskId = taskId;
            RunId = runId;
            AcceptedAt = acceptedAt;
            State = state;
        }

        public string CommandId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public DateTimeOffset AcceptedAt { get; set; }
        public CodexSupervisionRunState State { get; set; }
    }

    private sealed class LedgerDeliveryRecord
    {
        public LedgerDeliveryRecord() { }

        public LedgerDeliveryRecord(
            string deliveryId,
            string runId,
            string queueItemId,
            string conversationKey,
            string kind,
            CodexSupervisionDeliveryState state,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt,
            int attemptCount,
            string? outcomeCode)
        {
            DeliveryId = deliveryId;
            RunId = runId;
            QueueItemId = queueItemId;
            ConversationKey = conversationKey;
            Kind = kind;
            State = state;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
            AttemptCount = attemptCount;
            OutcomeCode = outcomeCode;
        }

        public string DeliveryId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public string QueueItemId { get; set; } = string.Empty;
        public string ConversationKey { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public CodexSupervisionDeliveryState State { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public int AttemptCount { get; set; }
        public string? OutcomeCode { get; set; }

        public CodexSupervisionDeliveryRecord ToPublicRecord()
            => new(
                DeliveryId,
                RunId,
                QueueItemId,
                TelegramConversationScope.TryParseStorageKey(ConversationKey, out TelegramConversationScope conversation)
                    ? conversation
                    : default,
                Kind,
                State,
                CreatedAt,
                UpdatedAt,
                AttemptCount,
                OutcomeCode);

        public CodexSupervisionDeliverySnapshot ToSnapshot()
        {
            CodexSupervisionDeliveryRecord record = ToPublicRecord();
            return new(
                record.DeliveryId,
                record.RunId,
                record.Conversation,
                record.Kind,
                record.State,
                record.CreatedAt,
                record.UpdatedAt,
                record.AttemptCount,
                record.OutcomeCode);
        }
    }

    private sealed class LedgerApprovalRecord
    {
        public LedgerApprovalRecord() { }

        public LedgerApprovalRecord(
            string approvalId,
            string runId,
            string turnId,
            string actionKind,
            CodexSupervisionApprovalState state,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt,
            DateTimeOffset expiresAt,
            long? decidedByUserId,
            string? outcomeCode)
        {
            ApprovalId = approvalId;
            RunId = runId;
            TurnId = turnId;
            ActionKind = actionKind;
            State = state;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
            ExpiresAt = expiresAt;
            DecidedByUserId = decidedByUserId;
            OutcomeCode = outcomeCode;
        }

        public string ApprovalId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public string TurnId { get; set; } = string.Empty;
        public string ActionKind { get; set; } = string.Empty;
        public CodexSupervisionApprovalState State { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public long? DecidedByUserId { get; set; }
        public string? OutcomeCode { get; set; }

        public CodexSupervisionApprovalRecord ToPublicRecord()
            => new(ApprovalId, RunId, TurnId, ActionKind, State, CreatedAt, UpdatedAt, ExpiresAt, DecidedByUserId, OutcomeCode);

        public CodexSupervisionApprovalSnapshot ToSnapshot()
            => new(ApprovalId, RunId, TurnId, ActionKind, State, CreatedAt, UpdatedAt, ExpiresAt, OutcomeCode);
    }

    private sealed class LedgerClaimRecord
    {
        public LedgerClaimRecord() { }

        public LedgerClaimRecord(
            string claimId,
            string taskId,
            long userId,
            CodexSupervisionClaimState state,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt,
            DateTimeOffset expiresAt)
        {
            ClaimId = claimId;
            TaskId = taskId;
            UserId = userId;
            State = state;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
            ExpiresAt = expiresAt;
        }

        public string ClaimId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public long UserId { get; set; }
        public CodexSupervisionClaimState State { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }

        public CodexSupervisionClaimRecord ToPublicRecord()
            => new(ClaimId, TaskId, UserId, State, CreatedAt, UpdatedAt, ExpiresAt);

        public CodexSupervisionClaimSnapshot ToSnapshot()
            => new(ClaimId, TaskId, UserId, State, CreatedAt, UpdatedAt, ExpiresAt);
    }

    private sealed class LedgerRecoveryRecord
    {
        public LedgerRecoveryRecord() { }

        public LedgerRecoveryRecord(
            string recoveryId,
            string runId,
            string actionKind,
            CodexSupervisionRecoveryState state,
            DateTimeOffset createdAt,
            DateTimeOffset updatedAt,
            string? outcomeCode)
        {
            RecoveryId = recoveryId;
            RunId = runId;
            ActionKind = actionKind;
            State = state;
            CreatedAt = createdAt;
            UpdatedAt = updatedAt;
            OutcomeCode = outcomeCode;
        }

        public string RecoveryId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public string ActionKind { get; set; } = string.Empty;
        public CodexSupervisionRecoveryState State { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? OutcomeCode { get; set; }

        public CodexSupervisionRecoveryRecord ToPublicRecord()
            => new(RecoveryId, RunId, ActionKind, State, CreatedAt, UpdatedAt, OutcomeCode);

        public CodexSupervisionRecoverySnapshot ToSnapshot()
            => new(RecoveryId, RunId, ActionKind, State, CreatedAt, UpdatedAt, OutcomeCode);
    }
}

internal sealed class NullCodexSupervisionLedger : ICodexSupervisionLedger
{
    public Task<CodexSupervisionTaskRecord?> RegisterTaskAsync(
        string taskId,
        string codexThreadId,
        string sessionName,
        TelegramConversationScope conversation,
        long userId,
        CancellationToken cancellationToken)
        => Task.FromResult<CodexSupervisionTaskRecord?>(null);

    public Task<CodexSupervisionCommandStart> StartCommandAsync(
        string? commandId,
        string sessionId,
        string sessionName,
        TelegramConversationScope conversation,
        long userId,
        string kind,
        CancellationToken cancellationToken)
        => Task.FromResult(new CodexSupervisionCommandStart(false, commandId ?? string.Empty, null, null));

    public Task<CodexSupervisionRunRecord?> UpdateRunAsync(
        string runId,
        CodexSupervisionRunState state,
        string? sessionId,
        string? turnId,
        string? outcomeCode,
        CancellationToken cancellationToken)
        => Task.FromResult<CodexSupervisionRunRecord?>(null);

    public Task<int> UpdateRunsForTurnAsync(
        string turnId,
        CodexSupervisionRunState state,
        string? sessionId,
        string? outcomeCode,
        CancellationToken cancellationToken)
        => Task.FromResult(0);

    public Task<int> ReconcileAfterRestartAsync(CancellationToken cancellationToken)
        => Task.FromResult(0);

    public Task<string?> FindRunIdForTurnAsync(string turnId, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public Task<CodexSupervisionDeliveryRecord?> StartDeliveryAsync(
        string runId,
        string queueItemId,
        TelegramConversationScope conversation,
        string kind,
        CancellationToken cancellationToken)
        => Task.FromResult<CodexSupervisionDeliveryRecord?>(null);

    public Task<CodexSupervisionDeliveryRecord?> UpdateDeliveryAsync(
        string deliveryId,
        CodexSupervisionDeliveryState state,
        string? outcomeCode,
        CancellationToken cancellationToken)
        => Task.FromResult<CodexSupervisionDeliveryRecord?>(null);

    public Task<IReadOnlyList<CodexSupervisionDeliverySnapshot>> ListDeliveriesAsync(
        long userId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CodexSupervisionDeliverySnapshot>>([]);

    public Task<CodexSupervisionApprovalRecord?> RecordApprovalRequestAsync(
        string turnId,
        string? codexThreadId,
        string actionKind,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
        => Task.FromResult<CodexSupervisionApprovalRecord?>(null);

    public Task<CodexSupervisionApprovalRecord?> RecordApprovalDecisionAsync(
        string turnId,
        string? codexThreadId,
        string actionKind,
        string decision,
        long? decidedByUserId,
        CancellationToken cancellationToken)
        => Task.FromResult<CodexSupervisionApprovalRecord?>(null);

    public Task<CodexSupervisionClaimRecord?> ClaimTaskAsync(
        string taskId,
        long userId,
        TimeSpan lease,
        CancellationToken cancellationToken)
        => Task.FromResult<CodexSupervisionClaimRecord?>(null);

    public Task<bool> ReleaseTaskClaimAsync(
        string taskId,
        long userId,
        CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task<CodexSupervisionRecoveryRecord?> RecordRecoveryActionAsync(
        string runId,
        string actionKind,
        CodexSupervisionRecoveryState state,
        string? outcomeCode,
        CancellationToken cancellationToken)
        => Task.FromResult<CodexSupervisionRecoveryRecord?>(null);

    public Task<IReadOnlyList<CodexSupervisionApprovalSnapshot>> ListApprovalsAsync(
        long userId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CodexSupervisionApprovalSnapshot>>([]);

    public Task<IReadOnlyList<CodexSupervisionClaimSnapshot>> ListClaimsAsync(
        long userId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CodexSupervisionClaimSnapshot>>([]);

    public Task<IReadOnlyList<CodexSupervisionRecoverySnapshot>> ListRecoveryActionsAsync(
        long userId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CodexSupervisionRecoverySnapshot>>([]);

    public Task<IReadOnlyList<CodexSupervisionTaskSnapshot>> ListTasksAsync(long userId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CodexSupervisionTaskSnapshot>>([]);

    public Task<CodexSupervisionTaskSnapshot?> GetTaskForSessionAsync(long userId, string sessionId, CancellationToken cancellationToken)
        => Task.FromResult<CodexSupervisionTaskSnapshot?>(null);
}
