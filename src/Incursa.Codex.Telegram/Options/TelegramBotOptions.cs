namespace Incursa.Codex.Telegram.Options;

public sealed class TelegramBotOptions
{
    public bool Enabled { get; set; }

    public string? Token { get; set; }

    public long[] AllowedUserIds { get; set; } = [];

    public long[] AllowedChatIds { get; set; } = [];

    public int PollingTimeoutSeconds { get; set; } = 30;

    public string? DefaultWorkingDirectory { get; set; }

    public string? CodexExecutablePath { get; set; }

    public int MaxTelegramMessageLength { get; set; } = 3500;

    public int MinAudioDurationSeconds { get; set; } = 1;

    public int MaxAudioDurationSeconds { get; set; } = 600;

    public string? SessionLogDirectory { get; set; }

    public TelegramOutboundOptions Outbound { get; set; } = new();
}
