using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Authenticated worker-side steering and interruption relay.
/// </summary>
internal static class CodexRemoteSessionControlEndpoints
{
    private const string ControlPath = "/api/worker/v1/sessions/control";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Map(WebApplication app)
        => app.MapPost(ControlPath, SendAsync);

    internal static async Task<IResult> SendAsync(
        HttpContext context,
        IOptions<CodexTelegramOptions> options,
        ICodexWorkerRegistry workerRegistry,
        ICodexSupervisionLedger supervisionLedger,
        ICodexSessionManager sessionManager,
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
            return Results.BadRequest(new { error = "Remote session control request is too large." });
        }

        CodexRemoteSessionControlRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CodexRemoteSessionControlRequest>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Remote session control request is malformed." });
        }

        if (request is null || !IsValid(request))
        {
            return Results.BadRequest(new { error = "Remote session control metadata is invalid." });
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
            || !string.Equals(task.CodexThreadId, request.CodexThreadId, StringComparison.Ordinal)
            || !string.Equals(task.WorkerId, worker.WorkerId, StringComparison.Ordinal))
        {
            return Results.Conflict(new { error = "Task ownership does not match the worker session." });
        }

        CodexSupervisionCommandStart command = await supervisionLedger.StartCommandAsync(
            request.CommandId,
            request.CodexThreadId,
            task.SessionName,
            new TelegramConversationScope(request.ChatId, request.MessageThreadId),
            request.OwnerUserId,
            "remote_" + request.Action,
            cancellationToken).ConfigureAwait(false);
        if (!command.Accepted)
        {
            return Results.Ok(new CodexRemoteSessionControlResponse(true, "duplicate command already accepted"));
        }

        try
        {
            switch (request.Action)
            {
                case "steer":
                    await sessionManager.SteerAsync(request.CodexThreadId, request.Input!, cancellationToken).ConfigureAwait(false);
                    await supervisionLedger.UpdateRunAsync(command.Run!.RunId, CodexSupervisionRunState.Completed, request.CodexThreadId, null, "steer_accepted", cancellationToken).ConfigureAwait(false);
                    return Results.Ok(new CodexRemoteSessionControlResponse(true, "steering accepted"));
                case "stop":
                    await sessionManager.StopAsync(request.CodexThreadId, cancellationToken).ConfigureAwait(false);
                    await supervisionLedger.UpdateRunAsync(command.Run!.RunId, CodexSupervisionRunState.Interrupted, request.CodexThreadId, null, "stop_requested", cancellationToken).ConfigureAwait(false);
                    return Results.Ok(new CodexRemoteSessionControlResponse(true, "stop requested"));
                case "kill":
                    await sessionManager.KillAsync(request.CodexThreadId, cancellationToken).ConfigureAwait(false);
                    await supervisionLedger.UpdateRunAsync(command.Run!.RunId, CodexSupervisionRunState.Interrupted, request.CodexThreadId, null, "kill_requested", cancellationToken).ConfigureAwait(false);
                    return Results.Ok(new CodexRemoteSessionControlResponse(true, "kill requested"));
                default:
                    return Results.BadRequest(new { error = "Remote session control action is unsupported." });
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await supervisionLedger.UpdateRunAsync(
                command.Run!.RunId,
                CodexSupervisionRunState.Unknown,
                request.CodexThreadId,
                null,
                "remote_control_outcome_unknown",
                CancellationToken.None).ConfigureAwait(false);
            return Results.Conflict(new { error = "Remote session control could not be completed; outcome is recorded as unknown." });
        }
    }

    private static bool IsValid(CodexRemoteSessionControlRequest request)
        => IsSafeToken(request.TaskId, 160)
            && request.OwnerUserId != 0
            && IsSafeToken(request.WorkerId, 120)
            && IsSafeToken(request.CodexThreadId, 256)
            && IsSafeToken(request.CommandId, 256)
            && request.ChatId != 0
            && (request.MessageThreadId is null or > 0)
            && (request.Action is "steer" or "stop" or "kill")
            && (request.Action != "steer"
                || (!string.IsNullOrWhiteSpace(request.Input) && request.Input.Length <= 16_000 && !request.Input.Contains('\0')))
            && (request.Action == "steer" || request.Input is null);

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
