using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Authenticated worker-side model and goal controls for an owner-bound task.
/// </summary>
internal static class CodexRemoteSessionSettingsEndpoints
{
    private const string SettingsPath = "/api/worker/v1/sessions/settings";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Map(WebApplication app)
        => app.MapPost(SettingsPath, HandleAsync);

    internal static async Task<IResult> HandleAsync(
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
            return Results.BadRequest(new { error = "Remote settings request is too large." });
        }

        CodexRemoteSessionSettingsRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CodexRemoteSessionSettingsRequest>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Remote settings request is malformed." });
        }

        if (request is null || !IsValid(request))
        {
            return Results.BadRequest(new { error = "Remote settings metadata is invalid." });
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
            return Results.Conflict(new { error = "Task ownership does not match the worker settings request." });
        }

        try
        {
            return request.Action switch
            {
                "get_model" => Results.Ok(new CodexRemoteSessionSettingsResponse(true, request.Action, await sessionManager.GetModelSettingsAsync(task.CodexThreadId, cancellationToken).ConfigureAwait(false), null, false, "model_read")),
                "update_model" => Results.Ok(new CodexRemoteSessionSettingsResponse(true, request.Action, await sessionManager.UpdateModelSettingsAsync(task.CodexThreadId, request.Model, request.ReasoningEffort, cancellationToken).ConfigureAwait(false), null, false, "model_updated")),
                "get_goal" => Results.Ok(new CodexRemoteSessionSettingsResponse(true, request.Action, null, await sessionManager.GetGoalAsync(task.CodexThreadId, cancellationToken).ConfigureAwait(false), false, "goal_read")),
                "set_goal" => Results.Ok(new CodexRemoteSessionSettingsResponse(true, request.Action, null, await sessionManager.SetGoalAsync(task.CodexThreadId, request.Objective!, request.TokenBudget, cancellationToken).ConfigureAwait(false), false, "goal_updated")),
                "set_goal_status" => Results.Ok(new CodexRemoteSessionSettingsResponse(true, request.Action, null, await sessionManager.SetGoalStatusAsync(task.CodexThreadId, request.GoalStatus!.Value, cancellationToken).ConfigureAwait(false), false, "goal_status_updated")),
                "clear_goal" => Results.Ok(new CodexRemoteSessionSettingsResponse(true, request.Action, null, null, await sessionManager.ClearGoalAsync(task.CodexThreadId, cancellationToken).ConfigureAwait(false), "goal_cleared")),
                _ => Results.BadRequest(new { error = "Unsupported settings operation." }),
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or CodexCapabilityNotSupportedException or CodexMethodNotFoundException)
        {
            return Results.Conflict(new { error = "The worker could not apply the settings operation." });
        }
    }

    private static bool IsValid(CodexRemoteSessionSettingsRequest request)
        => IsSafeToken(request.TaskId, 160)
            && request.OwnerUserId != 0
            && IsSafeToken(request.WorkerId, 120)
            && IsSafeToken(request.LeaseId, 160)
            && IsSafeToken(request.CodexThreadId, 256)
            && request.Action is "get_model" or "update_model" or "get_goal" or "set_goal" or "set_goal_status" or "clear_goal"
            && (request.Action != "update_model" || IsOptionalText(request.Model, 120) && IsOptionalText(request.ReasoningEffort, 40))
            && (request.Action != "set_goal" || IsBoundedText(request.Objective, 4_000) && (!request.TokenBudget.HasValue || request.TokenBudget.Value > 0))
            && (request.Action != "set_goal_status" || request.GoalStatus.HasValue);

    private static bool IsOptionalText(string? value, int maxLength)
        => value is null || IsBoundedText(value, maxLength);

    private static bool IsBoundedText(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength && !value.Any(char.IsControl);

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
