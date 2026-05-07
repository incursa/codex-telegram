namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Central authorization helper for Telegram users and chats.
/// </summary>
internal static class TelegramAuthorization
{
    /// <summary>
    /// Determines whether a Telegram user can operate the bot in the supplied chat.
    /// </summary>
    /// <param name="userId">Telegram user ID.</param>
    /// <param name="chatId">Telegram chat ID.</param>
    /// <param name="chatType">Telegram chat type.</param>
    /// <param name="allowedUserIds">Allowed Telegram user IDs.</param>
    /// <param name="allowedChatIds">Allowed group or supergroup chat IDs.</param>
    /// <param name="trustedChatIds">Group or supergroup chat IDs trusted from Telegram at runtime.</param>
    /// <returns><see langword="true"/> when the user and chat are authorized.</returns>
    public static bool IsAuthorized(
        long userId,
        long chatId,
        string chatType,
        IReadOnlyCollection<long> allowedUserIds,
        IReadOnlyCollection<long> allowedChatIds,
        IReadOnlyCollection<long>? trustedChatIds = null)
    {
        if (!allowedUserIds.Contains(userId))
        {
            return false;
        }

        if (IsPrivateChat(chatType))
        {
            return true;
        }

        // Private chats trust the user allowlist alone. Shared chats require a configured
        // allowlist entry or explicit runtime trust so Codex is not exposed in arbitrary groups.
        return allowedChatIds.Contains(chatId)
            || (trustedChatIds?.Contains(chatId) ?? false);
    }

    private static bool IsPrivateChat(string chatType)
        => chatType.Equals("private", StringComparison.OrdinalIgnoreCase);
}
