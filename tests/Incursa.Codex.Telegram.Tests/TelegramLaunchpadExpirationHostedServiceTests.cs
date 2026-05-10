using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramLaunchpadExpirationHostedServiceTests
{
    [Fact]
    public async Task SweepOnceAsync_ClearsExpiredRootLaunchpadAndNotifiesChat()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        TelegramBotStateStore store = CreateStore(temp.Path);
        TelegramConversationScope conversation = new(-1005555, null);
        await store.SetLaunchpadStateAsync(conversation, DateTimeOffset.UtcNow.AddMinutes(-11), CancellationToken.None);

        TestTelegramBotMessageSender sender = new();
        TelegramLaunchpadExpirationHostedService service = new(
            store,
            sender,
            Microsoft.Extensions.Options.Options.Create(new TelegramBotOptions { Enabled = true }),
            NullLogger<TelegramLaunchpadExpirationHostedService>.Instance);

        await service.SweepOnceAsync(CancellationToken.None);

        Assert.Null(await store.GetLaunchpadStateAsync(conversation, CancellationToken.None));
        SentTelegramMessage sent = Assert.Single(sender.Sent);
        Assert.Equal(conversation, sent.Conversation);
        Assert.Contains("Launchpad expired after 10 minutes of inactivity", sent.Text);
    }

    private static TelegramBotStateStore CreateStore(string dataRoot)
        => new(Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions
            {
                DataRoot = dataRoot,
            },
        }));

    private sealed class TestTelegramBotMessageSender : ITelegramBotMessageSender
    {
        public List<SentTelegramMessage> Sent { get; } = [];

        public Task SendTextMessageAsync(
            TelegramConversationScope conversation,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken)
        {
            Sent.Add(new SentTelegramMessage(conversation, text));
            return Task.CompletedTask;
        }

        public Task EditTextMessageAsync(
            TelegramConversationScope conversation,
            int messageId,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed record SentTelegramMessage(TelegramConversationScope Conversation, string Text);
}
