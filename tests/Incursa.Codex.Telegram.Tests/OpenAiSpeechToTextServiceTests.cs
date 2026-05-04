using System.Net;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class OpenAiSpeechToTextServiceTests
{
    [Fact]
    public async Task TranscribeAsync_FailsClearlyWhenApiKeyIsMissing()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string audioPath = CreateFile(temp, "audio.wav", [1, 2, 3]);
        TestHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK));
        OpenAiSpeechToTextService service = CreateService(handler, new OpenAiSpeechToTextOptions
        {
            ApiKey = "",
            Model = "whisper-1",
        });

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TranscribeAsync(audioPath, CancellationToken.None));

        Assert.Contains("OpenAI:ApiKey", exception.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task TranscribeAsync_IncludesHttpFailureDetails()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string audioPath = CreateFile(temp, "audio.wav", [1, 2, 3]);
        TestHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            ReasonPhrase = "Bad Request",
            Content = new StringContent("bad request body"),
        });
        OpenAiSpeechToTextService service = CreateService(handler, new OpenAiSpeechToTextOptions
        {
            ApiKey = "test-key",
            Model = "whisper-1",
        });

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TranscribeAsync(audioPath, CancellationToken.None));

        Assert.Contains("400 Bad Request", exception.Message);
        Assert.Contains("bad request body", exception.Message);
        Assert.Equal(1, handler.RequestCount);
    }

    private static OpenAiSpeechToTextService CreateService(TestHttpMessageHandler handler, OpenAiSpeechToTextOptions options)
        => new(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<OpenAiSpeechToTextService>.Instance);

    private static string CreateFile(TemporaryDirectory temp, string name, byte[] contents)
    {
        string path = Path.Combine(temp.Path, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(handle(request));
        }
    }
}
