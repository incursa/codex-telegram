using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Incursa.Codex.Telegram.Options;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Background service that drains queued user prompts once their Codex session is ready.
/// </summary>
internal sealed class TelegramQueuedPromptProcessorHostedService : BackgroundService
{
    private static readonly TimeSpan ProcessedPromptDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(2);

    private readonly ITelegramQueuedPromptProcessor _processor;
    private readonly TelegramBotOptions _options;
    private readonly ILogger<TelegramQueuedPromptProcessorHostedService> _logger;
    private readonly ITelegramProcessorDelayProvider _delayProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelegramQueuedPromptProcessorHostedService"/> class.
    /// </summary>
    /// <param name="processor">Queued prompt processor.</param>
    /// <param name="options">Telegram bot options.</param>
    /// <param name="logger">Logger for service lifecycle and failures.</param>
    public TelegramQueuedPromptProcessorHostedService(
        ITelegramQueuedPromptProcessor processor,
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramQueuedPromptProcessorHostedService> logger)
        : this(processor, options, logger, SystemTelegramProcessorDelayProvider.Instance)
    {
    }

    /// <summary>
    /// Initializes a new testable instance of the <see cref="TelegramQueuedPromptProcessorHostedService"/> class.
    /// </summary>
    /// <param name="processor">Queued prompt processor.</param>
    /// <param name="options">Telegram bot options.</param>
    /// <param name="logger">Logger for service lifecycle and failures.</param>
    /// <param name="delayProvider">Delay provider used to avoid real sleeps in tests.</param>
    internal TelegramQueuedPromptProcessorHostedService(
        ITelegramQueuedPromptProcessor processor,
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramQueuedPromptProcessorHostedService> logger,
        ITelegramProcessorDelayProvider delayProvider)
    {
        _processor = processor;
        _options = options.Value;
        _logger = logger;
        _delayProvider = delayProvider;
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => RunAsync(stoppingToken);

    /// <summary>
    /// Runs the queue-draining loop until cancellation.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown token.</param>
    /// <returns>A task that completes when the loop stops.</returns>
    internal async Task RunAsync(CancellationToken stoppingToken)
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
                await _delayProvider.DelayAsync(
                    processed ? ProcessedPromptDelay : IdleDelay,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Telegram queued prompt processor failed; retrying.");
                await _delayProvider.DelayAsync(FailureRetryDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Telegram queued prompt processor stopped.");
    }
}

/// <summary>
/// Provides delays for the queued prompt processor loop.
/// </summary>
internal interface ITelegramProcessorDelayProvider
{
    /// <summary>
    /// Waits for the supplied delay.
    /// </summary>
    /// <param name="delay">Delay duration.</param>
    /// <param name="cancellationToken">Cancellation token for shutdown.</param>
    /// <returns>A task that completes after the delay.</returns>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// Production delay provider backed by <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
/// </summary>
internal sealed class SystemTelegramProcessorDelayProvider : ITelegramProcessorDelayProvider
{
    /// <summary>
    /// Gets the singleton production delay provider.
    /// </summary>
    public static SystemTelegramProcessorDelayProvider Instance { get; } = new();

    private SystemTelegramProcessorDelayProvider()
    {
    }

    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}
