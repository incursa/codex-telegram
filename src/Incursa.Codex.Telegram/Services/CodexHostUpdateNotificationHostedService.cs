using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Publishes the result left by an external updater after the host restarts.
/// </summary>
internal sealed class CodexHostUpdateNotificationHostedService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly ICodexHostUpdateManager _updateManager;
    private readonly ITelegramBotMessageSender _sender;
    private readonly ILogger<CodexHostUpdateNotificationHostedService> _logger;

    public CodexHostUpdateNotificationHostedService(
        ICodexHostUpdateManager updateManager,
        ITelegramBotMessageSender sender,
        ILogger<CodexHostUpdateNotificationHostedService> logger)
    {
        _updateManager = updateManager;
        _sender = sender;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryPublishAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not publish the host update result to Telegram.");
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TryPublishAsync(CancellationToken cancellationToken)
    {
        CodexHostUpdateSnapshot? update = await _updateManager.TryGetPendingNotificationAsync(cancellationToken).ConfigureAwait(false);
        if (update is null || string.IsNullOrWhiteSpace(update.RequestId))
        {
            return;
        }

        // The conversation is written by the authorized Telegram request and is
        // intentionally parsed as a storage key rather than accepting a path or
        // arbitrary destination from the external updater.
        string? conversationKey = await _updateManager.GetConversationKeyAsync(update.RequestId, cancellationToken).ConfigureAwait(false);
        if (!TelegramConversationScope.TryParseStorageKey(conversationKey, out TelegramConversationScope conversation))
        {
            _logger.LogWarning("Host update {RequestId} has no valid Telegram conversation for completion delivery.", update.RequestId);
            return;
        }

        await _sender.SendTextMessageAsync(conversation, FormatUpdateResult(update), null, cancellationToken).ConfigureAwait(false);
        await _updateManager.MarkNotificationSentAsync(update.RequestId, cancellationToken).ConfigureAwait(false);
    }

    private static string FormatUpdateResult(CodexHostUpdateSnapshot update)
    {
        if (update.State is CodexHostUpdateState.Applying or CodexHostUpdateState.RollbackApplying)
        {
            string startAction = update.Action == CodexHostUpdateAction.Rollback ? "rollback" : "update";
            return string.Join(Environment.NewLine, [
                $"Host {startAction} starting.",
                $"Running version: {update.CurrentVersion}",
                $"Target version: {update.TargetVersion ?? "(external updater selects the release)"}",
                "The service will be offline briefly while the package is installed. I’ll report back after the restarted service passes its health check.",
            ]);
        }

        string heading = update.State is CodexHostUpdateState.Active or CodexHostUpdateState.RollbackActive
            ? "Host update complete."
            : "Host update failed.";
        string action = update.Action == CodexHostUpdateAction.Rollback ? "Rollback" : "Update";
        return string.Join(Environment.NewLine, [
            heading,
            $"Action: {action}",
            $"Running version: {update.CurrentVersion}",
            $"Target version: {update.TargetVersion ?? "(external updater selected the release)"}",
            $"Outcome: {update.OutcomeCode ?? "(none reported)"}",
            update.State is CodexHostUpdateState.Active or CodexHostUpdateState.RollbackActive
                ? "The service passed the external updater's post-restart health check."
                : "The service did not pass the external updater's post-restart health check; inspect the service logs before retrying.",
        ]);
    }
}
