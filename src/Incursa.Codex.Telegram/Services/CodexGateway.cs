using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal interface ICodexGateway
{
    Task<CodexRuntimeStateVm> GetRuntimeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodexThreadListItemVm>> ListThreadsAsync(CodexThreadListQuery query, CancellationToken cancellationToken = default);

    Task<CodexThreadDetailVm> GetThreadAsync(string threadId, string? workspaceQuery = null, CancellationToken cancellationToken = default);

    Task<CodexThreadListItemVm> CreateThreadShellAsync(CodexThreadContextSubmission submission, CancellationToken cancellationToken = default);

    Task<CodexThreadExecutionVm> CreateThreadAsync(CodexTurnSubmission submission, CancellationToken cancellationToken = default);

    Task<CodexThreadExecutionVm> SendTurnAsync(string threadId, CodexTurnSubmission submission, CancellationToken cancellationToken = default);

    Task<CodexThreadExecutionVm> SendTurnAsync(string threadId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken = default);

    Task<CodexThreadExecutionVm> ForkThreadAsync(string threadId, CodexTurnSubmission submission, CancellationToken cancellationToken = default);

    Task<CodexThreadDetailVm> RenameThreadAsync(string threadId, string name, CancellationToken cancellationToken = default);

    Task<CodexThreadDetailVm> SetArchiveStateAsync(string threadId, bool archived, CancellationToken cancellationToken = default);

    Task<CodexThreadDetailVm> CompactThreadAsync(string threadId, CancellationToken cancellationToken = default);

    Task<CodexThreadGoalVm?> GetThreadGoalAsync(string threadId, CancellationToken cancellationToken = default);

    Task<CodexThreadGoalVm> SetThreadGoalAsync(
        string threadId,
        string objective,
        long? tokenBudget = null,
        CancellationToken cancellationToken = default);

    Task<CodexThreadGoalVm> SetThreadGoalStatusAsync(
        string threadId,
        CodexThreadGoalStatus status,
        CancellationToken cancellationToken = default);

    Task<bool> ClearThreadGoalAsync(string threadId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodexWorkspaceEntryVm>> SearchWorkspaceAsync(string? query = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CodexModelVm>> ListModelsAsync(CancellationToken cancellationToken = default);
}

internal sealed class CodexGateway : ICodexGateway
{
    private readonly CodexTelegramOptions _options;
    private readonly CodexThreadManifestStore _manifestStore;
    private readonly CodexWorkspaceBrowser _workspaceBrowser;
    private readonly CodexSessionRuntimeRegistry _runtimeRegistry;

    public CodexGateway(
        IOptions<CodexTelegramOptions> options,
        CodexThreadManifestStore manifestStore,
        CodexWorkspaceBrowser workspaceBrowser,
        CodexSessionRuntimeRegistry runtimeRegistry)
    {
        _options = options.Value;
        _manifestStore = manifestStore;
        _workspaceBrowser = workspaceBrowser;
        _runtimeRegistry = runtimeRegistry;
    }

    public async Task<CodexRuntimeStateVm> GetRuntimeAsync(CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.GetDefaultAsync(cancellationToken).ConfigureAwait(false);
        return runtime.RuntimeState.ToViewModel();
    }

    public async Task<IReadOnlyList<CodexThreadListItemVm>> ListThreadsAsync(
        CodexThreadListQuery query,
        CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.GetDefaultAsync(cancellationToken).ConfigureAwait(false);

        CodexThreadListResult result = await runtime.Client.ListThreadsAsync(CodexOptionMapper.BuildThreadListOptions(query), cancellationToken).ConfigureAwait(false);
        List<CodexThreadListItemVm> threads = new(result.Threads.Count);
        foreach (CodexThreadSummary summary in result.Threads)
        {
            CodexThreadManifestRecord? manifest = await _manifestStore.ReadAsync(summary.Id, cancellationToken).ConfigureAwait(false);
            threads.Add(CodexViewModelMapper.ToThreadListItemVm(summary, manifest));
        }

        IOrderedEnumerable<CodexThreadListItemVm> ordered = query.SortKey.Equals("createdAt", StringComparison.OrdinalIgnoreCase)
            ? (query.Descending ? threads.OrderByDescending(thread => thread.CreatedAt) : threads.OrderBy(thread => thread.CreatedAt))
            : (query.Descending ? threads.OrderByDescending(thread => thread.UpdatedAt) : threads.OrderBy(thread => thread.UpdatedAt));

        return ordered.ToArray();
    }

    public async Task<CodexThreadDetailVm> GetThreadAsync(
        string threadId,
        string? workspaceQuery = null,
        CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.GetBestForThreadAsync(threadId, cancellationToken).ConfigureAwait(false);

        CodexThreadSnapshot snapshot = await runtime.Client.ReadThreadAsync(threadId, new CodexThreadReadOptions { IncludeTurns = true }, cancellationToken).ConfigureAwait(false);
        CodexThreadManifestRecord manifest = await _manifestStore.GetOrCreateAsync(threadId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CodexModelVm> models = await ListModelsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CodexWorkspaceEntryVm> workspaceEntries = await _workspaceBrowser.SearchAsync(workspaceQuery, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CodexThreadFileVm> files = manifest.Files
            .Select(file => CodexViewModelMapper.ToThreadFileVm(file, _manifestStore.GetThreadRoot(threadId)))
            .ToArray();

        return CodexViewModelMapper.ToThreadDetailVm(
            snapshot,
            manifest,
            files,
            models,
            workspaceEntries,
            runtime.RuntimeState.ToViewModel(),
            _runtimeRegistry.GetActiveTurnId(threadId));
    }

    public async Task<CodexThreadExecutionVm> CreateThreadAsync(
        CodexTurnSubmission submission,
        CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.CreateDedicatedSlotAsync(cancellationToken).ConfigureAwait(false);

        submission.ThreadName = CodexTextFormatting.InferThreadName(submission.ThreadName, submission.Prompt);
        CodexThreadOptions threadOptions = CodexOptionMapper.BuildThreadOptions(_options, submission);
        CodexThread thread = await runtime.Client.StartThreadAsync(threadOptions, cancellationToken).ConfigureAwait(false);
        string threadId = GetThreadIdOrThrow(thread);
        _runtimeRegistry.BindThread(threadId, runtime);
        await _manifestStore.SetContextAsync(threadId, submission, cancellationToken).ConfigureAwait(false);
        await _manifestStore.SetSelectedFilesAsync(threadId, submission.SelectedFileIds, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(submission.ThreadName))
        {
            await thread.SetNameAsync(submission.ThreadName, cancellationToken).ConfigureAwait(false);
        }

        CodexThreadManifestRecord updatedManifest = await _manifestStore.GetOrCreateAsync(threadId, cancellationToken).ConfigureAwait(false);
        return await StartTurnAsync(runtime, thread, threadId, submission, updatedManifest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexThreadListItemVm> CreateThreadShellAsync(
        CodexThreadContextSubmission submission,
        CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.CreateDedicatedSlotAsync(cancellationToken).ConfigureAwait(false);

        submission.ThreadName = CodexTextFormatting.InferThreadName(submission.ThreadName, null);
        CodexThreadOptions threadOptions = CodexOptionMapper.BuildThreadOptions(_options, submission);
        CodexThread thread = await runtime.Client.StartThreadAsync(threadOptions, cancellationToken).ConfigureAwait(false);
        string threadId = GetThreadIdOrThrow(thread);
        _runtimeRegistry.BindThread(threadId, runtime);
        await _manifestStore.SetContextAsync(threadId, submission, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(submission.ThreadName))
        {
            await thread.SetNameAsync(submission.ThreadName, cancellationToken).ConfigureAwait(false);
        }

        CodexThreadManifestRecord manifest = await _manifestStore.GetOrCreateAsync(threadId, cancellationToken).ConfigureAwait(false);
        return new CodexThreadListItemVm(
            threadId,
            submission.ThreadName,
            string.Empty,
            "idle",
            CodexTextFormatting.RepairUtf8Mojibake(submission.ModelProvider) ?? _options.Context.ModelProvider ?? "OpenAI",
            manifest.CreatedAt,
            manifest.UpdatedAt,
            submission.Ephemeral ?? false,
            threadOptions.WorkingDirectory,
            null,
            null,
            null,
            null,
            manifest.IsArchived,
            manifest.WorkingDirectory ?? threadOptions.WorkingDirectory);
    }

    public async Task<CodexThreadExecutionVm> SendTurnAsync(
        string threadId,
        CodexTurnSubmission submission,
        CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.GetOrCreateForThreadAsync(threadId, cancellationToken).ConfigureAwait(false);

        CodexThreadManifestRecord manifest = await _manifestStore.SetContextAsync(threadId, submission, cancellationToken).ConfigureAwait(false);
        manifest = await _manifestStore.SetSelectedFilesAsync(threadId, submission.SelectedFileIds, cancellationToken).ConfigureAwait(false);

        CodexThreadOptions threadOptions = CodexOptionMapper.BuildThreadOptions(_options, submission, manifest);
        CodexThread thread = await runtime.Client.ResumeThreadAsync(threadId, threadOptions, cancellationToken).ConfigureAwait(false);
        return await StartTurnAsync(runtime, thread, threadId, submission, manifest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexThreadExecutionVm> SendTurnAsync(
        string threadId,
        IReadOnlyList<CodexInputItem> input,
        CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.GetOrCreateForThreadAsync(threadId, cancellationToken).ConfigureAwait(false);

        CodexThreadManifestRecord manifest = await _manifestStore.GetOrCreateAsync(threadId, cancellationToken).ConfigureAwait(false);
        CodexThreadOptions threadOptions = CodexOptionMapper.BuildThreadOptions(_options, new CodexThreadContextSubmission(), manifest);
        CodexThread thread = await runtime.Client.ResumeThreadAsync(threadId, threadOptions, cancellationToken).ConfigureAwait(false);
        CodexTurnOptions turnOptions = CodexOptionMapper.BuildTurnOptions(_options, new CodexTurnSubmission(), manifest);

        CodexThreadExecutionVm execution = await runtime.TurnCoordinator.StartAsync(thread, input, turnOptions, cancellationToken).ConfigureAwait(false);
        await _manifestStore.UpdateAsync(threadId, current =>
        {
            current.LastTurnId = execution.TurnId;
            return current;
        }, cancellationToken).ConfigureAwait(false);

        return execution;
    }

    public async Task<CodexThreadExecutionVm> ForkThreadAsync(
        string threadId,
        CodexTurnSubmission submission,
        CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.CreateDedicatedSlotAsync(cancellationToken).ConfigureAwait(false);

        CodexThreadManifestRecord sourceManifest = await _manifestStore.GetOrCreateAsync(threadId, cancellationToken).ConfigureAwait(false);
        submission.ThreadName = CodexTextFormatting.InferThreadName(submission.ThreadName, submission.Prompt, sourceManifest.ThreadName);
        CodexThreadOptions sourceOptions = CodexOptionMapper.BuildThreadOptions(_options, submission, sourceManifest);
        CodexThreadForkOptions forkOptions = ToForkOptions(sourceOptions);
        CodexRuntimeSlot sourceRuntime = await _runtimeRegistry.GetBestForThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        CodexThread forkedThread = await sourceRuntime.Client.ForkThreadAsync(threadId, forkOptions, cancellationToken).ConfigureAwait(false);
        string forkedThreadId = GetThreadIdOrThrow(forkedThread);
        _runtimeRegistry.BindThread(forkedThreadId, runtime);
        forkedThread = await runtime.Client.ResumeThreadAsync(forkedThreadId, sourceOptions, cancellationToken).ConfigureAwait(false);
        await _manifestStore.SetContextAsync(forkedThreadId, submission, cancellationToken).ConfigureAwait(false);
        await _manifestStore.SetSelectedFilesAsync(forkedThreadId, submission.SelectedFileIds, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(submission.ThreadName))
        {
            await forkedThread.SetNameAsync(submission.ThreadName, cancellationToken).ConfigureAwait(false);
        }

        CodexThreadManifestRecord updatedManifest = await _manifestStore.GetOrCreateAsync(forkedThreadId, cancellationToken).ConfigureAwait(false);
        return await StartTurnAsync(runtime, forkedThread, forkedThreadId, submission, updatedManifest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexThreadDetailVm> RenameThreadAsync(string threadId, string name, CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.GetBestForThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        CodexThread thread = await runtime.Client.ResumeThreadAsync(threadId, null, cancellationToken).ConfigureAwait(false);
        await thread.SetNameAsync(name, cancellationToken).ConfigureAwait(false);
        await _manifestStore.SetThreadNameAsync(threadId, name, cancellationToken).ConfigureAwait(false);
        return await GetThreadAsync(threadId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexThreadDetailVm> SetArchiveStateAsync(string threadId, bool archived, CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.GetBestForThreadAsync(threadId, cancellationToken).ConfigureAwait(false);

        if (archived)
        {
            await runtime.Client.ArchiveThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await runtime.Client.UnarchiveThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        }

        await _manifestStore.SetArchiveStateAsync(threadId, archived, cancellationToken).ConfigureAwait(false);
        return await GetThreadAsync(threadId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexThreadDetailVm> CompactThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.GetBestForThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        CodexThread thread = await runtime.Client.ResumeThreadAsync(threadId, null, cancellationToken).ConfigureAwait(false);
        await thread.CompactAsync(cancellationToken).ConfigureAwait(false);
        return await GetThreadAsync(threadId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexThreadGoalVm?> GetThreadGoalAsync(string threadId, CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.GetBestForThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        CodexThread thread = await runtime.Client.ResumeThreadAsync(threadId, null, cancellationToken).ConfigureAwait(false);
        CodexThreadGoal? goal = await thread.GetGoalAsync(cancellationToken).ConfigureAwait(false);
        return goal is null ? null : CodexViewModelMapper.ToThreadGoalVm(goal);
    }

    public async Task<CodexThreadGoalVm> SetThreadGoalAsync(
        string threadId,
        string objective,
        long? tokenBudget = null,
        CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.GetBestForThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        CodexThread thread = await runtime.Client.ResumeThreadAsync(threadId, null, cancellationToken).ConfigureAwait(false);
        CodexThreadGoal goal = await thread.SetGoalAsync(objective, tokenBudget, cancellationToken).ConfigureAwait(false);
        return CodexViewModelMapper.ToThreadGoalVm(goal);
    }

    public async Task<CodexThreadGoalVm> SetThreadGoalStatusAsync(
        string threadId,
        CodexThreadGoalStatus status,
        CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.GetBestForThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        CodexThread thread = await runtime.Client.ResumeThreadAsync(threadId, null, cancellationToken).ConfigureAwait(false);
        CodexThreadGoal goal = await thread.SetGoalStatusAsync(status, cancellationToken).ConfigureAwait(false);
        return CodexViewModelMapper.ToThreadGoalVm(goal);
    }

    public async Task<bool> ClearThreadGoalAsync(string threadId, CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.GetBestForThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        CodexThread thread = await runtime.Client.ResumeThreadAsync(threadId, null, cancellationToken).ConfigureAwait(false);
        return await thread.ClearGoalAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<CodexWorkspaceEntryVm>> SearchWorkspaceAsync(string? query = null, CancellationToken cancellationToken = default)
        => _workspaceBrowser.SearchAsync(query, cancellationToken);

    public async Task<IReadOnlyList<CodexModelVm>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        CodexRuntimeSlot runtime = await _runtimeRegistry.GetDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (runtime.RuntimeState.Models.Count == 0)
        {
            try
            {
                CodexModelListResult result = await runtime.Client.ListModelsAsync(new CodexModelListOptions(), cancellationToken).ConfigureAwait(false);
                runtime.RuntimeState.SetModels(result.Models);
            }
            catch (CodexCapabilityNotSupportedException)
            {
                return Array.Empty<CodexModelVm>();
            }
        }

        return runtime.RuntimeState.Models.Select(CodexViewModelMapper.ToModelVm).ToArray();
    }

    private async Task<CodexThreadExecutionVm> StartTurnAsync(
        CodexRuntimeSlot runtime,
        CodexThread thread,
        string threadId,
        CodexTurnSubmission submission,
        CodexThreadManifestRecord manifest,
        CancellationToken cancellationToken)
    {
        CodexTurnOptions turnOptions = CodexOptionMapper.BuildTurnOptions(_options, submission, manifest);
        IReadOnlyList<CodexInputItem> input = CodexOptionMapper.BuildInputItems(
            manifest,
            submission,
            file => _manifestStore.ResolveFilePath(threadId, file.RelativePath));

        CodexThreadExecutionVm execution = await runtime.TurnCoordinator.StartAsync(thread, input, turnOptions, cancellationToken).ConfigureAwait(false);
        await _manifestStore.UpdateAsync(threadId, current =>
        {
            current.LastTurnId = execution.TurnId;
            return current;
        }, cancellationToken).ConfigureAwait(false);

        return execution;
    }

    private static CodexThreadForkOptions ToForkOptions(CodexThreadOptions options)
        => new()
        {
            ApprovalPolicy = options.ApprovalPolicy,
            ApprovalsReviewer = options.ApprovalsReviewer,
            BaseInstructions = options.BaseInstructions,
            Config = options.Config,
            DeveloperInstructions = options.DeveloperInstructions,
            Ephemeral = options.Ephemeral,
            Model = options.Model,
            ModelProvider = options.ModelProvider,
            Personality = options.Personality,
            Sandbox = options.Sandbox,
            ServiceTier = options.ServiceTier,
            WorkingDirectory = options.WorkingDirectory,
            ServiceName = options.ServiceName,
            ModelReasoningEffort = options.ModelReasoningEffort,
            NetworkAccessEnabled = options.NetworkAccessEnabled,
            WebSearchMode = options.WebSearchMode,
            WebSearchEnabled = options.WebSearchEnabled,
            SkipGitRepoCheck = options.SkipGitRepoCheck,
            AdditionalDirectories = options.AdditionalDirectories,
        };

    private static string GetThreadIdOrThrow(CodexThread thread)
        => string.IsNullOrWhiteSpace(thread.Id) ? throw new InvalidOperationException("Codex thread id was not populated.") : thread.Id;
}
