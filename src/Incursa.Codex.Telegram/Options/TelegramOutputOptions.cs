namespace Incursa.Codex.Telegram.Options;

/// <summary>
/// Controls how Codex turn output is presented in Telegram.
/// </summary>
public enum TelegramOutputPresentationMode
{
    /// <summary>
    /// Keep normal output quiet, send final output durably, and publish sparse "still working" pulses while a turn is active.
    /// </summary>
    Compact,

    /// <summary>
    /// Send progress and update events as durable Telegram messages according to existing filters.
    /// </summary>
    Verbose,

    /// <summary>
    /// Project progress and update events into an editable live card while keeping final output durable.
    /// </summary>
    LiveCard,

    /// <summary>
    /// Suppress normal progress and update chatter, sending only high-priority durable events.
    /// </summary>
    FinalOnly,
}

/// <summary>
/// Configuration for Telegram turn-output projection and operational turn history.
/// </summary>
public sealed class TelegramOutputOptions
{
    /// <summary>
    /// Gets or sets the configured presentation mode for Codex turn output.
    /// </summary>
    public TelegramOutputPresentationMode PresentationMode { get; set; } = TelegramOutputPresentationMode.Compact;

    /// <summary>
    /// Gets or sets the number of days operational turn history should be retained.
    /// </summary>
    public int HistoryRetentionDays { get; set; } = 14;

    /// <summary>
    /// Gets or sets the maximum operational history events retained for one turn.
    /// </summary>
    public int MaxHistoryEventsPerTurn { get; set; } = 500;

    /// <summary>
    /// Gets or sets a value indicating whether detailed progress events are retained in operational history.
    /// </summary>
    public bool CaptureProgressHistory { get; set; }

    /// <summary>
    /// Gets or sets the minimum seconds between non-critical live card edits.
    /// </summary>
    public int LiveCardMinEditIntervalSeconds { get; set; } = 3;

    /// <summary>
    /// Gets or sets the minimum seconds between compact "still working" pulses.
    /// </summary>
    public int CompactPulseIntervalSeconds { get; set; } = 45;
}

/// <summary>
/// Safety bounds for <see cref="TelegramOutputOptions"/>.
/// </summary>
public static class TelegramOutputLimits
{
    /// <summary>
    /// Minimum allowed operational history retention in days.
    /// </summary>
    public const int MinHistoryRetentionDays = 1;

    /// <summary>
    /// Maximum allowed operational history retention in days.
    /// </summary>
    public const int MaxHistoryRetentionDays = 90;

    /// <summary>
    /// Minimum allowed number of retained history events per turn.
    /// </summary>
    public const int MinHistoryEventsPerTurn = 50;

    /// <summary>
    /// Maximum allowed number of retained history events per turn.
    /// </summary>
    public const int MaxHistoryEventsPerTurn = 5000;

    /// <summary>
    /// Minimum allowed live card edit throttle in seconds.
    /// </summary>
    public const int MinLiveCardEditIntervalSeconds = 1;

    /// <summary>
    /// Maximum allowed live card edit throttle in seconds.
    /// </summary>
    public const int MaxLiveCardEditIntervalSeconds = 30;

    /// <summary>
    /// Minimum allowed compact pulse interval in seconds.
    /// </summary>
    public const int MinCompactPulseIntervalSeconds = 30;

    /// <summary>
    /// Maximum allowed compact pulse interval in seconds.
    /// </summary>
    public const int MaxCompactPulseIntervalSeconds = 120;
}
