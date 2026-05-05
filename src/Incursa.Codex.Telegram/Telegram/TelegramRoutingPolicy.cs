namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Routing rules for deciding whether a non-command Telegram message can go directly to Codex.
/// </summary>
internal static class TelegramRoutingPolicy
{
    /// <summary>
    /// Determines whether plain text or attachments can be automatically routed to the active Codex session.
    /// </summary>
    /// <param name="chatType">Telegram chat type.</param>
    /// <param name="messageThreadId">Telegram forum topic thread ID, when present.</param>
    /// <returns><see langword="true"/> when automatic routing is allowed.</returns>
    public static bool CanAutoRoute(string chatType, int? messageThreadId)
        => IsPrivateChat(chatType) || messageThreadId is not null;

    /// <summary>
    /// Determines whether the Telegram update came from a private chat.
    /// </summary>
    /// <param name="chatType">Telegram chat type.</param>
    /// <returns><see langword="true"/> for private chats.</returns>
    public static bool IsPrivateChat(string chatType)
        => string.Equals(chatType, "private", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the user-facing explanation shown when a message is not automatically routed.
    /// </summary>
    /// <param name="chatType">Telegram chat type.</param>
    /// <returns>Routing guidance message.</returns>
    public static string BuildNotRoutedMessage(string chatType)
        => string.Equals(chatType, "supergroup", StringComparison.OrdinalIgnoreCase)
            ? "I only auto-route messages from private chats or forum topics. This message was sent to the group root, so I did not send it to Codex. Use /send <text>, open a topic, or message me privately."
            : $"I only auto-route messages from private chats or topic threads. This {chatType} message was not sent to Codex. Use /send <text> or message me privately.";
}
