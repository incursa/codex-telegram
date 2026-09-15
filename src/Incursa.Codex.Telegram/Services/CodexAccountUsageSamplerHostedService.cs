using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Samples account quota independently of Mini App opens so sparklines retain
/// history while the browser surface is closed.
/// </summary>
internal sealed class CodexAccountUsageSamplerHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly ICodexAccountUsageService _usageService;
    private readonly ILogger<CodexAccountUsageSamplerHostedService> _logger;

    public CodexAccountUsageSamplerHostedService(
        ICodexAccountUsageService usageService,
        ILogger<CodexAccountUsageSamplerHostedService> logger)
    {
        _usageService = usageService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SampleAsync(stoppingToken).ConfigureAwait(false);
        using PeriodicTimer timer = new(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SampleAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _usageService.GetUsageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Account usage sampling is unavailable; the Mini App will report the latest confirmed snapshot.");
        }
    }
}
