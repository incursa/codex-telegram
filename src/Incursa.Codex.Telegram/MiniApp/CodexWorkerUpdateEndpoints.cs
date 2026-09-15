using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Authenticated post-install acknowledgement endpoint for an external worker installer.
/// </summary>
internal static class CodexWorkerUpdateEndpoints
{
    private const string CompletionPath = "/api/worker/v1/update/complete";
    private const string StatusPath = "/api/worker/v1/update/status";
    private const string ControlPath = "/api/worker/v1/update/control";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Map(WebApplication app)
    {
        app.MapPost(CompletionPath, CompleteAsync);
        app.MapGet(StatusPath, GetStatusAsync);
        app.MapPost(ControlPath, ControlAsync);
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext context,
        IOptions<CodexTelegramOptions> options,
        ICodexWorkerUpdateManager updateManager,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Updates.Enabled || !options.Value.Coordinator.WorkerRegistrationEnabled)
        {
            return Results.NotFound();
        }

        return !CodexCoordinatorWorkerEndpoints.TryAuthorize(context.Request, options.Value.Coordinator.AuthenticationToken)
            ? Results.Unauthorized()
            : Results.Ok(await updateManager.GetStatusAsync(cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<IResult> ControlAsync(
        HttpContext context,
        IOptions<CodexTelegramOptions> options,
        ICodexWorkerUpdateManager updateManager,
        ICodexWorkerRegistry workerRegistry,
        CancellationToken cancellationToken)
    {
        CodexTelegramOptions hostOptions = options.Value;
        if (!hostOptions.Updates.Enabled || !hostOptions.Coordinator.WorkerRegistrationEnabled)
        {
            return Results.NotFound();
        }

        if (!CodexCoordinatorWorkerEndpoints.TryAuthorize(context.Request, hostOptions.Coordinator.AuthenticationToken))
        {
            return Results.Unauthorized();
        }

        if (context.Request.ContentLength is > 32 * 1024)
        {
            return Results.BadRequest(new { error = "Worker update control request is too large." });
        }

        CodexWorkerUpdateControlRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CodexWorkerUpdateControlRequest>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Worker update control request is malformed." });
        }

        IReadOnlyList<string> requiredCapabilities = request?.RequiredCapabilities ?? [];
        if (request is null
            || !IsSafeToken(request.WorkerId, 120)
            || !IsSafeToken(request.RolloutId, 160)
            || request.Action is not ("stage" or "rollback")
            || !request.Confirm
            || requiredCapabilities.Count > 24
            || requiredCapabilities.Any(capability => !IsSafeToken(capability, 120)))
        {
            return Results.BadRequest(new { error = "Worker update control metadata is invalid or unconfirmed." });
        }

        CodexWorkerSnapshot worker = await workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(worker.WorkerId, request.WorkerId, StringComparison.Ordinal))
        {
            CodexWorkerUpdateSnapshot current = await updateManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return Results.Conflict(new CodexWorkerUpdateControlResponse(false, worker.WorkerId, request.RolloutId, current, "worker_identity_mismatch"));
        }

        if (request.Action == "stage")
        {
            string? targetVersion = request.TargetVersion?.Trim();
            string? expectedSha256 = request.ExpectedSha256?.Trim();
            CodexWorkerUpdateOptions configured = hostOptions.Updates;
            if (!IsSafeToken(targetVersion, 40)
                || !IsSha256(expectedSha256)
                || !string.Equals(configured.TargetVersion?.Trim(), targetVersion, StringComparison.Ordinal)
                || !string.Equals(configured.ExpectedSha256?.Trim(), expectedSha256, StringComparison.OrdinalIgnoreCase)
                || configured.RequiredCapabilities.Except(requiredCapabilities, StringComparer.Ordinal).Any()
                || requiredCapabilities.Except(configured.RequiredCapabilities, StringComparer.Ordinal).Any())
            {
                CodexWorkerUpdateSnapshot current = await updateManager.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                return Results.Conflict(new CodexWorkerUpdateControlResponse(false, worker.WorkerId, request.RolloutId, current, "rollout_target_mismatch"));
            }
        }

        CodexWorkerUpdateSnapshot result = request.Action == "stage"
            ? await updateManager.StageAsync(cancellationToken).ConfigureAwait(false)
            : await updateManager.StageRollbackAsync(cancellationToken).ConfigureAwait(false);
        CodexWorkerUpdateControlResponse response = new(
            result.State is CodexWorkerUpdateState.Staged or CodexWorkerUpdateState.RollbackStaged,
            worker.WorkerId,
            request.RolloutId,
            result,
            result.OutcomeCode ?? (request.Action == "stage" ? "stage_rejected" : "rollback_rejected"));
        return response.Accepted ? Results.Ok(response) : Results.Conflict(response);
    }

    internal static async Task<IResult> CompleteAsync(
        HttpContext context,
        IOptions<CodexTelegramOptions> options,
        ICodexWorkerUpdateManager updateManager,
        CancellationToken cancellationToken)
    {
        CodexWorkerUpdateOptions updates = options.Value.Updates;
        if (!updates.Enabled)
        {
            return Results.NotFound();
        }

        if (!CodexCoordinatorWorkerEndpoints.TryAuthorize(context.Request, updates.InstallerAuthenticationToken))
        {
            return Results.Unauthorized();
        }

        if (context.Request.ContentLength is > 16 * 1024)
        {
            return Results.BadRequest(new { error = "Worker update completion is too large." });
        }

        CodexWorkerUpdateCompletion? completion;
        try
        {
            completion = await JsonSerializer.DeserializeAsync<CodexWorkerUpdateCompletion>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Worker update completion is malformed." });
        }

        if (completion is null
            || !IsSafeToken(completion.Version, 40)
            || string.IsNullOrWhiteSpace(completion.Sha256)
            || completion.Sha256.Trim().Length != 64
            || !completion.Sha256.Trim().All(char.IsAsciiHexDigit))
        {
            return Results.BadRequest(new { error = "Worker update completion version or SHA-256 is invalid." });
        }

        CodexWorkerUpdateSnapshot result = await updateManager.CompleteAsync(completion, cancellationToken).ConfigureAwait(false);
        return result.State is CodexWorkerUpdateState.Active or CodexWorkerUpdateState.RollbackActive
            ? Results.Ok(result)
            : Results.Conflict(result);
    }

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or ':');

    private static bool IsSha256(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length == 64
            && value.Trim().All(char.IsAsciiHexDigit);
}
