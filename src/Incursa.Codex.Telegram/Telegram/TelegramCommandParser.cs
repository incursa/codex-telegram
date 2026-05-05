namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Parses raw Telegram text into the bot's command shape.
/// </summary>
internal sealed class TelegramCommandParser
{
    /// <summary>
    /// Parses a Telegram text message as either a slash command or ordinary prompt text.
    /// </summary>
    /// <param name="text">Raw Telegram message text.</param>
    /// <returns>Parsed command information.</returns>
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
            // Telegram sends group commands as /command@botname; the command handler only needs
            // the command verb because authorization and routing are checked separately.
            commandToken = commandToken[..mentionIndex];
        }

        return new ParsedTelegramCommand(true, commandToken.ToLowerInvariant(), arguments, normalized);
    }
}

/// <summary>
/// Parsed Telegram command or plain-text prompt.
/// </summary>
/// <param name="IsCommand">Whether the raw text was a slash command.</param>
/// <param name="Name">Lowercase command name without a leading slash or bot mention.</param>
/// <param name="Arguments">Command argument text.</param>
/// <param name="Text">Normalized original text.</param>
internal sealed record ParsedTelegramCommand(bool IsCommand, string Name, string Arguments, string Text);
