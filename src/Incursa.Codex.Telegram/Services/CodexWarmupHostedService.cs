using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal sealed class CodexWarmupHostedService : BackgroundService
{
    private readonly CodexSessionRuntimeRegistry _runtimeRegistry;
    private readonly CodexTelegramOptions _options;
    private readonly ILogger<CodexWarmupHostedService> _logger;

    public CodexWarmupHostedService(
        CodexSessionRuntimeRegistry runtimeRegistry,
        IOptions<CodexTelegramOptions> options,
        ILogger<CodexWarmupHostedService> logger)
    {
        _runtimeRegistry = runtimeRegistry;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.InitializeOnStart)
        {
            _logger.LogInformation("Codex runtime warmup is disabled.");
            return;
        }

        try
        {
            await _runtimeRegistry.GetDefaultAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("Codex runtime initialized.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Codex runtime warmup failed. The bot will retry when a command needs Codex.");
        }
    }
}
