using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Services;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexAccountUsageServiceTests
{
    [Fact]
    public void ToUsageVm_MapsMultiBucketSdkResult()
    {
        DateTimeOffset retrievedAtUtc = DateTimeOffset.Parse("2026-05-06T12:00:00Z");
        CodexAccountRateLimitsResult result = new()
        {
            RateLimits =
            [
                new CodexRateLimitSnapshot
                {
                    LimitId = "codex",
                    LimitName = "Codex",
                    PlanType = "pro",
                    Primary = new CodexRateLimitWindow
                    {
                        UsedPercent = 10,
                        ResetsAt = DateTimeOffset.FromUnixTimeSeconds(1778076000L),
                        WindowDurationMinutes = 300,
                    },
                    Secondary = new CodexRateLimitWindow
                    {
                        UsedPercent = 61,
                        ResetsAt = DateTimeOffset.FromUnixTimeSeconds(1778421600L),
                        WindowDurationMinutes = 10080,
                    },
                },
            ],
        };

        var usage = CodexAccountUsageService.ToUsageVm(result, retrievedAtUtc);

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
    public void ToUsageVm_ClampsWindowUsage()
    {
        CodexAccountRateLimitsResult result = new()
        {
            RateLimits =
            [
                new CodexRateLimitSnapshot
                {
                    LimitId = "codex",
                    RateLimitReachedType = "rate_limit_reached",
                    Primary = new CodexRateLimitWindow
                    {
                        UsedPercent = 125,
                        ResetsAt = DateTimeOffset.FromUnixTimeSeconds(1778076000L),
                        WindowDurationMinutes = 300,
                    },
                },
            ],
        };

        var usage = CodexAccountUsageService.ToUsageVm(result, DateTimeOffset.Parse("2026-05-06T12:00:00Z"));

        var bucket = Assert.Single(usage.RateLimits);
        Assert.Equal("codex", bucket.LimitId);
        Assert.Equal("rate_limit_reached", bucket.RateLimitReachedType);
        Assert.Equal(100, bucket.Primary!.UsedPercent);
        Assert.Equal(300, bucket.Primary.WindowDurationMinutes);
        Assert.Null(bucket.Secondary);
    }
}
