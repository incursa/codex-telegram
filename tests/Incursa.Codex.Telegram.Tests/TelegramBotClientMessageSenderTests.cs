using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramBotClientMessageSenderTests
{
    [Fact]
    public async Task SendTextMessageAsync_WhenDisabledDoesNotCallTelegram()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = new(
            new TelegramBotOptions
            {
                Enabled = false,
            },
            NullLogger<TelegramBotClientMessageSender>.Instance,
            client);

        await sender.SendTextMessageAsync(
            new TelegramConversationScope(1234, null),
            "ignored",
            null,
            CancellationToken.None);

        Assert.Empty(client.SentMessages);
    }

    [Fact]
    public async Task SendTextMessageAsync_MainChatSendKeepsThreadNull()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.SendTextMessageAsync(
            new TelegramConversationScope(1234, null),
            "main chat status",
            null,
            CancellationToken.None);

        SentTelegramApiMessage sent = Assert.Single(client.SentMessages);
        Assert.Equal(1234, sent.ChatId);
        Assert.Null(sent.MessageThreadId);
        Assert.Equal("main chat status", sent.Text);
    }

    [Fact]
    public async Task SendTextMessageAsync_TopicThreadFailureDoesNotRetryInMainChat()
    {
        FakeTelegramBotApiClient client = new();
        client.SendFailures.Enqueue(new ApiRequestException("Bad Request: message thread not found", 400));
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.SendTextMessageAsync(
            new TelegramConversationScope(-100123456, 55),
            "topic-scoped status",
            null,
            CancellationToken.None);

        SentTelegramApiMessage sent = Assert.Single(client.SentMessages);
        Assert.Equal(-100123456, sent.ChatId);
        Assert.Equal(55, sent.MessageThreadId);
        Assert.Equal("topic-scoped status", sent.Text);
    }

    [Fact]
    public async Task OutboundSendTextMessageAsync_TopicThreadFailureThrowsWithoutRetryingMainChat()
    {
        FakeTelegramBotApiClient client = new();
        client.SendFailures.Enqueue(new ApiRequestException("Bad Request: topic was closed", 400));
        IOutboundTelegramMessageSender sender = CreateSender(client);

        TelegramTopicSendException exception = await Assert.ThrowsAsync<TelegramTopicSendException>(
            () => sender.SendTextMessageAsync(
                new TelegramConversationScope(-100123456, 55),
                "topic-scoped Codex output",
                CancellationToken.None));

        Assert.IsType<ApiRequestException>(exception.InnerException);
        SentTelegramApiMessage sent = Assert.Single(client.SentMessages);
        Assert.Equal(55, sent.MessageThreadId);
    }

    [Fact]
    public async Task OutboundSendTextMessageAsync_RateLimitThrowsRetryAfter()
    {
        FakeTelegramBotApiClient client = new();
        client.SendFailures.Enqueue(new ApiRequestException(
            "Too Many Requests: retry after 7",
            429,
            new global::Telegram.Bot.Types.ResponseParameters { RetryAfter = 7 }));
        IOutboundTelegramMessageSender sender = CreateSender(client);

        TelegramOutboundRateLimitException exception = await Assert.ThrowsAsync<TelegramOutboundRateLimitException>(
            () => sender.SendTextMessageAsync(
                new TelegramConversationScope(1234, null),
                "rate limited output",
                CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(7), exception.RetryAfter);
        Assert.Single(client.SentMessages);
    }

    [Fact]
    public async Task EditTextMessageAsync_SuccessUsesEditWithoutFallbackSend()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.EditTextMessageAsync(
            new TelegramConversationScope(1234, null),
            42,
            "updated card",
            null,
            CancellationToken.None);

        EditedTelegramApiMessage edit = Assert.Single(client.EditedMessages);
        Assert.Equal(1234, edit.ChatId);
        Assert.Equal(42, edit.MessageId);
        Assert.Equal("updated card", edit.Text);
        Assert.Empty(client.SentMessages);
    }

    [Fact]
    public async Task EditTextMessageAsync_MessageNotModifiedDoesNotFallbackSend()
    {
        FakeTelegramBotApiClient client = new();
        client.EditFailures.Enqueue(new ApiRequestException("Bad Request: message is not modified", 400));
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.EditTextMessageAsync(
            new TelegramConversationScope(1234, null),
            42,
            "same card",
            null,
            CancellationToken.None);

        Assert.Single(client.EditedMessages);
        Assert.Empty(client.SentMessages);
    }

    [Fact]
    public async Task EditTextMessageAsync_GenericFailureFallsBackToSend()
    {
        FakeTelegramBotApiClient client = new();
        client.EditFailures.Enqueue(new InvalidOperationException("telegram edit transport failed"));
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.EditTextMessageAsync(
            new TelegramConversationScope(1234, 55),
            42,
            "replacement card",
            null,
            CancellationToken.None);

        Assert.Single(client.EditedMessages);
        SentTelegramApiMessage sent = Assert.Single(client.SentMessages);
        Assert.Equal(55, sent.MessageThreadId);
        Assert.Equal("replacement card", sent.Text);
    }

    [Fact]
    public async Task AnswerCallbackQueryAsync_SuccessCallsTelegram()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.AnswerCallbackQueryAsync("callback-1", "Updated", CancellationToken.None);

        CallbackAnswer answer = Assert.Single(client.CallbackAnswers);
        Assert.Equal("callback-1", answer.CallbackQueryId);
        Assert.Equal("Updated", answer.Text);
    }

    private static TelegramBotClientMessageSender CreateSender(FakeTelegramBotApiClient client)
        => new(
            new TelegramBotOptions
            {
                Enabled = true,
                Token = "123:token",
            },
            NullLogger<TelegramBotClientMessageSender>.Instance,
            client);

    private sealed class FakeTelegramBotApiClient : ITelegramBotApiClient
    {
        public List<SentTelegramApiMessage> SentMessages { get; } = [];

        public List<EditedTelegramApiMessage> EditedMessages { get; } = [];

        public List<CallbackAnswer> CallbackAnswers { get; } = [];

        public Queue<Exception> SendFailures { get; } = new();

        public Queue<Exception> EditFailures { get; } = new();

        public Queue<Exception> AnswerFailures { get; } = new();

        public Task SendMessageAsync(
            long chatId,
            string text,
            InlineKeyboardMarkup? replyMarkup,
            int? messageThreadId,
            CancellationToken cancellationToken)
        {
            SentMessages.Add(new SentTelegramApiMessage(chatId, text, messageThreadId));
            if (SendFailures.Count > 0)
            {
                throw SendFailures.Dequeue();
            }

            return Task.CompletedTask;
        }

        public Task EditMessageTextAsync(
            long chatId,
            int messageId,
            string text,
            InlineKeyboardMarkup? replyMarkup,
            CancellationToken cancellationToken)
        {
            EditedMessages.Add(new EditedTelegramApiMessage(chatId, messageId, text));
            if (EditFailures.Count > 0)
            {
                throw EditFailures.Dequeue();
            }

            return Task.CompletedTask;
        }

        public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
        {
            CallbackAnswers.Add(new CallbackAnswer(callbackQueryId, text));
            if (AnswerFailures.Count > 0)
            {
                throw AnswerFailures.Dequeue();
            }

            return Task.CompletedTask;
        }
    }

    private sealed record SentTelegramApiMessage(long ChatId, string Text, int? MessageThreadId);

    private sealed record EditedTelegramApiMessage(long ChatId, int MessageId, string Text);

    private sealed record CallbackAnswer(string CallbackQueryId, string? Text);
}
