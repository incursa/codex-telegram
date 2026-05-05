namespace Incursa.Codex.Telegram.Options;

/// <summary>
/// Default values for OpenAI speech-to-text integration.
/// </summary>
public static class OpenAiSpeechToTextDefaults
{
    /// <summary>
    /// Default transcription model.
    /// </summary>
    public const string Model = "whisper-1";

    /// <summary>
    /// Default OpenAI REST API base URL.
    /// </summary>
    public const string BaseUrl = "https://api.openai.com/v1/";

    /// <summary>
    /// Default executable name used for ffmpeg lookup.
    /// </summary>
    public const string FfmpegPath = "ffmpeg";
}

/// <summary>
/// Configuration for optional Telegram audio transcription through OpenAI.
/// </summary>
public sealed class OpenAiSpeechToTextOptions
{
    /// <summary>
    /// Gets or sets the OpenAI API key used for transcription requests.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the transcription model ID.
    /// </summary>
    public string? Model { get; set; } = OpenAiSpeechToTextDefaults.Model;

    /// <summary>
    /// Gets or sets the OpenAI-compatible API base URL.
    /// </summary>
    public string? BaseUrl { get; set; } = OpenAiSpeechToTextDefaults.BaseUrl;

    /// <summary>
    /// Gets or sets the ffmpeg executable path used when Telegram audio must be transcoded.
    /// </summary>
    public string? FfmpegPath { get; set; } = OpenAiSpeechToTextDefaults.FfmpegPath;
}
