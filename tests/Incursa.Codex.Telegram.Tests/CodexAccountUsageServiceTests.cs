using System.Text.Json.Nodes;
using Incursa.Codex.Telegram.Services;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexAccountUsageServiceTests
{
    [Fact]
    public void ParseRateLimitResponse_ParsesMultiBucketCamelCasePayload()
    {
        DateTimeOffset retrievedAtUtc = DateTimeOffset.Parse("2026-05-06T12:00:00Z");
        JsonObject payload = new()
        {
            ["rateLimitsByLimitId"] = new JsonObject
            {
                ["codex"] = new JsonObject
                {
                    ["limitName"] = "Codex",
                    ["planType"] = "pro",
                    ["primary"] = new JsonObject
                    {
                        ["usedPercent"] = 10,
                        ["resetsAt"] = 1778076000L,
                        ["windowDurationMins"] = 300,
                    },
                    ["secondary"] = new JsonObject
                    {
                        ["usedPercent"] = 61,
                        ["resetsAt"] = 1778421600L,
                        ["windowDurationMins"] = 10080,
                    },
                },
            },
        };

        var usage = CodexAccountUsageService.ParseRateLimitResponse(payload, retrievedAtUtc);

        var bucket = Assert.Single(usage.RateLimits);
        Assert.Equal("codex", bucket.LimitId);
        Assert.Equal("Codex", bucket.LimitName);
        Assert.Equal("pro", bucket.PlanType);
        Assert.Equal(10, bucket.Primary!.UsedPercent);
        Assert.Equal(300, bucket.Primary.WindowDurationMinutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1778076000L), bucket.Primary.ResetsAtUtc);
        Assert.Equal(61, bucket.Secondary!.UsedPercent);
        Assert.Equal(10080, bucket.Secondary.WindowDurationMinutes);
    }

    [Fact]
    public void ParseRateLimitResponse_ParsesLegacySnakeCasePayload()
    {
        JsonObject payload = new()
        {
            ["rate_limits"] = new JsonObject
            {
                ["limit_id"] = "codex",
                ["rate_limit_reached_type"] = "rate_limit_reached",
                ["primary"] = new JsonObject
                {
                    ["used_percent"] = 125,
                    ["resets_at"] = 1778076000L,
                    ["window_minutes"] = 300,
                },
            },
        };

        var usage = CodexAccountUsageService.ParseRateLimitResponse(payload, DateTimeOffset.Parse("2026-05-06T12:00:00Z"));

        var bucket = Assert.Single(usage.RateLimits);
        Assert.Equal("codex", bucket.LimitId);
        Assert.Equal("rate_limit_reached", bucket.RateLimitReachedType);
        Assert.Equal(100, bucket.Primary!.UsedPercent);
        Assert.Equal(300, bucket.Primary.WindowDurationMinutes);
        Assert.Null(bucket.Secondary);
    }
}
