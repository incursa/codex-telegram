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
/// Authenticated worker-side task provisioning endpoint. It accepts coordinator routing
/// metadata and keeps repository, worktree, lease, and Codex execution local to the worker.
/// </summary>
internal static class CodexRemoteTaskEndpoints
{
    private const string ProvisionPath = "/api/worker/v1/tasks/provision";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Map(WebApplication app)
        => app.MapPost(ProvisionPath, ProvisionAsync);

    internal static async Task<IResult> ProvisionAsync(
        HttpContext context,
        IOptions<CodexTelegramOptions> options,
        ICodexWorkerRegistry workerRegistry,
        ICodexTaskWorkspaceManager workspaceManager,
        ICodexSessionManager sessionManager,
        ICodexSupervisionLedger supervisionLedger,
        ICodexTaskRecipeCatalog recipeCatalog,
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
            return Results.BadRequest(new { error = "Remote task provisioning is too large." });
        }

        CodexRemoteTaskProvisionCommand? command;
        try
        {
            command = await JsonSerializer.DeserializeAsync<CodexRemoteTaskProvisionCommand>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Remote task provisioning is malformed." });
        }

        if (command is null || !IsValid(command))
        {
            return Results.BadRequest(new { error = "Remote task provisioning metadata is invalid." });
        }

        CodexWorkerSnapshot worker = await workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(worker.WorkerId, command.WorkerId, StringComparison.Ordinal))
        {
            return Results.Conflict(CodexRemoteTaskProvisionResponseExtensions.RejectedForEndpoint(command, worker.WorkerId, "worker_identity_mismatch"));
        }

        if (worker.State != CodexWorkerState.Online || !string.Equals(worker.Readiness, "ready", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(CodexRemoteTaskProvisionResponseExtensions.RejectedForEndpoint(command, worker.WorkerId, "worker_not_ready"));
        }

        if (hostOptions.Mode != CodexTelegramMode.Repository
            || string.IsNullOrWhiteSpace(hostOptions.RepositoryRoot)
            || !PathsEqual(hostOptions.RepositoryRoot, command.RepositoryRoot))
        {
            return Results.Conflict(CodexRemoteTaskProvisionResponseExtensions.RejectedForEndpoint(command, worker.WorkerId, "repository_not_admitted"));
        }

        CodexTaskRecipeSnapshot? recipe = null;
        if (command.Recipe is not null)
        {
            recipe = recipeCatalog.Find(command.Recipe.Id);
            if (recipe is null || !string.Equals(recipe.Version, command.Recipe.Version, StringComparison.Ordinal))
            {
                return Results.Conflict(CodexRemoteTaskProvisionResponseExtensions.RejectedForEndpoint(command, worker.WorkerId, "recipe_not_admitted"));
            }
        }

        CodexWorkerLease? lease = await workerRegistry.TryAcquireLeaseAsync(command.TaskId, TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false);
        if (lease is null || !string.Equals(lease.LeaseId, command.LeaseId, StringComparison.Ordinal))
        {
            return Results.Conflict(CodexRemoteTaskProvisionResponseExtensions.RejectedForEndpoint(command, worker.WorkerId, "lease_not_admitted"));
        }

        TelegramConversationScope conversation = new(command.ChatId, command.MessageThreadId);
        CodexSupervisionTaskRecord? existingTask = await supervisionLedger.GetTaskAsync(command.OwnerUserId, command.TaskId, cancellationToken).ConfigureAwait(false);
        if (existingTask is not null)
        {
            if (!string.Equals(existingTask.WorkerId, command.WorkerId, StringComparison.Ordinal)
                || !string.Equals(existingTask.LeaseId, command.LeaseId, StringComparison.Ordinal))
            {
                return Results.Conflict(CodexRemoteTaskProvisionResponseExtensions.RejectedForEndpoint(command, worker.WorkerId, "task_owner_conflict"));
            }

            CodexTaskWorkspaceRecord? existingWorkspace = await workspaceManager.GetAsync(command.TaskId, cancellationToken).ConfigureAwait(false);
            return existingWorkspace is null
                ? Results.Conflict(CodexRemoteTaskProvisionResponseExtensions.RejectedForEndpoint(command, worker.WorkerId, "workspace_missing"))
                : Results.Ok(ToResponse(existingTask, existingWorkspace, command, worker.WorkerId));
        }

        CodexTaskWorkspaceRecord workspace;
        try
        {
            workspace = await workspaceManager.CreateAsync(
                command.TaskId,
                hostOptions.RepositoryRoot,
                command.BaseRef,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or DirectoryNotFoundException)
        {
            return Results.Conflict(CodexRemoteTaskProvisionResponseExtensions.RejectedForEndpoint(command, worker.WorkerId, "workspace_provisioning_failed"));
        }

        CodexSessionSummary session;
        try
        {
            session = await sessionManager.CreateSessionAsync(
                new CreateCodexSessionRequest(command.SessionName, workspace.WorktreePath, recipe),
                cancellationToken).ConfigureAwait(false);
            CodexSupervisionTaskRecord? registered = await supervisionLedger.RegisterTaskAsync(
                command.TaskId,
                session.Id,
                session.Name,
                conversation,
                command.OwnerUserId,
                cancellationToken,
                recipe).ConfigureAwait(false);
            if (registered is null)
            {
                throw new InvalidOperationException("The remote task projection could not be registered.");
            }

            CodexSupervisionTaskRecord? bound = await supervisionLedger.BindTaskWorkerAsync(
                command.TaskId,
                command.OwnerUserId,
                worker.WorkerId,
                lease.LeaseId,
                workspace.WorkspaceId,
                cancellationToken).ConfigureAwait(false);
            if (bound is null)
            {
                throw new InvalidOperationException("The remote task worker binding could not be recorded.");
            }

            return Results.Ok(ToResponse(registered, workspace, command, worker.WorkerId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            try
            {
                await workspaceManager.ReleaseAsync(command.TaskId, discardChanges: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the bounded error response; the operator can inspect the worker workspace state.
            }

            await workerRegistry.ReleaseLeaseAsync(command.TaskId, CancellationToken.None).ConfigureAwait(false);
            return Results.Conflict(CodexRemoteTaskProvisionResponseExtensions.RejectedForEndpoint(command, worker.WorkerId, "task_provisioning_failed"));
        }
    }

    private static CodexRemoteTaskProvisionResponse ToResponse(
        CodexSupervisionTaskRecord task,
        CodexTaskWorkspaceRecord workspace,
        CodexRemoteTaskProvisionCommand command,
        string workerId)
        => new(
            true,
            command.TaskId,
            workerId,
            command.LeaseId,
            workspace.WorkspaceId,
            task.CodexThreadId,
            task.SessionName,
            workspace.Branch,
            workspace.DevelopmentPort,
            workspace.DatabaseNamespace,
            "task_provisioned");

    private static bool IsValid(CodexRemoteTaskProvisionCommand command)
        => IsSafeToken(command.TaskId, 160)
            && command.OwnerUserId != 0
            && command.ChatId != 0
            && IsBoundedText(command.SessionName, 200)
            && IsBoundedPath(command.RepositoryRoot, 2_000)
            && (string.IsNullOrWhiteSpace(command.BaseRef) || IsBoundedText(command.BaseRef, 200))
            && IsSafeToken(command.WorkerId, 120)
            && IsSafeToken(command.LeaseId, 160)
            && (command.MessageThreadId is null or > 0)
            && (command.Recipe is null || IsValidRecipe(command.Recipe));

    private static bool IsValidRecipe(CodexTaskRecipeSnapshot recipe)
        => IsSafeToken(recipe.Id, 120)
            && IsSafeToken(recipe.Version, 40)
            && IsBoundedText(recipe.DisplayName, 200)
            && IsBoundedText(recipe.Objective, 2_000)
            && IsOptionalBoundedText(recipe.BaseInstructions, 4_000)
            && IsOptionalBoundedText(recipe.DeveloperInstructions, 4_000)
            && IsOptionalBoundedText(recipe.Model, 120)
            && IsOptionalBoundedText(recipe.ReasoningEffort, 40)
            && recipe.ExpectedOutputs.Count <= 24
            && recipe.RequiredCapabilities.Count <= 24
            && recipe.ExpectedOutputs.All(output => IsBoundedText(output, 240))
            && recipe.RequiredCapabilities.All(capability => IsSafeToken(capability, 120));

    private static bool IsOptionalBoundedText(string? value, int maxLength)
        => value is null || IsBoundedText(value, maxLength);

    private static bool IsBoundedText(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength && !value.Any(char.IsControl);

    private static bool IsBoundedPath(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength && !value.Any(char.IsControl);

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

internal static class CodexRemoteTaskProvisionResponseExtensions
{
    public static CodexRemoteTaskProvisionResponse RejectedForEndpoint(
        CodexRemoteTaskProvisionCommand command,
        string workerId,
        string outcomeCode)
        => new(false, command.TaskId, workerId, command.LeaseId, null, null, null, null, null, null, outcomeCode);
}
