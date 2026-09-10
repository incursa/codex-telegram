using Telegram.Bot.Types;

namespace Incursa.Codex.Telegram.Configuration;

/// <summary>Identifies who owns a Telegram setting after setup.</summary>
internal enum TelegramSetupOwnership
{
    AppManaged,
    Manual,
}

/// <summary>Outcome of one independent Bot API setup operation.</summary>
internal enum TelegramSetupOperationStatus
{
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>Input to the repeatable command/menu/profile setup pass.</summary>
internal sealed record TelegramProfileSetupOptions
{
    public TelegramSetupOwnership CommandsOwnership { get; init; } = TelegramSetupOwnership.AppManaged;

    public TelegramSetupOwnership MenuButtonOwnership { get; init; } = TelegramSetupOwnership.AppManaged;

    public TelegramSetupOwnership NameOwnership { get; init; } = TelegramSetupOwnership.AppManaged;

    public TelegramSetupOwnership DescriptionOwnership { get; init; } = TelegramSetupOwnership.AppManaged;

    public TelegramSetupOwnership ShortDescriptionOwnership { get; init; } = TelegramSetupOwnership.AppManaged;

    public TelegramSetupOwnership ProfilePhotoOwnership { get; init; } = TelegramSetupOwnership.AppManaged;

    public IReadOnlyList<BotCommand>? Commands { get; init; }

    public long? MenuButtonChatId { get; init; }

    public MenuButton? MenuButton { get; init; }

    public string? LanguageCode { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? ShortDescription { get; init; }

    public string? ProfilePhotoPath { get; init; }
}

/// <summary>Result for one command/menu/profile operation.</summary>
internal sealed record TelegramSetupOperationResult(
    string Operation,
    TelegramSetupOperationStatus Status,
    TelegramSetupOwnership Ownership,
    string? FailureReason = null,
    string? BotFatherFallback = null)
{
    public bool Succeeded => Status == TelegramSetupOperationStatus.Succeeded;

    public bool Failed => Status == TelegramSetupOperationStatus.Failed;
}

/// <summary>Aggregate result from a repeatable setup pass.</summary>
internal sealed record TelegramProfileSetupResult(IReadOnlyList<TelegramSetupOperationResult> Operations)
{
    public bool Succeeded => Operations.All(operation => operation.Succeeded || operation.Status == TelegramSetupOperationStatus.Skipped);

    public bool HasFailures => Operations.Any(operation => operation.Failed);
}

/// <summary>
/// Applies app-owned Telegram commands and menu defaults and, when explicitly
/// selected, profile fields. Every operation is independent so one unsupported
/// optional Bot API feature does not hide the status of the others.
/// </summary>
internal sealed class TelegramProfileSetup
{
    internal const string ProfilePhotoBotFatherFallback =
        "The app could not apply the profile photo. Open @BotFather, send /setuserpic, choose the bot, and upload a neutral JPG photo.";

    private readonly ITelegramProfileSetupClient _client;

    internal TelegramProfileSetup(ITelegramProfileSetupClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>Applies one setup pass through an injected/fakeable client.</summary>
    internal static Task<TelegramProfileSetupResult> ApplyAsync(
        ITelegramProfileSetupClient client,
        string token,
        TelegramProfileSetupOptions? options = null,
        CancellationToken cancellationToken = default)
        => new TelegramProfileSetup(client).ApplyAsync(token, options, cancellationToken);

    internal Task<TelegramProfileSetupResult> ApplyAsync(
        string token,
        TelegramProfileSetupOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return ApplyCoreAsync(token, options ?? new TelegramProfileSetupOptions(), cancellationToken);
    }

    private async Task<TelegramProfileSetupResult> ApplyCoreAsync(
        string token,
        TelegramProfileSetupOptions options,
        CancellationToken cancellationToken)
    {
        List<TelegramSetupOperationResult> results = [];
        IReadOnlyList<BotCommand> commands = options.Commands ?? TelegramCommandCatalog.CreateDefaultCommands();

        results.Add(await RunAsync(
            "setMyCommands",
            options.CommandsOwnership,
            options.CommandsOwnership == TelegramSetupOwnership.AppManaged,
            () => _client.SetMyCommandsAsync(token, commands, options.LanguageCode, cancellationToken),
            token,
            cancellationToken).ConfigureAwait(false));

        results.Add(await RunAsync(
            "setChatMenuButton",
            options.MenuButtonOwnership,
            options.MenuButtonOwnership == TelegramSetupOwnership.AppManaged,
            () => _client.SetChatMenuButtonAsync(token, options.MenuButtonChatId, options.MenuButton ?? new MenuButtonCommands(), cancellationToken),
            token,
            cancellationToken).ConfigureAwait(false));

        if (options.Name is not null)
        {
            results.Add(await RunAsync(
                "setMyName",
                options.NameOwnership,
                options.NameOwnership == TelegramSetupOwnership.AppManaged,
                () => _client.SetMyNameAsync(token, options.Name, options.LanguageCode, cancellationToken),
                token,
                cancellationToken).ConfigureAwait(false));
        }

        if (options.Description is not null)
        {
            results.Add(await RunAsync(
                "setMyDescription",
                options.DescriptionOwnership,
                options.DescriptionOwnership == TelegramSetupOwnership.AppManaged,
                () => _client.SetMyDescriptionAsync(token, options.Description, options.LanguageCode, cancellationToken),
                token,
                cancellationToken).ConfigureAwait(false));
        }

        if (options.ShortDescription is not null)
        {
            results.Add(await RunAsync(
                "setMyShortDescription",
                options.ShortDescriptionOwnership,
                options.ShortDescriptionOwnership == TelegramSetupOwnership.AppManaged,
                () => _client.SetMyShortDescriptionAsync(token, options.ShortDescription, options.LanguageCode, cancellationToken),
                token,
                cancellationToken).ConfigureAwait(false));
        }

        if (options.ProfilePhotoPath is not null)
        {
            results.Add(await RunAsync(
                "setMyProfilePhoto",
                options.ProfilePhotoOwnership,
                options.ProfilePhotoOwnership == TelegramSetupOwnership.AppManaged,
                () => _client.SetMyProfilePhotoAsync(token, options.ProfilePhotoPath, cancellationToken),
                token,
                cancellationToken,
                ProfilePhotoBotFatherFallback).ConfigureAwait(false));
        }

        return new TelegramProfileSetupResult(results);
    }

    private static async Task<TelegramSetupOperationResult> RunAsync(
        string operation,
        TelegramSetupOwnership ownership,
        bool shouldRun,
        Func<Task> action,
        string token,
        CancellationToken cancellationToken,
        string? fallback = null)
    {
        if (!shouldRun)
        {
            return new TelegramSetupOperationResult(
                operation,
                TelegramSetupOperationStatus.Skipped,
                ownership,
                "This setting is manually managed and was not changed by the app.",
                fallback);
        }

        try
        {
            await action().ConfigureAwait(false);
            return new TelegramSetupOperationResult(operation, TelegramSetupOperationStatus.Succeeded, ownership);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new TelegramSetupOperationResult(
                operation,
                TelegramSetupOperationStatus.Failed,
                ownership,
                SanitizeError(exception.Message, token),
                fallback);
        }
    }

    private static string SanitizeError(string? message, string token)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Telegram rejected this setup operation.";
        }

        // Telegram.Bot normally returns a Bot API description, but keep this
        // contract safe if a custom transport includes request details.
        return message.Replace(token, "[redacted]", StringComparison.Ordinal);
    }
}
