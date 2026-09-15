using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal interface ICodexFleetRolloutStore
{
    Task<CodexFleetRolloutSnapshot> CreateAsync(
        long ownerUserId,
        string targetVersion,
        string expectedSha256,
        IReadOnlyList<string> requiredCapabilities,
        IReadOnlyList<string> workerIds,
        CancellationToken cancellationToken);

    Task<CodexFleetRolloutSnapshot?> GetAsync(long ownerUserId, string rolloutId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CodexFleetRolloutSnapshot>> ListAsync(long ownerUserId, CancellationToken cancellationToken);

    Task<CodexFleetRolloutSnapshot?> UpdateWorkerAsync(
        long ownerUserId,
        string rolloutId,
        string workerId,
        CodexFleetRolloutWorkerState state,
        string outcomeCode,
        CancellationToken cancellationToken);
}

internal enum CodexFleetRolloutState
{
    Planned,
    Staging,
    AwaitingInstaller,
    Completed,
    RollbackRequired,
    RollbackAwaitingInstaller,
    RolledBack,
}

internal enum CodexFleetRolloutWorkerState
{
    Pending,
    Blocked,
    Staged,
    Active,
    HealthFailed,
    RollbackStaged,
    RollbackActive,
}

internal sealed record CodexFleetRolloutWorkerSnapshot(
    string WorkerId,
    int Sequence,
    CodexFleetRolloutWorkerState State,
    DateTimeOffset UpdatedAtUtc,
    string? OutcomeCode);

internal sealed record CodexFleetRolloutSnapshot(
    string RolloutId,
    long OwnerUserId,
    string TargetVersion,
    string ExpectedSha256,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<CodexFleetRolloutWorkerSnapshot> Workers,
    CodexFleetRolloutState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? OutcomeCode);

/// <summary>
/// Persists bounded rollout intent and per-worker evidence. It never stores
/// package paths, credentials, private files, or installer commands.
/// </summary>
internal sealed class CodexFleetRolloutStore : ICodexFleetRolloutStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumRollouts = 100;
    private const int MaximumWorkersPerRollout = 32;
    private const int MaximumCapabilities = 24;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _dataRoot;

    public CodexFleetRolloutStore(IOptions<CodexTelegramOptions> options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        _dataRoot = GetDataRoot();
    }

    internal CodexFleetRolloutStore(IOptions<CodexTelegramOptions> options, TimeProvider timeProvider, string dataRootOverride)
    {
        _options = options;
        _timeProvider = timeProvider;
        _dataRoot = Path.GetFullPath(dataRootOverride);
    }

    public async Task<CodexFleetRolloutSnapshot> CreateAsync(
        long ownerUserId,
        string targetVersion,
        string expectedSha256,
        IReadOnlyList<string> requiredCapabilities,
        IReadOnlyList<string> workerIds,
        CancellationToken cancellationToken)
    {
        if (ownerUserId == 0
            || string.IsNullOrWhiteSpace(targetVersion)
            || string.IsNullOrWhiteSpace(expectedSha256)
            || workerIds.Count is < 1 or > MaximumWorkersPerRollout
            || requiredCapabilities.Count > MaximumCapabilities)
        {
            throw new ArgumentException("The fleet rollout metadata is invalid.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RolloutState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            CodexFleetRolloutRecord record = new()
            {
                RolloutId = $"rollout:{Guid.NewGuid():N}",
                OwnerUserId = ownerUserId,
                TargetVersion = targetVersion.Trim(),
                ExpectedSha256 = expectedSha256.Trim().ToLowerInvariant(),
                RequiredCapabilities = requiredCapabilities.ToList(),
                Workers = workerIds
                    .Select((workerId, index) => new CodexFleetRolloutWorkerRecord
                    {
                        WorkerId = workerId.Trim(),
                        Sequence = index + 1,
                        State = CodexFleetRolloutWorkerState.Pending,
                        UpdatedAtUtc = now,
                    })
                    .ToList(),
                State = CodexFleetRolloutState.Planned,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                OutcomeCode = "rollout_planned",
            };
            state.Rollouts.Add(record);
            state.Rollouts = state.Rollouts
                .OrderByDescending(rollout => rollout.UpdatedAtUtc)
                .Take(MaximumRollouts)
                .ToList();
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return record.ToSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexFleetRolloutSnapshot?> GetAsync(long ownerUserId, string rolloutId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RolloutState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return state.Rollouts
                .FirstOrDefault(rollout => rollout.OwnerUserId == ownerUserId
                    && string.Equals(rollout.RolloutId, rolloutId, StringComparison.Ordinal))
                ?.ToSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CodexFleetRolloutSnapshot>> ListAsync(long ownerUserId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RolloutState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return state.Rollouts
                .Where(rollout => rollout.OwnerUserId == ownerUserId)
                .OrderByDescending(rollout => rollout.UpdatedAtUtc)
                .Take(20)
                .Select(rollout => rollout.ToSnapshot())
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexFleetRolloutSnapshot?> UpdateWorkerAsync(
        long ownerUserId,
        string rolloutId,
        string workerId,
        CodexFleetRolloutWorkerState workerState,
        string outcomeCode,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RolloutState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            CodexFleetRolloutRecord? rollout = state.Rollouts.FirstOrDefault(candidate => candidate.OwnerUserId == ownerUserId
                && string.Equals(candidate.RolloutId, rolloutId, StringComparison.Ordinal));
            CodexFleetRolloutWorkerRecord? worker = rollout?.Workers.FirstOrDefault(candidate => string.Equals(candidate.WorkerId, workerId, StringComparison.Ordinal));
            if (rollout is null || worker is null)
            {
                return null;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            worker.State = workerState;
            worker.UpdatedAtUtc = now;
            worker.OutcomeCode = outcomeCode;
            rollout.State = ResolveState(rollout.Workers);
            rollout.UpdatedAtUtc = now;
            rollout.OutcomeCode = outcomeCode;
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return rollout.ToSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<RolloutState> LoadAsync(CancellationToken cancellationToken)
    {
        string path = GetStatePath();
        if (!File.Exists(path))
        {
            return new RolloutState { SchemaVersion = CurrentSchemaVersion };
        }

        await using FileStream stream = File.OpenRead(path);
        RolloutState? state = await JsonSerializer.DeserializeAsync<RolloutState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            throw new InvalidDataException("The fleet rollout state file was empty.");
        }

        state.Rollouts ??= [];
        foreach (CodexFleetRolloutRecord rollout in state.Rollouts)
        {
            rollout.RequiredCapabilities ??= [];
            rollout.Workers ??= [];
        }

        return state;
    }

    private async Task SaveAsync(RolloutState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataRoot);
        string path = GetStatePath();
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private string GetDataRoot()
        => string.IsNullOrWhiteSpace(_options.Value.Workspace.DataRoot)
            ? CodexTelegramDataRoot.GetDefaultDataRoot(_options.Value.InstanceId)
            : Path.GetFullPath(_options.Value.Workspace.DataRoot);

    private string GetStatePath() => Path.Combine(_dataRoot, "codex-fleet-rollouts.json");

    private static CodexFleetRolloutState ResolveState(IReadOnlyList<CodexFleetRolloutWorkerRecord> workers)
    {
        if (workers.Any(worker => worker.State == CodexFleetRolloutWorkerState.HealthFailed))
        {
            return CodexFleetRolloutState.RollbackRequired;
        }

        if (workers.All(worker => worker.State == CodexFleetRolloutWorkerState.Active))
        {
            return CodexFleetRolloutState.Completed;
        }

        if (workers.All(worker => worker.State is CodexFleetRolloutWorkerState.RollbackActive or CodexFleetRolloutWorkerState.Pending)
            && workers.Any(worker => worker.State == CodexFleetRolloutWorkerState.RollbackActive))
        {
            return CodexFleetRolloutState.RolledBack;
        }

        if (workers.Any(worker => worker.State == CodexFleetRolloutWorkerState.RollbackStaged))
        {
            return CodexFleetRolloutState.RollbackAwaitingInstaller;
        }

        if (workers.Any(worker => worker.State == CodexFleetRolloutWorkerState.Staged))
        {
            return CodexFleetRolloutState.AwaitingInstaller;
        }

        return workers.Any(worker => worker.State != CodexFleetRolloutWorkerState.Pending)
            ? CodexFleetRolloutState.Staging
            : CodexFleetRolloutState.Planned;
    }

    private sealed class RolloutState
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<CodexFleetRolloutRecord> Rollouts { get; set; } = [];
    }

    private sealed class CodexFleetRolloutRecord
    {
        public string RolloutId { get; set; } = string.Empty;
        public long OwnerUserId { get; set; }
        public string TargetVersion { get; set; } = string.Empty;
        public string ExpectedSha256 { get; set; } = string.Empty;
        public List<string> RequiredCapabilities { get; set; } = [];
        public List<CodexFleetRolloutWorkerRecord> Workers { get; set; } = [];
        public CodexFleetRolloutState State { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public string? OutcomeCode { get; set; }

        public CodexFleetRolloutSnapshot ToSnapshot()
            => new(RolloutId, OwnerUserId, TargetVersion, ExpectedSha256, RequiredCapabilities.ToArray(), Workers.OrderBy(worker => worker.Sequence).Select(worker => worker.ToSnapshot()).ToArray(), State, CreatedAtUtc, UpdatedAtUtc, OutcomeCode);
    }

    private sealed class CodexFleetRolloutWorkerRecord
    {
        public string WorkerId { get; set; } = string.Empty;
        public int Sequence { get; set; }
        public CodexFleetRolloutWorkerState State { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public string? OutcomeCode { get; set; }

        public CodexFleetRolloutWorkerSnapshot ToSnapshot()
            => new(WorkerId, Sequence, State, UpdatedAtUtc, OutcomeCode);
    }
}
