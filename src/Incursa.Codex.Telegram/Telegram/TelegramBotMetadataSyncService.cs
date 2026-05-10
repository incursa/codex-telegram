using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace Incursa.Codex.Telegram.Telegram;

internal sealed class TelegramBotMetadataSyncService : IHostedService
{
    private readonly TelegramBotOptions _options;
    private readonly ILogger<TelegramBotMetadataSyncService> _logger;
    private readonly Lazy<ITelegramBotProfileApiClient> _client;

    public TelegramBotMetadataSyncService(
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramBotMetadataSyncService> logger)
        : this(
            options.Value,
            logger,
            new Lazy<ITelegramBotProfileApiClient>(() => new TelegramBotProfileApiClient(new TelegramBotClient(RequireToken(options.Value)))))
    {
    }

    internal TelegramBotMetadataSyncService(
        TelegramBotOptions options,
        ILogger<TelegramBotMetadataSyncService> logger,
        ITelegramBotProfileApiClient client)
        : this(options, logger, new Lazy<ITelegramBotProfileApiClient>(() => client))
    {
    }

    private TelegramBotMetadataSyncService(
        TelegramBotOptions options,
        ILogger<TelegramBotMetadataSyncService> logger,
        Lazy<ITelegramBotProfileApiClient> client)
    {
        _options = options;
        _logger = logger;
        _client = client;
    }

    public Task StartAsync(CancellationToken cancellationToken)
        => SyncAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("Telegram bot metadata sync skipped because Telegram is disabled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            _logger.LogWarning("Telegram bot metadata sync skipped because TelegramBot:Token is missing.");
            return;
        }

        _logger.LogInformation("Synchronizing Telegram bot metadata and default group settings.");

        int appliedCount = 0;
        appliedCount += await TryApplyAsync(
            "commands",
            cancellationToken => _client.Value.SetMyCommandsAsync(TelegramBotMetadata.BuildPublicBotCommands(), cancellationToken),
            cancellationToken).ConfigureAwait(false) ? 1 : 0;
        appliedCount += await TryApplyAsync(
            "description",
            cancellationToken => _client.Value.SetMyDescriptionAsync(TelegramBotMetadata.Description, cancellationToken),
            cancellationToken).ConfigureAwait(false) ? 1 : 0;
        appliedCount += await TryApplyAsync(
            "short description",
            cancellationToken => _client.Value.SetMyShortDescriptionAsync(TelegramBotMetadata.ShortDescription, cancellationToken),
            cancellationToken).ConfigureAwait(false) ? 1 : 0;
        appliedCount += await TryApplyAsync(
            "default group admin rights",
            cancellationToken => _client.Value.SetMyDefaultAdministratorRightsAsync(TelegramBotMetadata.DefaultGroupAdministratorRights, cancellationToken),
            cancellationToken).ConfigureAwait(false) ? 1 : 0;

        _logger.LogInformation(
            "Telegram bot metadata sync completed; {AppliedCount} of {ExpectedCount} settings were applied or already current.",
            appliedCount,
            4);
    }

    private async Task<bool> TryApplyAsync(
        string label,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Telegram bot {Label} updated.", label);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiRequestException exception) when (label == "default group admin rights" && IsRightsNotModified(exception))
        {
            _logger.LogDebug("Telegram bot default group admin rights already match the desired defaults.");
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Telegram bot {Label} sync failed; continuing startup.", label);
            return false;
        }
    }

    private static bool IsRightsNotModified(ApiRequestException exception)
        => exception.Message.Contains("rights not modified", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("not modified", StringComparison.OrdinalIgnoreCase);

    private static string RequireToken(TelegramBotOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new InvalidOperationException("TelegramBot:Token must be configured when TelegramBot:Enabled is true.");
        }

        return options.Token.Trim();
    }
}

internal interface ITelegramBotProfileApiClient
{
    Task SetMyCommandsAsync(IEnumerable<BotCommand> commands, CancellationToken cancellationToken);

    Task SetMyDescriptionAsync(string description, CancellationToken cancellationToken);

    Task SetMyShortDescriptionAsync(string shortDescription, CancellationToken cancellationToken);

    Task SetMyDefaultAdministratorRightsAsync(ChatAdministratorRights rights, CancellationToken cancellationToken);
}

internal sealed class TelegramBotProfileApiClient : ITelegramBotProfileApiClient
{
    private readonly ITelegramBotClient _client;

    public TelegramBotProfileApiClient(ITelegramBotClient client)
    {
        _client = client;
    }

    public Task SetMyCommandsAsync(IEnumerable<BotCommand> commands, CancellationToken cancellationToken)
        => _client.SetMyCommands(commands, BotCommandScope.Default(), string.Empty, cancellationToken);

    public Task SetMyDescriptionAsync(string description, CancellationToken cancellationToken)
        => _client.SetMyDescription(description, string.Empty, cancellationToken);

    public Task SetMyShortDescriptionAsync(string shortDescription, CancellationToken cancellationToken)
        => _client.SetMyShortDescription(shortDescription, string.Empty, cancellationToken);

    public Task SetMyDefaultAdministratorRightsAsync(ChatAdministratorRights rights, CancellationToken cancellationToken)
        => _client.SetMyDefaultAdministratorRights(rights, false, cancellationToken);
}
