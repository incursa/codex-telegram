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
    private readonly ITelegramDebugPreambleMode _debugPreambleMode;
    private readonly ITelegramDebugTraceStore _traceStore;

    public TelegramBotClientMessageSender(
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramBotClientMessageSender> logger)
        : this(
            options.Value,
            logger,
            new Lazy<ITelegramBotApiClient>(() => new TelegramBotApiClient(new TelegramBotClient(RequireToken(options.Value)))),
            NullTelegramMessageContextStore.Instance,
            DisabledTelegramDebugPreambleMode.Instance,
            NullTelegramDebugTraceStore.Instance)
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
            messageContextStore,
            DisabledTelegramDebugPreambleMode.Instance,
            NullTelegramDebugTraceStore.Instance)
    {
    }

    public TelegramBotClientMessageSender(
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramBotClientMessageSender> logger,
        ITelegramMessageContextStore messageContextStore,
        ITelegramDebugPreambleMode debugPreambleMode)
        : this(options, logger, messageContextStore, debugPreambleMode, NullTelegramDebugTraceStore.Instance)
    {
    }

    public TelegramBotClientMessageSender(
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramBotClientMessageSender> logger,
        ITelegramMessageContextStore messageContextStore,
        ITelegramDebugPreambleMode debugPreambleMode,
        ITelegramDebugTraceStore traceStore)
        : this(
            options.Value,
            logger,
            new Lazy<ITelegramBotApiClient>(() => new TelegramBotApiClient(new TelegramBotClient(RequireToken(options.Value)))),
            messageContextStore,
            debugPreambleMode,
            traceStore)
    {
    }

    internal TelegramBotClientMessageSender(
        TelegramBotOptions options,
        ILogger<TelegramBotClientMessageSender> logger,
        ITelegramBotApiClient client,
        ITelegramMessageContextStore? messageContextStore = null,
        ITelegramDebugPreambleMode? debugPreambleMode = null,
        ITelegramDebugTraceStore? traceStore = null)
        : this(
            options,
            logger,
            new Lazy<ITelegramBotApiClient>(() => client),
            messageContextStore ?? NullTelegramMessageContextStore.Instance,
            debugPreambleMode ?? DisabledTelegramDebugPreambleMode.Instance,
            traceStore ?? NullTelegramDebugTraceStore.Instance)
    {
    }

    private TelegramBotClientMessageSender(
        TelegramBotOptions options,
        ILogger<TelegramBotClientMessageSender> logger,
        Lazy<ITelegramBotApiClient> client,
        ITelegramMessageContextStore messageContextStore,
        ITelegramDebugPreambleMode debugPreambleMode,
        ITelegramDebugTraceStore traceStore)
    {
        _options = options;
        _logger = logger;
        _client = client;
        _messageContextStore = messageContextStore;
        _debugPreambleMode = debugPreambleMode;
        _traceStore = traceStore;
    }

    public async Task SendTextMessageAsync(
        TelegramConversationScope conversation,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext = null)
        => _ = await SendTextMessageAndGetIdAsync(conversation, text, buttons, cancellationToken, debugContext).ConfigureAwait(false);

    public async Task<int?> SendTextMessageAndGetIdAsync(
        TelegramConversationScope conversation,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext = null)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        try
        {
            return await SendMessageReturningIdAsync(conversation, text, buttons, cancellationToken, debugContext).ConfigureAwait(false);
        }
        catch (ApiRequestException exception) when (conversation.MessageThreadId is not null && IsThreadReplyFailure(exception))
        {
            _logger.LogWarning(
                exception,
                "Telegram rejected a reply to chat {ChatId} topic {MessageThreadId}. The message was not retried in the main chat to preserve topic isolation.",
                conversation.ChatId,
                conversation.MessageThreadId);
            return null;
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
            return null;
        }
    }

    async Task IOutboundTelegramMessageSender.SendTextMessageAsync(
        TelegramConversationScope conversation,
        string text,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            await SendMessageAsync(conversation, text, null, cancellationToken, debugContext).ConfigureAwait(false);
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

    async Task IOutboundTelegramMessageSender.SendFileMessageAsync(
        TelegramConversationScope conversation,
        OutboundTelegramFile file,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            await SendFileMessageAsync(conversation, file, cancellationToken, debugContext).ConfigureAwait(false);
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
                $"Telegram rejected a file send to chat {conversation.ChatId} topic {conversation.MessageThreadId}; the file was not retried in the main chat.",
                exception);
        }
    }

    public async Task EditTextMessageAsync(
        TelegramConversationScope conversation,
        int messageId,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext = null)
        => _ = await EditTextMessageOrSendReplacementAsync(
            conversation,
            messageId,
            text,
            buttons,
            cancellationToken,
            debugContext).ConfigureAwait(false);

    public async Task<int?> EditTextMessageOrSendReplacementAsync(
        TelegramConversationScope conversation,
        int messageId,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext = null)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        try
        {
            string sendText = ApplyDebugPreamble(conversation, text, debugContext);
            string? traceId = ResolveTraceId(debugContext);
            await RecordTelegramApiTraceAsync(
                traceId,
                conversation,
                "telegram.api.edit.attempt",
                "attempt",
                text,
                messageId,
                error: null,
                new Dictionary<string, string>
                {
                    ["buttonRows"] = (buttons?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                cancellationToken).ConfigureAwait(false);
            await _client.Value.EditMessageTextAsync(
                conversation.ChatId,
                messageId,
                sendText,
                ToInlineKeyboardMarkup(buttons),
                cancellationToken).ConfigureAwait(false);
            await RecordTelegramApiTraceAsync(
                traceId,
                conversation,
                "telegram.api.edit.succeeded",
                "succeeded",
                text,
                messageId,
                error: null,
                null,
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
                sendText.Length,
                buttons?.Count ?? 0);
            return messageId;
        }
        catch (ApiRequestException exception) when (IsMessageNotModified(exception))
        {
            await RecordTelegramApiTraceAsync(
                ResolveTraceId(debugContext),
                conversation,
                "telegram.api.edit.noop",
                "not_modified",
                text,
                messageId,
                exception.Message,
                null,
                cancellationToken).ConfigureAwait(false);
            _logger.LogDebug(
                exception,
                "Telegram edit for chat {ChatId} message {MessageId} was a no-op because the content did not change.",
                conversation.ChatId,
                messageId);
            return messageId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RecordTelegramApiTraceAsync(
                ResolveTraceId(debugContext),
                conversation,
                "telegram.api.edit.failed",
                "failed",
                text,
                messageId,
                exception.Message,
                null,
                cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                exception,
                "Telegram edit failed for chat {ChatId} message {MessageId}; falling back to a new message.",
                conversation.ChatId,
                messageId);
            return await SendTextMessageAndGetIdAsync(conversation, text, buttons, cancellationToken, debugContext).ConfigureAwait(false);
        }
    }

    public async Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        string? traceId = _traceStore.IsFileTraceEnabled ? _traceStore.CreateTraceId() : null;
        try
        {
            await RecordTelegramApiTraceAsync(
                traceId,
                conversation: null,
                "telegram.api.callback_answer.attempt",
                "attempt",
                text,
                messageId: null,
                error: null,
                new Dictionary<string, string> { ["callbackQueryId"] = callbackQueryId },
                cancellationToken).ConfigureAwait(false);
            await _client.Value.AnswerCallbackQueryAsync(callbackQueryId, text, cancellationToken).ConfigureAwait(false);
            await RecordTelegramApiTraceAsync(
                traceId,
                conversation: null,
                "telegram.api.callback_answer.succeeded",
                "succeeded",
                text,
                messageId: null,
                error: null,
                new Dictionary<string, string> { ["callbackQueryId"] = callbackQueryId },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RecordTelegramApiTraceAsync(
                traceId,
                conversation: null,
                "telegram.api.callback_answer.failed",
                "failed",
                text,
                messageId: null,
                exception.Message,
                new Dictionary<string, string> { ["callbackQueryId"] = callbackQueryId },
                cancellationToken).ConfigureAwait(false);
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

            await ReactToMessageAsync(
                new TelegramMessageReaction(
                    acknowledgement.Conversation,
                    acknowledgement.MessageId,
                    TelegramMessageReactionKind.Accepted),
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
                "Telegram acknowledgement failed for chat {ChatId} topic {MessageThreadId} message {MessageId}; continuing.",
                acknowledgement.Conversation.ChatId,
                acknowledgement.Conversation.MessageThreadId,
                acknowledgement.MessageId);
        }
    }

    public async Task ReactToMessageAsync(TelegramMessageReaction reaction, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || reaction.MessageId <= 0)
        {
            return;
        }

        string? traceId = _traceStore.IsFileTraceEnabled ? _traceStore.CreateTraceId() : null;
        IReadOnlyDictionary<string, string> metadata = new Dictionary<string, string>
        {
            ["reactionKind"] = reaction.Kind.ToString(),
        };
        await RecordTelegramApiTraceAsync(
            traceId,
            reaction.Conversation,
            "telegram.api.reaction.attempt",
            "attempt",
            text: null,
            messageId: reaction.MessageId,
            error: null,
            metadata: metadata,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await _client.Value.SetMessageReactionAsync(
                reaction.Conversation.ChatId,
                reaction.MessageId,
                ResolveReactionEmoji(reaction.Kind),
                isBig: false,
                cancellationToken).ConfigureAwait(false);
            await RecordTelegramApiTraceAsync(
                traceId,
                reaction.Conversation,
                "telegram.api.reaction.succeeded",
                "succeeded",
                text: null,
                messageId: reaction.MessageId,
                error: null,
                metadata: metadata,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RecordTelegramApiTraceAsync(
                traceId,
                reaction.Conversation,
                "telegram.api.reaction.failed",
                "failed",
                text: null,
                messageId: reaction.MessageId,
                error: exception.Message,
                metadata: metadata,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogDebug(
                exception,
                "Telegram reaction {ReactionKind} failed for chat {ChatId} message {MessageId}; continuing.",
                reaction.Kind,
                reaction.Conversation.ChatId,
                reaction.MessageId);
        }
    }

    public async Task SendTypingActionAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        string? traceId = _traceStore.IsFileTraceEnabled ? _traceStore.CreateTraceId() : null;
        try
        {
            await RecordTelegramApiTraceAsync(
                traceId,
                conversation,
                "telegram.api.chat_action.attempt",
                "attempt",
                text: null,
                messageId: null,
                error: null,
                metadata: new Dictionary<string, string> { ["activity"] = TelegramChatActivity.Typing.ToString() },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await _client.Value.SendChatActionAsync(
                conversation.ChatId,
                conversation.MessageThreadId,
                TelegramChatActivity.Typing,
                cancellationToken).ConfigureAwait(false);
            await RecordTelegramApiTraceAsync(
                traceId,
                conversation,
                "telegram.api.chat_action.succeeded",
                "succeeded",
                text: null,
                messageId: null,
                error: null,
                metadata: new Dictionary<string, string> { ["activity"] = TelegramChatActivity.Typing.ToString() },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RecordTelegramApiTraceAsync(
                traceId,
                conversation,
                "telegram.api.chat_action.failed",
                "failed",
                text: null,
                messageId: null,
                error: exception.Message,
                metadata: new Dictionary<string, string> { ["activity"] = TelegramChatActivity.Typing.ToString() },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogDebug(
                exception,
                "Telegram typing action failed for chat {ChatId} topic {MessageThreadId}; continuing.",
                conversation.ChatId,
                conversation.MessageThreadId);
        }
    }

    private async Task SendFileMessageAsync(
        TelegramConversationScope conversation,
        OutboundTelegramFile file,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext)
    {
        string fileName = ResolveFileName(file);
        string caption = BuildFileCaption(conversation, file, fileName, debugContext);
        if (!File.Exists(file.Path))
        {
            await SendMessageAsync(
                conversation,
                $"Codex artifact could not be sent because the file is no longer available: {fileName}",
                null,
                cancellationToken,
                debugContext).ConfigureAwait(false);
            return;
        }

        TelegramChatActivity activity = file.Kind == TelegramOutboundFileKind.Photo
            ? TelegramChatActivity.UploadPhoto
            : TelegramChatActivity.UploadDocument;
        await TrySendChatActionAsync(conversation, activity, cancellationToken).ConfigureAwait(false);

        int messageId = file.Kind == TelegramOutboundFileKind.Photo
            ? await SendPhotoWithTraceAsync(conversation, file, fileName, caption, cancellationToken, debugContext).ConfigureAwait(false)
            : await SendDocumentWithTraceAsync(conversation, file, fileName, caption, cancellationToken, debugContext).ConfigureAwait(false);

        await _messageContextStore.RecordAsync(
            new TelegramMessageContextRecord(
                conversation,
                messageId,
                TelegramMessageAuthor.Bot,
                string.IsNullOrWhiteSpace(file.Caption) ? fileName : file.Caption,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Telegram {FileKind} send succeeded for chat {ChatId} topic {MessageThreadId}; file {FileName}; caption length {CaptionLength}.",
            file.Kind,
            conversation.ChatId,
            conversation.MessageThreadId,
            fileName,
            caption.Length);
    }

    private async Task TrySendChatActionAsync(TelegramConversationScope conversation, TelegramChatActivity activity, CancellationToken cancellationToken)
    {
        string? traceId = _traceStore.IsFileTraceEnabled ? _traceStore.CreateTraceId() : null;
        try
        {
            await RecordTelegramApiTraceAsync(
                traceId,
                conversation,
                "telegram.api.chat_action.attempt",
                "attempt",
                text: null,
                messageId: null,
                error: null,
                metadata: new Dictionary<string, string> { ["activity"] = activity.ToString() },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await _client.Value.SendChatActionAsync(
                conversation.ChatId,
                conversation.MessageThreadId,
                activity,
                cancellationToken).ConfigureAwait(false);
            await RecordTelegramApiTraceAsync(
                traceId,
                conversation,
                "telegram.api.chat_action.succeeded",
                "succeeded",
                text: null,
                messageId: null,
                error: null,
                metadata: new Dictionary<string, string> { ["activity"] = activity.ToString() },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RecordTelegramApiTraceAsync(
                traceId,
                conversation,
                "telegram.api.chat_action.failed",
                "failed",
                text: null,
                messageId: null,
                error: exception.Message,
                metadata: new Dictionary<string, string> { ["activity"] = activity.ToString() },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogDebug(
                exception,
                "Telegram chat action {Activity} failed for chat {ChatId} topic {MessageThreadId}; continuing with send.",
                activity,
                conversation.ChatId,
                conversation.MessageThreadId);
        }
    }

    private async Task<int> SendPhotoWithTraceAsync(
        TelegramConversationScope conversation,
        OutboundTelegramFile file,
        string fileName,
        string caption,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext)
    {
        string? traceId = ResolveTraceId(debugContext);
        IReadOnlyDictionary<string, string> metadata = BuildFileTraceMetadata(file, fileName);
        await RecordTelegramApiTraceAsync(traceId, conversation, "telegram.api.file_send.attempt", "attempt", caption, null, null, metadata, cancellationToken).ConfigureAwait(false);
        try
        {
            int messageId = await _client.Value.SendPhotoAsync(
                conversation.ChatId,
                file.Path,
                fileName,
                caption,
                conversation.MessageThreadId,
                cancellationToken).ConfigureAwait(false);
            await RecordTelegramApiTraceAsync(traceId, conversation, "telegram.api.file_send.succeeded", "succeeded", caption, messageId, null, metadata, cancellationToken).ConfigureAwait(false);
            return messageId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RecordTelegramApiTraceAsync(traceId, conversation, "telegram.api.file_send.failed", "failed", caption, null, exception.Message, metadata, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<int> SendDocumentWithTraceAsync(
        TelegramConversationScope conversation,
        OutboundTelegramFile file,
        string fileName,
        string caption,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext)
    {
        string? traceId = ResolveTraceId(debugContext);
        IReadOnlyDictionary<string, string> metadata = BuildFileTraceMetadata(file, fileName);
        await RecordTelegramApiTraceAsync(traceId, conversation, "telegram.api.file_send.attempt", "attempt", caption, null, null, metadata, cancellationToken).ConfigureAwait(false);
        try
        {
            int messageId = await _client.Value.SendDocumentAsync(
                conversation.ChatId,
                file.Path,
                fileName,
                caption,
                conversation.MessageThreadId,
                cancellationToken).ConfigureAwait(false);
            await RecordTelegramApiTraceAsync(traceId, conversation, "telegram.api.file_send.succeeded", "succeeded", caption, messageId, null, metadata, cancellationToken).ConfigureAwait(false);
            return messageId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RecordTelegramApiTraceAsync(traceId, conversation, "telegram.api.file_send.failed", "failed", caption, null, exception.Message, metadata, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private Task SendMessageAsync(
        TelegramConversationScope conversation,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext)
        => SendMessageReturningIdAsync(conversation, text, buttons, cancellationToken, debugContext);

    private async Task<int> SendMessageReturningIdAsync(
        TelegramConversationScope conversation,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext)
    {
        string sendText = ApplyDebugPreamble(conversation, text, debugContext);
        string? traceId = ResolveTraceId(debugContext);
        await RecordTelegramApiTraceAsync(
            traceId,
            conversation,
            "telegram.api.send.attempt",
            "attempt",
            text,
            messageId: null,
            error: null,
            new Dictionary<string, string>
            {
                ["buttonRows"] = (buttons?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            cancellationToken).ConfigureAwait(false);
        int messageId;
        try
        {
            messageId = await _client.Value.SendMessageAsync(
                conversation.ChatId,
                sendText,
                ToInlineKeyboardMarkup(buttons),
                conversation.MessageThreadId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RecordTelegramApiTraceAsync(
                traceId,
                conversation,
                "telegram.api.send.failed",
                "failed",
                text,
                messageId: null,
                exception.Message,
                null,
                cancellationToken).ConfigureAwait(false);
            throw;
        }

        await RecordTelegramApiTraceAsync(
            traceId,
            conversation,
            "telegram.api.send.succeeded",
            "succeeded",
            text,
            messageId,
            error: null,
            null,
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
            sendText.Length,
            buttons?.Count ?? 0);

        return messageId;
    }

    private string ApplyDebugPreamble(TelegramConversationScope conversation, string text, TelegramDebugMessageContext? debugContext)
        => _debugPreambleMode.IsEnabled
            ? TelegramDebugPreambleFormatter.Apply(conversation, text, debugContext)
            : text;

    private Task RecordTelegramApiTraceAsync(
        string? traceId,
        TelegramConversationScope? conversation,
        string kind,
        string status,
        string? text,
        int? messageId,
        string? error,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return Task.CompletedTask;
        }

        Dictionary<string, string>? eventMetadata = metadata is null
            ? null
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        if (messageId.HasValue)
        {
            eventMetadata ??= new Dictionary<string, string>(StringComparer.Ordinal);
            eventMetadata["messageId"] = messageId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return _traceStore.RecordAsync(
            new TelegramDebugTraceEvent(
                traceId,
                DateTimeOffset.UtcNow,
                kind,
                ChatId: conversation?.ChatId,
                MessageThreadId: conversation?.MessageThreadId,
                Direction: "outbound",
                Status: status,
                TextLength: text?.Length ?? 0,
                Error: error,
                Metadata: eventMetadata,
                TextBody: text,
                Source: "TelegramOutbound"),
            cancellationToken);
    }

    private static string? ResolveTraceId(TelegramDebugMessageContext? debugContext)
        => string.IsNullOrWhiteSpace(debugContext?.TraceId) ? null : debugContext.TraceId;

    private static IReadOnlyDictionary<string, string> BuildFileTraceMetadata(OutboundTelegramFile file, string fileName)
    {
        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["fileKind"] = file.Kind.ToString(),
            ["fileName"] = fileName,
            ["filePath"] = file.Path,
        };
        if (!string.IsNullOrWhiteSpace(file.ContentType))
        {
            metadata["contentType"] = file.ContentType;
        }

        if (File.Exists(file.Path))
        {
            metadata["fileBytes"] = new FileInfo(file.Path).Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return metadata;
    }

    private static string ResolveFileName(OutboundTelegramFile file)
        => string.IsNullOrWhiteSpace(file.FileName)
            ? Path.GetFileName(file.Path)
            : Path.GetFileName(file.FileName.Trim());

    private string BuildFileCaption(
        TelegramConversationScope conversation,
        OutboundTelegramFile file,
        string fileName,
        TelegramDebugMessageContext? debugContext)
    {
        string caption = string.IsNullOrWhiteSpace(file.Caption) ? fileName : file.Caption.Trim();
        if (_debugPreambleMode.IsEnabled)
        {
            caption = ApplyDebugPreamble(conversation, caption, debugContext);
        }

        const int maxTelegramCaptionLength = 1024;
        return caption.Length <= maxTelegramCaptionLength
            ? caption
            : caption[..(maxTelegramCaptionLength - 3)] + "...";
    }

    private static string ResolveReactionEmoji(TelegramMessageReactionKind kind)
        => kind switch
        {
            TelegramMessageReactionKind.Completed => "\u2705",
            TelegramMessageReactionKind.Failed => "\U0001F628",
            _ => "\U0001F440",
        };

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

    Task SendChatActionAsync(long chatId, int? messageThreadId, TelegramChatActivity activity, CancellationToken cancellationToken);

    Task<int> SendPhotoAsync(
        long chatId,
        string filePath,
        string fileName,
        string? caption,
        int? messageThreadId,
        CancellationToken cancellationToken);

    Task<int> SendDocumentAsync(
        long chatId,
        string filePath,
        string fileName,
        string? caption,
        int? messageThreadId,
        CancellationToken cancellationToken);

    Task SetMessageReactionAsync(long chatId, int messageId, string emoji, bool isBig, CancellationToken cancellationToken);

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

    public Task SendChatActionAsync(long chatId, int? messageThreadId, TelegramChatActivity activity, CancellationToken cancellationToken)
        => _client.SendChatAction(
            chatId,
            ToTelegramChatAction(activity),
            messageThreadId: messageThreadId,
            cancellationToken: cancellationToken);

    public async Task<int> SendPhotoAsync(
        long chatId,
        string filePath,
        string fileName,
        string? caption,
        int? messageThreadId,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(filePath);
        global::Telegram.Bot.Types.Message message = await _client.SendPhoto(
            chatId,
            global::Telegram.Bot.Types.InputFile.FromStream(stream, fileName),
            caption: caption,
            messageThreadId: messageThreadId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return message.MessageId;
    }

    public async Task<int> SendDocumentAsync(
        long chatId,
        string filePath,
        string fileName,
        string? caption,
        int? messageThreadId,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(filePath);
        global::Telegram.Bot.Types.Message message = await _client.SendDocument(
            chatId,
            global::Telegram.Bot.Types.InputFile.FromStream(stream, fileName),
            caption: caption,
            messageThreadId: messageThreadId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return message.MessageId;
    }

    public Task SetMessageReactionAsync(long chatId, int messageId, string emoji, bool isBig, CancellationToken cancellationToken)
    {
        global::Telegram.Bot.Types.ReactionType[] reaction = [emoji];
        return _client.SetMessageReaction(chatId, messageId, reaction, isBig, cancellationToken);
    }

    public Task ReadBusinessMessageAsync(string businessConnectionId, long chatId, int messageId, CancellationToken cancellationToken)
        => _client.ReadBusinessMessage(businessConnectionId, chatId, messageId, cancellationToken);

    private static global::Telegram.Bot.Types.Enums.ChatAction ToTelegramChatAction(TelegramChatActivity activity)
        => activity switch
        {
            TelegramChatActivity.UploadPhoto => global::Telegram.Bot.Types.Enums.ChatAction.UploadPhoto,
            TelegramChatActivity.UploadDocument => global::Telegram.Bot.Types.Enums.ChatAction.UploadDocument,
            _ => global::Telegram.Bot.Types.Enums.ChatAction.Typing,
        };
}
