namespace Incursa.Codex.Telegram.Options;

public sealed class TelegramOutboundOptions
{
    public bool Enabled { get; set; } = true;

    public int GroupMinimumSendIntervalSeconds { get; set; } = 5;

    public int PrivateMinimumSendIntervalSeconds { get; set; } = 1;

    public int GlobalMaxMessagesPerSecond { get; set; } = 20;

    public int MaxMessageChars { get; set; } = 3500;

    public int MaxBufferedCharsPerDestination { get; set; } = 25000;

    public int MaxBufferedMessagesPerDestination { get; set; } = 100;

    public int FlushIntervalMilliseconds { get; set; } = 500;

    public bool IncludeProgressMessages { get; set; }

    public int AgentMessageUpdateMinChars { get; set; } = 40;

    public int AgentMessageUpdateMaxChars { get; set; } = 900;

    public int BatchWindowSeconds { get; set; } = 5;

    public string DropPolicy { get; set; } = "DropOldestProgressThenOldestUpdateSummary";
}
