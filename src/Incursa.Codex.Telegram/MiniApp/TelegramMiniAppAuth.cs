using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Incursa.Codex.Telegram.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Identity asserted by Telegram Mini App initialization data.
/// </summary>
internal sealed record TelegramMiniAppIdentity(
    long UserId,
    string? Username,
    string? FirstName,
    string? LastName);

/// <summary>
/// Validates Telegram Mini App initialization data before serving private data.
/// </summary>
internal sealed class TelegramMiniAppAuth
{
    private const string InitDataHeader = "X-Telegram-Init-Data";
    private readonly TelegramBotOptions _botOptions;
    private readonly TelegramMiniAppOptions _miniAppOptions;
    private readonly TimeProvider _timeProvider;

    public TelegramMiniAppAuth(
        IOptions<TelegramBotOptions> botOptions,
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        TimeProvider timeProvider)
    {
        _botOptions = botOptions.Value;
        _miniAppOptions = miniAppOptions.Value;
        _timeProvider = timeProvider;
    }

    public bool TryAuthenticate(
        HttpRequest request,
        out TelegramMiniAppIdentity identity,
        out string failureReason)
    {
        identity = null!;
        failureReason = "Telegram initialization data is missing.";
        string? initData = request.Headers[InitDataHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(initData))
        {
            return false;
        }

        Dictionary<string, string> fields;
        try
        {
            fields = QueryHelpers.ParseQuery(initData)
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
        }
        catch (ArgumentException)
        {
            failureReason = "Telegram initialization data is malformed.";
            return false;
        }

        if (!fields.TryGetValue("hash", out string? suppliedHash)
            || string.IsNullOrWhiteSpace(suppliedHash)
            || !fields.TryGetValue("auth_date", out string? authDateText)
            || !long.TryParse(authDateText, out long authDate)
            || !fields.TryGetValue("user", out string? userJson))
        {
            failureReason = "Telegram initialization data is incomplete.";
            return false;
        }

        string dataCheckString = string.Join(
            '\n',
            fields
                .Where(pair => !pair.Key.Equals("hash", StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

        if (!IsValidHash(dataCheckString, suppliedHash))
        {
            failureReason = "Telegram initialization data failed signature validation.";
            return false;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset authenticatedAt;
        try
        {
            authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(authDate);
        }
        catch (ArgumentOutOfRangeException)
        {
            failureReason = "Telegram initialization data is malformed.";
            return false;
        }
        TimeSpan age = now - authenticatedAt;
        if (age < TimeSpan.FromSeconds(-60) || age > TimeSpan.FromSeconds(_miniAppOptions.InitDataMaxAgeSeconds))
        {
            failureReason = "Telegram initialization data has expired.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(userJson);
            JsonElement user = document.RootElement;
            if (!user.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt64(out long userId))
            {
                failureReason = "Telegram user identity is missing.";
                return false;
            }

            if (!_botOptions.AllowedUserIds.Contains(userId))
            {
                failureReason = "Telegram user is not allowlisted.";
                return false;
            }

            identity = new TelegramMiniAppIdentity(
                userId,
                GetOptionalString(user, "username"),
                GetOptionalString(user, "first_name"),
                GetOptionalString(user, "last_name"));
            failureReason = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            failureReason = "Telegram user identity is malformed.";
            return false;
        }
    }

    /// <summary>
    /// Rechecks the current allowlist for an already-issued browser session.
    /// </summary>
    public bool IsAllowlisted(long userId) => _botOptions.AllowedUserIds.Contains(userId);

    private bool IsValidHash(string dataCheckString, string suppliedHash)
    {
        if (string.IsNullOrWhiteSpace(_botOptions.Token)
            || suppliedHash.Length != 64)
        {
            return false;
        }

        byte[] secretKey;
        using (HMACSHA256 secretHmac = new(Encoding.UTF8.GetBytes("WebAppData")))
        {
            secretKey = secretHmac.ComputeHash(Encoding.UTF8.GetBytes(_botOptions.Token));
        }

        byte[] expectedHash;
        using (HMACSHA256 dataHmac = new(secretKey))
        {
            expectedHash = dataHmac.ComputeHash(Encoding.UTF8.GetBytes(dataCheckString));
        }

        byte[] suppliedHashBytes;
        try
        {
            suppliedHashBytes = Convert.FromHexString(suppliedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return suppliedHashBytes.Length == expectedHash.Length
            && CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHashBytes);
    }

    private static string? GetOptionalString(JsonElement user, string propertyName)
        => user.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
