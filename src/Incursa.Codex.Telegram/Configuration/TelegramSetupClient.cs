using System.Security.Cryptography;
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
internal sealed class TelegramSetupClient : ITelegramSetupClient
{
    private static readonly UpdateType[] SetupUpdates = [UpdateType.Message];

    /// <inheritdoc />
    public async Task<TelegramBotIdentity> ValidateBotTokenAsync(string token, CancellationToken cancellationToken)
    {
        TelegramBotClient client = CreateClient(token);
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

        TelegramBotClient client = CreateClient(token);
        int? offset = await CreateFreshOffsetAsync(client, cancellationToken).ConfigureAwait(false);

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

    internal static string CreateSetupChallenge()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return $"CT-{Convert.ToHexString(bytes)}";
    }

    internal static bool TextContainsSetupChallenge(string? messageText, string expectedChallenge)
        => !string.IsNullOrWhiteSpace(expectedChallenge)
            && !string.IsNullOrWhiteSpace(messageText)
            && messageText.Contains(expectedChallenge.Trim(), StringComparison.OrdinalIgnoreCase);

    private static TelegramBotClient CreateClient(string token)
    {
        if (!TelegramBotToken.TryNormalize(token, out string normalizedToken, out string error))
        {
            throw new ArgumentException(error, nameof(token));
        }

        return new TelegramBotClient(normalizedToken);
    }

    private static async Task<int?> CreateFreshOffsetAsync(TelegramBotClient client, CancellationToken cancellationToken)
    {
        Update[] updates = await client.GetUpdates(
            offset: -1,
            limit: 1,
            timeout: 0,
            allowedUpdates: SetupUpdates,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return updates.Length == 0 ? null : updates[^1].Id + 1;
    }

    private static async Task TryAcknowledgeCaptureAsync(
        TelegramBotClient client,
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
