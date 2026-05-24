using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramSessionCardBehaviorTests
{
    [Fact]
    public void Resolve_WhenIdle_HidesSteerQueueAndStop()
    {
        CodexSessionSummary session = CreateSession(CodexSessionStatus.Exited);

        TelegramSessionCardBehavior behavior = TelegramSessionCardBehavior.Resolve(
            session,
            activeTurnId: null,
            activeTurn: null,
            destination: null,
            diagnostics: EmptyDiagnostics(session.Id));

        Assert.Equal(TelegramSessionUiState.Idle, behavior.State);
        Assert.Equal("Ready", behavior.StateText);
        Assert.Equal(
            ["Send / Add Input", "Tail / History", "Debug / Trace", "Output Mode", "Show Updates", "Model", "Thinking"],
            Flatten(behavior.Buttons));
    }

    [Fact]
    public void Resolve_WhenActiveTurn_ShowsSteerQueueAndStop()
    {
        CodexSessionSummary session = CreateSession(CodexSessionStatus.Running);

        TelegramSessionCardBehavior behavior = TelegramSessionCardBehavior.Resolve(
            session,
            activeTurnId: "turn-1",
            activeTurn: null,
            destination: null,
            diagnostics: EmptyDiagnostics(session.Id));

        Assert.Equal(TelegramSessionUiState.Working, behavior.State);
        Assert.Equal("Codex is working", behavior.StateText);
        Assert.Equal(
            ["Steer", "Queue next", "Stop / Cancel", "Tail / History", "Debug / Trace", "Output Mode", "Show Updates", "Model", "Thinking"],
            Flatten(behavior.Buttons));
    }

    [Fact]
    public void Resolve_WhenTerminalSeenAndOutboundPending_SeparatesCodexCompletionFromTelegramDrain()
    {
        CodexSessionSummary session = CreateSession(CodexSessionStatus.Exited);
        TelegramOutboundDestinationStatus destination = new(
            ChatId: 5555,
            MessageThreadId: null,
            SessionId: session.Id,
            PendingMessageCount: 1,
            PendingChunkCount: 3,
            PendingCharacterCount: 1200,
            FirstPendingUtc: DateTimeOffset.UtcNow,
            LastEnqueuedUtc: DateTimeOffset.UtcNow,
            ChatBackoffUntilUtc: null,
            LastSentUtc: null);

        TelegramSessionCardBehavior behavior = TelegramSessionCardBehavior.Resolve(
            session,
            activeTurnId: null,
            activeTurn: null,
            destination,
            diagnostics: EmptyDiagnostics(session.Id) with
            {
                TerminalEventSeen = true,
                TerminalEventType = "turn.completed",
                PendingChunks = 3,
            });

        Assert.Equal(TelegramSessionUiState.Draining, behavior.State);
        Assert.Equal("Codex finished; sending remaining Telegram output", behavior.StateText);
        Assert.Equal(
            ["Status / Refresh", "Queue next", "Tail / History", "Debug / Trace", "Output Mode", "Show Updates", "Model", "Thinking"],
            Flatten(behavior.Buttons));
    }

    private static CodexSessionSummary CreateSession(CodexSessionStatus status)
        => new(
            "thread-1",
            "Demo session",
            status,
            @"C:\repo",
            DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
            DateTimeOffset.Parse("2026-05-23T10:01:00Z"),
            null,
            null);

    private static TelegramTurnDiagnostics EmptyDiagnostics(string sessionId)
        => new(
            sessionId,
            null,
            null,
            TelegramInputReceived: false,
            TelegramInputBundled: false,
            TelegramInputQueued: false,
            TelegramInputSent: false,
            TelegramInputSteered: false,
            CodexRequestStarted: false,
            CodexTurnStarted: false,
            CodexSteerCalled: false,
            TerminalEventSeen: false,
            TerminalEventType: null,
            FinalAssistantOutputCaptured: false,
            CodexEventsReceived: 0,
            AssistantOutputCharsReceived: 0,
            TelegramOutboundCharsQueued: 0,
            TelegramChunksSent: 0,
            TelegramCharsSent: 0,
            PendingChunks: 0,
            CompactedCount: 0,
            SendFailureCount: 0,
            RateLimitCount: 0,
            TimeoutCount: 0,
            LastError: null);

    private static string[] Flatten(IReadOnlyList<IReadOnlyList<TelegramReplyButton>> buttons)
        => buttons.SelectMany(row => row.Select(button => button.Text)).ToArray();
}
