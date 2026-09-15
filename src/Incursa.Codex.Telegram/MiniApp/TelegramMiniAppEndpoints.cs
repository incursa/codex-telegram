using System.Security.Cryptography;
using System.Text;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Read-only HTTP endpoints used by the Telegram Mini App companion surface.
/// </summary>
internal static class TelegramMiniAppEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/mini-app/pairing/start", StartBrowserPairingAsync);
        app.MapGet("/api/mini-app/pairing/status", GetBrowserPairingStatusAsync);
        app.MapGet("/api/mini-app/bootstrap", GetBootstrapAsync);
        app.MapGet("/api/mini-app/threads/{threadId}", GetThreadAsync);
    }

    private static async Task<IResult> StartBrowserPairingAsync(
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        ITelegramMiniAppBrowserPairingStore pairingStore,
        CancellationToken cancellationToken)
    {
        if (!miniAppOptions.Value.Enabled || !miniAppOptions.Value.BrowserPairingEnabled)
        {
            return Results.NotFound();
        }

        return Results.Ok(await pairingStore.StartAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> GetBrowserPairingStatusAsync(
        HttpContext context,
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        ITelegramMiniAppBrowserPairingStore pairingStore,
        CancellationToken cancellationToken)
    {
        if (!miniAppOptions.Value.Enabled || !miniAppOptions.Value.BrowserPairingEnabled)
        {
            return Results.NotFound();
        }

        string? pairingToken = context.Request.Headers["X-Codex-Browser-Pairing"].FirstOrDefault();
        TelegramMiniAppBrowserPairingStatus status = await pairingStore.GetPairingStatusAsync(pairingToken ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return status.State == "unknown" ? Results.Unauthorized() : Results.Ok(status);
    }

    private static async Task<IResult> GetBootstrapAsync(
        HttpContext context,
        TelegramMiniAppAuth auth,
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        ITelegramBotStateStore stateStore,
        ICodexProjectCatalogStore projectCatalog,
        ICodexGateway gateway,
        ICodexTurnExecutionCoordinator turnCoordinator,
        ICodexAccountUsageService usageService,
        ICodexSupervisionLedger supervisionLedger,
        ITelegramMiniAppBrowserPairingStore pairingStore,
        ICodexWorkerRegistry workerRegistry,
        ICodexCoordinatorWorkerStore coordinatorWorkerStore,
        CancellationToken cancellationToken)
    {
        if (!miniAppOptions.Value.Enabled)
        {
            return Results.NotFound();
        }

        TelegramMiniAppIdentity? identity = await TryAuthenticateAsync(
            context,
            auth,
            miniAppOptions.Value,
            pairingStore,
            cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return Results.Unauthorized();
        }

        TelegramConversationScope scope = new(identity.UserId, null);
        string? activeSessionId = null;
        string? activeProject = null;
        string? stateError = null;
        try
        {
            activeSessionId = await stateStore.GetActiveSessionIdAsync(scope, cancellationToken).ConfigureAwait(false);
            activeProject = await stateStore.GetActiveProjectWorkingDirectoryAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stateError = "Conversation context is currently unavailable.";
        }

        IReadOnlyList<TelegramMiniAppProjectVm> projects = Array.Empty<TelegramMiniAppProjectVm>();
        string? projectsError = null;
        try
        {
            projects = (await projectCatalog.ListAsync(cancellationToken).ConfigureAwait(false))
                .Select(project => ToProjectViewModel(project, activeProject))
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            projectsError = "Saved workspace projects are currently unavailable.";
        }

        IReadOnlyList<CodexThreadListItemVm> threads = Array.Empty<CodexThreadListItemVm>();
        string? threadsError = null;
        try
        {
            threads = await gateway.ListThreadsAsync(
                new CodexThreadListQuery(null, "all", "updatedAt", true, null, 24),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            threadsError = "Codex session list is currently unavailable.";
        }

        CodexRuntimeStateVm? runtime = null;
        string? runtimeError = null;
        try
        {
            runtime = await gateway.GetRuntimeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            runtimeError = "Codex runtime status is currently unavailable.";
        }

        CodexAccountUsageVm? usage = null;
        string? usageError = null;
        try
        {
            usage = await usageService.GetUsageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            usageError = "Codex usage is currently unavailable.";
        }

        IReadOnlyList<TelegramMiniAppSupervisionTaskVm> supervisionTasks = Array.Empty<TelegramMiniAppSupervisionTaskVm>();
        string? supervisionError = null;
        try
        {
            supervisionTasks = (await supervisionLedger.ListTasksAsync(identity.UserId, cancellationToken).ConfigureAwait(false))
                .Select(TelegramMiniAppProjection.ToSupervisionTaskViewModel)
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            supervisionError = "Durable task state is currently unavailable.";
        }

        IReadOnlyList<TelegramMiniAppWorkerVm> workers = Array.Empty<TelegramMiniAppWorkerVm>();
        string? workersError = null;
        try
        {
            CodexWorkerSnapshot worker = await workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            workers = [ToWorkerViewModel(worker)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            workersError = "Worker status is currently unavailable.";
        }
        try
        {
            IReadOnlyList<CodexWorkerSnapshot> remoteWorkers = await coordinatorWorkerStore.ListAsync(cancellationToken).ConfigureAwait(false);
            workers = [.. workers, .. remoteWorkers.Select(ToWorkerViewModel)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            workersError ??= "Coordinator worker status is currently unavailable.";
        }

        TelegramMiniAppThreadVm[] projectedThreads = threads
            .Select(thread => TelegramMiniAppProjection.ToThreadViewModel(thread, turnCoordinator.TryGetActiveTurnState(thread.Id)))
            .ToArray();

        DateTimeOffset serverTime = DateTimeOffset.UtcNow;
        return Results.Ok(new TelegramMiniAppBootstrapVm(
            new TelegramMiniAppUserVm(identity.UserId, identity.Username, identity.FirstName, identity.LastName),
            activeSessionId,
            projects,
            projectedThreads,
            runtime,
            usage,
            runtimeError,
            threadsError,
            usageError,
            serverTime) with
        {
            NeedsAttention = TelegramMiniAppProjection.BuildNeedsAttention(projectedThreads, runtimeError, threadsError, projectsError, stateError, serverTime, supervisionTasks),
            RecentActivity = projectedThreads,
            ProjectsError = projectsError,
            StateError = stateError,
            SupervisionTasks = supervisionTasks,
            SupervisionError = supervisionError,
            Workers = workers,
            WorkersError = workersError,
        });
    }

    private static async Task<IResult> GetThreadAsync(
        string threadId,
        HttpContext context,
        TelegramMiniAppAuth auth,
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        ICodexGateway gateway,
        ICodexTurnExecutionCoordinator turnCoordinator,
        ICodexSupervisionLedger supervisionLedger,
        ITelegramMiniAppBrowserPairingStore pairingStore,
        ICodexWorkerRegistry workerRegistry,
        CodexRemoteTaskDetailRelay remoteTaskDetailRelay,
        CancellationToken cancellationToken)
    {
        if (!miniAppOptions.Value.Enabled)
        {
            return Results.NotFound();
        }

        TelegramMiniAppIdentity? identity = await TryAuthenticateAsync(
            context,
            auth,
            miniAppOptions.Value,
            pairingStore,
            cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(threadId) || threadId.Length > 256)
        {
            return Results.BadRequest(new { message = "A valid Codex thread id is required." });
        }

        try
        {
            CodexSupervisionTaskSnapshot? supervisionTask = null;
            try
            {
                supervisionTask = await supervisionLedger
                    .GetTaskForSessionAsync(identity.UserId, threadId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Local Codex detail remains useful when the optional task projection is unavailable.
            }

            if (supervisionTask?.WorkerId is { Length: > 0 } assignedWorkerId)
            {
                CodexWorkerSnapshot localWorker = await workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(assignedWorkerId, localWorker.WorkerId, StringComparison.Ordinal))
                {
                    CodexSupervisionTaskRecord? remoteTask = await supervisionLedger
                        .GetTaskAsync(identity.UserId, supervisionTask.TaskId, cancellationToken)
                        .ConfigureAwait(false);
                    if (remoteTask is null)
                    {
                        return Results.NotFound();
                    }

                    return Results.Ok(await remoteTaskDetailRelay.GetAsync(remoteTask, cancellationToken).ConfigureAwait(false));
                }
            }

            IReadOnlyList<CodexThreadListItemVm> threads = await gateway.ListThreadsAsync(
                new CodexThreadListQuery(null, "all", "updatedAt", true, null, 200),
                cancellationToken).ConfigureAwait(false);
            CodexThreadListItemVm? summary = threads.FirstOrDefault(thread =>
                string.Equals(thread.Id, threadId, StringComparison.Ordinal));
            if (summary is null)
            {
                return Results.NotFound();
            }

            CodexThreadDetailVm detail = await gateway.GetThreadAsync(threadId, cancellationToken: cancellationToken).ConfigureAwait(false);
            TelegramMiniAppThreadDetailVm projected = TelegramMiniAppProjection.ToThreadDetailViewModel(
                detail,
                turnCoordinator.TryGetActiveTurnState(threadId),
                DateTimeOffset.UtcNow);
            TelegramMiniAppSupervisionTaskVm? supervision = null;
            try
            {
                supervision = (await supervisionLedger.GetTaskForSessionAsync(identity.UserId, threadId, cancellationToken).ConfigureAwait(false)) is { } task
                    ? TelegramMiniAppProjection.ToSupervisionTaskViewModel(task)
                    : null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Thread detail remains useful when the optional durable projection is unavailable.
            }

            projected = projected with
            {
                Supervision = supervision,
                ReviewPacket = TelegramMiniAppProjection.BuildReviewPacket(detail, supervision, projected.RetrievedAtUtc),
            };

            return Results.Ok(projected);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Codex thread detail is currently unavailable.");
        }
    }

    private static TelegramMiniAppProjectVm ToProjectViewModel(CodexProjectCatalogRecord project, string? activeProject)
    {
        string normalizedPath = project.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string displayName = Path.GetFileName(normalizedPath);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "Workspace";
        }

        return new TelegramMiniAppProjectVm(
            BuildProjectId(project.WorkingDirectory),
            displayName,
            string.Equals(project.WorkingDirectory, activeProject, StringComparison.OrdinalIgnoreCase),
            project.AddedAt);
    }

    private static string BuildProjectId(string path)
        => "project-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..16];

    private static TelegramMiniAppWorkerVm ToWorkerViewModel(CodexWorkerSnapshot worker)
        => new(
            worker.WorkerId,
            worker.DisplayName,
            worker.State.ToString().ToLowerInvariant(),
            worker.Readiness,
            worker.Version,
            worker.ActiveLeaseCount,
            worker.MaximumConcurrentTasks,
            worker.LastHeartbeatUtc,
            worker.Capabilities,
            worker.Issues,
            worker.IsRemote);

    private static async Task<TelegramMiniAppIdentity?> TryAuthenticateAsync(
        HttpContext context,
        TelegramMiniAppAuth auth,
        TelegramMiniAppOptions miniAppOptions,
        ITelegramMiniAppBrowserPairingStore pairingStore,
        CancellationToken cancellationToken)
    {
        if (auth.TryAuthenticate(context.Request, out TelegramMiniAppIdentity telegramIdentity, out _))
        {
            return telegramIdentity;
        }

        if (!miniAppOptions.BrowserPairingEnabled)
        {
            return null;
        }

        string? sessionToken = context.Request.Headers["X-Codex-Browser-Session"].FirstOrDefault();
        long? userId = await pairingStore.AuthenticateSessionAsync(sessionToken ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return userId.HasValue && auth.IsAllowlisted(userId.Value)
            ? new TelegramMiniAppIdentity(userId.Value, null, null, null)
            : null;
    }

}

internal sealed record TelegramMiniAppBootstrapVm(
    TelegramMiniAppUserVm User,
    string? ActiveSessionId,
    IReadOnlyList<TelegramMiniAppProjectVm> Projects,
    IReadOnlyList<TelegramMiniAppThreadVm> Threads,
    CodexRuntimeStateVm? Runtime,
    CodexAccountUsageVm? Usage,
    string? RuntimeError,
    string? ThreadsError,
    string? UsageError,
    DateTimeOffset ServerTimeUtc)
{
    public IReadOnlyList<TelegramMiniAppAttentionVm> NeedsAttention { get; init; } = Array.Empty<TelegramMiniAppAttentionVm>();

    public IReadOnlyList<TelegramMiniAppThreadVm> RecentActivity { get; init; } = Array.Empty<TelegramMiniAppThreadVm>();

    public string? ProjectsError { get; init; }

    public string? StateError { get; init; }

    public IReadOnlyList<TelegramMiniAppSupervisionTaskVm> SupervisionTasks { get; init; } = Array.Empty<TelegramMiniAppSupervisionTaskVm>();

    public string? SupervisionError { get; init; }

    public IReadOnlyList<TelegramMiniAppWorkerVm> Workers { get; init; } = Array.Empty<TelegramMiniAppWorkerVm>();

    public string? WorkersError { get; init; }
}

internal sealed record TelegramMiniAppProjectVm(
    string Id,
    string DisplayName,
    bool IsActive,
    DateTimeOffset AddedAt);

internal sealed record TelegramMiniAppWorkerVm(
    string WorkerId,
    string DisplayName,
    string State,
    string Readiness,
    string Version,
    int ActiveLeaseCount,
    int MaximumConcurrentTasks,
    DateTimeOffset LastHeartbeatUtc,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Issues,
    bool IsRemote = false);

internal sealed record TelegramMiniAppThreadVm(
    string Id,
    string? Name,
    string Preview,
    string Status,
    string ModelProvider,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Archived,
    string? WorkingDirectory,
    string LifecycleState,
    bool NeedsAttention,
    string? AttentionKind,
    string? AttentionTitle,
    string? LatestEventTitle);

internal sealed record TelegramMiniAppUserVm(
    long Id,
    string? Username,
    string? FirstName,
    string? LastName);

internal sealed record TelegramMiniAppSupervisionTaskVm(
    string TaskId,
    string CodexThreadId,
    string SessionName,
    string State,
    string? RunId,
    string? CommandId,
    string? TurnId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset LastRunUpdatedAt,
    string? RecipeId = null,
    string? RecipeVersion = null,
    string? RecipeDisplayName = null,
    string? WorkerId = null,
    string? LeaseId = null,
    string? WorkspaceId = null);
