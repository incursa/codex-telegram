using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal interface ITelegramMiniAppBrowserPairingStore
{
    Task<TelegramMiniAppBrowserPairingStart> StartAsync(CancellationToken cancellationToken);

    Task<TelegramMiniAppBrowserPairingStatus> GetPairingStatusAsync(string pairingToken, CancellationToken cancellationToken);

    Task<bool> ApproveAsync(string pairingCode, long userId, CancellationToken cancellationToken);

    Task<int> RevokeAsync(long userId, CancellationToken cancellationToken);

    Task<long?> AuthenticateSessionAsync(string sessionToken, CancellationToken cancellationToken);

    Task<IReadOnlyList<TelegramMiniAppBrowserSessionSummary>> ListSessionsAsync(long userId, CancellationToken cancellationToken);
}

internal sealed record TelegramMiniAppBrowserPairingStart(
    string PairingId,
    string PairingCode,
    string PairingToken,
    DateTimeOffset ExpiresAtUtc);

internal sealed record TelegramMiniAppBrowserPairingStatus(
    string State,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? SessionExpiresAtUtc,
    string? SessionToken);

internal sealed record TelegramMiniAppBrowserSessionSummary(
    string PairingId,
    DateTimeOffset ApprovedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool Revoked);

internal sealed class TelegramMiniAppBrowserPairingStore : ITelegramMiniAppBrowserPairingStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumRecords = 200;
    private const int PairingCodeLength = 12;
    private static readonly char[] PairingAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IOptions<CodexTelegramOptions> _codexOptions;
    private readonly IOptions<TelegramMiniAppOptions> _miniAppOptions;
    private readonly TimeProvider _timeProvider;
    private readonly string _dataRoot;

    public TelegramMiniAppBrowserPairingStore(
        IOptions<CodexTelegramOptions> codexOptions,
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        TimeProvider timeProvider)
    {
        _codexOptions = codexOptions;
        _miniAppOptions = miniAppOptions;
        _timeProvider = timeProvider;
        _dataRoot = GetDataRoot();
    }

    internal TelegramMiniAppBrowserPairingStore(
        IOptions<CodexTelegramOptions> codexOptions,
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        TimeProvider timeProvider,
        string dataRootOverride)
    {
        _codexOptions = codexOptions;
        _miniAppOptions = miniAppOptions;
        _timeProvider = timeProvider;
        _dataRoot = Path.GetFullPath(dataRootOverride);
    }

    public async Task<TelegramMiniAppBrowserPairingStart> StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PairingState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            Prune(state, now);
            string pairingId = $"pairing:{Guid.NewGuid():N}";
            string pairingCode = CreatePairingCode();
            string pairingToken = CreateToken();
            PairingRecord record = new(
                pairingId,
                Hash(pairingCode),
                Hash(pairingToken),
                now,
                now.AddMinutes(_miniAppOptions.Value.BrowserPairingLifetimeMinutes));
            state.Records.Add(record);
            Trim(state);
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new TelegramMiniAppBrowserPairingStart(pairingId, pairingCode, pairingToken, record.ExpiresAtUtc);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TelegramMiniAppBrowserPairingStatus> GetPairingStatusAsync(
        string pairingToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pairingToken))
        {
            return new TelegramMiniAppBrowserPairingStatus("unknown", DateTimeOffset.MinValue, null, null);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PairingState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            PairingRecord? record = state.Records.FirstOrDefault(candidate => HashEquals(candidate.TokenHash, pairingToken.Trim()));
            if (record is null)
            {
                return new TelegramMiniAppBrowserPairingStatus("unknown", DateTimeOffset.MinValue, null, null);
            }

            if (record.RevokedAtUtc.HasValue
                || (record.SessionExpiresAtUtc.HasValue && record.SessionExpiresAtUtc.Value <= now)
                || (!record.UserId.HasValue && record.ExpiresAtUtc <= now))
            {
                return new TelegramMiniAppBrowserPairingStatus("expired", record.ExpiresAtUtc, record.SessionExpiresAtUtc, null);
            }

            return record.UserId.HasValue
                ? new TelegramMiniAppBrowserPairingStatus("approved", record.ExpiresAtUtc, record.SessionExpiresAtUtc, pairingToken.Trim())
                : new TelegramMiniAppBrowserPairingStatus("pending", record.ExpiresAtUtc, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ApproveAsync(
        string pairingCode,
        long userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pairingCode) || userId <= 0)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PairingState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            PairingRecord? record = state.Records.FirstOrDefault(candidate =>
                !candidate.UserId.HasValue
                && !candidate.RevokedAtUtc.HasValue
                && candidate.ExpiresAtUtc > now
                && HashEquals(candidate.CodeHash, pairingCode.Trim()));
            if (record is null)
            {
                return false;
            }

            record.UserId = userId;
            record.ApprovedAtUtc = now;
            record.SessionExpiresAtUtc = now.AddHours(_miniAppOptions.Value.BrowserSessionLifetimeHours);
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> RevokeAsync(long userId, CancellationToken cancellationToken)
    {
        if (userId <= 0)
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PairingState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            int revoked = 0;
            foreach (PairingRecord record in state.Records.Where(candidate => candidate.UserId == userId && !candidate.RevokedAtUtc.HasValue))
            {
                record.RevokedAtUtc = now;
                revoked++;
            }

            if (revoked > 0)
            {
                await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }

            return revoked;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long?> AuthenticateSessionAsync(string sessionToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PairingState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            PairingRecord? record = state.Records.FirstOrDefault(candidate =>
                candidate.UserId.HasValue
                && !candidate.RevokedAtUtc.HasValue
                && candidate.SessionExpiresAtUtc > now
                && HashEquals(candidate.TokenHash, sessionToken.Trim()));
            return record?.UserId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TelegramMiniAppBrowserSessionSummary>> ListSessionsAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PairingState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return state.Records
                .Where(record => record.UserId == userId && record.ApprovedAtUtc.HasValue && record.SessionExpiresAtUtc.HasValue)
                .OrderByDescending(record => record.ApprovedAtUtc)
                .Select(record => new TelegramMiniAppBrowserSessionSummary(
                    record.PairingId,
                    record.ApprovedAtUtc!.Value,
                    record.SessionExpiresAtUtc!.Value,
                    record.RevokedAtUtc.HasValue))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<PairingState> LoadAsync(CancellationToken cancellationToken)
    {
        string path = GetStatePath();
        if (!File.Exists(path))
        {
            return new PairingState { SchemaVersion = CurrentSchemaVersion };
        }

        await using FileStream stream = File.OpenRead(path);
        PairingState? state = await JsonSerializer.DeserializeAsync<PairingState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            throw new InvalidDataException("The Mini App browser pairing state file was empty.");
        }

        state.Records ??= [];
        return state;
    }

    private async Task SaveAsync(PairingState state, CancellationToken cancellationToken)
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

    private static void Prune(PairingState state, DateTimeOffset now)
    {
        state.Records.RemoveAll(record =>
            (record.ExpiresAtUtc <= now && !record.UserId.HasValue)
            || (record.SessionExpiresAtUtc.HasValue && record.SessionExpiresAtUtc.Value <= now.AddDays(-7)));
    }

    private static void Trim(PairingState state)
    {
        if (state.Records.Count <= MaximumRecords)
        {
            return;
        }

        state.Records = state.Records
            .OrderByDescending(record => record.CreatedAtUtc)
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

    private string GetStatePath() => Path.Combine(_dataRoot, "telegram-mini-app-browser-pairing.json");

    private static string CreatePairingCode()
    {
        char[] code = new char[PairingCodeLength];
        byte[] random = RandomNumberGenerator.GetBytes(PairingCodeLength * 2);
        int output = 0;
        foreach (byte value in random)
        {
            if (value >= byte.MaxValue - (byte.MaxValue % PairingAlphabet.Length))
            {
                continue;
            }

            code[output++] = PairingAlphabet[value % PairingAlphabet.Length];
            if (output == code.Length)
            {
                break;
            }
        }

        return new string(code);
    }

    private static string CreateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool HashEquals(string expectedHash, string value)
    {
        byte[] expected = Encoding.ASCII.GetBytes(expectedHash);
        byte[] actual = Encoding.ASCII.GetBytes(Hash(value));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private sealed class PairingState
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<PairingRecord> Records { get; set; } = [];
    }

    private sealed class PairingRecord
    {
        public PairingRecord() { }

        public PairingRecord(
            string pairingId,
            string codeHash,
            string tokenHash,
            DateTimeOffset createdAtUtc,
            DateTimeOffset expiresAtUtc)
        {
            PairingId = pairingId;
            CodeHash = codeHash;
            TokenHash = tokenHash;
            CreatedAtUtc = createdAtUtc;
            ExpiresAtUtc = expiresAtUtc;
        }

        public string PairingId { get; set; } = string.Empty;
        public string CodeHash { get; set; } = string.Empty;
        public string TokenHash { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public long? UserId { get; set; }
        public DateTimeOffset? ApprovedAtUtc { get; set; }
        public DateTimeOffset? SessionExpiresAtUtc { get; set; }
        public DateTimeOffset? RevokedAtUtc { get; set; }
    }
}
