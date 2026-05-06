using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Reads account-level Codex usage windows from the local Codex app-server.
/// </summary>
internal interface ICodexAccountUsageService
{
    /// <summary>
    /// Gets the latest account usage snapshot reported by Codex.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the app-server query.</param>
    /// <returns>Current Codex rate-limit usage.</returns>
    Task<CodexAccountUsageVm> GetUsageAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads Codex account usage windows through the typed Codex SDK.
/// </summary>
internal sealed class CodexAccountUsageService : ICodexAccountUsageService
{
    private readonly CodexClientOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodexAccountUsageService"/> class.
    /// </summary>
    /// <param name="options">Codex client options used by the SDK client.</param>
    /// <param name="timeProvider">Clock used to stamp returned snapshots.</param>
    public CodexAccountUsageService(
        IOptions<CodexClientOptions> options,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<CodexAccountUsageVm> GetUsageAsync(CancellationToken cancellationToken)
    {
        await using CodexClient client = new(_options);
        CodexAccountRateLimitsResult result = await client.GetAccountRateLimitsAsync(cancellationToken).ConfigureAwait(false);
        return ToUsageVm(result, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Converts the SDK account rate-limit result into Telegram view models.
    /// </summary>
    /// <param name="result">SDK account rate-limit result.</param>
    /// <param name="retrievedAtUtc">UTC time when the result was retrieved.</param>
    /// <returns>Parsed account usage snapshot.</returns>
    internal static CodexAccountUsageVm ToUsageVm(CodexAccountRateLimitsResult result, DateTimeOffset retrievedAtUtc)
        => new(
            retrievedAtUtc.ToUniversalTime(),
            result.RateLimits.Select(ToRateLimitSnapshotVm).ToArray());

    private static CodexRateLimitSnapshotVm ToRateLimitSnapshotVm(CodexRateLimitSnapshot snapshot)
        => new(
            snapshot.LimitId,
            snapshot.LimitName,
            snapshot.PlanType,
            snapshot.RateLimitReachedType,
            ToRateLimitWindowVm(snapshot.Primary),
            ToRateLimitWindowVm(snapshot.Secondary));

    private static CodexRateLimitWindowVm? ToRateLimitWindowVm(CodexRateLimitWindow? window)
        => window is null
            ? null
            : new CodexRateLimitWindowVm(
                Math.Clamp(window.UsedPercent, 0, 100),
                window.ResetsAt?.ToUniversalTime(),
                window.WindowDurationMinutes);
}
