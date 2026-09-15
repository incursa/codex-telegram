using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Authenticated worker-side status and explicit release for task-owned workspaces.
/// </summary>
internal static class CodexRemoteTaskWorkspaceEndpoints
{
    private const string WorkspacePath = "/api/worker/v1/tasks/workspace";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Map(WebApplication app)
        => app.MapPost(WorkspacePath, HandleAsync);

    internal static async Task<IResult> HandleAsync(
        HttpContext context,
        IOptions<CodexTelegramOptions> options,
        ICodexWorkerRegistry workerRegistry,
        ICodexSupervisionLedger supervisionLedger,
        ICodexTaskWorkspaceManager workspaceManager,
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
            return Results.BadRequest(new { error = "Remote workspace request is too large." });
        }

        CodexRemoteTaskWorkspaceRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<CodexRemoteTaskWorkspaceRequest>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Remote workspace request is malformed." });
        }

        if (request is null || !IsValid(request))
        {
            return Results.BadRequest(new { error = "Remote workspace metadata is invalid." });
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
            return Results.Conflict(new { error = "Task ownership does not match the worker workspace." });
        }

        if (request.Action == "status")
        {
            CodexTaskWorkspaceRecord? workspace = await workspaceManager.GetAsync(task.TaskId, cancellationToken).ConfigureAwait(false);
            return workspace is null
                ? Results.NotFound()
                : Results.Ok(ToResponse(task.TaskId, worker.WorkerId, workspace));
        }

        CodexSessionSummary? session = await sessionManager.GetSessionAsync(task.CodexThreadId, cancellationToken).ConfigureAwait(false);
        if (session is not null && IsLive(session.Status))
        {
            return Results.Conflict(new { error = "Stop the task session before releasing its workspace." });
        }

        CodexTaskWorkspaceRecord? released = await workspaceManager.ReleaseAsync(task.TaskId, request.DiscardChanges, cancellationToken).ConfigureAwait(false);
        if (released is null)
        {
            return Results.NotFound();
        }

        await workerRegistry.ReleaseLeaseAsync(task.TaskId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToResponse(task.TaskId, worker.WorkerId, released));
    }

    private static CodexRemoteTaskWorkspaceResponse ToResponse(string taskId, string workerId, CodexTaskWorkspaceRecord workspace)
        => new(taskId, workerId, workspace.State, workspace.Branch, workspace.DevelopmentPort, workspace.DatabaseNamespace, workspace.OutcomeCode);

    private static bool IsValid(CodexRemoteTaskWorkspaceRequest request)
        => IsSafeToken(request.TaskId, 160)
            && request.OwnerUserId != 0
            && IsSafeToken(request.WorkerId, 120)
            && IsSafeToken(request.LeaseId, 160)
            && IsSafeToken(request.CodexThreadId, 256)
            && request.Action is "status" or "release"
            && (!request.DiscardChanges || request.Action == "release");

    private static bool IsLive(CodexSessionStatus status)
        => status is CodexSessionStatus.Starting or CodexSessionStatus.Running;

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
