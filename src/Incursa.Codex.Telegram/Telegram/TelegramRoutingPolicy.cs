namespace Incursa.Codex.Telegram.Telegram;

internal static class TelegramRoutingPolicy
{
    public static bool CanAutoRoute(string chatType, int? messageThreadId)
        => IsPrivateChat(chatType) || messageThreadId is not null;

    public static bool IsPrivateChat(string chatType)
        => string.Equals(chatType, "private", StringComparison.OrdinalIgnoreCase);

    public static string BuildNotRoutedMessage(string chatType)
        => string.Equals(chatType, "supergroup", StringComparison.OrdinalIgnoreCase)
            ? "I only auto-route messages from private chats or forum topics. This message was sent to the group root, so I did not send it to Codex. Use /send <text>, open a topic, or message me privately."
            : $"I only auto-route messages from private chats or topic threads. This {chatType} message was not sent to Codex. Use /send <text> or message me privately.";
}
