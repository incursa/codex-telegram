using System.Text.Json;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal interface ITelegramMiniAppAcknowledgementStore
{
    Task<IReadOnlyList<TelegramMiniAppAcknowledgementSnapshot>> ListAsync(
        long userId,
        CancellationToken cancellationToken);

    Task<TelegramMiniAppAcknowledgementSnapshot?> AcknowledgeAsync(
        long userId,
        string taskId,
        string runId,
        string packetId,
        CancellationToken cancellationToken);
}

internal sealed record TelegramMiniAppAcknowledgementSnapshot(
    string TaskId,
    string RunId,
    string PacketId,
    DateTimeOffset AcknowledgedAtUtc);

/// <summary>
/// Persists only the operator's acknowledgement of a bounded Mini App review packet.
/// The task/run/packet tuple is the identity; no transcript or artifact payload is stored.
/// </summary>
internal sealed class TelegramMiniAppAcknowledgementStore : ITelegramMiniAppAcknowledgementStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumRecords = 2_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly IOptions<CodexTelegramOptions> _codexOptions;
    private readonly TimeProvider _timeProvider;
    private readonly string _dataRoot;

    public TelegramMiniAppAcknowledgementStore(
        IOptions<CodexTelegramOptions> codexOptions,
        TimeProvider timeProvider)
    {
        _codexOptions = codexOptions;
        _timeProvider = timeProvider;
        _dataRoot = GetDataRoot();
    }

    internal TelegramMiniAppAcknowledgementStore(
        IOptions<CodexTelegramOptions> codexOptions,
        TimeProvider timeProvider,
        string dataRootOverride)
    {
        _codexOptions = codexOptions;
        _timeProvider = timeProvider;
        _dataRoot = Path.GetFullPath(dataRootOverride);
    }

    public async Task<IReadOnlyList<TelegramMiniAppAcknowledgementSnapshot>> ListAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        if (userId <= 0)
        {
            return Array.Empty<TelegramMiniAppAcknowledgementSnapshot>();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AcknowledgementState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return state.Records
                .Where(record => record.UserId == userId)
                .OrderByDescending(record => record.AcknowledgedAtUtc)
                .Select(record => record.ToSnapshot())
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TelegramMiniAppAcknowledgementSnapshot?> AcknowledgeAsync(
        long userId,
        string taskId,
        string runId,
        string packetId,
        CancellationToken cancellationToken)
    {
        if (userId <= 0
            || !IsSafeToken(taskId, 160)
            || !IsSafeToken(runId, 160)
            || !IsSafeToken(packetId, 200))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AcknowledgementState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            AcknowledgementRecord? existing = state.Records.FirstOrDefault(record =>
                record.UserId == userId
                && string.Equals(record.TaskId, taskId, StringComparison.Ordinal)
                && string.Equals(record.RunId, runId, StringComparison.Ordinal)
                && string.Equals(record.PacketId, packetId, StringComparison.Ordinal));
            if (existing is not null)
            {
                return existing.ToSnapshot();
            }

            AcknowledgementRecord record = new(
                userId,
                taskId.Trim(),
                runId.Trim(),
                packetId.Trim(),
                _timeProvider.GetUtcNow());
            state.Records.Add(record);
            Trim(state);
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return record.ToSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<AcknowledgementState> LoadAsync(CancellationToken cancellationToken)
    {
        string path = GetStatePath();
        if (!File.Exists(path))
        {
            return new AcknowledgementState { SchemaVersion = CurrentSchemaVersion };
        }

        await using FileStream stream = File.OpenRead(path);
        AcknowledgementState? state = await JsonSerializer.DeserializeAsync<AcknowledgementState>(
            stream,
            _jsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            throw new InvalidDataException("The Mini App acknowledgement state file was empty.");
        }

        state.Records ??= [];
        return state;
    }

    private async Task SaveAsync(AcknowledgementState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataRoot);
        string path = GetStatePath();
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // The committed state is already durable; a temporary cleanup failure is harmless.
            }
        }
    }

    private static void Trim(AcknowledgementState state)
    {
        if (state.Records.Count <= MaximumRecords)
        {
            return;
        }

        state.Records = state.Records
            .OrderByDescending(record => record.AcknowledgedAtUtc)
            .Take(MaximumRecords)
            .ToList();
    }

    private string GetDataRoot()
    {
        string? configuredRoot = _codexOptions.Value.Workspace.DataRoot;
        return string.IsNullOrWhiteSpace(configuredRoot)
            ? CodexTelegramDataRoot.GetDefaultDataRoot(_codexOptions.Value.InstanceId)
            : Path.GetFullPath(configuredRoot);
    }

    private string GetStatePath() => Path.Combine(_dataRoot, "telegram-mini-app-acknowledgements.json");

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');

    private sealed class AcknowledgementState
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<AcknowledgementRecord> Records { get; set; } = [];
    }

    private sealed class AcknowledgementRecord
    {
        public AcknowledgementRecord() { }

        public AcknowledgementRecord(
            long userId,
            string taskId,
            string runId,
            string packetId,
            DateTimeOffset acknowledgedAtUtc)
        {
            UserId = userId;
            TaskId = taskId;
            RunId = runId;
            PacketId = packetId;
            AcknowledgedAtUtc = acknowledgedAtUtc;
        }

        public long UserId { get; set; }
        public string TaskId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public string PacketId { get; set; } = string.Empty;
        public DateTimeOffset AcknowledgedAtUtc { get; set; }

        public TelegramMiniAppAcknowledgementSnapshot ToSnapshot()
            => new(TaskId, RunId, PacketId, AcknowledgedAtUtc);
    }
}
