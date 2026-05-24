using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

internal sealed record TelegramDebugTraceEvent(
    string TraceId,
    DateTimeOffset TimestampUtc,
    string Kind,
    string? SessionId = null,
    string? TurnId = null,
    long? ChatId = null,
    int? MessageThreadId = null,
    int? SourceMessageId = null,
    long? UserId = null,
    string? BundleId = null,
    string? Direction = null,
    string? Status = null,
    int? TextLength = null,
    int? AttachmentCount = null,
    int? InputItemCount = null,
    string? OutboundQueueItemId = null,
    int? ChunkCount = null,
    int? ChunkLength = null,
    int? CompactedCount = null,
    string? Error = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? TextBody = null,
    string? Source = null);

internal enum TelegramDebugCaptureMode
{
    Off,
    Metadata,
    Full,
}

internal sealed record TelegramDebugCaptureStatus(
    TelegramDebugCaptureMode Mode,
    bool FileTraceEnabled,
    bool FullCaptureEnabled,
    bool? RuntimeEnabledOverride,
    bool? RuntimeFullCaptureOverride,
    DateTimeOffset? FullCaptureExpiresUtc,
    string? LatestTraceId);

internal sealed record TelegramTurnDiagnostics(
    string? SessionId,
    string? TurnId,
    string? TraceId,
    bool TelegramInputReceived,
    bool TelegramInputBundled,
    bool TelegramInputQueued,
    bool TelegramInputSent,
    bool TelegramInputSteered,
    bool CodexRequestStarted,
    bool CodexTurnStarted,
    bool CodexSteerCalled,
    bool TerminalEventSeen,
    string? TerminalEventType,
    bool FinalAssistantOutputCaptured,
    int CodexEventsReceived,
    int AssistantOutputCharsReceived,
    int TelegramOutboundCharsQueued,
    int TelegramChunksSent,
    int TelegramCharsSent,
    int PendingChunks,
    int CompactedCount,
    int SendFailureCount,
    int RateLimitCount,
    int TimeoutCount,
    string? LastError)
{
    public bool Compacted => CompactedCount > 0;

    public bool SendFailed => SendFailureCount > 0;

    public bool RateLimited => RateLimitCount > 0;

    public bool SendTimedOut => TimeoutCount > 0;

    public string LikelyStatus
    {
        get
        {
            if (SendFailed || RateLimited || SendTimedOut || !string.IsNullOrWhiteSpace(LastError))
            {
                return "Telegram delivery delayed";
            }

            if (!TerminalEventSeen && (CodexRequestStarted || CodexTurnStarted || CodexSteerCalled))
            {
                return "Codex is working";
            }

            if (TerminalEventType?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "Codex failed";
            }

            if (PendingChunks > 0)
            {
                return TerminalEventSeen
                    ? "Codex finished; sending remaining Telegram output"
                    : "Telegram output queued; Codex completion unknown";
            }

            if (Compacted)
            {
                return "Output compacted; open trace/history for full details";
            }

            return TerminalEventSeen
                ? "Codex finished; Telegram delivery complete"
                : "Unknown, inspect trace";
        }
    }
}

internal interface ITelegramDebugTraceStore
{
    bool IsFileTraceEnabled { get; }

    bool IsFullCaptureEnabled { get; }

    bool? RuntimeEnabledOverride { get; }

    bool? RuntimeFullCaptureOverride { get; }

    DateTimeOffset? FullCaptureExpiresUtc { get; }

    TelegramDebugCaptureStatus CaptureStatus { get; }

    string CreateTraceId();

    void SetRuntimeEnabledOverride(bool enabled);

    void ClearRuntimeEnabledOverride();

    void EnableMetadataCapture();

    void DisableCapture();

    void EnableFullCapture(TimeSpan? ttl = null);

    void DisableFullCapture();

    Task RecordAsync(TelegramDebugTraceEvent evt, CancellationToken cancellationToken);

    Task BindTurnAsync(string traceId, string sessionId, string turnId, CancellationToken cancellationToken);

    string? TryGetTraceIdForTurn(string sessionId, string? turnId);

    string? LatestTraceId { get; }

    TelegramTurnDiagnostics GetDiagnostics(string? traceId = null, string? sessionId = null, string? turnId = null);
}

internal sealed class TelegramDebugTraceStore : ITelegramDebugTraceStore
{
    private static readonly Regex SecretAssignmentRegex = new(
        @"(?i)\b((?:authorization|api[_-]?key|token|secret|password|cookie)\s*[:=]\s*)([^\s;,'""]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerRegex = new(
        @"(?i)\b(Bearer\s+)([A-Za-z0-9._~+/=-]{12,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpenAiKeyRegex = new(
        @"\bsk-[A-Za-z0-9_-]{16,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SlackTokenRegex = new(
        @"\bxox[aboprs]-[A-Za-z0-9-]{12,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TelegramBotTokenRegex = new(
        @"\b\d{6,}:[A-Za-z0-9_-]{24,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TelegramTraceAccumulator> _traces = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _turnTraceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptions<CodexTelegramOptions> _codexOptions;
    private readonly IOptions<TelegramDebugTraceOptions> _traceOptions;
    private readonly TimeProvider _timeProvider;
    private readonly object _runtimeGate = new();
    private string? _latestTraceId;
    private bool? _runtimeEnabledOverride;
    private bool? _runtimeFullCaptureOverride;
    private DateTimeOffset? _fullCaptureExpiresUtc;
    private DateTimeOffset _lastRetentionCleanupUtc = DateTimeOffset.MinValue;

    public TelegramDebugTraceStore(
        IOptions<CodexTelegramOptions> codexOptions,
        IOptions<TelegramDebugTraceOptions> traceOptions,
        TimeProvider? timeProvider = null)
    {
        _codexOptions = codexOptions;
        _traceOptions = traceOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string? LatestTraceId => _latestTraceId;

    public bool IsFileTraceEnabled => RuntimeEnabledOverride ?? _traceOptions.Value.Enabled;

    public bool IsFullCaptureEnabled
    {
        get
        {
            lock (_runtimeGate)
            {
                ExpireFullCaptureIfNeededLocked();
                return _runtimeFullCaptureOverride
                    ?? (_traceOptions.Value.CaptureInputText || _traceOptions.Value.CaptureOutputText);
            }
        }
    }

    public bool? RuntimeEnabledOverride
    {
        get
        {
            lock (_runtimeGate)
            {
                return _runtimeEnabledOverride;
            }
        }
    }

    public bool? RuntimeFullCaptureOverride
    {
        get
        {
            lock (_runtimeGate)
            {
                ExpireFullCaptureIfNeededLocked();
                return _runtimeFullCaptureOverride;
            }
        }
    }

    public DateTimeOffset? FullCaptureExpiresUtc
    {
        get
        {
            lock (_runtimeGate)
            {
                ExpireFullCaptureIfNeededLocked();
                return _fullCaptureExpiresUtc;
            }
        }
    }

    public TelegramDebugCaptureStatus CaptureStatus
    {
        get
        {
            bool fileEnabled = IsFileTraceEnabled;
            bool fullEnabled = IsFullCaptureEnabled;
            return new TelegramDebugCaptureStatus(
                fileEnabled ? fullEnabled ? TelegramDebugCaptureMode.Full : TelegramDebugCaptureMode.Metadata : TelegramDebugCaptureMode.Off,
                fileEnabled,
                fullEnabled,
                RuntimeEnabledOverride,
                RuntimeFullCaptureOverride,
                FullCaptureExpiresUtc,
                LatestTraceId);
        }
    }

    public void SetRuntimeEnabledOverride(bool enabled)
    {
        lock (_runtimeGate)
        {
            _runtimeEnabledOverride = enabled;
        }
    }

    public void EnableMetadataCapture()
    {
        lock (_runtimeGate)
        {
            _runtimeEnabledOverride = true;
            _runtimeFullCaptureOverride = false;
            _fullCaptureExpiresUtc = null;
        }
    }

    public void DisableCapture()
    {
        lock (_runtimeGate)
        {
            _runtimeEnabledOverride = false;
            _runtimeFullCaptureOverride = false;
            _fullCaptureExpiresUtc = null;
        }
    }

    public void EnableFullCapture(TimeSpan? ttl = null)
    {
        lock (_runtimeGate)
        {
            _runtimeEnabledOverride = true;
            _runtimeFullCaptureOverride = true;
            TimeSpan effectiveTtl = ttl ?? TimeSpan.FromMinutes(Math.Clamp(
                _traceOptions.Value.FullCaptureTtlMinutes,
                TelegramDebugTraceLimits.MinFullCaptureTtlMinutes,
                TelegramDebugTraceLimits.MaxFullCaptureTtlMinutes));
            if (effectiveTtl <= TimeSpan.Zero)
            {
                effectiveTtl = TimeSpan.FromMinutes(TelegramDebugTraceLimits.MinFullCaptureTtlMinutes);
            }

            _fullCaptureExpiresUtc = _timeProvider.GetUtcNow() + effectiveTtl;
        }
    }

    public void DisableFullCapture()
    {
        lock (_runtimeGate)
        {
            _runtimeFullCaptureOverride = false;
            _fullCaptureExpiresUtc = null;
        }
    }

    public void ClearRuntimeEnabledOverride()
    {
        lock (_runtimeGate)
        {
            _runtimeEnabledOverride = null;
        }
    }

    public string CreateTraceId()
    {
        string traceId = Guid.NewGuid().ToString("n");
        _latestTraceId = traceId;
        _traces.TryAdd(traceId, new TelegramTraceAccumulator(traceId));
        return traceId;
    }

    public async Task RecordAsync(TelegramDebugTraceEvent evt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(evt.TraceId))
        {
            return;
        }

        TelegramDebugTraceEvent memoryEvent = RedactSensitiveFields(
            evt,
            includeBody: false,
            includeAttachmentMetadata: _traceOptions.Value.CaptureAttachmentMetadata);
        _latestTraceId = memoryEvent.TraceId;
        TelegramTraceAccumulator accumulator = _traces.GetOrAdd(memoryEvent.TraceId, traceId => new TelegramTraceAccumulator(traceId));
        accumulator.Apply(memoryEvent);

        if (!IsFileTraceEnabled)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        CleanupRetainedTraceFiles(now);
        TelegramDebugTraceEvent fileEvent = SanitizeForFile(evt);
        string path = GetTracePath(memoryEvent.TraceId, memoryEvent.TimestampUtc);
        long maxBytes = Math.Clamp(
            _traceOptions.Value.MaxTraceFileBytes,
            TelegramDebugTraceLimits.MinTraceFileBytes,
            TelegramDebugTraceLimits.MaxTraceFileBytes);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string line = JsonSerializer.Serialize(fileEvent, _jsonOptions);
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FileInfo? existing = File.Exists(path) ? new FileInfo(path) : null;
            if (existing is not null && existing.Length >= maxBytes)
            {
                return;
            }

            await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task BindTurnAsync(string traceId, string sessionId, string turnId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(traceId) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        _turnTraceIds[CreateTurnKey(sessionId, turnId)] = traceId;
        await RecordAsync(
            new TelegramDebugTraceEvent(
                traceId,
                DateTimeOffset.UtcNow,
                "codex.turn.bound",
                SessionId: sessionId,
                TurnId: turnId),
            cancellationToken).ConfigureAwait(false);
    }

    public string? TryGetTraceIdForTurn(string sessionId, string? turnId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(turnId))
        {
            return null;
        }

        return _turnTraceIds.TryGetValue(CreateTurnKey(sessionId, turnId), out string? traceId)
            ? traceId
            : null;
    }

    public TelegramTurnDiagnostics GetDiagnostics(string? traceId = null, string? sessionId = null, string? turnId = null)
    {
        TelegramTraceAccumulator? accumulator = null;
        if (!string.IsNullOrWhiteSpace(traceId))
        {
            _traces.TryGetValue(traceId, out accumulator);
        }

        if (accumulator is null && !string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(turnId))
        {
            string? resolvedTraceId = TryGetTraceIdForTurn(sessionId, turnId);
            if (!string.IsNullOrWhiteSpace(resolvedTraceId))
            {
                _traces.TryGetValue(resolvedTraceId, out accumulator);
            }
        }

        bool useLatestTrace = string.IsNullOrWhiteSpace(traceId)
            && string.IsNullOrWhiteSpace(sessionId)
            && string.IsNullOrWhiteSpace(turnId);
        if (accumulator is null && useLatestTrace && !string.IsNullOrWhiteSpace(_latestTraceId))
        {
            _traces.TryGetValue(_latestTraceId, out accumulator);
        }

        return accumulator?.ToDiagnostics()
            ?? new TelegramTurnDiagnostics(sessionId, turnId, traceId, false, false, false, false, false, false, false, false, false, null, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);
    }

    internal string GetTracePath(string traceId, DateTimeOffset timestampUtc)
    {
        return Path.Combine(
            GetTraceRoot(),
            timestampUtc.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
            traceId + ".jsonl");
    }

    private TelegramDebugTraceEvent SanitizeForFile(TelegramDebugTraceEvent evt)
    {
        TelegramDebugTraceEvent sanitized = RedactSensitiveFields(
            evt,
            includeBody: IsFullCaptureEnabled,
            includeAttachmentMetadata: _traceOptions.Value.CaptureAttachmentMetadata);
        if (!IsFullCaptureEnabled || !_traceOptions.Value.CaptureAttachmentCopies)
        {
            return sanitized;
        }

        IReadOnlyDictionary<string, string> attachmentCopyMetadata = CaptureAttachmentCopies(evt);
        if (attachmentCopyMetadata.Count == 0)
        {
            return sanitized;
        }

        Dictionary<string, string> metadata = sanitized.Metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(sanitized.Metadata, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in attachmentCopyMetadata)
        {
            metadata[pair.Key] = Redact(pair.Value) ?? string.Empty;
        }

        return sanitized with { Metadata = metadata };
    }

    private static TelegramDebugTraceEvent RedactSensitiveFields(
        TelegramDebugTraceEvent evt,
        bool includeBody,
        bool includeAttachmentMetadata)
    {
        IReadOnlyDictionary<string, string>? metadata = evt.Metadata is null
            ? null
            : evt.Metadata
                .Where(pair => includeAttachmentMetadata || !IsAttachmentMetadataKey(pair.Key))
                .ToDictionary(pair => Redact(pair.Key) ?? string.Empty, pair => Redact(pair.Value) ?? string.Empty, StringComparer.Ordinal);
        return evt with
        {
            Error = Redact(evt.Error),
            Metadata = metadata,
            TextBody = includeBody ? Redact(evt.TextBody) : null,
            Source = string.IsNullOrWhiteSpace(evt.Source) ? InferSource(evt.Kind, evt.Direction) : Redact(evt.Source),
        };
    }

    private void ExpireFullCaptureIfNeededLocked()
    {
        if (_runtimeFullCaptureOverride != true || _fullCaptureExpiresUtc is null)
        {
            return;
        }

        if (_timeProvider.GetUtcNow() >= _fullCaptureExpiresUtc.Value)
        {
            _runtimeFullCaptureOverride = false;
            _fullCaptureExpiresUtc = null;
        }
    }

    private void CleanupRetainedTraceFiles(DateTimeOffset now)
    {
        if (now - _lastRetentionCleanupUtc < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastRetentionCleanupUtc = now;
        int retentionDays = Math.Clamp(
            _traceOptions.Value.RetentionDays,
            TelegramDebugTraceLimits.MinRetentionDays,
            TelegramDebugTraceLimits.MaxRetentionDays);
        DateTimeOffset cutoff = now.AddDays(-retentionDays);
        string root = GetTraceRoot();
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(directory);
            if (!DateTimeOffset.TryParseExact(
                name,
                "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTimeOffset traceDate))
            {
                continue;
            }

            if (traceDate.UtcDateTime.Date >= cutoff.UtcDateTime.Date)
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Retention cleanup is best effort; trace writing must never fail because cleanup did.
            }
        }
    }

    private string GetTraceRoot()
    {
        string root = _traceOptions.Value.TraceDirectory ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(root))
        {
            return Path.GetFullPath(root);
        }

        string? configuredDataRoot = _codexOptions.Value.Workspace.DataRoot;
        return string.IsNullOrWhiteSpace(configuredDataRoot)
            ? Path.Combine(AppContext.BaseDirectory, "App_Data", "codex-telegram", "telegram-traces")
            : Path.Combine(configuredDataRoot, "telegram-traces");
    }

    private IReadOnlyDictionary<string, string> CaptureAttachmentCopies(TelegramDebugTraceEvent evt)
    {
        List<string> paths = ResolveAttachmentCopySources(evt.Metadata).ToList();
        if (paths.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["attachmentCopySourceCount"] = paths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        string directory = GetTraceAttachmentDirectory(evt.TraceId, evt.TimestampUtc);
        int copyIndex = 0;
        int failureIndex = 0;
        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                failureIndex++;
                metadata[$"attachmentCopyFailure.{failureIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}"] = $"missing:{path}";
                continue;
            }

            try
            {
                Directory.CreateDirectory(directory);
                copyIndex++;
                string destination = Path.Combine(directory, CreateAttachmentCopyFileName(copyIndex, path));
                File.Copy(path, destination, overwrite: false);
                metadata[$"attachmentCopyPath.{copyIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}"] = destination;
            }
            catch (Exception exception)
            {
                failureIndex++;
                metadata[$"attachmentCopyFailure.{failureIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}"] = $"{exception.GetType().Name}:{exception.Message}";
            }
        }

        metadata["attachmentCopyCount"] = copyIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (failureIndex > 0)
        {
            metadata["attachmentCopyFailureCount"] = failureIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return metadata;
    }

    private string GetTraceAttachmentDirectory(string traceId, DateTimeOffset timestampUtc)
        => Path.Combine(
            GetTraceRoot(),
            timestampUtc.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
            traceId + ".attachments");

    private static IEnumerable<string> ResolveAttachmentCopySources(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            yield break;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in metadata)
        {
            if (!IsAttachmentPathMetadataKey(pair.Key))
            {
                continue;
            }

            foreach (string candidate in SplitAttachmentPathValue(pair.Key, pair.Value))
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string path;
                try
                {
                    path = Path.GetFullPath(candidate.Trim());
                }
                catch
                {
                    continue;
                }

                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> SplitAttachmentPathValue(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        if (key.EndsWith("Paths", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return part;
            }

            yield break;
        }

        yield return value;
    }

    private static bool IsAttachmentPathMetadataKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return key.EndsWith("Path", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("Paths", StringComparison.OrdinalIgnoreCase)
            || key.Contains("audioPath", StringComparison.OrdinalIgnoreCase)
            || key.Contains("filePath", StringComparison.OrdinalIgnoreCase)
            || key.Contains("localPath", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateAttachmentCopyFileName(int index, string sourcePath)
    {
        string fileName = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "attachment";
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }

        if (fileName.Length > 120)
        {
            string extension = Path.GetExtension(fileName);
            int stemLength = Math.Max(1, 120 - extension.Length);
            fileName = fileName[..stemLength] + extension;
        }

        return index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + "-" + fileName;
    }

    private static string InferSource(string kind, string? direction)
    {
        if (kind.StartsWith("telegram.inbound.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(direction, "inbound", StringComparison.OrdinalIgnoreCase))
        {
            return "TelegramInbound";
        }

        if (kind.StartsWith("telegram.outbound.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(direction, "outbound", StringComparison.OrdinalIgnoreCase))
        {
            return "TelegramOutbound";
        }

        if (kind.StartsWith("codex.", StringComparison.OrdinalIgnoreCase))
        {
            return kind.Contains("event", StringComparison.OrdinalIgnoreCase)
                || kind.Contains("terminal", StringComparison.OrdinalIgnoreCase)
                    ? "CodexEvent"
                    : "CodexRequest";
        }

        if (kind.StartsWith("telegram.bundle.", StringComparison.OrdinalIgnoreCase))
        {
            return "Bundle";
        }

        if (kind.StartsWith("telegram.input.", StringComparison.OrdinalIgnoreCase))
        {
            return "TelegramInbound";
        }

        return string.IsNullOrWhiteSpace(direction) ? "Diagnostics" : direction;
    }

    private static string? Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string redacted = SecretAssignmentRegex.Replace(value, match => $"{match.Groups[1].Value}<redacted>");
        redacted = BearerRegex.Replace(redacted, "$1<redacted>");
        redacted = OpenAiKeyRegex.Replace(redacted, "<redacted-openai-key>");
        redacted = SlackTokenRegex.Replace(redacted, "<redacted-token>");
        redacted = TelegramBotTokenRegex.Replace(redacted, "<redacted-telegram-token>");
        return redacted;
    }

    private static bool IsAttachmentMetadataKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return key.Contains("attachment", StringComparison.OrdinalIgnoreCase)
            || key.Contains("file", StringComparison.OrdinalIgnoreCase)
            || key.Contains("audioPath", StringComparison.OrdinalIgnoreCase)
            || key.Contains("localPath", StringComparison.OrdinalIgnoreCase)
            || key.Contains("contentType", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTurnKey(string sessionId, string turnId)
        => $"{sessionId}\u001f{turnId}";

    private sealed class TelegramTraceAccumulator
    {
        private readonly object _gate = new();

        public TelegramTraceAccumulator(string traceId)
        {
            TraceId = traceId;
        }

        public string TraceId { get; }

        public string? SessionId { get; private set; }

        public string? TurnId { get; private set; }

        public bool TelegramInputReceived { get; private set; }

        public bool TelegramInputBundled { get; private set; }

        public bool TelegramInputQueued { get; private set; }

        public bool TelegramInputSent { get; private set; }

        public bool TelegramInputSteered { get; private set; }

        public bool CodexRequestStarted { get; private set; }

        public bool CodexTurnStarted { get; private set; }

        public bool CodexSteerCalled { get; private set; }

        public bool TerminalEventSeen { get; private set; }

        public string? TerminalEventType { get; private set; }

        public bool FinalAssistantOutputCaptured { get; private set; }

        public int CodexEventsReceived { get; private set; }

        public int AssistantOutputCharsReceived { get; private set; }

        public int TelegramOutboundCharsQueued { get; private set; }

        public int TelegramChunksSent { get; private set; }

        public int TelegramCharsSent { get; private set; }

        public int PendingChunks { get; private set; }

        public int CompactedCount { get; private set; }

        public int SendFailureCount { get; private set; }

        public int RateLimitCount { get; private set; }

        public int TimeoutCount { get; private set; }

        public string? LastError { get; private set; }

        public void Apply(TelegramDebugTraceEvent evt)
        {
            lock (_gate)
            {
                SessionId = string.IsNullOrWhiteSpace(evt.SessionId) ? SessionId : evt.SessionId;
                TurnId = string.IsNullOrWhiteSpace(evt.TurnId) ? TurnId : evt.TurnId;

                switch (evt.Kind)
                {
                    case "telegram.input.captured":
                        TelegramInputReceived = true;
                        break;
                    case "telegram.bundle.updated":
                        TelegramInputBundled = true;
                        break;
                    case "telegram.input.queued":
                        TelegramInputReceived = true;
                        TelegramInputQueued = true;
                        break;
                    case "telegram.input.failed":
                    case "telegram.bundle.dispatch_failed":
                        TelegramInputReceived = true;
                        LastError = evt.Error ?? LastError;
                        break;
                    case "codex.send.start":
                    case "codex.plan.start":
                        TelegramInputReceived = true;
                        TelegramInputSent = true;
                        CodexRequestStarted = true;
                        break;
                    case "codex.steer.start":
                    case "codex.steer.text_only.start":
                        TelegramInputReceived = true;
                        TelegramInputSteered = true;
                        CodexSteerCalled = true;
                        break;
                    case "codex.turn.bound":
                        CodexTurnStarted = true;
                        break;
                    case "codex.event":
                        CodexEventsReceived++;
                        if (IsAssistantOutput(evt))
                        {
                            AssistantOutputCharsReceived += evt.TextLength.GetValueOrDefault();
                            FinalAssistantOutputCaptured |= IsFinalResponse(evt) && evt.TextLength.GetValueOrDefault() > 0;
                        }
                        break;
                    case "codex.terminal":
                        TerminalEventSeen = true;
                        TerminalEventType = evt.Status ?? evt.Metadata?.GetValueOrDefault("eventType");
                        CodexEventsReceived++;
                        break;
                    case "telegram.outbound.enqueue":
                        TelegramOutboundCharsQueued += evt.TextLength.GetValueOrDefault();
                        PendingChunks += Math.Max(0, evt.ChunkCount.GetValueOrDefault(1));
                        break;
                    case "telegram.outbound.sent":
                        TelegramChunksSent++;
                        TelegramCharsSent += evt.ChunkLength.GetValueOrDefault();
                        PendingChunks = Math.Max(0, PendingChunks - 1);
                        break;
                    case "telegram.outbound.compacted":
                        CompactedCount += Math.Max(1, evt.CompactedCount.GetValueOrDefault(1));
                        LastError = evt.Error ?? LastError;
                        break;
                    case "telegram.outbound.rate_limited":
                        RateLimitCount++;
                        LastError = evt.Error ?? LastError;
                        break;
                    case "telegram.outbound.timeout":
                        TimeoutCount++;
                        LastError = evt.Error ?? LastError;
                        break;
                    case "telegram.outbound.failed":
                        SendFailureCount++;
                        LastError = evt.Error ?? LastError;
                        break;
                }
            }
        }

        public TelegramTurnDiagnostics ToDiagnostics()
        {
            lock (_gate)
            {
                return new TelegramTurnDiagnostics(
                    SessionId,
                    TurnId,
                    TraceId,
                    TelegramInputReceived,
                    TelegramInputBundled,
                    TelegramInputQueued,
                    TelegramInputSent,
                    TelegramInputSteered,
                    CodexRequestStarted,
                    CodexTurnStarted,
                    CodexSteerCalled,
                    TerminalEventSeen,
                    TerminalEventType,
                    FinalAssistantOutputCaptured,
                    CodexEventsReceived,
                    AssistantOutputCharsReceived,
                    TelegramOutboundCharsQueued,
                    TelegramChunksSent,
                    TelegramCharsSent,
                    PendingChunks,
                    CompactedCount,
                    SendFailureCount,
                    RateLimitCount,
                    TimeoutCount,
                    LastError);
            }
        }

        private static bool IsAssistantOutput(TelegramDebugTraceEvent evt)
        {
            if (evt.Metadata is not null
                && evt.Metadata.TryGetValue("visibleAssistantOutput", out string? value)
                && bool.TryParse(value, out bool visibleAssistantOutput))
            {
                return visibleAssistantOutput;
            }

            string? eventType = evt.Status ?? evt.Metadata?.GetValueOrDefault("eventType");
            return string.Equals(eventType, "item.agentMessage.delta", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventType, "turn.assistant.recovered", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventType, "turn.finalResponse", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFinalResponse(TelegramDebugTraceEvent evt)
        {
            string? eventType = evt.Status ?? evt.Metadata?.GetValueOrDefault("eventType");
            return string.Equals(eventType, "turn.finalResponse", StringComparison.OrdinalIgnoreCase);
        }
    }
}

internal sealed class NullTelegramDebugTraceStore : ITelegramDebugTraceStore
{
    public static NullTelegramDebugTraceStore Instance { get; } = new();

    private NullTelegramDebugTraceStore()
    {
    }

    public string? LatestTraceId => null;

    public bool IsFileTraceEnabled => false;

    public bool IsFullCaptureEnabled => false;

    public bool? RuntimeEnabledOverride => null;

    public bool? RuntimeFullCaptureOverride => null;

    public DateTimeOffset? FullCaptureExpiresUtc => null;

    public TelegramDebugCaptureStatus CaptureStatus => new(
        TelegramDebugCaptureMode.Off,
        FileTraceEnabled: false,
        FullCaptureEnabled: false,
        RuntimeEnabledOverride: null,
        RuntimeFullCaptureOverride: null,
        FullCaptureExpiresUtc: null,
        LatestTraceId: null);

    public string CreateTraceId()
        => Guid.NewGuid().ToString("n");

    public void SetRuntimeEnabledOverride(bool enabled)
    {
    }

    public void ClearRuntimeEnabledOverride()
    {
    }

    public void EnableMetadataCapture()
    {
    }

    public void DisableCapture()
    {
    }

    public void EnableFullCapture(TimeSpan? ttl = null)
    {
    }

    public void DisableFullCapture()
    {
    }

    public Task RecordAsync(TelegramDebugTraceEvent evt, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task BindTurnAsync(string traceId, string sessionId, string turnId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public string? TryGetTraceIdForTurn(string sessionId, string? turnId)
        => null;

    public TelegramTurnDiagnostics GetDiagnostics(string? traceId = null, string? sessionId = null, string? turnId = null)
        => new(sessionId, turnId, traceId, false, false, false, false, false, false, false, false, false, null, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);
}
