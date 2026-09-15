namespace Incursa.Codex.Telegram.Options;

/// <summary>
/// Configuration for the optional Telegram Mini App companion surface.
/// </summary>
public sealed class TelegramMiniAppOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the Mini App endpoints are enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the URL Kestrel listens on for the local Mini App host.
    /// </summary>
    public string ListenUrl { get; set; } = "http://127.0.0.1:5287";

    /// <summary>
    /// Gets or sets the public HTTPS URL used when configuring Telegram's menu button.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Gets or sets the maximum age of Telegram initialization data accepted by the API.
    /// </summary>
    public int InitDataMaxAgeSeconds { get; set; } = 900;

    /// <summary>
    /// Gets or sets whether the explicit Telegram-approved standalone browser pairing flow is enabled.
    /// </summary>
    public bool BrowserPairingEnabled { get; set; }

    /// <summary>
    /// Gets or sets how long an unapproved browser pairing remains valid.
    /// </summary>
    public int BrowserPairingLifetimeMinutes { get; set; } = 10;

    /// <summary>
    /// Gets or sets how long an approved browser session remains valid.
    /// </summary>
    public int BrowserSessionLifetimeHours { get; set; } = 12;
}
