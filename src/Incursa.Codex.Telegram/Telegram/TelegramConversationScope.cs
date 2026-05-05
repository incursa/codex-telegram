using System.Globalization;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Identifies one Telegram conversation boundary: either a chat root or one forum topic.
/// </summary>
/// <param name="ChatId">Telegram chat ID.</param>
/// <param name="MessageThreadId">Telegram forum topic thread ID, when scoped to a topic.</param>
internal readonly record struct TelegramConversationScope(long ChatId, int? MessageThreadId)
{
    /// <summary>
    /// Formats the conversation scope as a stable storage key.
    /// </summary>
    /// <returns>Storage key in <c>chatId</c> or <c>chatId:threadId</c> form.</returns>
    public string ToStorageKey()
        => MessageThreadId is null
            ? ChatId.ToString(CultureInfo.InvariantCulture)
            : string.Join(':',
                ChatId.ToString(CultureInfo.InvariantCulture),
                MessageThreadId.Value.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public override string ToString()
        => ToStorageKey();

    /// <summary>
    /// Parses a storage key created by <see cref="ToStorageKey"/>.
    /// </summary>
    /// <param name="value">Storage key to parse.</param>
    /// <param name="scope">Parsed conversation scope.</param>
    /// <returns><see langword="true"/> when parsing succeeds.</returns>
    public static bool TryParseStorageKey(string? value, out TelegramConversationScope scope)
    {
        scope = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long chatId))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            scope = new TelegramConversationScope(chatId, null);
            return true;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int threadId))
        {
            return false;
        }

        scope = new TelegramConversationScope(chatId, threadId);
        return true;
    }
}
