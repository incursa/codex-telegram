using System.Text.Json;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Authenticated worker-side drain/resume endpoint for the coordinator.
/// </summary>
internal static class CodexRemoteWorkerControlEndpoints
{
    private const string ControlPath = "/api/worker/v1/worker/control";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
        => app.MapPost(ControlPath, HandleAsync);

    internal static async Task<IResult> HandleAsync(
        HttpContext context,
        IOptions<CodexTelegramOptions> options,
        ICodexWorkerRegistry workerRegistry,
        CancellationToken cancellationToken)
    {
        CodexTelegramOptions hostOptions = options.Value;
        if (!hostOptions.Coordinator.Enabled || !hostOptions.Coordinator.WorkerRegistrationEnabled)
        {
            return Results.NotFound();
        }

        if (!CodexCoordinatorWorkerEndpoints.TryAuthorize(context.Request, hostOptions.Coordinator.AuthenticationToken))
        {
            return Results.Unauthorized();
        }

        if (context.Request.ContentLength is > 16 * 1024)
        {
            return Results.BadRequest(new { error = "Worker control request is too large." });
        }

        CodexRemoteWorkerControlRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CodexRemoteWorkerControlRequest>(
                context.Request.Body,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Worker control request is malformed." });
        }

        if (request is null
            || !IsSafeToken(request.WorkerId, 120)
            || request.Action is not ("drain" or "resume")
            || !request.Confirm)
        {
            return Results.BadRequest(new { error = "Worker control metadata is invalid or unconfirmed." });
        }

        CodexWorkerSnapshot current = await workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current.WorkerId, request.WorkerId, StringComparison.Ordinal))
        {
            return Results.Conflict(new CodexRemoteWorkerControlResponse(false, current, "worker_identity_mismatch"));
        }

        bool draining = request.Action == "drain";
        bool changed = await workerRegistry.SetDrainingAsync(draining, cancellationToken).ConfigureAwait(false);
        CodexWorkerSnapshot updated = await workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        string outcome = draining ? "drained" : "resumed";
        return Results.Ok(new CodexRemoteWorkerControlResponse(
            true,
            updated,
            changed ? $"worker_{outcome}" : $"worker_already_{outcome}"));
    }

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
