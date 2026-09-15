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
}

internal sealed class NullCodexSupervisionLedger : ICodexSupervisionLedger
{
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

    public Task<IReadOnlyList<CodexSupervisionTaskSnapshot>> ListTasksAsync(long userId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CodexSupervisionTaskSnapshot>>([]);

    public Task<CodexSupervisionTaskSnapshot?> GetTaskForSessionAsync(long userId, string sessionId, CancellationToken cancellationToken)
        => Task.FromResult<CodexSupervisionTaskSnapshot?>(null);
}
