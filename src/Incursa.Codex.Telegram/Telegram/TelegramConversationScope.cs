using System.Globalization;

namespace Incursa.Codex.Telegram.Telegram;

public readonly record struct TelegramConversationScope(long ChatId, int? MessageThreadId)
{
    public string ToStorageKey()
        => MessageThreadId is null
            ? ChatId.ToString(CultureInfo.InvariantCulture)
            : string.Join(':',
                ChatId.ToString(CultureInfo.InvariantCulture),
                MessageThreadId.Value.ToString(CultureInfo.InvariantCulture));

    public override string ToString()
        => ToStorageKey();

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
