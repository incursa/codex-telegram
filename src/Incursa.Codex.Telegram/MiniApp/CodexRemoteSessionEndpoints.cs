using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Authenticated worker-side prompt relay. The worker remains the only process that calls Codex.
/// </summary>
internal static class CodexRemoteSessionEndpoints
{
    private const string SendPath = "/api/worker/v1/sessions/send";
    private const string EventPath = "/api/coordinator/v1/worker-events";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Map(WebApplication app)
        => app.MapPost(SendPath, SendAsync);

    internal static async Task<IResult> SendAsync(
        HttpContext context,
        IOptions<CodexTelegramOptions> options,
        ICodexWorkerRegistry workerRegistry,
        ICodexSupervisionLedger supervisionLedger,
        ICodexSessionManager sessionManager,
        ICodexRemoteTurnEventForwarder eventForwarder,
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
            return Results.BadRequest(new { error = "Remote session request is too large." });
        }

        CodexRemoteSessionSendRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CodexRemoteSessionSendRequest>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Remote session request is malformed." });
        }

        if (request is null || !IsValid(request))
        {
            return Results.BadRequest(new { error = "Remote session request metadata is invalid." });
        }

        CodexWorkerSnapshot worker = await workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(worker.WorkerId, request.WorkerId, StringComparison.Ordinal))
        {
            return Results.Conflict(new { error = "Worker identity does not match the request." });
        }

        if (worker.State != CodexWorkerState.Online || !string.Equals(worker.Readiness, "ready", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { error = "Worker is not ready." });
        }

        if (!IsExpectedCallbackUrl(hostOptions.Coordinator.Url, request.CallbackUrl))
        {
            return Results.Conflict(new { error = "Callback endpoint is not the configured coordinator." });
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
            request.PlanMode ? "remote_plan" : "remote_prompt",
            cancellationToken).ConfigureAwait(false);
        if (!command.Accepted)
        {
            return command.Run is { TurnId: { Length: > 0 } turnId }
                ? Results.Ok(new CodexThreadExecutionVm(request.CodexThreadId, turnId, "running", "duplicate command already accepted"))
                : Results.Conflict(new { error = "Remote command was already recorded without an execution result." });
        }

        eventForwarder.RegisterPending(request.CodexThreadId, worker.WorkerId, request.CallbackUrl);
        try
        {
            CodexThreadExecutionVm execution = request.PlanMode
                ? await sessionManager.SendPlanAsync(request.CodexThreadId, request.Input, cancellationToken).ConfigureAwait(false)
                : await sessionManager.SendAsync(request.CodexThreadId, request.Input, cancellationToken).ConfigureAwait(false);
            await supervisionLedger.UpdateRunAsync(
                command.Run?.RunId ?? string.Empty,
                CodexSupervisionRunState.Running,
                execution.ThreadId,
                execution.TurnId,
                null,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(execution);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            if (command.Run is not null)
            {
                await supervisionLedger.UpdateRunAsync(
                    command.Run.RunId,
                    CodexSupervisionRunState.Unknown,
                    request.CodexThreadId,
                    null,
                    "remote_execution_outcome_unknown",
                    CancellationToken.None).ConfigureAwait(false);
            }

            return Results.Conflict(new { error = "Remote session execution could not be started; outcome is recorded as unknown." });
        }
    }

    private static bool IsValid(CodexRemoteSessionSendRequest request)
        => IsSafeToken(request.TaskId, 160)
            && request.OwnerUserId != 0
            && IsSafeToken(request.WorkerId, 120)
            && IsSafeToken(request.CodexThreadId, 256)
            && IsSafeToken(request.CommandId, 256)
            && request.ChatId != 0
            && (request.MessageThreadId is null or > 0)
            && !string.IsNullOrWhiteSpace(request.Input)
            && request.Input.Length <= 16_000
            && !request.Input.Contains('\0')
            && Uri.TryCreate(request.CallbackUrl, UriKind.Absolute, out Uri? callback)
            && callback.Scheme is "http" or "https";

    private static bool IsExpectedCallbackUrl(string? configuredCoordinatorUrl, string requestedCallbackUrl)
    {
        if (!Uri.TryCreate(configuredCoordinatorUrl, UriKind.Absolute, out Uri? coordinator)
            || coordinator.Scheme is not ("http" or "https")
            || !Uri.TryCreate(requestedCallbackUrl, UriKind.Absolute, out Uri? requested))
        {
            return false;
        }

        Uri expected = new(new Uri(coordinator.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute), EventPath.TrimStart('/'));
        return Uri.Compare(expected, requested, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
