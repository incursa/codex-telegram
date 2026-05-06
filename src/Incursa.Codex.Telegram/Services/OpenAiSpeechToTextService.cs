using System.Diagnostics;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal sealed class OpenAiSpeechToTextService : IAudioTranscriptionService
{
    private const long MaxTranscriptionUploadBytes = 25L * 1024 * 1024;
    private const long MinimumPlausibleAudioBytes = 16;
    private const string FfmpegUnavailableMessage =
        "ffmpeg is not installed or OpenAI:FfmpegPath points to an executable that cannot be started. " +
        "Voice-note transcription is optional, but Telegram voice notes often need ffmpeg to convert OGG/OPUS audio before OpenAI transcription. " +
        "Install ffmpeg and put it on PATH, or set OpenAI:FfmpegPath to the full executable path.";

    private static readonly HashSet<string> DirectUploadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".mp4",
        ".mpeg",
        ".mpga",
        ".m4a",
        ".wav",
        ".webm",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly OpenAiSpeechToTextOptions _options;
    private readonly ILogger<OpenAiSpeechToTextService> _logger;

    public OpenAiSpeechToTextService(
        HttpClient httpClient,
        IOptions<OpenAiSpeechToTextOptions> options,
        ILogger<OpenAiSpeechToTextService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken)
    {
        string sourcePath = Path.GetFullPath(audioFilePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Audio file was not found.", sourcePath);
        }

        string apiKey = RequireApiKey();
        string model = RequireModel();
        EnsureSourceAudioFileIsUsable(sourcePath);

        string preparedPath = sourcePath;
        string? tempTranscodedPath = null;

        try
        {
            if (!IsDirectUploadSupported(sourcePath))
            {
                tempTranscodedPath = await TranscodeToSupportedFormatAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                preparedPath = tempTranscodedPath;
            }

            EnsureWithinOpenAiLimit(preparedPath);
            using FileStream fileStream = File.OpenRead(preparedPath);
            using MultipartFormDataContent form = new();
            form.Add(new StringContent(model, Encoding.UTF8), "model");
            StreamContent fileContent = new(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(preparedPath));
            form.Add(fileContent, "file", Path.GetFileName(preparedPath));

            using HttpRequestMessage request = new(HttpMethod.Post, BuildEndpoint());
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = form;

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OpenAI transcription failed with {(int)response.StatusCode} {response.ReasonPhrase}: {payload}");
            }

            OpenAiTranscriptionResponse? transcription = JsonSerializer.Deserialize<OpenAiTranscriptionResponse>(payload, JsonOptions);
            string? text = transcription?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("OpenAI transcription response did not include text.");
            }

            return text;
        }
        finally
        {
            if (tempTranscodedPath is not null)
            {
                TryDelete(tempTranscodedPath);
            }
        }
    }

    private Uri BuildEndpoint()
    {
        string baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl)
            ? "https://api.openai.com/v1/"
            : _options.BaseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(baseUrl, UriKind.Absolute), "audio/transcriptions");
    }

    private string RequireApiKey()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("OpenAI:ApiKey must be configured to transcribe Telegram audio.");
        }

        return _options.ApiKey.Trim();
    }

    private string RequireModel()
    {
        if (string.IsNullOrWhiteSpace(_options.Model))
        {
            return "whisper-1";
        }

        return _options.Model.Trim();
    }

    private bool IsDirectUploadSupported(string audioFilePath)
        => DirectUploadExtensions.Contains(Path.GetExtension(audioFilePath));

    private static void EnsureSourceAudioFileIsUsable(string filePath)
    {
        FileInfo info = new(filePath);
        if (info.Length == 0)
        {
            throw new InvalidOperationException("Audio file is empty.");
        }

        if (info.Length < MinimumPlausibleAudioBytes)
        {
            throw new InvalidOperationException("Audio file is too small to be a valid recording.");
        }

        EnsureWithinOpenAiLimit(filePath);
    }

    private async Task<string> TranscodeToSupportedFormatAsync(string inputFilePath, CancellationToken cancellationToken)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "codex-telegram", "telegram-audio");
        Directory.CreateDirectory(tempDirectory);
        string outputFilePath = Path.Combine(tempDirectory, $"{Guid.NewGuid():n}.m4a");

        string ffmpegPath = string.IsNullOrWhiteSpace(_options.FfmpegPath) ? "ffmpeg" : _options.FfmpegPath.Trim();
        ProcessStartInfo startInfo = new()
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputFilePath);
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add("16000");
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("aac");
        startInfo.ArgumentList.Add("-b:a");
        startInfo.ArgumentList.Add("64k");
        startInfo.ArgumentList.Add(outputFilePath);

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            });

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg failed with exit code {process.ExitCode}: {stderr}".Trim());
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"{FfmpegUnavailableMessage} Configured ffmpeg path: '{ffmpegPath}'.", exception);
        }

        EnsureWithinOpenAiLimit(outputFilePath);
        _logger.LogDebug("Transcoded audio file {InputFilePath} to {OutputFilePath} for OpenAI upload.", inputFilePath, outputFilePath);
        return outputFilePath;
    }

    private static void EnsureWithinOpenAiLimit(string filePath)
    {
        FileInfo info = new(filePath);
        if (info.Length > MaxTranscriptionUploadBytes)
        {
            throw new InvalidOperationException($"Audio file '{info.Name}' is larger than the OpenAI transcription limit of 25 MB.");
        }
    }

    private static string GetContentType(string audioFilePath)
        => Path.GetExtension(audioFilePath).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".mp4" => "audio/mp4",
            ".mpeg" => "audio/mpeg",
            ".mpga" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".webm" => "audio/webm",
            _ => "application/octet-stream",
        };

    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
        }
    }

    private sealed record OpenAiTranscriptionResponse([property: JsonPropertyName("text")] string Text);
}
