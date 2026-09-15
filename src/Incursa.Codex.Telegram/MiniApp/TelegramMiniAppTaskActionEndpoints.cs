using System.Text.Json;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Authenticated, bounded Mini App actions. These actions acknowledge evidence or
/// prepare a Telegram handoff; they do not execute Codex commands.
/// </summary>
internal static class TelegramMiniAppTaskActionEndpoints
{
    private const string ActionPath = "/api/mini-app/tasks/{taskId}/actions";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
        => app.MapPost(ActionPath, HandleAsync);

    internal static async Task<IResult> HandleAsync(
        string taskId,
        HttpContext context,
        TelegramMiniAppAuth auth,
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        ICodexSupervisionLedger supervisionLedger,
        ITelegramMiniAppBrowserPairingStore pairingStore,
        ITelegramMiniAppAcknowledgementStore acknowledgementStore,
        ICodexGateway gateway,
        ICodexTurnExecutionCoordinator turnCoordinator,
        ICodexWorkerRegistry workerRegistry,
        CodexRemoteTaskDetailRelay remoteTaskDetailRelay,
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

        if (!IsSafeToken(taskId, 160))
        {
            return Results.BadRequest(new { message = "A valid task id is required." });
        }

        if (context.Request.ContentLength is > 32 * 1024)
        {
            return Results.BadRequest(new { message = "Mini App task action is too large." });
        }

        TelegramMiniAppTaskActionRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<TelegramMiniAppTaskActionRequest>(
                context.Request.Body,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { message = "Mini App task action is malformed." });
        }

        if (request is null || request.Action is not ("acknowledge" or "handoff"))
        {
            return Results.BadRequest(new { message = "The requested Mini App task action is not supported." });
        }

        CodexSupervisionTaskSnapshot? task = (await supervisionLedger.ListTasksAsync(identity.UserId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(candidate.TaskId, taskId.Trim(), StringComparison.Ordinal));
        if (task is null)
        {
            return Results.NotFound();
        }

        CodexSupervisionRunSnapshot? latestRun = task.LatestRun;
        if (request.Action == "acknowledge")
        {
            if (!IsSafeToken(request.RunId, 160) || !IsSafeToken(request.PacketId, 200))
            {
                return Results.BadRequest(new { message = "A review acknowledgement requires a task run and packet id." });
            }

            if (latestRun is null || !string.Equals(latestRun.RunId, request.RunId, StringComparison.Ordinal))
            {
                return Results.Conflict(new { message = "The review packet is stale; refresh the task before acknowledging it." });
            }

            TelegramMiniAppAcknowledgementSnapshot? acknowledgement = await acknowledgementStore.AcknowledgeAsync(
                identity.UserId,
                task.TaskId,
                request.RunId!,
                request.PacketId!,
                cancellationToken).ConfigureAwait(false);
            if (acknowledgement is null)
            {
                return Results.BadRequest(new { message = "The review acknowledgement metadata is invalid." });
            }

            return Results.Ok(new TelegramMiniAppTaskActionResponse(
                request.Action,
                true,
                task.TaskId,
                task.CodexThreadId,
                acknowledgement.RunId,
                acknowledgement.PacketId,
                null,
                "acknowledged",
                acknowledgement.AcknowledgedAtUtc));
        }

        if (request.RunId is not null
            && (!IsSafeToken(request.RunId, 160)
                || latestRun is null
                || !string.Equals(latestRun.RunId, request.RunId, StringComparison.Ordinal)))
        {
            return Results.Conflict(new { message = "The task changed; refresh before preparing a handoff." });
        }

        if (!IsSafeToken(task.CodexThreadId, 256)
            || (request.PacketId is not null && !IsSafeToken(request.PacketId, 200)))
        {
            return Results.BadRequest(new { message = "The handoff metadata is invalid." });
        }

        string telegramCommand = $"/handoff {task.CodexThreadId}";
        TelegramMiniAppReviewPacketVm? packet = null;
        if (gateway is not null && turnCoordinator is not null && workerRegistry is not null && remoteTaskDetailRelay is not null)
        {
            try
            {
                CodexSupervisionTaskRecord? taskRecord = await supervisionLedger
                    .GetTaskAsync(identity.UserId, task.TaskId, cancellationToken)
                    .ConfigureAwait(false);
                if (taskRecord is not null)
                {
                    bool assignedToRemoteWorker = false;
                    if (task.WorkerId is { Length: > 0 })
                    {
                        CodexWorkerSnapshot localWorker = await workerRegistry
                            .GetSnapshotAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.Equals(task.WorkerId, localWorker.WorkerId, StringComparison.Ordinal))
                        {
                            assignedToRemoteWorker = true;
                            packet = (await remoteTaskDetailRelay
                                .GetAsync(taskRecord, cancellationToken)
                                .ConfigureAwait(false)).ReviewPacket;
                        }
                    }

                    if (!assignedToRemoteWorker && packet is null)
                    {
                        CodexThreadDetailVm detail = await gateway
                            .GetThreadAsync(task.CodexThreadId, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        packet = TelegramMiniAppProjection.BuildReviewPacket(
                            detail,
                            TelegramMiniAppProjection.ToSupervisionTaskViewModel(task),
                            DateTimeOffset.UtcNow);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The Telegram command remains useful when the runtime is temporarily unavailable.
            }
        }

        TelegramMiniAppHandoffVm handoff = TelegramMiniAppHandoff.Create(task, packet);
        return Results.Ok(new TelegramMiniAppTaskActionResponse(
            request.Action,
            true,
            task.TaskId,
            task.CodexThreadId,
            latestRun?.RunId,
            request.PacketId,
            telegramCommand,
            "telegram_handoff_ready",
            null,
            handoff));
    }

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}

internal sealed record TelegramMiniAppTaskActionRequest(
    string Action,
    string? RunId = null,
    string? PacketId = null);

internal sealed record TelegramMiniAppTaskActionResponse(
    string Action,
    bool Accepted,
    string TaskId,
    string CodexThreadId,
    string? RunId,
    string? PacketId,
    string? TelegramCommand,
    string OutcomeCode,
    DateTimeOffset? AcknowledgedAtUtc,
    TelegramMiniAppHandoffVm? Handoff = null);
