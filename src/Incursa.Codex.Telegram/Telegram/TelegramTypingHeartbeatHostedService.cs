using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Keeps Telegram's native typing indicator alive while followed Codex turns are running.
/// </summary>
internal sealed class TelegramTypingHeartbeatHostedService : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(4);

    private readonly TelegramBotOptions _options;
    private readonly ICodexTurnExecutionCoordinator _turnCoordinator;
    private readonly ITelegramThreadFollowRegistry _followRegistry;
    private readonly ITelegramBotMessageSender _sender;
    private readonly ILogger<TelegramTypingHeartbeatHostedService> _logger;

    public TelegramTypingHeartbeatHostedService(
        IOptions<TelegramBotOptions> options,
        ICodexTurnExecutionCoordinator turnCoordinator,
        ITelegramThreadFollowRegistry followRegistry,
        ITelegramBotMessageSender sender,
        ILogger<TelegramTypingHeartbeatHostedService> logger)
    {
        _options = options.Value;
        _turnCoordinator = turnCoordinator;
        _followRegistry = followRegistry;
        _sender = sender;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => RunAsync(stoppingToken);

    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Telegram typing heartbeat is disabled.");
            return;
        }

        _logger.LogInformation("Telegram typing heartbeat started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendHeartbeatAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(HeartbeatInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Telegram typing heartbeat failed; retrying.");
                await Task.Delay(HeartbeatInterval, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Telegram typing heartbeat stopped.");
    }

    internal async Task<int> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        HashSet<TelegramConversationScope> targets = [];
        foreach (string threadId in _turnCoordinator.GetActiveThreadIds())
        {
            foreach (TelegramConversationScope target in _followRegistry.GetTargets(threadId))
            {
                targets.Add(target);
            }
        }

        foreach (TelegramConversationScope target in targets)
        {
            await _sender.SendTypingActionAsync(target, cancellationToken).ConfigureAwait(false);
        }

        return targets.Count;
    }
}
