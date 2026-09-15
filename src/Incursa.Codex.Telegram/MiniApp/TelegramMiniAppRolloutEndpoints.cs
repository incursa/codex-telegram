using System.Text.Json;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Authenticated fleet rollout projection and explicitly confirmed actions.
/// A rollout advances one worker at a time and never activates a package.
/// </summary>
internal static class TelegramMiniAppRolloutEndpoints
{
    private const string ListPath = "/api/mini-app/rollouts";
    private const string ActionPath = "/api/mini-app/rollouts/actions";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        app.MapGet(ListPath, ListAsync);
        app.MapPost(ActionPath, HandleAsync);
    }

    internal static async Task<IResult> ListAsync(
        HttpContext context,
        TelegramMiniAppAuth auth,
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        ITelegramMiniAppBrowserPairingStore pairingStore,
        ICodexFleetRolloutStore rolloutStore,
        CancellationToken cancellationToken)
    {
        TelegramMiniAppIdentity? identity = await AuthenticateAsync(
            context,
            auth,
            miniAppOptions.Value,
            pairingStore,
            cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return Results.Unauthorized();
        }

        IReadOnlyList<CodexFleetRolloutSnapshot> rollouts = await rolloutStore.ListAsync(identity.UserId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(rollouts.Select(ToViewModel).ToArray());
    }

    internal static async Task<IResult> HandleAsync(
        HttpContext context,
        TelegramMiniAppAuth auth,
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        ICodexFleetRolloutCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        if (!miniAppOptions.Value.Enabled
            || !auth.TryAuthenticate(context.Request, out TelegramMiniAppIdentity identity, out _))
        {
            return Results.Unauthorized();
        }

        if (context.Request.ContentLength is > 32 * 1024)
        {
            return Results.BadRequest(new { message = "Fleet rollout action is too large." });
        }

        TelegramMiniAppRolloutActionRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<TelegramMiniAppRolloutActionRequest>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { message = "Fleet rollout action is malformed." });
        }

        if (request is null || !request.Confirm || request.Action is not ("start" or "advance" or "rollback" or "finalize"))
        {
            return Results.BadRequest(new { message = "Fleet rollout actions require explicit confirmation." });
        }

        CodexFleetRolloutActionResult result;
        if (request.Action == "start")
        {
            if (!IsSafeVersion(request.TargetVersion)
                || !IsSha256(request.ExpectedSha256)
                || request.WorkerIds is null
                || request.WorkerIds.Count is < 1 or > 32
                || request.WorkerIds.Any(workerId => !IsSafeToken(workerId, 120))
                || request.WorkerIds.Distinct(StringComparer.Ordinal).Count() != request.WorkerIds.Count
                || request.RequiredCapabilities is null
                || request.RequiredCapabilities.Count > 24
                || request.RequiredCapabilities.Any(capability => !IsSafeToken(capability, 120)))
            {
                return Results.BadRequest(new { message = "A rollout requires a version, SHA-256, capabilities, and distinct worker ids." });
            }

            result = await coordinator.StartAsync(
                identity.UserId,
                request.TargetVersion!.Trim(),
                request.ExpectedSha256!.Trim(),
                request.RequiredCapabilities,
                request.WorkerIds,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!IsSafeToken(request.RolloutId, 160))
            {
                return Results.BadRequest(new { message = "A valid rollout id is required." });
            }

            result = request.Action switch
            {
                "advance" => await coordinator.AdvanceAsync(identity.UserId, request.RolloutId!, cancellationToken).ConfigureAwait(false),
                "rollback" => await coordinator.RollbackAsync(identity.UserId, request.RolloutId!, cancellationToken).ConfigureAwait(false),
                _ => await coordinator.FinalizeAsync(identity.UserId, request.RolloutId!, cancellationToken).ConfigureAwait(false),
            };
        }

        TelegramMiniAppRolloutActionResponse response = new(
            result.Accepted,
            result.OutcomeCode,
            result.Rollout is null ? null : ToViewModel(result.Rollout));
        return result.Accepted ? Results.Ok(response) : Results.Conflict(response);
    }

    private static async Task<TelegramMiniAppIdentity?> AuthenticateAsync(
        HttpContext context,
        TelegramMiniAppAuth auth,
        TelegramMiniAppOptions options,
        ITelegramMiniAppBrowserPairingStore pairingStore,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return null;
        }

        return await TelegramMiniAppEndpoints.TryAuthenticateAsync(
            context,
            auth,
            options,
            pairingStore,
            cancellationToken).ConfigureAwait(false);
    }

    internal static TelegramMiniAppRolloutVm ToViewModel(CodexFleetRolloutSnapshot rollout)
        => new(
            rollout.RolloutId,
            rollout.TargetVersion,
            rollout.State.ToString().ToLowerInvariant(),
            rollout.UpdatedAtUtc,
            rollout.OutcomeCode,
            rollout.Workers
                .OrderBy(worker => worker.Sequence)
                .Select(worker => new TelegramMiniAppRolloutWorkerVm(
                    worker.WorkerId,
                    worker.Sequence,
                    worker.State.ToString().ToLowerInvariant(),
                    worker.UpdatedAtUtc,
                    worker.OutcomeCode))
                .ToArray());

    private static bool IsSafeVersion(string? value)
        => IsSafeToken(value, 40) && Version.TryParse(value, out _);

    private static bool IsSha256(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length == 64 && value.Trim().All(char.IsAsciiHexDigit);

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or ':');
}

internal sealed record TelegramMiniAppRolloutActionRequest(
    string Action,
    bool Confirm,
    string? RolloutId = null,
    string? TargetVersion = null,
    string? ExpectedSha256 = null,
    IReadOnlyList<string>? RequiredCapabilities = null,
    IReadOnlyList<string>? WorkerIds = null);

internal sealed record TelegramMiniAppRolloutActionResponse(
    bool Accepted,
    string OutcomeCode,
    TelegramMiniAppRolloutVm? Rollout);

internal sealed record TelegramMiniAppRolloutVm(
    string RolloutId,
    string TargetVersion,
    string State,
    DateTimeOffset UpdatedAtUtc,
    string? OutcomeCode,
    IReadOnlyList<TelegramMiniAppRolloutWorkerVm> Workers);

internal sealed record TelegramMiniAppRolloutWorkerVm(
    string WorkerId,
    int Sequence,
    string State,
    DateTimeOffset UpdatedAtUtc,
    string? OutcomeCode);
