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
}
