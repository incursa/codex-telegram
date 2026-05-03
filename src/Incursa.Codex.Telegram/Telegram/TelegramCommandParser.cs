namespace Incursa.Codex.Telegram.Telegram;

public sealed class TelegramCommandParser
{
    public ParsedTelegramCommand Parse(string? text)
    {
        string normalized = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            return new ParsedTelegramCommand(false, string.Empty, string.Empty, normalized);
        }

        int separatorIndex = normalized.IndexOfAny([' ', '\t', '\r', '\n']);
        string commandToken = separatorIndex < 0 ? normalized[1..] : normalized[1..separatorIndex];
        string arguments = separatorIndex < 0 ? string.Empty : normalized[separatorIndex..].Trim();
        int mentionIndex = commandToken.IndexOf('@', StringComparison.Ordinal);
        if (mentionIndex >= 0)
        {
            commandToken = commandToken[..mentionIndex];
        }

        return new ParsedTelegramCommand(true, commandToken.ToLowerInvariant(), arguments, normalized);
    }
}

public sealed record ParsedTelegramCommand(bool IsCommand, string Name, string Arguments, string Text);
