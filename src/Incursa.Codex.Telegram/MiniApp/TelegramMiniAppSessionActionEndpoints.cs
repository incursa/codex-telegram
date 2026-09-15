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
/// Authenticated control-panel actions for the session selected by a Telegram
/// conversation. Chat messages remain the only prompt and approval workflow.
/// </summary>
internal static class TelegramMiniAppSessionActionEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/mini-app/session/actions", HandleSessionActionAsync);
        app.MapPost("/api/mini-app/instructions", SaveInstructionsAsync);
    }

    private static async Task<IResult> HandleSessionActionAsync(
        HttpContext context,
        TelegramMiniAppAuth auth,
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        ITelegramBotStateStore stateStore,
        ICodexSessionManager sessionManager,
        ICodexGateway gateway,
        ICodexSupervisionLedger supervisionLedger,
        ICodexWorkerRegistry workerRegistry,
        CodexRemoteSessionSettingsRelay remoteSettingsRelay,
        ITelegramMiniAppBrowserPairingStore pairingStore,
        CancellationToken cancellationToken)
    {
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

        if (context.Request.ContentLength is > 64 * 1024)
        {
            return Results.BadRequest(new { error = "Session action request is too large." });
        }

        TelegramMiniAppSessionActionRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<TelegramMiniAppSessionActionRequest>(
                context.Request.Body,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Session action request is malformed." });
        }

        if (request is null || !IsValid(request))
        {
            return Results.BadRequest(new { error = "Session action metadata is invalid." });
        }

        TelegramConversationScope scope = new(identity.ChatId ?? identity.UserId, null);
        string? activeSessionId = await stateStore.GetActiveSessionIdAsync(scope, cancellationToken).ConfigureAwait(false);
        if (request.Action == "select")
        {
            if (!await IsKnownSessionAsync(request.SessionId, identity.UserId, sessionManager, gateway, supervisionLedger, cancellationToken).ConfigureAwait(false))
            {
                return Results.NotFound(new { error = "That session is not available to this Telegram account." });
            }

            await stateStore.SetActiveSessionIdAsync(scope, request.SessionId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new TelegramMiniAppSessionActionResponse(true, request.Action, request.SessionId, "session_selected_for_this_conversation"));
        }

        if (!string.Equals(activeSessionId, request.SessionId, StringComparison.Ordinal))
        {
            return Results.Conflict(new { error = "The conversation selected another session. Refresh the Mini App before changing it." });
        }

        try
        {
            CodexSupervisionTaskSnapshot? task = await supervisionLedger
                .GetTaskForSessionAsync(identity.UserId, request.SessionId, cancellationToken)
                .ConfigureAwait(false);
            bool remote = task?.WorkerId is { Length: > 0 } assignedWorkerId
                && !string.Equals(
                    assignedWorkerId,
                    (await workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).WorkerId,
                    StringComparison.Ordinal);

            switch (request.Action)
            {
                case "rename":
                    if (remote)
                    {
                        return Results.Conflict(new { error = "Renaming a remote session is not available from this worker." });
                    }

                    await sessionManager.RenameAsync(request.SessionId, request.Name!, cancellationToken).ConfigureAwait(false);
                    return Results.Ok(new TelegramMiniAppSessionActionResponse(true, request.Action, request.SessionId, "session_name_accepted_by_codex", request.Name));
                case "update_model":
                    CodexSessionModelSettings modelSettings = remote
                        ? await remoteSettingsRelay.UpdateModelSettingsAsync(task!, identity.UserId, request.Model, request.ReasoningEffort, cancellationToken).ConfigureAwait(false)
                        : await sessionManager.UpdateModelSettingsAsync(request.SessionId, request.Model, request.ReasoningEffort, cancellationToken).ConfigureAwait(false);
                    return Results.Ok(new TelegramMiniAppSessionActionResponse(true, request.Action, request.SessionId, "next_turn_settings_saved", ModelSettings: modelSettings));
                case "set_goal":
                    CodexThreadGoalVm goal = remote
                        ? await remoteSettingsRelay.SetGoalAsync(task!, identity.UserId, request.Objective!, request.TokenBudget, cancellationToken).ConfigureAwait(false)
                        : await sessionManager.SetGoalAsync(request.SessionId, request.Objective!, request.TokenBudget, cancellationToken).ConfigureAwait(false);
                    return Results.Ok(new TelegramMiniAppSessionActionResponse(true, request.Action, request.SessionId, "goal_accepted_by_codex", Goal: goal));
                case "set_goal_status":
                    CodexThreadGoalVm goalStatus = remote
                        ? await remoteSettingsRelay.SetGoalStatusAsync(task!, identity.UserId, request.GoalStatus!.Value, cancellationToken).ConfigureAwait(false)
                        : await sessionManager.SetGoalStatusAsync(request.SessionId, request.GoalStatus!.Value, cancellationToken).ConfigureAwait(false);
                    return Results.Ok(new TelegramMiniAppSessionActionResponse(true, request.Action, request.SessionId, "goal_status_accepted_by_codex", Goal: goalStatus));
                case "clear_goal":
                    bool cleared = remote
                        ? await remoteSettingsRelay.ClearGoalAsync(task!, identity.UserId, cancellationToken).ConfigureAwait(false)
                        : await sessionManager.ClearGoalAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
                    return Results.Ok(new TelegramMiniAppSessionActionResponse(true, request.Action, request.SessionId, "goal_cleared_by_codex", Cleared: cleared));
                default:
                    return Results.BadRequest(new { error = "Unsupported session action." });
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or CodexCapabilityNotSupportedException or CodexMethodNotFoundException)
        {
            return Results.Conflict(new { error = "Codex could not accept that session change." });
        }
    }

    private static async Task<IResult> SaveInstructionsAsync(
        HttpContext context,
        TelegramMiniAppAuth auth,
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        ITelegramMiniAppBrowserPairingStore pairingStore,
        ICodexGlobalInstructionsStore instructionsStore,
        CancellationToken cancellationToken)
    {
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

        TelegramMiniAppGlobalInstructionsRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<TelegramMiniAppGlobalInstructionsRequest>(
                context.Request.Body,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Global instructions request is malformed." });
        }

        if (request is null || request.Text is null || request.Text.Length > 8_000 || request.Text.Any(char.IsControl))
        {
            return Results.BadRequest(new { error = "Global instructions must be at most 8,000 characters and contain no control characters." });
        }

        CodexGlobalInstructionsSnapshot saved = await instructionsStore.SaveAsync(request.Text, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new TelegramMiniAppGlobalInstructionsVm(
            saved.Text,
            saved.UpdatedAtUtc,
            saved.HasSavedValue,
            "saved_for_next_session_or_turn"));
    }

    private static async Task<bool> IsKnownSessionAsync(
        string sessionId,
        long userId,
        ICodexSessionManager sessionManager,
        ICodexGateway gateway,
        ICodexSupervisionLedger supervisionLedger,
        CancellationToken cancellationToken)
    {
        if (await sessionManager.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return true;
        }

        if (await supervisionLedger.GetTaskForSessionAsync(userId, sessionId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return true;
        }

        return (await gateway.ListThreadsAsync(
                new CodexThreadListQuery(null, "all", "updatedAt", true, null, 200),
                cancellationToken).ConfigureAwait(false))
            .Any(thread => string.Equals(thread.Id, sessionId, StringComparison.Ordinal));
    }

    private static bool IsValid(TelegramMiniAppSessionActionRequest request)
        => IsSafeToken(request.SessionId, 256)
            && request.Action is "select" or "rename" or "update_model" or "set_goal" or "set_goal_status" or "clear_goal"
            && (request.Action != "rename" || IsBoundedText(request.Name, 160))
            && (request.Action != "update_model" || IsOptionalText(request.Model, 120) && IsOptionalText(request.ReasoningEffort, 40))
            && (request.Action != "set_goal" || IsBoundedText(request.Objective, 4_000) && (!request.TokenBudget.HasValue || request.TokenBudget.Value > 0))
            && (request.Action != "set_goal_status" || request.GoalStatus.HasValue);

    private static bool IsBoundedText(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength && !value.Any(char.IsControl);

    private static bool IsOptionalText(string? value, int maxLength)
        => value is null || IsBoundedText(value, maxLength);

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}

internal sealed record TelegramMiniAppSessionActionRequest(
    string SessionId,
    string Action,
    string? Name = null,
    string? Model = null,
    string? ReasoningEffort = null,
    string? Objective = null,
    long? TokenBudget = null,
    CodexThreadGoalStatus? GoalStatus = null);

internal sealed record TelegramMiniAppSessionActionResponse(
    bool Accepted,
    string Action,
    string SessionId,
    string OutcomeCode,
    string? SessionName = null,
    CodexSessionModelSettings? ModelSettings = null,
    CodexThreadGoalVm? Goal = null,
    bool Cleared = false);

internal sealed record TelegramMiniAppGlobalInstructionsRequest(string? Text);
