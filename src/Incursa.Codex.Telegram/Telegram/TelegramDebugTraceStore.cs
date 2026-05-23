using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    IReadOnlyDictionary<string, string>? Metadata = null);

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

    bool? RuntimeEnabledOverride { get; }

    string CreateTraceId();

    void SetRuntimeEnabledOverride(bool enabled);

    void ClearRuntimeEnabledOverride();

    Task RecordAsync(TelegramDebugTraceEvent evt, CancellationToken cancellationToken);

    Task BindTurnAsync(string traceId, string sessionId, string turnId, CancellationToken cancellationToken);

    string? TryGetTraceIdForTurn(string sessionId, string? turnId);

    string? LatestTraceId { get; }

    TelegramTurnDiagnostics GetDiagnostics(string? traceId = null, string? sessionId = null, string? turnId = null);
}

internal sealed class TelegramDebugTraceStore : ITelegramDebugTraceStore
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TelegramTraceAccumulator> _traces = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _turnTraceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptions<CodexTelegramOptions> _codexOptions;
    private readonly IOptions<TelegramDebugTraceOptions> _traceOptions;
    private readonly object _runtimeGate = new();
    private string? _latestTraceId;
    private bool? _runtimeEnabledOverride;

    public TelegramDebugTraceStore(
        IOptions<CodexTelegramOptions> codexOptions,
        IOptions<TelegramDebugTraceOptions> traceOptions)
    {
        _codexOptions = codexOptions;
        _traceOptions = traceOptions;
    }

    public string? LatestTraceId => _latestTraceId;

    public bool IsFileTraceEnabled => RuntimeEnabledOverride ?? _traceOptions.Value.Enabled;

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

    public void SetRuntimeEnabledOverride(bool enabled)
    {
        lock (_runtimeGate)
        {
            _runtimeEnabledOverride = enabled;
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

        _latestTraceId = evt.TraceId;
        TelegramTraceAccumulator accumulator = _traces.GetOrAdd(evt.TraceId, traceId => new TelegramTraceAccumulator(traceId));
        accumulator.Apply(evt);

        if (!IsFileTraceEnabled)
        {
            return;
        }

        string path = GetTracePath(evt.TraceId, evt.TimestampUtc);
        long maxBytes = Math.Clamp(
            _traceOptions.Value.MaxTraceFileBytes,
            TelegramDebugTraceLimits.MinTraceFileBytes,
            TelegramDebugTraceLimits.MaxTraceFileBytes);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string line = JsonSerializer.Serialize(evt, _jsonOptions);
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
        string root = _traceOptions.Value.TraceDirectory ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
        {
            string? configuredDataRoot = _codexOptions.Value.Workspace.DataRoot;
            root = string.IsNullOrWhiteSpace(configuredDataRoot)
                ? Path.Combine(AppContext.BaseDirectory, "App_Data", "codex-telegram", "telegram-traces")
                : Path.Combine(configuredDataRoot, "telegram-traces");
        }

        return Path.Combine(
            Path.GetFullPath(root),
            timestampUtc.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture),
            traceId + ".jsonl");
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

    public bool? RuntimeEnabledOverride => null;

    public string CreateTraceId()
        => Guid.NewGuid().ToString("n");

    public void SetRuntimeEnabledOverride(bool enabled)
    {
    }

    public void ClearRuntimeEnabledOverride()
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
