using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Incursa.Codex.Telegram.Options;

namespace Incursa.Codex.Telegram.Telegram;

internal sealed class TelegramQueuedPromptProcessorHostedService : BackgroundService
{
    private readonly ITelegramQueuedPromptProcessor _processor;
    private readonly TelegramBotOptions _options;
    private readonly ILogger<TelegramQueuedPromptProcessorHostedService> _logger;

    public TelegramQueuedPromptProcessorHostedService(
        ITelegramQueuedPromptProcessor processor,
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramQueuedPromptProcessorHostedService> logger)
    {
        _processor = processor;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Telegram queued prompt processor is disabled.");
            return;
        }

        _logger.LogInformation("Telegram queued prompt processor started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool processed = await _processor.ProcessNextAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(processed ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Telegram queued prompt processor failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Telegram queued prompt processor stopped.");
    }
}
