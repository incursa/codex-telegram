using System.Collections.Concurrent;
using System.Security.Cryptography;
using Incursa.Codex.Telegram.Telegram;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Incursa.Codex.Telegram.Configuration;

/// <summary>
/// Minimal Telegram Bot API client used only by the local first-run setup wizard.
/// </summary>
internal interface ITelegramSetupClient
{
    /// <summary>
    /// Calls Telegram's <c>getMe</c> endpoint to verify that a bot token is usable.
    /// </summary>
    /// <param name="token">The normalized Telegram bot token.</param>
    /// <param name="cancellationToken">Cancellation token for the API request.</param>
    /// <returns>Identity details for the configured bot.</returns>
    Task<TelegramBotIdentity> ValidateBotTokenAsync(string token, CancellationToken cancellationToken);

    /// <summary>
    /// Waits for one private Telegram message containing the setup challenge so setup can capture the operator's numeric user ID.
    /// </summary>
    /// <param name="token">The normalized Telegram bot token.</param>
    /// <param name="expectedChallenge">Random setup challenge that must be present in the message text.</param>
    /// <param name="timeout">Maximum time to wait for a matching private message.</param>
    /// <param name="cancellationToken">Cancellation token for the polling operation.</param>
    /// <returns>The captured Telegram user when a matching private message arrives; otherwise <see langword="null"/>.</returns>
    Task<TelegramSetupUser?> WaitForPrivateUserMessageAsync(string token, string expectedChallenge, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The Bot API operations used by the repeatable setup/profile pass. Keeping this
/// seam separate from the first-run wizard interface makes the pass fakeable
/// without requiring a live bot or a token in tests.
/// </summary>
internal interface ITelegramProfileSetupClient
{
    Task SetMyCommandsAsync(string token, IEnumerable<BotCommand> commands, string? languageCode, CancellationToken cancellationToken);

    Task SetChatMenuButtonAsync(string token, long? chatId, MenuButton menuButton, CancellationToken cancellationToken);

    Task SetMyNameAsync(string token, string name, string? languageCode, CancellationToken cancellationToken);

    Task SetMyDescriptionAsync(string token, string description, string? languageCode, CancellationToken cancellationToken);

    Task SetMyShortDescriptionAsync(string token, string shortDescription, string? languageCode, CancellationToken cancellationToken);

    Task SetMyProfilePhotoAsync(string token, string profilePhotoPath, CancellationToken cancellationToken);
}

/// <summary>
/// Identity details returned for a validated Telegram bot token.
/// </summary>
/// <param name="Id">Telegram bot user ID.</param>
/// <param name="Username">Telegram bot username without the leading at sign.</param>
/// <param name="DisplayName">Human-readable bot display name.</param>
/// <param name="CanJoinGroups">Whether Telegram reports that the bot can be invited to groups.</param>
/// <param name="CanReadAllGroupMessages">Whether Telegram reports privacy mode is disabled for the bot.</param>
internal sealed record TelegramBotIdentity(
    long Id,
    string? Username,
    string DisplayName,
    bool? CanJoinGroups,
    bool? CanReadAllGroupMessages);

/// <summary>
/// Telegram operator identity captured by the setup wizard from a private chat message.
/// </summary>
/// <param name="UserId">Numeric Telegram user ID to allowlist.</param>
/// <param name="Username">Telegram username without the leading at sign, when present.</param>
/// <param name="DisplayName">Human-readable sender name.</param>
/// <param name="ChatId">Private chat ID for the captured setup message.</param>
internal sealed record TelegramSetupUser(
    long UserId,
    string? Username,
    string DisplayName,
    long ChatId);

/// <summary>
/// Telegram Bot API implementation of the first-run setup operations.
/// </summary>
internal sealed class TelegramSetupClient : ITelegramSetupClient, ITelegramProfileSetupClient
{
    private static readonly UpdateType[] SetupUpdates = [UpdateType.Message];
    private static readonly ConcurrentDictionary<string, ChallengeLease> ActiveChallenges = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ChallengeGate = new();
    private static readonly TimeSpan DefaultChallengeLifetime = TimeSpan.FromMinutes(2);
    private readonly Func<string, ITelegramBotClient> _clientFactory;

    internal TelegramSetupClient()
        : this(static token => new TelegramBotClient(token))
    {
    }

    internal TelegramSetupClient(ITelegramBotClient client)
        : this(_ => client ?? throw new ArgumentNullException(nameof(client)))
    {
    }

    internal TelegramSetupClient(Func<string, ITelegramBotClient> clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    /// <summary>
    /// Applies the app-owned command/menu defaults and explicitly opted-in
    /// profile fields. The operation result never contains the bot token.
    /// </summary>
    internal Task<TelegramProfileSetupResult> ApplyProfileSetupAsync(
        string token,
        TelegramProfileSetupOptions? options = null,
        CancellationToken cancellationToken = default)
        => new TelegramProfileSetup(this).ApplyAsync(token, options, cancellationToken);

    /// <inheritdoc />
    public async Task<TelegramBotIdentity> ValidateBotTokenAsync(string token, CancellationToken cancellationToken)
    {
        ITelegramBotClient client = CreateClient(token);
        User bot = await client.GetMe(cancellationToken).ConfigureAwait(false);
        return new TelegramBotIdentity(
            bot.Id,
            bot.Username,
            BuildDisplayName(bot),
            bot.CanJoinGroups,
            bot.CanReadAllGroupMessages);
    }

    /// <inheritdoc />
    public async Task<TelegramSetupUser?> WaitForPrivateUserMessageAsync(string token, string expectedChallenge, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || string.IsNullOrWhiteSpace(expectedChallenge))
        {
            return null;
        }

        string normalizedChallenge = expectedChallenge.Trim();
        if (!TryClaimChallenge(normalizedChallenge, timeout))
        {
            return null;
        }

        try
        {
            ITelegramBotClient client = CreateClient(token);
            using IDisposable receiverLease = TelegramUpdateReceiverLock.Acquire(token);
            // Coordinate with the hosted receiver before reading the polling
            // stream. A null offset starts at Telegram's earliest unconfirmed
            // update and, unlike the old negative offset, does not flush the
            // queue before setup begins.
            int? offset = null;

            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            while (!timeoutSource.IsCancellationRequested)
            {
                Update[] updates;
                try
                {
                    updates = await client.GetUpdates(
                        offset: offset,
                        limit: 20,
                        timeout: 5,
                        allowedUpdates: SetupUpdates,
                        cancellationToken: timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                foreach (Update update in updates)
                {
                    offset = update.Id + 1;
                    Message? message = update.Message;
                    User? sender = message?.From;
                    if (message is null || sender is null || message.Chat.Type is not ChatType.Private || !TextContainsSetupChallenge(message.Text, normalizedChallenge))
                    {
                        TelegramSetupUpdateBuffer.Enqueue(token, update);
                        continue;
                    }

                    TelegramSetupUser setupUser = new(
                        sender.Id,
                        sender.Username,
                        BuildDisplayName(sender),
                        message.Chat.Id);

                    await TryAcknowledgeCaptureAsync(client, setupUser, timeoutSource.Token).ConfigureAwait(false);
                    return setupUser;
                }
            }

            return null;
        }
        finally
        {
            // A challenge can be claimed only once. Removing it here also
            // bounds the in-memory state when polling times out or is canceled.
            ActiveChallenges.TryRemove(normalizedChallenge, out _);
        }
    }

    internal static string CreateSetupChallenge()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach ((string key, ChallengeLease lease) in ActiveChallenges)
        {
            if (lease.ExpiresAtUtc <= now)
            {
                ActiveChallenges.TryRemove(key, out _);
            }
        }

        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        string challenge = $"CT-{Convert.ToHexString(bytes)}";
        ActiveChallenges[challenge] = new(now.Add(DefaultChallengeLifetime), Claimed: false);
        return challenge;
    }

    private static bool TryClaimChallenge(string challenge, TimeSpan timeout)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiry = now.Add(timeout < DefaultChallengeLifetime ? timeout : DefaultChallengeLifetime);

        lock (ChallengeGate)
        {
            if (ActiveChallenges.TryGetValue(challenge, out ChallengeLease? existing))
            {
                if (existing.ExpiresAtUtc <= now)
                {
                    ActiveChallenges.TryRemove(challenge, out _);
                }
                else if (existing.Claimed)
                {
                    return false;
                }
            }

            // Keep compatibility with callers that supplied a challenge string
            // directly before challenge registration was introduced.
            ActiveChallenges[challenge] = new(expiry, Claimed: true);
            return true;
        }
    }

    private sealed record ChallengeLease(DateTimeOffset ExpiresAtUtc, bool Claimed);

    internal static bool TextContainsSetupChallenge(string? messageText, string expectedChallenge)
        => !string.IsNullOrWhiteSpace(expectedChallenge)
            && !string.IsNullOrWhiteSpace(messageText)
            && messageText.Contains(expectedChallenge.Trim(), StringComparison.OrdinalIgnoreCase);

    private ITelegramBotClient CreateClient(string token)
    {
        if (!TelegramBotToken.TryNormalize(token, out string normalizedToken, out string error))
        {
            throw new ArgumentException(error, nameof(token));
        }

        return _clientFactory(normalizedToken);
    }

    async Task ITelegramProfileSetupClient.SetMyCommandsAsync(
        string token,
        IEnumerable<BotCommand> commands,
        string? languageCode,
        CancellationToken cancellationToken)
        => await CreateClient(token).SetMyCommands(
            commands,
            BotCommandScope.Default(),
            languageCode ?? string.Empty,
            cancellationToken).ConfigureAwait(false);

    async Task ITelegramProfileSetupClient.SetChatMenuButtonAsync(
        string token,
        long? chatId,
        MenuButton menuButton,
        CancellationToken cancellationToken)
        => await CreateClient(token).SetChatMenuButton(chatId, menuButton, cancellationToken).ConfigureAwait(false);

    async Task ITelegramProfileSetupClient.SetMyNameAsync(
        string token,
        string name,
        string? languageCode,
        CancellationToken cancellationToken)
        => await CreateClient(token).SetMyName(name, languageCode ?? string.Empty, cancellationToken).ConfigureAwait(false);

    async Task ITelegramProfileSetupClient.SetMyDescriptionAsync(
        string token,
        string description,
        string? languageCode,
        CancellationToken cancellationToken)
        => await CreateClient(token).SetMyDescription(description, languageCode ?? string.Empty, cancellationToken).ConfigureAwait(false);

    async Task ITelegramProfileSetupClient.SetMyShortDescriptionAsync(
        string token,
        string shortDescription,
        string? languageCode,
        CancellationToken cancellationToken)
        => await CreateClient(token).SetMyShortDescription(shortDescription, languageCode ?? string.Empty, cancellationToken).ConfigureAwait(false);

    async Task ITelegramProfileSetupClient.SetMyProfilePhotoAsync(
        string token,
        string profilePhotoPath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(profilePhotoPath);
        InputProfilePhotoStatic photo = new()
        {
            Photo = InputFile.FromStream(stream, Path.GetFileName(profilePhotoPath)),
        };
        await CreateClient(token).SetMyProfilePhoto(photo, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryAcknowledgeCaptureAsync(
        ITelegramBotClient client,
        TelegramSetupUser setupUser,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.SendMessage(
                setupUser.ChatId,
                "Codex Telegram setup captured your user ID because your message matched the terminal challenge. Return to the terminal to finish setup.",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Setup can still complete if the acknowledgement message fails after the inbound message was captured.
        }
    }

    private static string BuildDisplayName(User user)
    {
        string fullName = string.Join(' ', new[] { user.FirstName, user.LastName }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            return fullName;
        }

        return string.IsNullOrWhiteSpace(user.Username) ? user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : user.Username;
    }
}
