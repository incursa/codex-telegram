namespace Incursa.Codex.Telegram.Options;

/// <summary>
/// Default values for Telegram bot hosting and user-message handling.
/// </summary>
public static class TelegramBotDefaults
{
    /// <summary>
    /// Default Telegram long-polling timeout, in seconds.
    /// </summary>
    public const int PollingTimeoutSeconds = 30;

    /// <summary>
    /// Default maximum text length sent in a single Telegram message.
    /// </summary>
    public const int MaxTelegramMessageLength = 3500;

    /// <summary>
    /// Default minimum Telegram audio duration accepted for transcription.
    /// </summary>
    public const int MinAudioDurationSeconds = 1;

    /// <summary>
    /// Default maximum Telegram audio duration accepted for transcription.
    /// </summary>
    public const int MaxAudioDurationSeconds = 600;
}

/// <summary>
/// Safety bounds applied to <see cref="TelegramBotOptions"/> after configuration is loaded.
/// </summary>
public static class TelegramBotLimits
{
    /// <summary>
    /// Smallest allowed Telegram long-polling timeout, in seconds.
    /// </summary>
    public const int MinPollingTimeoutSeconds = 1;

    /// <summary>
    /// Largest allowed Telegram long-polling timeout, in seconds.
    /// </summary>
    public const int MaxPollingTimeoutSeconds = 120;

    /// <summary>
    /// Smallest allowed Telegram message length.
    /// </summary>
    public const int MinTelegramMessageLength = 1;

    /// <summary>
    /// Conservative maximum Telegram message length used by this app.
    /// </summary>
    public const int MaxTelegramMessageLength = 4000;

    /// <summary>
    /// Smallest accepted Telegram audio duration, in seconds.
    /// </summary>
    public const int MinAudioDurationSeconds = 0;

    /// <summary>
    /// Largest configured minimum audio duration, in seconds.
    /// </summary>
    public const int MaxMinimumAudioDurationSeconds = 60;

    /// <summary>
    /// Smallest accepted maximum audio duration, in seconds.
    /// </summary>
    public const int MinMaximumAudioDurationSeconds = 1;

    /// <summary>
    /// Largest accepted maximum audio duration, in seconds.
    /// </summary>
    public const int MaxAudioDurationSeconds = 6 * 60 * 60;
}

/// <summary>
/// Configuration for Telegram bot polling, authorization, routing, and message constraints.
/// </summary>
public sealed class TelegramBotOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether Telegram polling is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the BotFather token used to authenticate this bot.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Gets or sets the Telegram user IDs allowed to operate the bot.
    /// </summary>
    public long[] AllowedUserIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the Telegram group or supergroup chat IDs allowed to operate the bot.
    /// </summary>
    public long[] AllowedChatIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the Telegram long-polling timeout, in seconds.
    /// </summary>
    public int PollingTimeoutSeconds { get; set; } = TelegramBotDefaults.PollingTimeoutSeconds;

    /// <summary>
    /// Gets or sets the fallback working directory used when a Telegram conversation has no selected project.
    /// </summary>
    public string? DefaultWorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets the legacy Codex executable path override.
    /// </summary>
    public string? CodexExecutablePath { get; set; }

    /// <summary>
    /// Gets or sets the maximum text length used when splitting direct Telegram replies.
    /// </summary>
    public int MaxTelegramMessageLength { get; set; } = TelegramBotDefaults.MaxTelegramMessageLength;

    /// <summary>
    /// Gets or sets the shortest Telegram audio message accepted for transcription.
    /// </summary>
    public int MinAudioDurationSeconds { get; set; } = TelegramBotDefaults.MinAudioDurationSeconds;

    /// <summary>
    /// Gets or sets the longest Telegram audio message accepted for transcription.
    /// </summary>
    public int MaxAudioDurationSeconds { get; set; } = TelegramBotDefaults.MaxAudioDurationSeconds;

    /// <summary>
    /// Gets or sets the optional directory used for session-specific logs.
    /// </summary>
    public string? SessionLogDirectory { get; set; }

    /// <summary>
    /// Gets or sets outbound delivery options for Codex output.
    /// </summary>
    public TelegramOutboundOptions Outbound { get; set; } = new();
}
