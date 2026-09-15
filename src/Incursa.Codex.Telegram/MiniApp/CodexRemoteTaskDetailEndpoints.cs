using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Authenticated worker-side task detail projection. Codex detail is read and
/// redacted on the worker before it crosses the coordinator boundary.
/// </summary>
internal static class CodexRemoteTaskDetailEndpoints
{
    private const string DetailPath = "/api/worker/v1/tasks/detail";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Map(WebApplication app)
        => app.MapPost(DetailPath, HandleAsync);

    internal static async Task<IResult> HandleAsync(
        HttpContext context,
        IOptions<CodexTelegramOptions> options,
        ICodexWorkerRegistry workerRegistry,
        ICodexSupervisionLedger supervisionLedger,
        ICodexGateway gateway,
        ICodexTurnExecutionCoordinator turnCoordinator,
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

        if (context.Request.ContentLength is > 64 * 1024)
        {
            return Results.BadRequest(new { error = "Remote task detail request is too large." });
        }

        CodexRemoteTaskDetailRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CodexRemoteTaskDetailRequest>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Remote task detail request is malformed." });
        }

        if (request is null || !IsValid(request))
        {
            return Results.BadRequest(new { error = "Remote task detail metadata is invalid." });
        }

        CodexWorkerSnapshot worker = await workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(worker.WorkerId, request.WorkerId, StringComparison.Ordinal))
        {
            return Results.Conflict(new { error = "Worker identity does not match the request." });
        }

        if (worker.State is not (CodexWorkerState.Online or CodexWorkerState.Draining)
            || !string.Equals(worker.Readiness, "ready", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { error = "Worker is not ready." });
        }

        CodexSupervisionTaskRecord? task = await supervisionLedger.GetTaskAsync(request.OwnerUserId, request.TaskId, cancellationToken).ConfigureAwait(false);
        if (task is null
            || !string.Equals(task.WorkerId, request.WorkerId, StringComparison.Ordinal)
            || !string.Equals(task.LeaseId, request.LeaseId, StringComparison.Ordinal)
            || !string.Equals(task.CodexThreadId, request.CodexThreadId, StringComparison.Ordinal))
        {
            return Results.Conflict(new { error = "Task ownership does not match the worker detail request." });
        }

        CodexThreadDetailVm detail;
        try
        {
            detail = await gateway.GetThreadAsync(request.CodexThreadId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            return Results.NotFound();
        }

        CodexSupervisionTaskSnapshot? snapshot = await supervisionLedger
            .GetTaskForSessionAsync(request.OwnerUserId, request.CodexThreadId, cancellationToken)
            .ConfigureAwait(false);
        TelegramMiniAppSupervisionTaskVm? supervision = snapshot is null
            ? null
            : TelegramMiniAppProjection.ToSupervisionTaskViewModel(snapshot);
        DateTimeOffset retrievedAtUtc = DateTimeOffset.UtcNow;
        TelegramMiniAppThreadDetailVm projected = TelegramMiniAppProjection.ToThreadDetailViewModel(
            detail,
            turnCoordinator.TryGetActiveTurnState(request.CodexThreadId),
            retrievedAtUtc) with
        {
            Supervision = supervision,
            ReviewPacket = TelegramMiniAppProjection.BuildReviewPacket(detail, supervision, retrievedAtUtc),
        };

        return Results.Ok(projected);
    }

    private static bool IsValid(CodexRemoteTaskDetailRequest request)
        => IsSafeToken(request.TaskId, 160)
            && request.OwnerUserId != 0
            && IsSafeToken(request.WorkerId, 120)
            && IsSafeToken(request.LeaseId, 160)
            && IsSafeToken(request.CodexThreadId, 256);

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
