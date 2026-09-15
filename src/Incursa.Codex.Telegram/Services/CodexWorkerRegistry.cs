using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal interface ICodexWorkerRegistry
{
    Task<CodexWorkerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    Task<CodexWorkerLease?> TryAcquireLeaseAsync(string taskId, TimeSpan lifetime, CancellationToken cancellationToken);

    Task<bool> ReleaseLeaseAsync(string taskId, CancellationToken cancellationToken);

    Task<bool> SetDrainingAsync(bool draining, CancellationToken cancellationToken);
}

internal enum CodexWorkerState
{
    Online,
    Draining,
}

internal sealed record CodexWorkerSnapshot(
    string WorkerId,
    string DisplayName,
    CodexWorkerState State,
    string Readiness,
    string Version,
    int ActiveLeaseCount,
    int MaximumConcurrentTasks,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastHeartbeatUtc,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Issues,
    bool IsRemote = false,
    string? ControlPlaneUrl = null);

internal sealed record CodexWorkerLease(
    string LeaseId,
    string TaskId,
    string WorkerId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

internal sealed class CodexWorkerRegistry : ICodexWorkerRegistry, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private static readonly TimeSpan DefaultLeaseLifetime = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly ICodexGateway _gateway;
    private readonly TimeProvider _timeProvider;
    private readonly string _dataRoot;

    public CodexWorkerRegistry(
        IOptions<CodexTelegramOptions> options,
        ICodexGateway gateway,
        TimeProvider timeProvider)
    {
        _options = options;
        _gateway = gateway;
        _timeProvider = timeProvider;
        _dataRoot = GetDataRoot();
    }

    internal CodexWorkerRegistry(
        IOptions<CodexTelegramOptions> options,
        ICodexGateway gateway,
        TimeProvider timeProvider,
        string dataRootOverride)
    {
        _options = options;
        _gateway = gateway;
        _timeProvider = timeProvider;
        _dataRoot = Path.GetFullPath(dataRootOverride);
    }

    public async Task<CodexWorkerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        WorkerState state;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            bool changed = RemoveExpiredLeases(state, now);
            DateTimeOffset previousHeartbeat = state.LastHeartbeatUtc;
            state.LastHeartbeatUtc = now;
            if (changed || previousHeartbeat != now)
            {
                await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        CodexRuntimeStateVm? runtime = null;
        List<string> issues = [];
        try
        {
            runtime = await _gateway.GetRuntimeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            issues.Add("Codex runtime status is unavailable.");
        }

        string readiness = state.Draining
            ? "draining"
            : runtime?.Initialized == true
                ? "ready"
                : "starting";
        if (runtime is { Initialized: false } && !string.IsNullOrWhiteSpace(runtime.Message))
        {
            issues.Add("Codex is still initializing.");
        }

        CodexWorkerOptions options = _options.Value.Worker;
        return new CodexWorkerSnapshot(
            state.WorkerId,
            state.DisplayName,
            state.Draining ? CodexWorkerState.Draining : CodexWorkerState.Online,
            readiness,
            typeof(CodexWorkerRegistry).Assembly.GetName().Version?.ToString() ?? "unknown",
            state.Leases.Count,
            options.MaxConcurrentTasks,
            state.RegisteredAtUtc,
            state.LastHeartbeatUtc,
            ["telegram-control", "codex-execution", "task-workspaces", "read-only-mini-app"],
            issues,
            false,
            options.ControlPlaneUrl);
    }

    public async Task<CodexWorkerLease?> TryAcquireLeaseAsync(
        string taskId,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkerState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            bool changed = RemoveExpiredLeases(state, now);
            WorkerLeaseRecord? existing = state.Leases.FirstOrDefault(lease => string.Equals(lease.TaskId, taskId.Trim(), StringComparison.Ordinal));
            if (existing is not null)
            {
                return existing.ToPublicRecord();
            }

            if (state.Draining || state.Leases.Count >= _options.Value.Worker.MaxConcurrentTasks)
            {
                if (changed)
                {
                    await SaveAsync(state, cancellationToken).ConfigureAwait(false);
                }

                return null;
            }

            TimeSpan boundedLifetime = lifetime <= TimeSpan.Zero ? DefaultLeaseLifetime : TimeSpan.FromMinutes(Math.Clamp(lifetime.TotalMinutes, 1, 1440));
            WorkerLeaseRecord record = new(
                $"lease:{Guid.NewGuid():N}",
                taskId.Trim(),
                state.WorkerId,
                now,
                now.Add(boundedLifetime));
            state.Leases.Add(record);
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return record.ToPublicRecord();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ReleaseLeaseAsync(string taskId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkerState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            bool removed = state.Leases.RemoveAll(lease => string.Equals(lease.TaskId, taskId.Trim(), StringComparison.Ordinal)) > 0;
            if (removed)
            {
                await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SetDrainingAsync(bool draining, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkerState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state.Draining == draining)
            {
                return false;
            }

            state.Draining = draining;
            state.LastHeartbeatUtc = _timeProvider.GetUtcNow();
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<WorkerState> LoadAsync(CancellationToken cancellationToken)
    {
        string path = GetStatePath();
        if (!File.Exists(path))
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            WorkerState created = new()
            {
                SchemaVersion = CurrentSchemaVersion,
                WorkerId = BuildWorkerId(),
                DisplayName = BuildDisplayName(),
                RegisteredAtUtc = now,
                LastHeartbeatUtc = now,
            };
            await SaveAsync(created, cancellationToken).ConfigureAwait(false);
            return created;
        }

        await using FileStream stream = File.OpenRead(path);
        WorkerState? state = await JsonSerializer.DeserializeAsync<WorkerState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            throw new InvalidDataException("The worker registration state file was empty.");
        }

        state.Leases ??= [];
        state.WorkerId = string.IsNullOrWhiteSpace(state.WorkerId) ? BuildWorkerId() : state.WorkerId;
        state.DisplayName = string.IsNullOrWhiteSpace(state.DisplayName) ? BuildDisplayName() : state.DisplayName;
        return state;
    }

    private async Task SaveAsync(WorkerState state, CancellationToken cancellationToken)
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

    private static bool RemoveExpiredLeases(WorkerState state, DateTimeOffset now)
        => state.Leases.RemoveAll(lease => lease.ExpiresAtUtc <= now) > 0;

    private string BuildWorkerId()
        => string.IsNullOrWhiteSpace(_options.Value.Worker.WorkerId)
            ? $"worker:{Guid.NewGuid():N}"
            : _options.Value.Worker.WorkerId.Trim();

    private string BuildDisplayName()
        => string.IsNullOrWhiteSpace(_options.Value.Worker.DisplayName)
            ? Environment.MachineName
            : _options.Value.Worker.DisplayName.Trim();

    private string GetDataRoot()
    {
        string? configuredRoot = _options.Value.Workspace.DataRoot;
        return string.IsNullOrWhiteSpace(configuredRoot)
            ? CodexTelegramDataRoot.GetDefaultDataRoot(_options.Value.InstanceId)
            : Path.GetFullPath(configuredRoot);
    }

    private string GetStatePath() => Path.Combine(_dataRoot, "codex-worker-state.json");

    private sealed class WorkerState
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string WorkerId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public DateTimeOffset RegisteredAtUtc { get; set; }
        public DateTimeOffset LastHeartbeatUtc { get; set; }
        public bool Draining { get; set; }
        public List<WorkerLeaseRecord> Leases { get; set; } = [];
    }

    private sealed class WorkerLeaseRecord
    {
        public WorkerLeaseRecord() { }

        public WorkerLeaseRecord(string leaseId, string taskId, string workerId, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc)
        {
            LeaseId = leaseId;
            TaskId = taskId;
            WorkerId = workerId;
            CreatedAtUtc = createdAtUtc;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string LeaseId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string WorkerId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }

        public CodexWorkerLease ToPublicRecord() => new(LeaseId, TaskId, WorkerId, CreatedAtUtc, ExpiresAtUtc);
    }
}
