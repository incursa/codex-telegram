using Incursa.Codex.Telegram.Configuration;
using Microsoft.Extensions.Logging;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Owns the short-lived, private-chat-only state used when an administrator chooses to enter an
/// OpenAI transcription key through Telegram. The key itself is never held in this state object.
/// </summary>
internal interface IOpenAiCredentialSetupService
{
    void Begin(long userId, long chatId);

    void Cancel(long userId, long chatId);

    bool IsAwaitingApiKey(long userId, long chatId);

    Task<bool> TryHandlePendingMessageAsync(
        long userId,
        long chatId,
        string chatType,
        int? sourceMessageId,
        string? text,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken);
}

internal sealed class OpenAiCredentialSetupService : IOpenAiCredentialSetupService
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);
    private const int MinimumApiKeyLength = 20;
    private const int MaximumApiKeyLength = 512;

    private readonly LocalSettingsStore _settingsStore;
    private readonly ILogger<OpenAiCredentialSetupService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<CredentialSetupKey, DateTimeOffset> _pending = [];

    public OpenAiCredentialSetupService(
        LocalSettingsStore settingsStore,
        ILogger<OpenAiCredentialSetupService> logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public void Begin(long userId, long chatId)
    {
        lock (_gate)
        {
            RemoveExpiredEntries(DateTimeOffset.UtcNow);
            _pending[new CredentialSetupKey(userId, chatId)] = DateTimeOffset.UtcNow.Add(PendingLifetime);
        }
    }

    public void Cancel(long userId, long chatId)
    {
        lock (_gate)
        {
            _pending.Remove(new CredentialSetupKey(userId, chatId));
        }
    }

    public bool IsAwaitingApiKey(long userId, long chatId)
    {
        lock (_gate)
        {
            RemoveExpiredEntries(DateTimeOffset.UtcNow);
            return _pending.ContainsKey(new CredentialSetupKey(userId, chatId));
        }
    }

    public async Task<bool> TryHandlePendingMessageAsync(
        long userId,
        long chatId,
        string chatType,
        int? sourceMessageId,
        string? text,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        if (!TelegramRoutingPolicy.IsPrivateChat(chatType) || !IsAwaitingApiKey(userId, chatId))
        {
            return false;
        }

        if (string.Equals(text?.Trim(), "/cancel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text?.Trim(), "/setup cancel", StringComparison.OrdinalIgnoreCase))
        {
            Cancel(userId, chatId);
            await sender.SendTextMessageAsync(
                new TelegramConversationScope(chatId, null),
                "Telegram setup cancelled.",
                null,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Text messages always have a Telegram message ID in production. If a caller cannot
        // provide one, refuse to process the candidate rather than accepting an undeletable key.
        if (sourceMessageId is not int messageId)
        {
            Cancel(userId, chatId);
            await sender.SendTextMessageAsync(
                new TelegramConversationScope(chatId, null),
                "I could not safely receive that key because the source message could not be deleted. Run /setup openai-key again.",
                null,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        Cancel(userId, chatId);
        bool deleted = await sender.TryDeleteMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            _logger.LogWarning(
                "Could not delete a Telegram OpenAI key message for user {UserId} in private chat {ChatId}; the key was not saved.",
                userId,
                chatId);
            await sender.SendTextMessageAsync(
                new TelegramConversationScope(chatId, null),
                "I could not delete that message, so I did not save the key. Delete it manually and run /setup openai-key again.",
                null,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        string candidate = text?.Trim() ?? string.Empty;
        if (!IsPlausibleApiKey(candidate))
        {
            await sender.SendTextMessageAsync(
                new TelegramConversationScope(chatId, null),
                "That did not look like a usable API key. The message was deleted; run /setup openai-key to try again.",
                null,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        try
        {
            _settingsStore.SetOpenAiApiKey(candidate);
            _settingsStore.Save();
        }
        catch (Exception exception)
        {
            // Do not include the candidate in this log or in the user-facing response.
            _logger.LogError(
                exception,
                "Could not save the OpenAI API key received through Telegram for private chat {ChatId}.",
                chatId);
            await sender.SendTextMessageAsync(
                new TelegramConversationScope(chatId, null),
                "The key message was deleted, but I could not save the key locally. Please use the CLI setup menu and try again.",
                null,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        await sender.SendTextMessageAsync(
            new TelegramConversationScope(chatId, null),
            "OpenAI transcription is configured. The key was saved locally and voice messages can use it now.",
            null,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool IsPlausibleApiKey(string candidate)
        => candidate.Length is >= MinimumApiKeyLength and <= MaximumApiKeyLength
            && !candidate.Any(char.IsWhiteSpace)
            && !candidate.Contains('\u0000');

    private void RemoveExpiredEntries(DateTimeOffset now)
    {
        foreach (CredentialSetupKey key in _pending
                     .Where(pair => pair.Value <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _pending.Remove(key);
        }
    }

    private readonly record struct CredentialSetupKey(long UserId, long ChatId);
}

internal sealed class NullOpenAiCredentialSetupService : IOpenAiCredentialSetupService
{
    public static NullOpenAiCredentialSetupService Instance { get; } = new();

    public void Begin(long userId, long chatId)
    {
    }

    public void Cancel(long userId, long chatId)
    {
    }

    public bool IsAwaitingApiKey(long userId, long chatId)
        => false;

    public Task<bool> TryHandlePendingMessageAsync(
        long userId,
        long chatId,
        string chatType,
        int? sourceMessageId,
        string? text,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
        => Task.FromResult(false);
}
