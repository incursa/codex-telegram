using System.Security.Cryptography;
using System.Text;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramMiniAppAuthTests
{
    [Fact]
    public void AcceptsFreshSignedDataForAllowlistedUser()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TelegramMiniAppAuth auth = CreateAuth(now);
        DefaultHttpContext context = CreateContext(BuildInitData("secret-token", now, 12345));

        bool result = auth.TryAuthenticate(context.Request, out TelegramMiniAppIdentity identity, out string failureReason);

        Assert.True(result, failureReason);
        Assert.Equal(12345, identity.UserId);
        Assert.Equal("samuel", identity.Username);
    }

    [Fact]
    public void RejectsTamperedSignedData()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TelegramMiniAppAuth auth = CreateAuth(now);
        string initData = BuildInitData("secret-token", now, 12345).Replace("samuel", "attacker", StringComparison.Ordinal);
        DefaultHttpContext context = CreateContext(initData);

        bool result = auth.TryAuthenticate(context.Request, out _, out string failureReason);

        Assert.False(result);
        Assert.Contains("signature", failureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsUserOutsideAllowlist()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TelegramMiniAppAuth auth = CreateAuth(now);
        DefaultHttpContext context = CreateContext(BuildInitData("secret-token", now, 99999));

        bool result = auth.TryAuthenticate(context.Request, out _, out string failureReason);

        Assert.False(result);
        Assert.Contains("allowlisted", failureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsExpiredData()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TelegramMiniAppAuth auth = CreateAuth(now);
        DefaultHttpContext context = CreateContext(BuildInitData("secret-token", now.AddMinutes(-16), 12345));

        bool result = auth.TryAuthenticate(context.Request, out _, out string failureReason);

        Assert.False(result);
        Assert.Contains("expired", failureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RechecksCurrentAllowlistForBrowserSessions()
    {
        TelegramBotOptions options = new()
        {
            Token = "secret-token",
            AllowedUserIds = [12345L],
        };
        TelegramMiniAppAuth auth = new(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(new TelegramMiniAppOptions { Enabled = true }),
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        Assert.True(auth.IsAllowlisted(12345));
        options.AllowedUserIds = [];
        Assert.False(auth.IsAllowlisted(12345));
    }

    private static TelegramMiniAppAuth CreateAuth(DateTimeOffset now)
        => new(
            Microsoft.Extensions.Options.Options.Create(new TelegramBotOptions
            {
                Token = "secret-token",
                AllowedUserIds = [12345],
            }),
            Microsoft.Extensions.Options.Options.Create(new TelegramMiniAppOptions
            {
                Enabled = true,
                InitDataMaxAgeSeconds = 900,
            }),
            new FixedTimeProvider(now));

    private static DefaultHttpContext CreateContext(string initData)
    {
        DefaultHttpContext context = new();
        context.Request.Headers["X-Telegram-Init-Data"] = initData;
        return context;
    }

    private static string BuildInitData(string token, DateTimeOffset authenticatedAt, long userId)
    {
        string userJson = $"{{\"id\":{userId},\"first_name\":\"Samuel\",\"username\":\"samuel\"}}";
        string dataCheckString = string.Join(
            '\n',
            new[]
            {
                $"auth_date={authenticatedAt.ToUnixTimeSeconds()}",
                "query_id=query-123",
                $"user={userJson}",
            }.OrderBy(value => value, StringComparer.Ordinal));

        byte[] secretKey;
        using (HMACSHA256 secretHmac = new(Encoding.UTF8.GetBytes("WebAppData")))
        {
            secretKey = secretHmac.ComputeHash(Encoding.UTF8.GetBytes(token));
        }

        byte[] hash;
        using (HMACSHA256 dataHmac = new(secretKey))
        {
            hash = dataHmac.ComputeHash(Encoding.UTF8.GetBytes(dataCheckString));
        }

        return $"auth_date={authenticatedAt.ToUnixTimeSeconds()}&query_id=query-123&user={Uri.EscapeDataString(userJson)}&hash={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
