using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class OutboundTelegramQueueTests
{
    [Fact]
    public async Task EnqueueAsync_DropsProgressWhenProgressMessagesAreDisabled()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            IncludeProgressMessages = false,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Progress, "working"), CancellationToken.None);

        TelegramOutboundQueueStatus status = await scheduler.GetStatusAsync(CancellationToken.None);

        Assert.Equal(0, status.PendingMessageCount);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task ProcessNextAsync_BatchesSameDestinationMessagesIntoOneSend()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
            MaxMessageChars = 3500,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "first update"), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Completion, "second update"), CancellationToken.None);

        bool processed = await scheduler.ProcessNextAsync(CancellationToken.None);

        Assert.True(processed);
        SentTelegramMessage sent = Assert.Single(sender.Sent);
        Assert.Equal(1234, sent.Conversation.ChatId);
        Assert.Contains("2 updates", sent.Text);
        Assert.Contains("first update", sent.Text);
        Assert.Contains("second update", sent.Text);
        Assert.Contains("Use /tail 100 for more detail.", sent.Text);
    }

    [Fact]
    public async Task ProcessNextAsync_KeepsMessagesQueuedAfterSendFailure()
    {
        TestTelegramSender sender = new() { ThrowOnSend = true };
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "still pending"), CancellationToken.None);

        bool processed = await scheduler.ProcessNextAsync(CancellationToken.None);
        TelegramOutboundQueueStatus status = await scheduler.GetStatusAsync(CancellationToken.None);

        Assert.False(processed);
        Assert.Equal(1, status.PendingChunkCount);
    }

    private static OutboundTelegramScheduler CreateScheduler(TestTelegramSender sender, TelegramOutboundOptions options)
        => new(
            sender,
            new TelegramMessageChunker(),
            TimeProvider.System,
            new StaticOptionsMonitor<TelegramOutboundOptions>(options),
            NullLogger<OutboundTelegramScheduler>.Instance);

    private static OutboundTelegramMessage CreateMessage(CodexOutboundMessageKind kind, string text)
        => new()
        {
            MessageId = Guid.NewGuid().ToString("n"),
            ChatId = 1234,
            SessionId = "thread-1234567890",
            Kind = kind,
            Text = text,
            CreatedUtc = DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
        };

    private sealed class TestTelegramSender : IOutboundTelegramMessageSender
    {
        public List<SentTelegramMessage> Sent { get; } = [];

        public bool ThrowOnSend { get; init; }

        public Task SendTextMessageAsync(TelegramConversationScope conversation, string text, CancellationToken cancellationToken)
        {
            if (ThrowOnSend)
            {
                throw new InvalidOperationException("send failed");
            }

            Sent.Add(new SentTelegramMessage(conversation, text));
            return Task.CompletedTask;
        }
    }

    private sealed record SentTelegramMessage(TelegramConversationScope Conversation, string Text);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
