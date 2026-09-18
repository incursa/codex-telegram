using System.Diagnostics;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Configuration;
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
    private readonly OpenAiSpeechToTextOptions _configuredOptions;
    private readonly LocalSettingsStore? _localSettingsStore;
    private readonly ILogger<OpenAiSpeechToTextService> _logger;
    private readonly string _tempRoot;

    public OpenAiSpeechToTextService(
        HttpClient httpClient,
        IOptions<OpenAiSpeechToTextOptions> options,
        ILogger<OpenAiSpeechToTextService> logger,
        IOptions<CodexTelegramOptions>? codexOptions = null,
        LocalSettingsStore? localSettingsStore = null)
    {
        _httpClient = httpClient;
        // This dedicated client is governed by RequestTimeoutSeconds below. Leaving the
        // default HttpClient timeout in place would cancel long transcriptions at 100 seconds.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _configuredOptions = options.Value;
        _localSettingsStore = localSettingsStore;
        _logger = logger;
        _tempRoot = codexOptions is null
            ? Path.Combine(Path.GetTempPath(), "codex-telegram")
            : CodexTelegramDataRoot.GetTempRoot(codexOptions.Value);
    }

    public async Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken)
    {
        string sourcePath = Path.GetFullPath(audioFilePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Audio file was not found.", sourcePath);
        }

        OpenAiSpeechToTextOptions options = GetRuntimeOptions();
        string apiKey = RequireApiKey(options);
        string model = RequireModel(options);
        EnsureSourceAudioFileIsUsable(sourcePath);

        string preparedPath = sourcePath;
        string? tempTranscodedPath = null;

        try
        {
            if (!IsDirectUploadSupported(sourcePath))
            {
                tempTranscodedPath = await TranscodeToSupportedFormatAsync(sourcePath, options, cancellationToken).ConfigureAwait(false);
                preparedPath = tempTranscodedPath;
            }

            EnsureWithinOpenAiLimit(preparedPath);
            using FileStream fileStream = File.OpenRead(preparedPath);
            using MultipartFormDataContent form = new();
            form.Add(new StringContent(model, Encoding.UTF8), "model");
            StreamContent fileContent = new(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(preparedPath));
            form.Add(fileContent, "file", Path.GetFileName(preparedPath));

            using HttpRequestMessage request = new(HttpMethod.Post, BuildEndpoint(options));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = form;

            int requestTimeoutSeconds = GetRequestTimeoutSeconds(options);
            using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(requestTimeoutSeconds));
            try
            {
                using HttpResponseMessage response = await _httpClient.SendAsync(request, requestTimeout.Token).ConfigureAwait(false);
                string payload = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
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
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && requestTimeout.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"OpenAI transcription timed out after {FormatDuration(requestTimeoutSeconds)}. " +
                    "Increase OpenAI:RequestTimeoutSeconds if longer transcriptions are expected.");
            }
        }
        finally
        {
            if (tempTranscodedPath is not null)
            {
                TryDelete(tempTranscodedPath);
            }
        }
    }

    private OpenAiSpeechToTextOptions GetRuntimeOptions()
    {
        if (_localSettingsStore is null)
        {
            return _configuredOptions;
        }

        try
        {
            string? localApiKey = LocalSettingsStore.Load(_localSettingsStore.FilePath).GetOpenAiApiKeyForRuntime();
            return new OpenAiSpeechToTextOptions
            {
                ApiKey = localApiKey ?? _configuredOptions.ApiKey,
                Model = _configuredOptions.Model,
                BaseUrl = _configuredOptions.BaseUrl,
                FfmpegPath = _configuredOptions.FfmpegPath,
                RequestTimeoutSeconds = _configuredOptions.RequestTimeoutSeconds,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Could not refresh local OpenAI transcription settings; using startup configuration.");
            return _configuredOptions;
        }
    }

    private static Uri BuildEndpoint(OpenAiSpeechToTextOptions options)
    {
        string baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
            ? "https://api.openai.com/v1/"
            : options.BaseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(baseUrl, UriKind.Absolute), "audio/transcriptions");
    }

    private static string RequireApiKey(OpenAiSpeechToTextOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException("OpenAI:ApiKey must be configured to transcribe Telegram audio.");
        }

        return options.ApiKey.Trim();
    }

    private static string RequireModel(OpenAiSpeechToTextOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            return "whisper-1";
        }

        return options.Model.Trim();
    }

    private static int GetRequestTimeoutSeconds(OpenAiSpeechToTextOptions options)
        => options.RequestTimeoutSeconds > 0
            ? options.RequestTimeoutSeconds
            : OpenAiSpeechToTextDefaults.RequestTimeoutSeconds;

    private static string FormatDuration(int seconds)
        => seconds % 60 == 0
            ? $"{seconds / 60} minutes"
            : $"{seconds} seconds";

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

    private async Task<string> TranscodeToSupportedFormatAsync(
        string inputFilePath,
        OpenAiSpeechToTextOptions options,
        CancellationToken cancellationToken)
    {
        string tempDirectory = Path.Combine(_tempRoot, "telegram-audio");
        Directory.CreateDirectory(tempDirectory);
        string outputFilePath = Path.Combine(tempDirectory, $"{Guid.NewGuid():n}.m4a");

        string ffmpegPath = string.IsNullOrWhiteSpace(options.FfmpegPath) ? "ffmpeg" : options.FfmpegPath.Trim();
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
