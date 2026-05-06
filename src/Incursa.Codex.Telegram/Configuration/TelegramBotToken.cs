namespace Incursa.Codex.Telegram.Configuration;

/// <summary>
/// Performs conservative normalization and shape validation for Telegram Bot API tokens.
/// </summary>
internal static class TelegramBotToken
{
    /// <summary>
    /// Attempts to trim and validate the basic Telegram bot token shape before any network call is made.
    /// </summary>
    /// <param name="value">The operator-provided token value.</param>
    /// <param name="token">The normalized token when validation succeeds.</param>
    /// <param name="error">A user-facing validation error when validation fails.</param>
    /// <returns><see langword="true"/> when the token has the expected Bot API token shape.</returns>
    public static bool TryNormalize(string? value, out string token, out string error)
    {
        token = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Telegram bot token is required.";
            return false;
        }

        string normalized = value.Trim();
        int separatorIndex = normalized.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == normalized.Length - 1)
        {
            error = "Telegram bot tokens look like <bot-id>:<secret>.";
            return false;
        }

        ReadOnlySpan<char> botId = normalized.AsSpan(0, separatorIndex);
        foreach (char ch in botId)
        {
            if (!char.IsAsciiDigit(ch))
            {
                error = "The bot-id portion before ':' must contain only digits.";
                return false;
            }
        }

        ReadOnlySpan<char> secret = normalized.AsSpan(separatorIndex + 1);
        foreach (char ch in secret)
        {
            if (char.IsWhiteSpace(ch))
            {
                error = "Telegram bot tokens cannot contain whitespace.";
                return false;
            }
        }

        token = normalized;
        return true;
    }
}
