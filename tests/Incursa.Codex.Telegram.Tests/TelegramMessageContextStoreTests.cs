using Incursa.Codex.Telegram.Telegram;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramMessageContextStoreTests
{
    [Theory]
    [InlineData(
        """
        Codex failed
        Session: 019e5d81
        Mode: LiveCard
        Updates: 0 captured
        Progress: 0 suppressed
        Artifacts: 0
        Final response: captured
        Telegram delivery: draining (0 messages, 1 chunks)
        Latest: Codex turn was interrupted.
        """)]
    [InlineData(
        """
        Session status card
        State: Codex is working
        Queue count: 1
        Telegram delivery: idle
        """)]
    public void ShouldSuppressBotReplyContext_ReturnsTrueForOperationalCardText(string text)
        => Assert.True(TelegramReplyContextTextClassifier.ShouldSuppressBotReplyContext(text));

    [Fact]
    public async Task ResolveReplyContextAsync_ReturnsNullForOperationalBotReply()
    {
        TelegramMessageContextStore store = new();
        TelegramConversationScope conversation = new(5555, null);

        TelegramReplyContext? context = await store.ResolveReplyContextAsync(
            conversation,
            messageId: 12,
            TelegramMessageAuthor.Bot,
            """
            Codex failed
            Session: 019e5d81
            Mode: LiveCard
            Updates: 0 captured
            Progress: 0 suppressed
            Artifacts: 0
            Final response: captured
            Telegram delivery: draining (0 messages, 1 chunks)
            Latest: Codex turn was interrupted.
            """,
            CancellationToken.None);

        Assert.Null(context);
    }

    [Fact]
    public async Task ResolveReplyContextAsync_FiltersOperationalBotPriorMessages()
    {
        TelegramMessageContextStore store = new();
        TelegramConversationScope conversation = new(5555, null);
        await store.RecordAsync(new TelegramMessageContextRecord(conversation, 10, TelegramMessageAuthor.User, "First context.", DateTimeOffset.Parse("2026-05-10T12:00:00Z")), CancellationToken.None);
        await store.RecordAsync(new TelegramMessageContextRecord(conversation, 11, TelegramMessageAuthor.Bot, """
            Codex failed
            Session: 019e5d81
            Mode: LiveCard
            Updates: 0 captured
            Progress: 0 suppressed
            Artifacts: 0
            Final response: captured
            Telegram delivery: draining (0 messages, 1 chunks)
            Latest: Codex turn was interrupted.
            """, DateTimeOffset.Parse("2026-05-10T12:01:00Z")), CancellationToken.None);
        await store.RecordAsync(new TelegramMessageContextRecord(conversation, 12, TelegramMessageAuthor.User, "Do not delete that.", DateTimeOffset.Parse("2026-05-10T12:02:00Z")), CancellationToken.None);

        TelegramReplyContext? context = await store.ResolveReplyContextAsync(
            conversation,
            messageId: 12,
            TelegramMessageAuthor.User,
            "Do not delete that.",
            CancellationToken.None);

        Assert.NotNull(context);
        Assert.Equal("Do not delete that.", context.Text);
        Assert.Collection(
            context.PriorMessages,
            prior => Assert.Equal("First context.", prior.Text));
    }
}
