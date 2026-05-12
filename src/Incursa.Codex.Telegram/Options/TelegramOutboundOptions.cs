namespace Incursa.Codex.Telegram.Options;

/// <summary>
/// Default values for the Telegram outbound scheduler.
/// </summary>
/// <remarks>
/// These defaults favor private-chat responsiveness while keeping group chats conservative enough
/// to avoid avoidable Telegram flood-limit responses during active Codex turns.
/// </remarks>
public static class TelegramOutboundDefaults
{
    /// <summary>
    /// Default minimum spacing, in seconds, between sends to the same group or supergroup chat.
    /// </summary>
    public const int GroupMinimumSendIntervalSeconds = 5;

    /// <summary>
    /// Default minimum spacing, in seconds, between sends to the same private chat.
    /// </summary>
    public const int PrivateMinimumSendIntervalSeconds = 1;

    /// <summary>
    /// Default global process-wide send budget per one-second window.
    /// </summary>
    public const int GlobalMaxMessagesPerSecond = 20;

    /// <summary>
    /// Default outbound Telegram message size used before chunking.
    /// </summary>
    public const int MaxMessageChars = 3500;

    /// <summary>
    /// Default maximum buffered text per destination before local compaction begins.
    /// </summary>
    public const int MaxBufferedCharsPerDestination = 25000;

    /// <summary>
    /// Default maximum buffered update count per destination before local compaction begins.
    /// </summary>
    public const int MaxBufferedMessagesPerDestination = 100;

    /// <summary>
    /// Default scheduler wake-up interval while checking for ready outbound work.
    /// </summary>
    public const int FlushIntervalMilliseconds = 500;

    /// <summary>
    /// Default maximum time to wait for one Telegram outbound send before retrying later.
    /// </summary>
    public const int SendTimeoutSeconds = 30;

    /// <summary>
    /// Default minimum accumulated assistant-message characters before live progress is published.
    /// </summary>
    public const int AgentMessageUpdateMinChars = 40;

    /// <summary>
    /// Default maximum live assistant-message chunk size before a progress update is published.
    /// </summary>
    public const int AgentMessageUpdateMaxChars = 900;

    /// <summary>
    /// Default amount of time to collect normal-priority updates before batching and sending them.
    /// </summary>
    public const int BatchWindowSeconds = 5;

    /// <summary>
    /// Default compaction policy name used in configuration and diagnostics.
    /// </summary>
    public const string DropPolicy = "DropOldestProgressThenOldestUpdateSummary";

    /// <summary>
    /// Default setting for per-message Telegram debug preambles.
    /// </summary>
    public const bool DebugPreambleEnabled = false;
}

/// <summary>
/// Safety bounds applied to <see cref="TelegramOutboundOptions"/> after configuration is loaded.
/// </summary>
public static class TelegramOutboundLimits
{
    /// <summary>
    /// Smallest allowed same-group send interval.
    /// </summary>
    public const int MinGroupSendIntervalSeconds = 1;

    /// <summary>
    /// Largest allowed same-group send interval.
    /// </summary>
    public const int MaxGroupSendIntervalSeconds = 120;

    /// <summary>
    /// Smallest allowed same-private-chat send interval.
    /// </summary>
    public const int MinPrivateSendIntervalSeconds = 1;

    /// <summary>
    /// Largest allowed same-private-chat send interval.
    /// </summary>
    public const int MaxPrivateSendIntervalSeconds = 120;

    /// <summary>
    /// Smallest allowed global send budget per one-second window.
    /// </summary>
    public const int MinGlobalMessagesPerSecond = 1;

    /// <summary>
    /// Largest allowed global send budget per one-second window.
    /// </summary>
    public const int MaxGlobalMessagesPerSecond = 25;

    /// <summary>
    /// Smallest allowed outbound message chunk size.
    /// </summary>
    public const int MinMessageChars = 500;

    /// <summary>
    /// Largest allowed outbound message chunk size. This intentionally stays below Telegram's
    /// documented 4096-character text limit to leave room for future formatting changes.
    /// </summary>
    public const int MaxMessageChars = 3500;

    /// <summary>
    /// Largest allowed per-destination buffered text before local compaction must run.
    /// </summary>
    public const int MaxBufferedCharsPerDestination = 250000;

    /// <summary>
    /// Smallest allowed buffered update count per destination.
    /// </summary>
    public const int MinBufferedMessagesPerDestination = 1;

    /// <summary>
    /// Largest allowed buffered update count per destination.
    /// </summary>
    public const int MaxBufferedMessagesPerDestination = 1000;

    /// <summary>
    /// Fastest allowed scheduler loop delay.
    /// </summary>
    public const int MinFlushIntervalMilliseconds = 50;

    /// <summary>
    /// Slowest allowed scheduler loop delay.
    /// </summary>
    public const int MaxFlushIntervalMilliseconds = 10000;

    /// <summary>
    /// Smallest allowed Telegram outbound send timeout.
    /// </summary>
    public const int MinSendTimeoutSeconds = 1;

    /// <summary>
    /// Largest allowed Telegram outbound send timeout.
    /// </summary>
    public const int MaxSendTimeoutSeconds = 300;

    /// <summary>
    /// Smallest allowed assistant progress update size.
    /// </summary>
    public const int MinAgentMessageUpdateChars = 12;

    /// <summary>
    /// Smallest allowed batching delay.
    /// </summary>
    public const int MinBatchWindowSeconds = 0;

    /// <summary>
    /// Largest allowed batching delay.
    /// </summary>
    public const int MaxBatchWindowSeconds = 120;
}

/// <summary>
/// Configuration for rate-limited Telegram delivery of Codex output.
/// </summary>
public sealed class TelegramOutboundOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether live Codex output should be queued for Telegram delivery.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the minimum spacing, in seconds, between sends to the same group or supergroup chat.
    /// </summary>
    public int GroupMinimumSendIntervalSeconds { get; set; } = TelegramOutboundDefaults.GroupMinimumSendIntervalSeconds;

    /// <summary>
    /// Gets or sets the minimum spacing, in seconds, between sends to the same private chat.
    /// </summary>
    public int PrivateMinimumSendIntervalSeconds { get; set; } = TelegramOutboundDefaults.PrivateMinimumSendIntervalSeconds;

    /// <summary>
    /// Gets or sets the process-wide send budget per one-second window.
    /// </summary>
    public int GlobalMaxMessagesPerSecond { get; set; } = TelegramOutboundDefaults.GlobalMaxMessagesPerSecond;

    /// <summary>
    /// Gets or sets the maximum text length handed to Telegram for one queued send.
    /// </summary>
    public int MaxMessageChars { get; set; } = TelegramOutboundDefaults.MaxMessageChars;

    /// <summary>
    /// Gets or sets the maximum buffered text per chat/topic destination before compaction starts.
    /// </summary>
    public int MaxBufferedCharsPerDestination { get; set; } = TelegramOutboundDefaults.MaxBufferedCharsPerDestination;

    /// <summary>
    /// Gets or sets the maximum buffered update count per chat/topic destination before compaction starts.
    /// </summary>
    public int MaxBufferedMessagesPerDestination { get; set; } = TelegramOutboundDefaults.MaxBufferedMessagesPerDestination;

    /// <summary>
    /// Gets or sets the scheduler loop delay used while polling for outbound work.
    /// </summary>
    public int FlushIntervalMilliseconds { get; set; } = TelegramOutboundDefaults.FlushIntervalMilliseconds;

    /// <summary>
    /// Gets or sets the maximum time to wait for one Telegram API send before keeping it queued for retry.
    /// </summary>
    public int SendTimeoutSeconds { get; set; } = TelegramOutboundDefaults.SendTimeoutSeconds;

    /// <summary>
    /// Gets or sets a value indicating whether internal Codex progress events should be sent to Telegram.
    /// </summary>
    public bool IncludeProgressMessages { get; set; }

    /// <summary>
    /// Gets or sets the minimum accumulated assistant-message characters before a live update is published.
    /// </summary>
    public int AgentMessageUpdateMinChars { get; set; } = TelegramOutboundDefaults.AgentMessageUpdateMinChars;

    /// <summary>
    /// Gets or sets the maximum assistant-message characters included in one live update.
    /// </summary>
    public int AgentMessageUpdateMaxChars { get; set; } = TelegramOutboundDefaults.AgentMessageUpdateMaxChars;

    /// <summary>
    /// Gets or sets how long normal-priority updates may wait so adjacent updates can be batched.
    /// </summary>
    public int BatchWindowSeconds { get; set; } = TelegramOutboundDefaults.BatchWindowSeconds;

    /// <summary>
    /// Gets or sets the configured local compaction policy name.
    /// </summary>
    public string DropPolicy { get; set; } = TelegramOutboundDefaults.DropPolicy;

    /// <summary>
    /// Gets or sets a value indicating whether each outbound Telegram text message should include diagnostic session and turn metadata.
    /// </summary>
    public bool DebugPreambleEnabled { get; set; } = TelegramOutboundDefaults.DebugPreambleEnabled;
}
