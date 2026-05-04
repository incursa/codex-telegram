namespace Incursa.Codex.Telegram.Telegram;

internal static class TelegramAuthorization
{
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

        return allowedChatIds.Contains(chatId);
    }

    private static bool IsPrivateChat(string chatType)
        => chatType.Equals("private", StringComparison.OrdinalIgnoreCase);
}
