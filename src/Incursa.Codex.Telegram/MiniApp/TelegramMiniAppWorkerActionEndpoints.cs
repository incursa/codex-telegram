using System.Text.Json;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Authenticated, explicitly confirmed Mini App worker admission controls.
/// Draining prevents new leases but never interrupts existing sessions.
/// </summary>
internal static class TelegramMiniAppWorkerActionEndpoints
{
    private const string ActionPath = "/api/mini-app/workers/{workerId}/actions";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
        => app.MapPost(ActionPath, HandleAsync);

    internal static async Task<IResult> HandleAsync(
        string workerId,
        HttpContext context,
        TelegramMiniAppAuth auth,
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        ICodexWorkerRegistry workerRegistry,
        ICodexCoordinatorWorkerStore coordinatorWorkerStore,
        CodexRemoteWorkerControlRelay remoteWorkerControlRelay,
        ITelegramMiniAppBrowserPairingStore pairingStore,
        CancellationToken cancellationToken)
    {
        if (!miniAppOptions.Value.Enabled)
        {
            return Results.NotFound();
        }

        TelegramMiniAppIdentity? identity = await TelegramMiniAppEndpoints.TryAuthenticateAsync(
            context,
            auth,
            miniAppOptions.Value,
            pairingStore,
            cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return Results.Unauthorized();
        }

        if (!IsSafeToken(workerId, 120))
        {
            return Results.BadRequest(new { message = "A valid worker id is required." });
        }

        TelegramMiniAppWorkerActionRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<TelegramMiniAppWorkerActionRequest>(
                context.Request.Body,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { message = "Worker action is malformed." });
        }

        if (request is null
            || request.Action is not ("drain" or "resume")
            || !request.Confirm)
        {
            return Results.BadRequest(new { message = "Worker drain/resume actions require explicit confirmation." });
        }

        string requestedWorkerId = workerId.Trim();
        CodexWorkerSnapshot localWorker = await workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        CodexWorkerSnapshot? updated;
        if (string.Equals(localWorker.WorkerId, requestedWorkerId, StringComparison.Ordinal))
        {
            bool changed = await workerRegistry.SetDrainingAsync(request.Action == "drain", cancellationToken).ConfigureAwait(false);
            updated = await workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            string outcome = request.Action == "drain" ? "drained" : "resumed";
            return Results.Ok(new TelegramMiniAppWorkerActionResponse(
                request.Action,
                true,
                TelegramMiniAppEndpoints.ToWorkerViewModel(updated),
                changed ? $"worker_{outcome}" : $"worker_already_{outcome}"));
        }

        CodexWorkerSnapshot? remoteWorker = (await coordinatorWorkerStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(worker => worker.IsRemote
                && string.Equals(worker.WorkerId, requestedWorkerId, StringComparison.Ordinal));
        if (remoteWorker is null)
        {
            return Results.NotFound();
        }

        try
        {
            updated = await remoteWorkerControlRelay
                .SetDrainingAsync(remoteWorker, request.Action == "drain", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { message = exception.Message });
        }

        return Results.Ok(new TelegramMiniAppWorkerActionResponse(
            request.Action,
            true,
            TelegramMiniAppEndpoints.ToWorkerViewModel(updated),
            $"worker_{request.Action}d"));
    }

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}

internal sealed record TelegramMiniAppWorkerActionRequest(
    string Action,
    bool Confirm);

internal sealed record TelegramMiniAppWorkerActionResponse(
    string Action,
    bool Accepted,
    TelegramMiniAppWorkerVm Worker,
    string OutcomeCode);
