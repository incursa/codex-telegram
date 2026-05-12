using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;

namespace Incursa.Codex.Telegram.Telegram;

internal sealed class TelegramBotClientMessageSender : ITelegramBotMessageSender, IOutboundTelegramMessageSender
{
    private readonly TelegramBotOptions _options;
    private readonly ILogger<TelegramBotClientMessageSender> _logger;
    private readonly Lazy<ITelegramBotApiClient> _client;
    private readonly ITelegramMessageContextStore _messageContextStore;

    public TelegramBotClientMessageSender(
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramBotClientMessageSender> logger)
        : this(
            options.Value,
            logger,
            new Lazy<ITelegramBotApiClient>(() => new TelegramBotApiClient(new TelegramBotClient(RequireToken(options.Value)))),
            NullTelegramMessageContextStore.Instance)
    {
    }

    public TelegramBotClientMessageSender(
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramBotClientMessageSender> logger,
        ITelegramMessageContextStore messageContextStore)
        : this(
            options.Value,
            logger,
            new Lazy<ITelegramBotApiClient>(() => new TelegramBotApiClient(new TelegramBotClient(RequireToken(options.Value)))),
            messageContextStore)
    {
    }

    internal TelegramBotClientMessageSender(
        TelegramBotOptions options,
        ILogger<TelegramBotClientMessageSender> logger,
        ITelegramBotApiClient client,
        ITelegramMessageContextStore? messageContextStore = null)
        : this(options, logger, new Lazy<ITelegramBotApiClient>(() => client), messageContextStore ?? NullTelegramMessageContextStore.Instance)
    {
    }

    private TelegramBotClientMessageSender(
        TelegramBotOptions options,
        ILogger<TelegramBotClientMessageSender> logger,
        Lazy<ITelegramBotApiClient> client,
        ITelegramMessageContextStore messageContextStore)
    {
        _options = options;
        _logger = logger;
        _client = client;
        _messageContextStore = messageContextStore;
    }

    public async Task SendTextMessageAsync(
        TelegramConversationScope conversation,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            await SendMessageAsync(conversation, text, buttons, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiRequestException exception) when (conversation.MessageThreadId is not null && IsThreadReplyFailure(exception))
        {
            _logger.LogWarning(
                exception,
                "Telegram rejected a reply to chat {ChatId} topic {MessageThreadId}. The message was not retried in the main chat to preserve topic isolation.",
                conversation.ChatId,
                conversation.MessageThreadId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Telegram send failed for chat {ChatId} topic {MessageThreadId}; the bot will continue running.",
                conversation.ChatId,
                conversation.MessageThreadId);
        }
    }

    async Task IOutboundTelegramMessageSender.SendTextMessageAsync(
        TelegramConversationScope conversation,
        string text,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            await SendMessageAsync(conversation, text, null, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiRequestException exception) when (IsRateLimited(exception))
        {
            throw new TelegramOutboundRateLimitException(
                "Telegram Bot API returned a rate limit response.",
                ResolveRetryAfter(exception),
                exception);
        }
        catch (ApiRequestException exception) when (conversation.MessageThreadId is not null && IsThreadReplyFailure(exception))
        {
            throw new TelegramTopicSendException(
                $"Telegram rejected a reply to chat {conversation.ChatId} topic {conversation.MessageThreadId}; the message was not retried in the main chat.",
                exception);
        }
    }

    public async Task EditTextMessageAsync(
        TelegramConversationScope conversation,
        int messageId,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            await _client.Value.EditMessageTextAsync(
                conversation.ChatId,
                messageId,
                text,
                ToInlineKeyboardMarkup(buttons),
                cancellationToken).ConfigureAwait(false);
            await _messageContextStore.RecordAsync(
                new TelegramMessageContextRecord(
                    conversation,
                    messageId,
                    TelegramMessageAuthor.Bot,
                    text,
                    DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);

            _logger.LogDebug(
                "Telegram edit succeeded for chat {ChatId} message {MessageId}; topic {MessageThreadId}; text length {TextLength}; button rows {ButtonRowCount}.",
                conversation.ChatId,
                messageId,
                conversation.MessageThreadId,
                text.Length,
                buttons?.Count ?? 0);
        }
        catch (ApiRequestException exception) when (IsMessageNotModified(exception))
        {
            _logger.LogDebug(
                exception,
                "Telegram edit for chat {ChatId} message {MessageId} was a no-op because the content did not change.",
                conversation.ChatId,
                messageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Telegram edit failed for chat {ChatId} message {MessageId}; falling back to a new message.",
                conversation.ChatId,
                messageId);
            await SendTextMessageAsync(conversation, text, buttons, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            await _client.Value.AnswerCallbackQueryAsync(callbackQueryId, text, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Telegram callback answer failed for callback {CallbackQueryId}; the bot will continue running.", callbackQueryId);
        }
    }

    public async Task AcknowledgeMessageAsync(TelegramMessageAcknowledgement acknowledgement, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(acknowledgement.BusinessConnectionId))
            {
                await _client.Value.ReadBusinessMessageAsync(
                    acknowledgement.BusinessConnectionId,
                    acknowledgement.Conversation.ChatId,
                    acknowledgement.MessageId,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendTypingActionAsync(acknowledgement.Conversation, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "Telegram acknowledgement failed for chat {ChatId} topic {MessageThreadId} message {MessageId}; continuing.",
                acknowledgement.Conversation.ChatId,
                acknowledgement.Conversation.MessageThreadId,
                acknowledgement.MessageId);
        }
    }

    public async Task SendTypingActionAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            await _client.Value.SendChatActionAsync(
                conversation.ChatId,
                conversation.MessageThreadId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "Telegram typing action failed for chat {ChatId} topic {MessageThreadId}; continuing.",
                conversation.ChatId,
                conversation.MessageThreadId);
        }
    }

    private Task SendMessageAsync(
        TelegramConversationScope conversation,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken)
        => SendMessageCoreAsync(conversation, text, buttons, cancellationToken);

    private async Task SendMessageCoreAsync(
        TelegramConversationScope conversation,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken)
    {
        int messageId = await _client.Value.SendMessageAsync(
            conversation.ChatId,
            text,
            ToInlineKeyboardMarkup(buttons),
            conversation.MessageThreadId,
            cancellationToken).ConfigureAwait(false);
        await _messageContextStore.RecordAsync(
            new TelegramMessageContextRecord(
                conversation,
                messageId,
                TelegramMessageAuthor.Bot,
                text,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Telegram send succeeded for chat {ChatId} topic {MessageThreadId}; text length {TextLength}; button rows {ButtonRowCount}.",
            conversation.ChatId,
            conversation.MessageThreadId,
            text.Length,
            buttons?.Count ?? 0);
    }

    private static string RequireToken(TelegramBotOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new InvalidOperationException("TelegramBot:Token must be configured when TelegramBot:Enabled is true.");
        }

        return options.Token.Trim();
    }

    private static InlineKeyboardMarkup? ToInlineKeyboardMarkup(IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons)
    {
        if (buttons is null || buttons.Count == 0)
        {
            return null;
        }

        return new InlineKeyboardMarkup(buttons.Select(row =>
            row.Select(button => InlineKeyboardButton.WithCallbackData(button.Text, button.CallbackData))));
    }

    private static bool IsThreadReplyFailure(ApiRequestException exception)
        => exception.Message.Contains("thread", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("topic", StringComparison.OrdinalIgnoreCase);

    private static bool IsMessageNotModified(ApiRequestException exception)
        => exception.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);

    private static bool IsRateLimited(ApiRequestException exception)
        => exception.ErrorCode == 429
            || exception.Message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("retry after", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? ResolveRetryAfter(ApiRequestException exception)
    {
        int? retryAfter = exception.Parameters?.RetryAfter;
        return retryAfter is > 0 ? TimeSpan.FromSeconds(retryAfter.Value) : null;
    }
}

internal interface ITelegramBotApiClient
{
    Task<int> SendMessageAsync(
        long chatId,
        string text,
        InlineKeyboardMarkup? replyMarkup,
        int? messageThreadId,
        CancellationToken cancellationToken);

    Task EditMessageTextAsync(
        long chatId,
        int messageId,
        string text,
        InlineKeyboardMarkup? replyMarkup,
        CancellationToken cancellationToken);

    Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken);

    Task SendChatActionAsync(long chatId, int? messageThreadId, CancellationToken cancellationToken);

    Task ReadBusinessMessageAsync(string businessConnectionId, long chatId, int messageId, CancellationToken cancellationToken);
}

internal sealed class TelegramTopicSendException : Exception
{
    public TelegramTopicSendException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal sealed class TelegramBotApiClient : ITelegramBotApiClient
{
    private readonly ITelegramBotClient _client;

    public TelegramBotApiClient(ITelegramBotClient client)
    {
        _client = client;
    }

    public async Task<int> SendMessageAsync(
        long chatId,
        string text,
        InlineKeyboardMarkup? replyMarkup,
        int? messageThreadId,
        CancellationToken cancellationToken)
    {
        global::Telegram.Bot.Types.Message message = await _client.SendMessage(
            chatId,
            text,
            replyMarkup: replyMarkup,
            messageThreadId: messageThreadId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return message.MessageId;
    }

    public Task EditMessageTextAsync(
        long chatId,
        int messageId,
        string text,
        InlineKeyboardMarkup? replyMarkup,
        CancellationToken cancellationToken)
        => _client.EditMessageText(
            chatId,
            messageId,
            text,
            global::Telegram.Bot.Types.Enums.ParseMode.None,
            replyMarkup,
            cancellationToken: cancellationToken);

    public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
        => _client.AnswerCallbackQuery(callbackQueryId, text, cancellationToken: cancellationToken);

    public Task SendChatActionAsync(long chatId, int? messageThreadId, CancellationToken cancellationToken)
        => _client.SendChatAction(
            chatId,
            global::Telegram.Bot.Types.Enums.ChatAction.Typing,
            messageThreadId: messageThreadId,
            cancellationToken: cancellationToken);

    public Task ReadBusinessMessageAsync(string businessConnectionId, long chatId, int messageId, CancellationToken cancellationToken)
        => _client.ReadBusinessMessage(businessConnectionId, chatId, messageId, cancellationToken);
}
