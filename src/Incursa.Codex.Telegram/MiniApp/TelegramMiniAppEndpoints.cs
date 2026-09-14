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
        app.MapGet("/api/mini-app/bootstrap", GetBootstrapAsync);
    }

    private static async Task<IResult> GetBootstrapAsync(
        HttpContext context,
        TelegramMiniAppAuth auth,
        IOptions<TelegramMiniAppOptions> miniAppOptions,
        ITelegramBotStateStore stateStore,
        ICodexProjectCatalogStore projectCatalog,
        ICodexGateway gateway,
        ICodexAccountUsageService usageService,
        CancellationToken cancellationToken)
    {
        if (!miniAppOptions.Value.Enabled)
        {
            return Results.NotFound();
        }

        if (!auth.TryAuthenticate(context.Request, out TelegramMiniAppIdentity identity, out _))
        {
            return Results.Unauthorized();
        }

        TelegramConversationScope scope = new(identity.UserId, null);
        string? activeSessionId = await stateStore.GetActiveSessionIdAsync(scope, cancellationToken).ConfigureAwait(false);
        string? activeProject = await stateStore.GetActiveProjectWorkingDirectoryAsync(scope, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CodexProjectCatalogEntryVm> projects = (await projectCatalog.ListAsync(cancellationToken).ConfigureAwait(false))
            .Select(ToProjectViewModel)
            .ToArray();

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

        return Results.Ok(new TelegramMiniAppBootstrapVm(
            new TelegramMiniAppUserVm(identity.UserId, identity.Username, identity.FirstName, identity.LastName),
            activeSessionId,
            activeProject,
            projects,
            threads.Select(ToThreadViewModel).ToArray(),
            runtime,
            usage,
            runtimeError,
            threadsError,
            usageError,
            DateTimeOffset.UtcNow));
    }

    private static CodexProjectCatalogEntryVm ToProjectViewModel(CodexProjectCatalogRecord project)
    {
        string normalizedPath = project.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string displayName = Path.GetFileName(normalizedPath);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = normalizedPath;
        }

        return new CodexProjectCatalogEntryVm(project.WorkingDirectory, displayName, project.AddedAt);
    }

    private static TelegramMiniAppThreadVm ToThreadViewModel(CodexThreadListItemVm thread)
        => new(
            thread.Id,
            thread.Name,
            thread.Preview,
            thread.Status,
            thread.ModelProvider,
            thread.CreatedAt,
            thread.UpdatedAt,
            thread.Archived,
            thread.WorkingDirectory);
}

internal sealed record TelegramMiniAppBootstrapVm(
    TelegramMiniAppUserVm User,
    string? ActiveSessionId,
    string? ActiveProjectWorkingDirectory,
    IReadOnlyList<CodexProjectCatalogEntryVm> Projects,
    IReadOnlyList<TelegramMiniAppThreadVm> Threads,
    CodexRuntimeStateVm? Runtime,
    CodexAccountUsageVm? Usage,
    string? RuntimeError,
    string? ThreadsError,
    string? UsageError,
    DateTimeOffset ServerTimeUtc);

internal sealed record TelegramMiniAppThreadVm(
    string Id,
    string? Name,
    string Preview,
    string Status,
    string ModelProvider,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Archived,
    string? WorkingDirectory);

internal sealed record TelegramMiniAppUserVm(
    long Id,
    string? Username,
    string? FirstName,
    string? LastName);
