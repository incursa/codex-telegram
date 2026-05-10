using System.Globalization;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

internal sealed class TelegramLaunchpadExpirationHostedService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ITelegramBotStateStore _stateStore;
    private readonly ITelegramBotMessageSender _sender;
    private readonly TelegramBotOptions _options;
    private readonly ILogger<TelegramLaunchpadExpirationHostedService> _logger;

    public TelegramLaunchpadExpirationHostedService(
        ITelegramBotStateStore stateStore,
        ITelegramBotMessageSender sender,
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramLaunchpadExpirationHostedService> logger)
    {
        _stateStore = stateStore;
        _sender = sender;
        _options = options.Value;
        _logger = logger;
    }

    internal async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        IReadOnlyCollection<TelegramConversationState> conversationStates;
        try
        {
            conversationStates = await _stateStore.ListConversationStatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Launchpad expiry sweep failed while reading Telegram state.");
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (TelegramConversationState state in conversationStates.Where(state =>
            state.Scope.MessageThreadId is null
            && state.LaunchpadLastTouchedUtc is not null
            && TelegramLaunchpadPolicy.IsExpired(state.LaunchpadLastTouchedUtc.Value, now)))
        {
            bool removed;
            try
            {
                removed = await _stateStore.ClearLaunchpadStateAsync(state.Scope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    exception,
                    "Launchpad expiry sweep could not clear state for chat {ChatId}.",
                    state.Scope.ChatId);
                continue;
            }

            if (!removed)
            {
                continue;
            }

            try
            {
                await _sender.SendTextMessageAsync(
                    state.Scope,
                    $"Launchpad expired after {TelegramLaunchpadPolicy.InactivityTimeout.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes of inactivity. Use /launchpad on to arm it again.",
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    exception,
                    "Launchpad expiry notification failed for chat {ChatId}.",
                    state.Scope.ChatId);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Telegram launchpad expiry sweep is disabled because Telegram is disabled.");
            return;
        }

        await SweepOnceAsync(stoppingToken).ConfigureAwait(false);

        using PeriodicTimer timer = new(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }
}
