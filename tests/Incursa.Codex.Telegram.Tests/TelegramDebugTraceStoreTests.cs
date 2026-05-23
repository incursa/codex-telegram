using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramDebugTraceStoreTests
{
    [Fact]
    public async Task RecordAsync_WritesJsonlAndAggregatesTurnDiagnostics()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        TelegramDebugTraceStore store = CreateStore(dataRoot.Path, enabled: true);
        string traceId = store.CreateTraceId();
        DateTimeOffset timestamp = DateTimeOffset.Parse("2026-05-23T12:00:00Z");

        await store.RecordAsync(CreateEvent(traceId, timestamp, "codex.send.start"), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(1), "codex.event", status: "item.agentMessage.delta", textLength: 12), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(2), "codex.event", status: "turn.finalResponse", textLength: 20), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(3), "codex.terminal", status: "turn.completed"), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(4), "telegram.outbound.enqueue", textLength: 50, chunkCount: 2), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(5), "telegram.outbound.sent", chunkLength: 30), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(6), "telegram.outbound.compacted", compactedCount: 3), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(7), "telegram.outbound.rate_limited", error: "429"), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(8), "telegram.outbound.timeout", error: "timeout"), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(9), "telegram.outbound.failed", error: "send failed"), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(10), "codex.event", status: "tool.progress", textLength: 99), CancellationToken.None);

        TelegramTurnDiagnostics diagnostics = store.GetDiagnostics(traceId);

        Assert.True(diagnostics.CodexRequestStarted);
        Assert.False(diagnostics.CodexTurnStarted);
        Assert.True(diagnostics.TerminalEventSeen);
        Assert.Equal("turn.completed", diagnostics.TerminalEventType);
        Assert.True(diagnostics.FinalAssistantOutputCaptured);
        Assert.Equal(4, diagnostics.CodexEventsReceived);
        Assert.Equal(32, diagnostics.AssistantOutputCharsReceived);
        Assert.Equal(50, diagnostics.TelegramOutboundCharsQueued);
        Assert.Equal(1, diagnostics.TelegramChunksSent);
        Assert.Equal(30, diagnostics.TelegramCharsSent);
        Assert.Equal(1, diagnostics.PendingChunks);
        Assert.True(diagnostics.Compacted);
        Assert.Equal(3, diagnostics.CompactedCount);
        Assert.True(diagnostics.SendFailed);
        Assert.True(diagnostics.RateLimited);
        Assert.True(diagnostics.SendTimedOut);
        Assert.Equal(1, diagnostics.SendFailureCount);
        Assert.Equal(1, diagnostics.RateLimitCount);
        Assert.Equal(1, diagnostics.TimeoutCount);
        Assert.Equal("send failed", diagnostics.LastError);
        Assert.Equal("Telegram delivery delayed", diagnostics.LikelyStatus);

        string[] lines = await File.ReadAllLinesAsync(store.GetTracePath(traceId, timestamp), CancellationToken.None);
        Assert.Equal(11, lines.Length);
        Assert.Contains(lines, line => line.Contains("\"kind\":\"telegram.outbound.enqueue\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"chunkCount\":2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordAsync_AggregatesInputRoutingDiagnosticsSeparatelyFromTurnStart()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        TelegramDebugTraceStore store = CreateStore(dataRoot.Path, enabled: false);
        string traceId = store.CreateTraceId();
        DateTimeOffset timestamp = DateTimeOffset.Parse("2026-05-23T12:00:00Z");

        await store.RecordAsync(CreateEvent(traceId, timestamp, "telegram.input.captured"), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(1), "telegram.bundle.updated"), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(2), "telegram.input.queued"), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(3), "codex.steer.start"), CancellationToken.None);
        await store.RecordAsync(CreateEvent(traceId, timestamp.AddSeconds(4), "codex.plan.start"), CancellationToken.None);

        TelegramTurnDiagnostics diagnostics = store.GetDiagnostics(traceId);

        Assert.True(diagnostics.TelegramInputReceived);
        Assert.True(diagnostics.TelegramInputBundled);
        Assert.True(diagnostics.TelegramInputQueued);
        Assert.True(diagnostics.TelegramInputSteered);
        Assert.True(diagnostics.TelegramInputSent);
        Assert.True(diagnostics.CodexRequestStarted);
        Assert.True(diagnostics.CodexSteerCalled);
        Assert.False(diagnostics.CodexTurnStarted);
    }

    [Fact]
    public async Task BindTurnAsync_AllowsDiagnosticsLookupBySessionAndTurn()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        TelegramDebugTraceStore store = CreateStore(dataRoot.Path, enabled: false);
        string traceId = store.CreateTraceId();

        await store.BindTurnAsync(traceId, "thread-1", "turn-1", CancellationToken.None);

        Assert.Equal(traceId, store.TryGetTraceIdForTurn("thread-1", "turn-1"));
        TelegramTurnDiagnostics diagnostics = store.GetDiagnostics(sessionId: "thread-1", turnId: "turn-1");
        Assert.Equal(traceId, diagnostics.TraceId);
        Assert.True(diagnostics.CodexTurnStarted);
        Assert.Equal("thread-1", diagnostics.SessionId);
        Assert.Equal("turn-1", diagnostics.TurnId);
    }

    [Fact]
    public async Task GetDiagnostics_WhenRequestedTraceOrTurnIsMissing_DoesNotFallbackToLatestTrace()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        TelegramDebugTraceStore store = CreateStore(dataRoot.Path, enabled: false);
        string traceId = store.CreateTraceId();

        await store.RecordAsync(CreateEvent(traceId, DateTimeOffset.Parse("2026-05-23T12:00:00Z"), "codex.event", textLength: 5), CancellationToken.None);

        TelegramTurnDiagnostics latest = store.GetDiagnostics();
        TelegramTurnDiagnostics missingTrace = store.GetDiagnostics("missing-trace");
        TelegramTurnDiagnostics missingTurn = store.GetDiagnostics(sessionId: "thread-1", turnId: "missing-turn");

        Assert.Equal(traceId, latest.TraceId);
        Assert.Equal(1, latest.CodexEventsReceived);
        Assert.Equal("missing-trace", missingTrace.TraceId);
        Assert.Equal(0, missingTrace.CodexEventsReceived);
        Assert.Null(missingTurn.TraceId);
        Assert.Equal("thread-1", missingTurn.SessionId);
        Assert.Equal("missing-turn", missingTurn.TurnId);
        Assert.Equal(0, missingTurn.CodexEventsReceived);
    }

    [Fact]
    public async Task RecordAsync_WhenTraceFileIsAtLimitKeepsInMemoryDiagnosticsWithoutAppending()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        TelegramDebugTraceStore store = CreateStore(
            dataRoot.Path,
            enabled: true,
            maxTraceFileBytes: TelegramDebugTraceLimits.MinTraceFileBytes);
        string traceId = store.CreateTraceId();
        DateTimeOffset timestamp = DateTimeOffset.Parse("2026-05-23T12:00:00Z");
        string tracePath = store.GetTracePath(traceId, timestamp);
        Directory.CreateDirectory(Path.GetDirectoryName(tracePath)!);
        await File.WriteAllBytesAsync(tracePath, new byte[TelegramDebugTraceLimits.MinTraceFileBytes], CancellationToken.None);

        await store.RecordAsync(CreateEvent(traceId, timestamp, "codex.event", textLength: 5), CancellationToken.None);

        TelegramTurnDiagnostics diagnostics = store.GetDiagnostics(traceId);
        Assert.Equal(1, diagnostics.CodexEventsReceived);
        Assert.Equal(0, diagnostics.AssistantOutputCharsReceived);
        Assert.Equal(TelegramDebugTraceLimits.MinTraceFileBytes, new FileInfo(tracePath).Length);
    }

    private static TelegramDebugTraceStore CreateStore(
        string dataRoot,
        bool enabled,
        long? maxTraceFileBytes = null)
        => new(
            Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
            {
                Workspace = new CodexWorkspaceOptions
                {
                    DataRoot = dataRoot,
                },
            }),
            Microsoft.Extensions.Options.Options.Create(new TelegramDebugTraceOptions
            {
                Enabled = enabled,
                MaxTraceFileBytes = maxTraceFileBytes ?? new TelegramDebugTraceOptions().MaxTraceFileBytes,
            }));

    private static TelegramDebugTraceEvent CreateEvent(
        string traceId,
        DateTimeOffset timestamp,
        string kind,
        string? status = null,
        int? textLength = null,
        int? chunkCount = null,
        int? chunkLength = null,
        int? compactedCount = null,
        string? error = null)
        => new(
            traceId,
            timestamp,
            kind,
            SessionId: "thread-1",
            TurnId: "turn-1",
            ChatId: 1234,
            MessageThreadId: 55,
            Status: status,
            TextLength: textLength,
            ChunkCount: chunkCount,
            ChunkLength: chunkLength,
            CompactedCount: compactedCount,
            Error: error);
}
