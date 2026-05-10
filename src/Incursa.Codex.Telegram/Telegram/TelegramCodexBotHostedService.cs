using System.Globalization;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Incursa.Codex.Telegram.Telegram;

internal sealed class TelegramCodexBotHostedService : BackgroundService
{
    private static readonly UpdateType[] AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery];

    private readonly ITelegramCodexBotUpdateHandler _handler;
    private readonly ITelegramBotMessageSender _sender;
    private readonly ITelegramBotStateStore _stateStore;
    private readonly TelegramBotOptions _options;
    private readonly ILogger<TelegramCodexBotHostedService> _logger;

    public TelegramCodexBotHostedService(
        ITelegramCodexBotUpdateHandler handler,
        ITelegramBotMessageSender sender,
        ITelegramBotStateStore stateStore,
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramCodexBotHostedService> logger)
    {
        _handler = handler;
        _sender = sender;
        _stateStore = stateStore;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Telegram bot is disabled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            _logger.LogWarning("Telegram bot is enabled but TelegramBot:Token is missing. Telegram integration will be disabled.");
            _options.Enabled = false;
            return;
        }

        TelegramBotClient client;
        try
        {
            client = new TelegramBotClient(_options.Token.Trim());
        }
        catch (ArgumentException exception)
        {
            _logger.LogError(exception, "Telegram bot token is invalid. Telegram integration will be disabled.");
            _options.Enabled = false;
            return;
        }

        int pollingTimeoutSeconds = Math.Clamp(_options.PollingTimeoutSeconds, 1, 120);
        int? offset = null;

        _logger.LogInformation(
            "Telegram bot long polling started with timeout {PollingTimeoutSeconds}s and {AllowedUserCount} allowed users.",
            pollingTimeoutSeconds,
            _options.AllowedUserIds.Length);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Update[] updates = await client.GetUpdates(
                    offset: offset,
                    timeout: pollingTimeoutSeconds,
                    allowedUpdates: AllowedUpdates,
                    cancellationToken: stoppingToken).ConfigureAwait(false);

                foreach (Update update in updates)
                {
                    offset = update.Id + 1;
                    try
                    {
                        await HandleUpdateAsync(new TelegramUpdateFileClient(client), update, _sender, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "Telegram update {UpdateId} failed. Polling will continue with the next update.", update.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (RequestException exception) when (stoppingToken.IsCancellationRequested && exception.InnerException is OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Telegram bot polling failed. Polling will retry.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Telegram bot long polling stopped.");
    }

    internal async Task HandleUpdateAsync(
        ITelegramUpdateFileClient client,
        Update update,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        Message? message = update.Message;
        if (message is not null)
        {
            _logger.LogDebug(
                "Received Telegram message {MessageId} from sender {SenderId} ({SenderKind}) in chat {ChatId} ({ChatType}) topic {MessageThreadId}; type {MessageType}; has text: {HasText}; has audio: {HasAudio}; has voice: {HasVoice}; has attachments: {HasAttachments}.",
                message.MessageId,
                GetSenderId(message),
                GetSenderKind(message),
                message.Chat.Id,
                message.Chat.Type,
                message.MessageThreadId,
                message.Type,
                !string.IsNullOrWhiteSpace(message.Text ?? message.Caption),
                message.Audio is not null,
                message.Voice is not null,
                HasAttachments(message));
        }

        if (message is not null && TryGetAudioMessage(message, out TelegramAudioMessage audioMessage))
        {
            if (!await IsAuthorizedAsync(message, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "Ignoring unauthorized Telegram audio sender {SenderId} ({SenderKind}) in chat {ChatId}.",
                    GetSenderId(message),
                    GetSenderKind(message),
                    message.Chat.Id);
                return;
            }

            if (!await ValidateAutoRouteAsync(message, sender, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (!await ValidateAudioMessageAsync(message, audioMessage, sender, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await HandleAudioMessageAsync(client, message, audioMessage.FileId, sender, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message is not null)
        {
            if (!await IsAuthorizedAsync(message, cancellationToken).ConfigureAwait(false))
            {
                if (CanForwardUnauthorizedSetupCommand(message))
                {
                    // `/whoami`, `/doctor`, and `/trust` are intentionally reachable before a
                    // shared chat is trusted so setup can be completed without manual ID copying.
                }
                else if (CanExplainUntrustedSharedChat(message))
                {
                    await sender.SendTextMessageAsync(
                        new TelegramConversationScope(message.Chat.Id, message.MessageThreadId),
                        "This chat is not trusted yet. Ask an allowlisted admin to send /trust here, or continue in a private chat.",
                        null,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                else
                {
                    _logger.LogWarning(
                        "Ignoring unauthorized Telegram sender {SenderId} ({SenderKind}) in chat {ChatId}.",
                        GetSenderId(message),
                        GetSenderKind(message),
                        message.Chat.Id);
                    return;
                }
            }

            string? text = message.Text ?? message.Caption;
            IReadOnlyList<TelegramAttachmentDescriptor>? attachments = null;
            AttachmentHandlingDecision attachmentDecision = await ResolveAttachmentHandlingAsync(message, sender, cancellationToken).ConfigureAwait(false);
            if (attachmentDecision is AttachmentHandlingDecision.Reject)
            {
                return;
            }

            if (attachmentDecision is AttachmentHandlingDecision.Download)
            {
                try
                {
                    attachments = await DownloadAttachmentsAsync(client, message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Telegram attachment download failed for chat {ChatId}.", message.Chat.Id);
                    try
                    {
                        await sender.SendTextMessageAsync(
                            new TelegramConversationScope(message.Chat.Id, message.MessageThreadId),
                            $"Attachment could not be processed: {exception.Message}",
                            null,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception sendException) when (sendException is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogError(sendException, "Failed to notify chat {ChatId} about an attachment processing failure.", message.Chat.Id);
                    }

                    return;
                }
            }

            if (text is null && (attachments is null || attachments.Count == 0))
            {
                return;
            }

            TelegramInboundMessage inbound = new(
                GetSenderId(message),
                message.Chat.Id,
                message.Chat.Type.ToString(),
                text,
                message.MessageThreadId,
                ChatTitle: message.Chat.Title);

            if (attachments is { Count: > 0 })
            {
                inbound = inbound with { Attachments = attachments };
            }

            await _handler.HandleMessageAsync(inbound, sender, cancellationToken).ConfigureAwait(false);
            return;
        }

        CallbackQuery? callback = update.CallbackQuery;
        if (callback?.Data is null || callback.Message is null)
        {
            return;
        }

        _logger.LogDebug(
            "Received Telegram callback {CallbackQueryId} from user {UserId} in chat {ChatId} ({ChatType}) topic {MessageThreadId} with data prefix {CallbackDataPrefix}.",
            callback.Id,
            callback.From.Id,
            callback.Message.Chat.Id,
            callback.Message.Chat.Type,
            callback.Message.MessageThreadId,
            GetCallbackDataPrefix(callback.Data));

        TelegramInboundCallback inboundCallback = new(
            callback.Id,
            callback.From.Id,
            callback.Message.Chat.Id,
            callback.Message.Chat.Type.ToString(),
            callback.Data,
            callback.Message.MessageThreadId,
            callback.Message.MessageId);

        await _handler.HandleCallbackAsync(inboundCallback, sender, cancellationToken).ConfigureAwait(false);
    }

    private static string GetCallbackDataPrefix(string data)
    {
        int separatorIndex = data.IndexOf(':', StringComparison.Ordinal);
        return separatorIndex < 0 ? data : data[..separatorIndex];
    }

    private async Task HandleAudioMessageAsync(
        ITelegramUpdateFileClient client,
        Message message,
        string audioFileId,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        TelegramConversationScope conversation = new(message.Chat.Id, message.MessageThreadId);
        string? tempAudioPath = null;

        try
        {
            TGFile file = await client.GetFileAsync(audioFileId, cancellationToken).ConfigureAwait(false);
            tempAudioPath = CreateTemporaryAudioPath(file.FilePath);
            await using (FileStream stream = File.Create(tempAudioPath))
            {
                await client.DownloadFileAsync(file, stream, cancellationToken).ConfigureAwait(false);
            }

            TelegramInboundMessage inbound = new(
                GetSenderId(message),
                message.Chat.Id,
                message.Chat.Type.ToString(),
                null,
                message.MessageThreadId,
                tempAudioPath,
                ChatTitle: message.Chat.Title);

            await _handler.HandleMessageAsync(inbound, sender, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Telegram audio download or forwarding failed for chat {ChatId}.", message.Chat.Id);
            try
            {
                await sender.SendTextMessageAsync(
                    conversation,
                    $"Audio message could not be processed: {exception.Message}",
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception sendException) when (sendException is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(sendException, "Failed to notify chat {ChatId} about an audio processing failure.", message.Chat.Id);
            }
        }
        finally
        {
            TryDelete(tempAudioPath);
        }
    }

    private async Task<bool> ValidateAudioMessageAsync(
        Message message,
        TelegramAudioMessage audioMessage,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        string? rejection = BuildAudioRejectionMessage(audioMessage);
        if (rejection is null)
        {
            return true;
        }

        await sender.SendTextMessageAsync(
            new TelegramConversationScope(message.Chat.Id, message.MessageThreadId),
            rejection,
            null,
            cancellationToken).ConfigureAwait(false);
        return false;
    }

    private static async Task<bool> ValidateAutoRouteAsync(
        Message message,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        string chatType = message.Chat.Type.ToString();
        if (TelegramRoutingPolicy.CanAutoRoute(chatType, message.MessageThreadId))
        {
            return true;
        }

        await sender.SendTextMessageAsync(
            new TelegramConversationScope(message.Chat.Id, message.MessageThreadId),
            TelegramRoutingPolicy.BuildNotRoutedMessage(chatType),
            null,
            cancellationToken).ConfigureAwait(false);
        return false;
    }

    private static async Task<AttachmentHandlingDecision> ResolveAttachmentHandlingAsync(
        Message message,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        if (!HasAttachments(message))
        {
            return AttachmentHandlingDecision.Skip;
        }

        if (IsSendCommandWithArguments(message))
        {
            return AttachmentHandlingDecision.Download;
        }

        if (IsCommandMessage(message))
        {
            return AttachmentHandlingDecision.Skip;
        }

        string chatType = message.Chat.Type.ToString();
        if (TelegramRoutingPolicy.CanAutoRoute(chatType, message.MessageThreadId))
        {
            return AttachmentHandlingDecision.Download;
        }

        await sender.SendTextMessageAsync(
            new TelegramConversationScope(message.Chat.Id, message.MessageThreadId),
            TelegramRoutingPolicy.BuildNotRoutedMessage(chatType),
            null,
            cancellationToken).ConfigureAwait(false);
        return AttachmentHandlingDecision.Reject;
    }

    private string? BuildAudioRejectionMessage(TelegramAudioMessage audioMessage)
    {
        if (audioMessage.DurationSeconds < _options.MinAudioDurationSeconds)
        {
            return $"Audio message is too short to transcribe. Record at least {FormatSeconds(_options.MinAudioDurationSeconds)}.";
        }

        if (audioMessage.DurationSeconds > _options.MaxAudioDurationSeconds)
        {
            return $"Audio message is too long to transcribe. Keep audio at or below {FormatSeconds(_options.MaxAudioDurationSeconds)}.";
        }

        return null;
    }

    private static bool TryGetAudioMessage(Message message, out TelegramAudioMessage audioMessage)
    {
        if (message.Voice is not null)
        {
            audioMessage = new TelegramAudioMessage(message.Voice.FileId, message.Voice.Duration);
            return true;
        }

        if (message.Audio is not null)
        {
            audioMessage = new TelegramAudioMessage(message.Audio.FileId, message.Audio.Duration);
            return true;
        }

        audioMessage = default;
        return false;
    }

    private static string FormatSeconds(int seconds)
        => seconds == 1 ? "1 second" : $"{seconds.ToString(CultureInfo.InvariantCulture)} seconds";

    private static bool HasAttachments(Message message)
        => message.Photo is { Length: > 0 }
            || message.Document is not null
            || message.Video is not null
            || message.Animation is not null
            || message.Sticker is not null
            || message.VideoNote is not null;

    private async Task<IReadOnlyList<TelegramAttachmentDescriptor>> DownloadAttachmentsAsync(
        ITelegramUpdateFileClient client,
        Message message,
        CancellationToken cancellationToken)
    {
        List<AttachmentDownloadRequest> requests = BuildAttachmentRequests(message);
        List<TelegramAttachmentDescriptor> attachments = new(requests.Count);

        try
        {
            foreach (AttachmentDownloadRequest request in requests)
            {
                attachments.Add(await DownloadAttachmentAsync(client, request, cancellationToken).ConfigureAwait(false));
            }
        }
        catch
        {
            TryDeleteAttachments(attachments);
            throw;
        }

        return attachments;
    }

    private static List<AttachmentDownloadRequest> BuildAttachmentRequests(Message message)
    {
        List<AttachmentDownloadRequest> requests = new();

        if (message.Photo is { Length: > 0 } photos)
        {
            PhotoSize photo = photos[^1];
            requests.Add(new AttachmentDownloadRequest(
                photo.FileId,
                "telegram-photo.jpg",
                "image/jpeg",
                true));
        }

        if (message.Document is not null)
        {
            requests.Add(new AttachmentDownloadRequest(
                message.Document.FileId,
                ResolveDisplayName(message.Document.FileName, "telegram-document", message.Document.MimeType, false),
                message.Document.MimeType,
                IsImageMimeType(message.Document.MimeType)));
        }

        if (message.Video is not null)
        {
            requests.Add(new AttachmentDownloadRequest(
                message.Video.FileId,
                ResolveDisplayName(message.Video.FileName, "telegram-video", message.Video.MimeType, false),
                message.Video.MimeType,
                false));
        }

        if (message.Animation is not null)
        {
            requests.Add(new AttachmentDownloadRequest(
                message.Animation.FileId,
                ResolveDisplayName(message.Animation.FileName, "telegram-animation", message.Animation.MimeType, false),
                message.Animation.MimeType,
                false));
        }

        if (message.Sticker is not null)
        {
            bool isImageSticker = !message.Sticker.IsAnimated && !message.Sticker.IsVideo;
            requests.Add(new AttachmentDownloadRequest(
                message.Sticker.FileId,
                isImageSticker ? "telegram-sticker.webp" : "telegram-sticker.bin",
                isImageSticker ? "image/webp" : null,
                isImageSticker));
        }

        if (message.VideoNote is not null)
        {
            requests.Add(new AttachmentDownloadRequest(
                message.VideoNote.FileId,
                "telegram-video-note.mp4",
                "video/mp4",
                false));
        }

        return requests;
    }

    private async Task<TelegramAttachmentDescriptor> DownloadAttachmentAsync(
        ITelegramUpdateFileClient client,
        AttachmentDownloadRequest request,
        CancellationToken cancellationToken)
    {
        TGFile file = await client.GetFileAsync(request.FileId, cancellationToken).ConfigureAwait(false);
        string tempPath = CreateTemporaryAttachmentPath(file.FilePath, request.FileName, request.ContentType);
        await using (FileStream stream = File.Create(tempPath))
        {
            await client.DownloadFileAsync(file, stream, cancellationToken).ConfigureAwait(false);
        }

        return new TelegramAttachmentDescriptor(
            tempPath,
            request.FileName,
            request.ContentType,
            request.IsImage);
    }

    private static string CreateTemporaryAudioPath(string? telegramFilePath)
    {
        string extension = string.IsNullOrWhiteSpace(telegramFilePath)
            ? ".ogg"
            : Path.GetExtension(telegramFilePath);

        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".ogg";
        }

        string directory = Path.Combine(Path.GetTempPath(), "codex-telegram", "telegram-audio");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():n}{extension}");
    }

    private static string CreateTemporaryAttachmentPath(string? telegramFilePath, string? fileName, string? contentType)
    {
        string extension = Path.GetExtension(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = Path.GetExtension(telegramFilePath ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = GuessExtensionFromContentType(contentType);
        }

        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }

        string directory = Path.Combine(Path.GetTempPath(), "codex-telegram", "telegram-attachments");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():n}{extension}");
    }

    private static string ResolveDisplayName(string? candidate, string fallbackBaseName, string? contentType, bool isImage)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        string extension = GuessExtensionFromContentType(contentType);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = isImage ? ".jpg" : ".bin";
        }

        return $"{fallbackBaseName}{extension}";
    }

    private static string GuessExtensionFromContentType(string? contentType)
        => contentType?.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "application/pdf" => ".pdf",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "application/octet-stream" => ".bin",
            _ => string.Empty,
        };

    private static bool IsImageMimeType(string? contentType)
        => !string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            File.Delete(filePath);
        }
        catch
        {
        }
    }

    private static void TryDeleteAttachments(IReadOnlyList<TelegramAttachmentDescriptor> attachments)
    {
        foreach (TelegramAttachmentDescriptor attachment in attachments)
        {
            TryDelete(attachment.FilePath);
        }
    }

    private async Task<bool> IsAuthorizedAsync(Message message, CancellationToken cancellationToken)
    {
        if (message.From is null)
        {
            return false;
        }

        IReadOnlyCollection<long> trustedChatIds = await _stateStore.GetTrustedChatIdsAsync(cancellationToken).ConfigureAwait(false);
        return TelegramAuthorization.IsAuthorized(
            message.From.Id,
            message.Chat.Id,
            message.Chat.Type.ToString(),
            _options.AllowedUserIds,
            _options.AllowedChatIds,
            trustedChatIds);
    }

    private bool CanForwardUnauthorizedSetupCommand(Message message)
        => IsWhoAmIMessage(message)
            || (IsAllowedUser(message)
                && !IsPrivateChat(message.Chat.Type.ToString())
                && (IsTrustMessage(message) || IsDoctorMessage(message)));

    private bool CanExplainUntrustedSharedChat(Message message)
        => IsAllowedUser(message)
            && !IsPrivateChat(message.Chat.Type.ToString())
            && IsCommandMessage(message);

    private bool IsAllowedUser(Message message)
        => message.From is not null && _options.AllowedUserIds.Contains(message.From.Id);

    private static bool IsWhoAmIMessage(Message message)
        => TryGetCommand(message, out string commandToken, out _)
            && commandToken.Equals("whoami", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustMessage(Message message)
        => TryGetCommand(message, out string commandToken, out _)
            && commandToken.Equals("trust", StringComparison.OrdinalIgnoreCase);

    private static bool IsDoctorMessage(Message message)
        => TryGetCommand(message, out string commandToken, out _)
            && (commandToken.Equals("doctor", StringComparison.OrdinalIgnoreCase)
                || commandToken.Equals("diag", StringComparison.OrdinalIgnoreCase)
                || commandToken.Equals("diagnostics", StringComparison.OrdinalIgnoreCase));

    private static bool IsCommandMessage(Message message)
        => TryGetCommand(message, out _, out _);

    private static bool IsPrivateChat(string chatType)
        => chatType.Equals("private", StringComparison.OrdinalIgnoreCase);

    private static bool IsSendCommandWithArguments(Message message)
        => TryGetCommand(message, out string commandToken, out string arguments)
            && commandToken.Equals("send", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(arguments);

    private static bool TryGetCommand(Message message, out string commandToken, out string arguments)
    {
        string? text = message.Text ?? message.Caption;
        if (string.IsNullOrWhiteSpace(text))
        {
            commandToken = string.Empty;
            arguments = string.Empty;
            return false;
        }

        string normalized = text.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            commandToken = string.Empty;
            arguments = string.Empty;
            return false;
        }

        int separatorIndex = normalized.IndexOfAny([' ', '\t', '\r', '\n']);
        commandToken = separatorIndex < 0 ? normalized[1..] : normalized[1..separatorIndex];
        arguments = separatorIndex < 0 ? string.Empty : normalized[(separatorIndex + 1)..].Trim();
        int mentionIndex = commandToken.IndexOf('@', StringComparison.Ordinal);
        if (mentionIndex >= 0)
        {
            commandToken = commandToken[..mentionIndex];
        }

        return true;
    }

    private static long GetSenderId(Message message)
        => message.From?.Id
            ?? message.SenderChat?.Id
            ?? message.Chat.Id;

    private static string GetSenderKind(Message message)
        => message.From is not null
            ? "user"
            : message.SenderChat is not null
                ? "sender-chat"
                : "chat";

    private sealed record AttachmentDownloadRequest(string FileId, string FileName, string? ContentType, bool IsImage);

    private readonly record struct TelegramAudioMessage(string FileId, int DurationSeconds);

    private enum AttachmentHandlingDecision
    {
        Skip,
        Download,
        Reject,
    }
}

internal interface ITelegramUpdateFileClient
{
    Task<TGFile> GetFileAsync(string fileId, CancellationToken cancellationToken);

    Task DownloadFileAsync(TGFile file, Stream destination, CancellationToken cancellationToken);
}

internal sealed class TelegramUpdateFileClient : ITelegramUpdateFileClient
{
    private readonly ITelegramBotClient _client;

    public TelegramUpdateFileClient(ITelegramBotClient client)
    {
        _client = client;
    }

    public Task<TGFile> GetFileAsync(string fileId, CancellationToken cancellationToken)
        => _client.GetFile(fileId, cancellationToken);

    public Task DownloadFileAsync(TGFile file, Stream destination, CancellationToken cancellationToken)
        => _client.DownloadFile(file, destination, cancellationToken);
}
