namespace Incursa.Codex.Telegram.Options;

/// <summary>
/// Configuration for local Telegram/Codex diagnostic trace capture.
/// </summary>
public sealed class TelegramDebugTraceOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether JSONL trace files are written.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the trace root directory. When empty, the CodexTelegram data root is used.
    /// </summary>
    public string? TraceDirectory { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether inbound user text and transcripts are captured.
    /// </summary>
    public bool CaptureInputText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether outbound assistant text is captured.
    /// </summary>
    public bool CaptureOutputText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether attachment metadata is captured.
    /// </summary>
    public bool CaptureAttachmentMetadata { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether attachment files are copied into trace storage.
    /// </summary>
    public bool CaptureAttachmentCopies { get; set; }

    /// <summary>
    /// Gets or sets the maximum trace file size before events are summarized only in memory.
    /// </summary>
    public long MaxTraceFileBytes { get; set; } = 10L * 1024L * 1024L;

    /// <summary>
    /// Gets or sets the number of days trace files should be retained.
    /// </summary>
    public int RetentionDays { get; set; } = 14;
}

/// <summary>
/// Safety bounds for <see cref="TelegramDebugTraceOptions"/>.
/// </summary>
public static class TelegramDebugTraceLimits
{
    /// <summary>
    /// Smallest trace file size.
    /// </summary>
    public const long MinTraceFileBytes = 64L * 1024L;

    /// <summary>
    /// Largest trace file size.
    /// </summary>
    public const long MaxTraceFileBytes = 256L * 1024L * 1024L;

    /// <summary>
    /// Smallest retention period.
    /// </summary>
    public const int MinRetentionDays = 1;

    /// <summary>
    /// Largest retention period.
    /// </summary>
    public const int MaxRetentionDays = 365;
}
