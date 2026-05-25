namespace Incursa.Codex.Telegram.Options;

/// <summary>
/// Defines when normal Telegram input should be captured into an editable draft bundle.
/// </summary>
public enum TelegramInputCaptureMode
{
    /// <summary>
    /// Keep simple text immediate unless a draft already exists.
    /// </summary>
    ImmediateText,

    /// <summary>
    /// Capture every non-command message into a draft bundle.
    /// </summary>
    BundleAlways,

    /// <summary>
    /// Capture input while a turn/output is active, or when the input contains media.
    /// </summary>
    BundleWhenActiveOrMedia,
}

/// <summary>
/// Configuration for Telegram-side draft input bundles.
/// </summary>
public sealed class TelegramInputOptions
{
    /// <summary>
    /// Gets or sets the default capture behavior for non-command Telegram input.
    /// </summary>
    public TelegramInputCaptureMode DefaultCaptureMode { get; set; } = TelegramInputCaptureMode.BundleAlways;

    /// <summary>
    /// Gets or sets the number of minutes a draft bundle remains editable.
    /// </summary>
    public int BundleExpirationMinutes { get; set; } = 30;

    /// <summary>
    /// Gets or sets the maximum number of text characters shown in bundle cards.
    /// </summary>
    public int PreviewCharacters { get; set; } = 600;

    /// <summary>
    /// Gets or sets the debounce window used to collect Telegram album/media-group messages.
    /// </summary>
    public int MediaGroupDebounceMilliseconds { get; set; } = 1500;

    /// <summary>
    /// Gets or sets a value indicating whether active-turn steering can include Telegram attachments.
    /// </summary>
    public bool AllowAttachmentSteering { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of idle seconds after which a captured bundle is automatically sent or queued.
    /// Set to 0 to disable automatic dispatch.
    /// </summary>
    public int AutoDispatchAfterSeconds { get; set; } = 25;
}

/// <summary>
/// Safety bounds for <see cref="TelegramInputOptions"/>.
/// </summary>
public static class TelegramInputLimits
{
    /// <summary>
    /// Smallest bundle lifetime, in minutes.
    /// </summary>
    public const int MinBundleExpirationMinutes = 1;

    /// <summary>
    /// Largest bundle lifetime, in minutes.
    /// </summary>
    public const int MaxBundleExpirationMinutes = 24 * 60;

    /// <summary>
    /// Smallest bundle-card preview length.
    /// </summary>
    public const int MinPreviewCharacters = 80;

    /// <summary>
    /// Largest bundle-card preview length.
    /// </summary>
    public const int MaxPreviewCharacters = 2_000;

    /// <summary>
    /// Smallest media-group debounce window.
    /// </summary>
    public const int MinMediaGroupDebounceMilliseconds = 100;

    /// <summary>
    /// Largest media-group debounce window.
    /// </summary>
    public const int MaxMediaGroupDebounceMilliseconds = 5_000;

    /// <summary>
    /// Smallest non-zero automatic bundle dispatch delay.
    /// </summary>
    public const int MinAutoDispatchAfterSeconds = 5;

    /// <summary>
    /// Largest automatic bundle dispatch delay.
    /// </summary>
    public const int MaxAutoDispatchAfterSeconds = 300;
}
