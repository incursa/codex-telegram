using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Background service that sends or queues idle input bundles after their inactivity window elapses.
/// </summary>
internal sealed class TelegramInputBundleAutoDispatchHostedService : BackgroundService
{
    private static readonly TimeSpan ProcessedBundleDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(2);

    private readonly TelegramCodexBotCommandHandler _handler;
    private readonly ITelegramBotMessageSender _sender;
    private readonly TelegramBotOptions _botOptions;
    private readonly TelegramInputOptions _inputOptions;
    private readonly ILogger<TelegramInputBundleAutoDispatchHostedService> _logger;
    private readonly ITelegramProcessorDelayProvider _delayProvider;

    public TelegramInputBundleAutoDispatchHostedService(
        TelegramCodexBotCommandHandler handler,
        ITelegramBotMessageSender sender,
        IOptions<TelegramBotOptions> botOptions,
        IOptions<TelegramInputOptions> inputOptions,
        ILogger<TelegramInputBundleAutoDispatchHostedService> logger)
        : this(handler, sender, botOptions, inputOptions, logger, SystemTelegramProcessorDelayProvider.Instance)
    {
    }

    internal TelegramInputBundleAutoDispatchHostedService(
        TelegramCodexBotCommandHandler handler,
        ITelegramBotMessageSender sender,
        IOptions<TelegramBotOptions> botOptions,
        IOptions<TelegramInputOptions> inputOptions,
        ILogger<TelegramInputBundleAutoDispatchHostedService> logger,
        ITelegramProcessorDelayProvider delayProvider)
    {
        _handler = handler;
        _sender = sender;
        _botOptions = botOptions.Value;
        _inputOptions = inputOptions.Value;
        _logger = logger;
        _delayProvider = delayProvider;
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => RunAsync(stoppingToken);

    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        if (!_botOptions.Enabled)
        {
            _logger.LogInformation("Telegram input bundle auto-dispatch is disabled because Telegram is disabled.");
            return;
        }

        if (_inputOptions.AutoDispatchAfterSeconds <= 0)
        {
            _logger.LogInformation("Telegram input bundle auto-dispatch is disabled.");
            return;
        }

        _logger.LogInformation(
            "Telegram input bundle auto-dispatch started with a {AutoDispatchAfterSeconds}s inactivity window.",
            _inputOptions.AutoDispatchAfterSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool processed = await _handler.ProcessDueInputBundleAsync(_sender, stoppingToken).ConfigureAwait(false);
                await _delayProvider.DelayAsync(
                    processed ? ProcessedBundleDelay : IdleDelay,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Telegram input bundle auto-dispatch failed; retrying.");
                await _delayProvider.DelayAsync(FailureRetryDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Telegram input bundle auto-dispatch stopped.");
    }
}
