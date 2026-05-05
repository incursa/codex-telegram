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
    /// <returns><see langword="true"/> when the user and chat are authorized.</returns>
    public static bool IsAuthorized(
        long userId,
        long chatId,
        string chatType,
        IReadOnlyCollection<long> allowedUserIds,
        IReadOnlyCollection<long> allowedChatIds)
    {
        if (!allowedUserIds.Contains(userId))
        {
            return false;
        }

        if (IsPrivateChat(chatType))
        {
            return true;
        }

        // Private chats trust the user allowlist alone. Shared chats require an explicit chat
        // allowlist too so an allowed user cannot accidentally expose Codex in an arbitrary group.
        return allowedChatIds.Contains(chatId);
    }

    private static bool IsPrivateChat(string chatType)
        => chatType.Equals("private", StringComparison.OrdinalIgnoreCase);
}
