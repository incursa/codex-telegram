namespace Incursa.Codex.Telegram.Telegram;

internal static class TelegramLaunchpadPolicy
{
    internal static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(10);

    internal static bool IsExpired(DateTimeOffset lastTouchedUtc, DateTimeOffset now)
        => GetRemaining(lastTouchedUtc, now) <= TimeSpan.Zero;

    internal static DateTimeOffset GetExpiresAtUtc(DateTimeOffset lastTouchedUtc)
        => lastTouchedUtc.ToUniversalTime().Add(InactivityTimeout);

    internal static TimeSpan GetRemaining(DateTimeOffset lastTouchedUtc, DateTimeOffset now)
        => GetExpiresAtUtc(lastTouchedUtc) - now.ToUniversalTime();

    internal static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return "0s";
        }

        if (remaining < TimeSpan.FromMinutes(1))
        {
            return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))}s";
        }

        if (remaining < TimeSpan.FromHours(1))
        {
            return $"{(int)Math.Ceiling(remaining.TotalMinutes)}m";
        }

        if (remaining < TimeSpan.FromDays(1))
        {
            return $"{(int)Math.Ceiling(remaining.TotalHours)}h";
        }

        return $"{(int)Math.Ceiling(remaining.TotalDays)}d";
    }
}
