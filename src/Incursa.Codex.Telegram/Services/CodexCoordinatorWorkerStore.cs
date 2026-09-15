using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Stores the coordinator's bounded, redacted view of authenticated remote workers.
/// </summary>
internal interface ICodexCoordinatorWorkerStore
{
    Task<bool> UpsertAsync(CodexWorkerSnapshot worker, CancellationToken cancellationToken);

    Task<IReadOnlyList<CodexWorkerSnapshot>> ListAsync(CancellationToken cancellationToken);
}

internal sealed class CodexCoordinatorWorkerStore : ICodexCoordinatorWorkerStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _dataRoot;

    public CodexCoordinatorWorkerStore(IOptions<CodexTelegramOptions> options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        _dataRoot = GetDataRoot();
    }

    internal CodexCoordinatorWorkerStore(
        IOptions<CodexTelegramOptions> options,
        TimeProvider timeProvider,
        string dataRootOverride)
    {
        _options = options;
        _timeProvider = timeProvider;
        _dataRoot = Path.GetFullPath(dataRootOverride);
    }

    public async Task<bool> UpsertAsync(CodexWorkerSnapshot worker, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worker.WorkerId))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CoordinatorState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            RemoteWorkerRecord? existing = state.Workers.FirstOrDefault(candidate =>
                string.Equals(candidate.Worker.WorkerId, worker.WorkerId, StringComparison.Ordinal));
            if (existing is null && state.Workers.Count >= _options.Value.Coordinator.MaximumRegisteredWorkers)
            {
                return false;
            }

            RemoteWorkerRecord updated = new(worker with { IsRemote = true }, _timeProvider.GetUtcNow());
            if (existing is null)
            {
                state.Workers.Add(updated);
            }
            else
            {
                state.Workers[state.Workers.IndexOf(existing)] = updated;
            }

            state.Workers = state.Workers
                .OrderByDescending(record => record.ReceivedAtUtc)
                .Take(_options.Value.Coordinator.MaximumRegisteredWorkers)
                .ToList();
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CodexWorkerSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CoordinatorState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            TimeSpan staleAfter = TimeSpan.FromSeconds(Math.Max(30, _options.Value.Coordinator.HeartbeatIntervalSeconds * 3));
            return state.Workers
                .Select(record => IsStale(record, now, staleAfter) ? MarkStale(record.Worker) : record.Worker)
                .OrderBy(worker => worker.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static bool IsStale(RemoteWorkerRecord record, DateTimeOffset now, TimeSpan staleAfter)
        => now - record.ReceivedAtUtc > staleAfter;

    private static CodexWorkerSnapshot MarkStale(CodexWorkerSnapshot worker)
        => worker with
        {
            Readiness = "unavailable",
            Issues = [.. worker.Issues, "Coordinator heartbeat is stale."],
        };

    private async Task<CoordinatorState> LoadAsync(CancellationToken cancellationToken)
    {
        string path = GetStatePath();
        if (!File.Exists(path))
        {
            return new CoordinatorState { SchemaVersion = CurrentSchemaVersion };
        }

        await using FileStream stream = File.OpenRead(path);
        CoordinatorState? state = await JsonSerializer.DeserializeAsync<CoordinatorState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            throw new InvalidDataException("The coordinator worker state file was empty.");
        }

        state.Workers ??= [];
        return state;
    }

    private async Task SaveAsync(CoordinatorState state, CancellationToken cancellationToken)
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
    {
        string? configuredRoot = _options.Value.Workspace.DataRoot;
        return string.IsNullOrWhiteSpace(configuredRoot)
            ? CodexTelegramDataRoot.GetDefaultDataRoot(_options.Value.InstanceId)
            : Path.GetFullPath(configuredRoot);
    }

    private string GetStatePath() => Path.Combine(_dataRoot, "codex-coordinator-workers.json");

    private sealed class CoordinatorState
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<RemoteWorkerRecord> Workers { get; set; } = [];
    }

    private sealed record RemoteWorkerRecord(CodexWorkerSnapshot Worker, DateTimeOffset ReceivedAtUtc);
}
