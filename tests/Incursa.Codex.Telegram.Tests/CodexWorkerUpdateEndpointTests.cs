using System.Text.Json;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexWorkerUpdateEndpointTests
{
    [Fact]
    public async Task CompletionEndpointRequiresSeparateInstallerToken()
    {
        IOptions<CodexTelegramOptions> options = CreateOptions();
        FakeUpdateManager updateManager = new();
        DefaultHttpContext context = CreateContext(
            "Bearer wrong-installer-token",
            new { Version = "1.0.39", Sha256 = new string('a', 64), Healthy = true });

        IResult result = await CodexWorkerUpdateEndpoints.CompleteAsync(
            context,
            options,
            updateManager,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(updateManager.Called);
    }

    [Fact]
    public async Task CompletionEndpointReturnsSuccessOnlyForActiveEvidence()
    {
        IOptions<CodexTelegramOptions> options = CreateOptions();
        FakeUpdateManager updateManager = new();
        DefaultHttpContext context = CreateContext(
            "Bearer installer-token-that-is-long-enough-1234",
            new { Version = "1.0.39", Sha256 = new string('a', 64), Healthy = true });

        IResult result = await CodexWorkerUpdateEndpoints.CompleteAsync(
            context,
            options,
            updateManager,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(updateManager.Called);
    }

    private static IOptions<CodexTelegramOptions> CreateOptions()
        => Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Updates = new CodexWorkerUpdateOptions
            {
                Enabled = true,
                InstallerAuthenticationToken = "installer-token-that-is-long-enough-1234",
            },
        });

    private static DefaultHttpContext CreateContext(string authorization, object payload)
    {
        DefaultHttpContext context = new();
        context.RequestServices = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();
        context.Request.Headers.Authorization = authorization;
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FakeUpdateManager : ICodexWorkerUpdateManager
    {
        public bool Called { get; private set; }

        public Task<CodexWorkerUpdateSnapshot> GetStatusAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CodexWorkerUpdateSnapshot> StageAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CodexWorkerUpdateSnapshot> StageRollbackAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CodexWorkerUpdateSnapshot> CompleteAsync(
            CodexWorkerUpdateCompletion completion,
            CancellationToken cancellationToken)
        {
            Called = true;
            return Task.FromResult(new CodexWorkerUpdateSnapshot(
                CodexWorkerUpdateState.Active,
                completion.Version,
                completion.Sha256,
                "candidate.staged",
                "rollback.bin",
                DateTimeOffset.UtcNow,
                "health_verified"));
        }
    }
}
