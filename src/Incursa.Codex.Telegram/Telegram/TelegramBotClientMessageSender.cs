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
    private readonly Lazy<ITelegramBotClient> _client;

    public TelegramBotClientMessageSender(
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramBotClientMessageSender> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new Lazy<ITelegramBotClient>(() => new TelegramBotClient(RequireToken()));
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
                "Telegram rejected a reply to chat {ChatId} topic {MessageThreadId}. Retrying in the main chat.",
                conversation.ChatId,
                conversation.MessageThreadId);
            await SendMessageAsync(conversation with { MessageThreadId = null }, text, buttons, cancellationToken).ConfigureAwait(false);
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
            await SendMessageAsync(conversation with { MessageThreadId = null }, text, null, cancellationToken).ConfigureAwait(false);
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
            await _client.Value.EditMessageText(
                conversation.ChatId,
                messageId,
                text,
                global::Telegram.Bot.Types.Enums.ParseMode.None,
                ToInlineKeyboardMarkup(buttons),
                null,
                null,
                null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

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
            await _client.Value.AnswerCallbackQuery(callbackQueryId, text, cancellationToken: cancellationToken).ConfigureAwait(false);
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
        await _client.Value.SendMessage(
            conversation.ChatId,
            text,
            replyMarkup: ToInlineKeyboardMarkup(buttons),
            messageThreadId: conversation.MessageThreadId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Telegram send succeeded for chat {ChatId} topic {MessageThreadId}; text length {TextLength}; button rows {ButtonRowCount}.",
            conversation.ChatId,
            conversation.MessageThreadId,
            text.Length,
            buttons?.Count ?? 0);
    }

    private string RequireToken()
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException("TelegramBot:Token must be configured when TelegramBot:Enabled is true.");
        }

        return _options.Token.Trim();
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
