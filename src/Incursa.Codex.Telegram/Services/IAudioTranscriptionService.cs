namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Converts local audio files into text prompts.
/// </summary>
internal interface IAudioTranscriptionService
{
    /// <summary>
    /// Transcribes a local audio file.
    /// </summary>
    /// <param name="audioFilePath">Local audio file path.</param>
    /// <param name="cancellationToken">Cancellation token for request aborts.</param>
    /// <returns>Transcribed text.</returns>
    Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken);
}
