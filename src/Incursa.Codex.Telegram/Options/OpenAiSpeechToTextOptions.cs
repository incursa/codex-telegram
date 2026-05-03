namespace Incursa.Codex.Telegram.Options;

public sealed class OpenAiSpeechToTextOptions
{
    public string? ApiKey { get; set; }

    public string? Model { get; set; } = "whisper-1";

    public string? BaseUrl { get; set; } = "https://api.openai.com/v1/";

    public string? FfmpegPath { get; set; } = "ffmpeg";
}
