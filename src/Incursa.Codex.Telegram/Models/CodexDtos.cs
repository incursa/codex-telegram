using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Models;

/// <summary>
/// Runtime capability and health snapshot for the connected Codex installation.
/// </summary>
/// <param name="Initialized">Whether runtime initialization completed.</param>
/// <param name="Message">Human-readable runtime status message.</param>
/// <param name="ServerName">Connected Codex server name.</param>
/// <param name="ServerVersion">Connected Codex server version.</param>
/// <param name="PlatformFamily">Platform family reported by Codex.</param>
/// <param name="PlatformOs">Operating system reported by Codex.</param>
/// <param name="UserAgent">Codex user-agent string.</param>
/// <param name="Warnings">Runtime warnings discovered during initialization.</param>
/// <param name="Errors">Runtime errors discovered during initialization.</param>
/// <param name="SupportsThreadStreaming">Whether live thread streaming is available.</param>
/// <param name="SupportsTurnSteering">Whether active turns can be steered.</param>
/// <param name="SupportsTurnInterruption">Whether active turns can be interrupted.</param>
/// <param name="SupportsStartThread">Whether Codex can start new threads.</param>
/// <param name="SupportsResumeThread">Whether Codex can resume existing threads.</param>
/// <param name="SupportsForkThread">Whether Codex can fork threads.</param>
/// <param name="SupportsListThreads">Whether Codex can list threads.</param>
/// <param name="SupportsCompactThread">Whether Codex can compact threads.</param>
/// <param name="SupportsArchiveThread">Whether Codex can archive threads.</param>
/// <param name="SupportsUnarchiveThread">Whether Codex can unarchive threads.</param>
/// <param name="SupportsListModels">Whether Codex can list available models.</param>
/// <param name="SupportsThreadGoals">Whether Codex can read and mutate thread goals.</param>
internal sealed record CodexRuntimeStateVm(
    bool Initialized,
    string? Message,
    string? ServerName,
    string? ServerVersion,
    string? PlatformFamily,
    string? PlatformOs,
    string? UserAgent,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    bool SupportsThreadStreaming,
    bool SupportsTurnSteering,
    bool SupportsTurnInterruption,
    bool SupportsStartThread,
    bool SupportsResumeThread,
    bool SupportsForkThread,
    bool SupportsListThreads,
    bool SupportsCompactThread,
    bool SupportsArchiveThread,
    bool SupportsUnarchiveThread,
    bool SupportsListModels,
    bool SupportsThreadGoals);

/// <summary>
/// Current Codex goal state attached to a thread.
/// </summary>
/// <param name="ThreadId">Codex thread ID.</param>
/// <param name="Objective">Goal objective text.</param>
/// <param name="Status">Goal lifecycle status.</param>
/// <param name="TokenBudget">Optional token budget.</param>
/// <param name="TokensUsed">Tokens consumed against the goal.</param>
/// <param name="TimeUsedSeconds">Elapsed time consumed against the goal.</param>
/// <param name="CreatedAt">Goal creation time.</param>
/// <param name="UpdatedAt">Most recent goal update time.</param>
internal sealed record CodexThreadGoalVm(
    string ThreadId,
    string Objective,
    CodexThreadGoalStatus Status,
    long? TokenBudget,
    long TokensUsed,
    long TimeUsedSeconds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Summary row for a Codex thread shown in Telegram session lists.
/// </summary>
/// <param name="Id">Codex thread ID.</param>
/// <param name="Name">Thread display name.</param>
/// <param name="Preview">Short thread preview text.</param>
/// <param name="Status">Thread lifecycle status.</param>
/// <param name="ModelProvider">Model provider reported by Codex.</param>
/// <param name="CreatedAt">Thread creation time.</param>
/// <param name="UpdatedAt">Most recent thread update time.</param>
/// <param name="Ephemeral">Whether the thread is ephemeral.</param>
/// <param name="Path">Codex-reported thread path.</param>
/// <param name="AgentRole">Codex agent role, when available.</param>
/// <param name="AgentNickname">Codex agent nickname, when available.</param>
/// <param name="GitBranch">Git branch associated with the thread, when known.</param>
/// <param name="GitSha">Git SHA associated with the thread, when known.</param>
/// <param name="Archived">Whether the thread is archived.</param>
/// <param name="WorkingDirectory">Local working directory associated with the thread.</param>
internal sealed record CodexThreadListItemVm(
    string Id,
    string? Name,
    string Preview,
    string Status,
    string ModelProvider,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Ephemeral,
    string? Path,
    string? AgentRole,
    string? AgentNickname,
    string? GitBranch,
    string? GitSha,
    bool Archived,
    string? WorkingDirectory);

/// <summary>
/// Group of Codex threads that share a working directory.
/// </summary>
/// <param name="DisplayName">Human-readable group label.</param>
/// <param name="WorkingDirectory">Grouped working directory.</param>
/// <param name="Threads">Threads in the group.</param>
internal sealed record CodexThreadProjectGroupVm(
    string DisplayName,
    string? WorkingDirectory,
    IReadOnlyList<CodexThreadListItemVm> Threads);

/// <summary>
/// Project directory known to the local Telegram bot.
/// </summary>
/// <param name="WorkingDirectory">Absolute local project directory.</param>
/// <param name="DisplayName">Display name derived from the directory.</param>
/// <param name="AddedAt">UTC time when the project was added.</param>
internal sealed record CodexProjectCatalogEntryVm(
    string WorkingDirectory,
    string DisplayName,
    DateTimeOffset AddedAt);

/// <summary>
/// Token usage summary for a Codex turn.
/// </summary>
/// <param name="CachedInputTokens">Input tokens served from cache.</param>
/// <param name="InputTokens">Total input tokens.</param>
/// <param name="OutputTokens">Total output tokens.</param>
/// <param name="ReasoningOutputTokens">Output tokens used for reasoning.</param>
/// <param name="TotalTokens">Total tokens consumed by the turn.</param>
/// <param name="ModelContextWindow">Model context window, when known.</param>
internal sealed record CodexUsageVm(
    int CachedInputTokens,
    int InputTokens,
    int OutputTokens,
    int ReasoningOutputTokens,
    int TotalTokens,
    int? ModelContextWindow);

/// <summary>
/// Account-level Codex usage and rate-limit windows reported by the local Codex app-server.
/// </summary>
/// <param name="RetrievedAtUtc">UTC time when the usage snapshot was read.</param>
/// <param name="RateLimits">Reported Codex rate-limit buckets.</param>
internal sealed record CodexAccountUsageVm(
    DateTimeOffset RetrievedAtUtc,
    IReadOnlyList<CodexRateLimitSnapshotVm> RateLimits);

/// <summary>
/// One Codex metering bucket containing short and long window usage.
/// </summary>
/// <param name="LimitId">Stable limit identifier, such as <c>codex</c>.</param>
/// <param name="LimitName">Human-readable limit name, when reported.</param>
/// <param name="PlanType">Codex plan type, when reported.</param>
/// <param name="RateLimitReachedType">Reached-limit reason, when Codex reports one.</param>
/// <param name="Primary">Short-window usage, usually the five-hour window.</param>
/// <param name="Secondary">Long-window usage, usually the weekly window.</param>
internal sealed record CodexRateLimitSnapshotVm(
    string? LimitId,
    string? LimitName,
    string? PlanType,
    string? RateLimitReachedType,
    CodexRateLimitWindowVm? Primary,
    CodexRateLimitWindowVm? Secondary);

/// <summary>
/// One Codex rate-limit window.
/// </summary>
/// <param name="UsedPercent">Percentage of the window already consumed.</param>
/// <param name="ResetsAtUtc">UTC reset time, when reported.</param>
/// <param name="WindowDurationMinutes">Window duration in minutes, when reported.</param>
internal sealed record CodexRateLimitWindowVm(
    int UsedPercent,
    DateTimeOffset? ResetsAtUtc,
    long? WindowDurationMinutes);

/// <summary>
/// Timeline entry rendered in Telegram status, tail, and live-output views.
/// </summary>
/// <param name="Type">Codex event type.</param>
/// <param name="Title">Short user-facing title.</param>
/// <param name="Subtitle">Optional subtitle.</param>
/// <param name="Body">Optional body text.</param>
/// <param name="Severity">Display severity.</param>
/// <param name="Timestamp">UTC timestamp for the event.</param>
/// <param name="ThreadId">Associated Codex thread ID.</param>
/// <param name="TurnId">Associated Codex turn ID.</param>
/// <param name="Metadata">Additional event metadata.</param>
/// <param name="IsInternal">Whether the event is internal progress rather than primary assistant output.</param>
internal sealed record CodexTimelineEntryVm(
    string Type,
    string Title,
    string? Subtitle,
    string? Body,
    string Severity,
    DateTimeOffset Timestamp,
    string? ThreadId,
    string? TurnId,
    IReadOnlyDictionary<string, string?> Metadata,
    bool IsInternal);

/// <summary>
/// Codex turn detail with timeline entries and final response data.
/// </summary>
/// <param name="Id">Codex turn ID.</param>
/// <param name="Status">Turn lifecycle status.</param>
/// <param name="ErrorMessage">Turn error message, when failed.</param>
/// <param name="FinalResponse">Final assistant response, when available.</param>
/// <param name="Usage">Token usage summary.</param>
/// <param name="Items">Timeline entries in the turn.</param>
internal sealed record CodexTurnVm(
    string Id,
    string Status,
    string? ErrorMessage,
    string? FinalResponse,
    CodexUsageVm? Usage,
    IReadOnlyList<CodexTimelineEntryVm> Items);

/// <summary>
/// File attached to a Codex thread.
/// </summary>
/// <param name="Id">Codex file ID.</param>
/// <param name="Name">File display name.</param>
/// <param name="Path">Local or Codex file path.</param>
/// <param name="Length">File size in bytes.</param>
/// <param name="ContentType">Media type, when known.</param>
/// <param name="UploadedAt">UTC upload time.</param>
/// <param name="Selected">Whether the file is selected for the current turn.</param>
/// <param name="IsImage">Whether the file is image-like.</param>
internal sealed record CodexThreadFileVm(
    string Id,
    string Name,
    string Path,
    long Length,
    string? ContentType,
    DateTimeOffset UploadedAt,
    bool Selected,
    bool IsImage);

/// <summary>
/// Local workspace entry exposed to Telegram project/session selection.
/// </summary>
/// <param name="Path">Absolute local path.</param>
/// <param name="Name">Display name.</param>
/// <param name="Root">Workspace root containing the path.</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
/// <param name="Length">File size in bytes, when this is a file.</param>
/// <param name="LastModified">Last modified time, when available.</param>
/// <param name="ContentType">Media type, when known.</param>
internal sealed record CodexWorkspaceEntryVm(
    string Path,
    string Name,
    string Root,
    bool IsDirectory,
    long? Length,
    DateTimeOffset? LastModified,
    string? ContentType);

/// <summary>
/// Configured workspace root and its local filesystem status.
/// </summary>
/// <param name="Path">Configured root path.</param>
/// <param name="Exists">Whether the path exists.</param>
/// <param name="IsDirectory">Whether the path is a directory.</param>
/// <param name="Status">Short status code.</param>
/// <param name="Message">Human-readable status explanation.</param>
internal sealed record CodexWorkspaceRootVm(
    string Path,
    bool Exists,
    bool IsDirectory,
    string Status,
    string Message);

/// <summary>
/// Workspace overview used by diagnostics and project setup flows.
/// </summary>
/// <param name="ServerPlatform">Server platform label.</param>
/// <param name="PathFormatHint">Path syntax hint for the current platform.</param>
/// <param name="CurrentWorkingDirectory">Process current working directory.</param>
/// <param name="ConfiguredWorkingDirectory">Configured default working directory.</param>
/// <param name="WorkspaceRoots">Configured workspace roots and their status.</param>
internal sealed record CodexWorkspaceOverviewVm(
    string ServerPlatform,
    string PathFormatHint,
    string CurrentWorkingDirectory,
    string? ConfiguredWorkingDirectory,
    IReadOnlyList<CodexWorkspaceRootVm> WorkspaceRoots);

/// <summary>
/// Result of validating a user-supplied local workspace path.
/// </summary>
/// <param name="InputPath">Original user input.</param>
/// <param name="NormalizedPath">Normalized absolute path, when parseable.</param>
/// <param name="IsValid">Whether the path can be used as a project.</param>
/// <param name="Reason">Stable validation reason code.</param>
/// <param name="Message">Human-readable validation message.</param>
/// <param name="IsAllowlisted">Whether the path is under an allowed workspace root.</param>
/// <param name="Exists">Whether the path exists.</param>
/// <param name="IsDirectory">Whether the path is a directory.</param>
/// <param name="AllowlistedRoot">Workspace root that allowed the path.</param>
/// <param name="AllowedRoots">Configured allowed workspace roots.</param>
/// <param name="PathFormatHint">Path syntax hint for the current platform.</param>
/// <param name="ServerPlatform">Server platform label.</param>
internal sealed record CodexWorkspaceValidationVm(
    string? InputPath,
    string? NormalizedPath,
    bool IsValid,
    string Reason,
    string Message,
    bool IsAllowlisted,
    bool Exists,
    bool IsDirectory,
    string? AllowlistedRoot,
    IReadOnlyList<string> AllowedRoots,
    string PathFormatHint,
    string ServerPlatform);

/// <summary>
/// Codex model option displayed in Telegram model menus.
/// </summary>
/// <param name="Id">Model ID passed back to Codex.</param>
/// <param name="DisplayName">Human-readable model name.</param>
/// <param name="Description">Model description.</param>
/// <param name="DefaultReasoningEffort">Default reasoning effort for the model.</param>
/// <param name="SupportedEfforts">Supported reasoning efforts.</param>
/// <param name="IsDefault">Whether Codex reports this model as default.</param>
/// <param name="Hidden">Whether Codex reports this model as hidden.</param>
/// <param name="SupportsPersonality">Whether the model supports personality settings.</param>
/// <param name="AvailabilityMessage">Availability message, when the model is gated or unavailable.</param>
internal sealed record CodexModelVm(
    string Id,
    string DisplayName,
    string Description,
    CodexReasoningEffort DefaultReasoningEffort,
    IReadOnlyList<CodexReasoningEffort> SupportedEfforts,
    bool IsDefault,
    bool Hidden,
    bool SupportsPersonality,
    string? AvailabilityMessage);

/// <summary>
/// Full Codex thread detail projected into Telegram-friendly view models.
/// </summary>
/// <param name="Summary">Thread summary.</param>
/// <param name="Turns">Thread turns.</param>
/// <param name="Files">Thread files.</param>
/// <param name="WorkspaceEntries">Workspace entries near the thread working directory.</param>
/// <param name="Models">Known Codex models.</param>
/// <param name="Runtime">Codex runtime state.</param>
/// <param name="ActiveTurnId">Active turn ID, when a turn is running.</param>
/// <param name="ThreadModel">Model configured on the thread.</param>
/// <param name="ThreadWorkingDirectory">Working directory configured on the thread.</param>
/// <param name="ThreadBaseInstructions">Base instructions configured on the thread.</param>
/// <param name="ThreadDeveloperInstructions">Developer instructions configured on the thread.</param>
/// <param name="AdditionalDirectories">Additional directories exposed to the thread.</param>
internal sealed record CodexThreadDetailVm(
    CodexThreadListItemVm Summary,
    IReadOnlyList<CodexTurnVm> Turns,
    IReadOnlyList<CodexThreadFileVm> Files,
    IReadOnlyList<CodexWorkspaceEntryVm> WorkspaceEntries,
    IReadOnlyList<CodexModelVm> Models,
    CodexRuntimeStateVm Runtime,
    string? ActiveTurnId,
    string? ThreadModel,
    string? ThreadWorkingDirectory,
    string? ThreadBaseInstructions,
    string? ThreadDeveloperInstructions,
    IReadOnlyList<string> AdditionalDirectories);

/// <summary>
/// Result returned after sending a prompt to Codex.
/// </summary>
/// <param name="ThreadId">Codex thread ID.</param>
/// <param name="TurnId">Codex turn ID.</param>
/// <param name="Status">Turn status.</param>
/// <param name="Message">Optional user-facing message.</param>
internal sealed record CodexThreadExecutionVm(
    string ThreadId,
    string TurnId,
    string Status,
    string? Message);

/// <summary>
/// Tracks the currently active Codex turn for a thread.
/// </summary>
/// <param name="ThreadId">Codex thread ID.</param>
/// <param name="TurnId">Codex turn ID.</param>
/// <param name="StartedAt">UTC time when the active turn was registered.</param>
/// <param name="UpdatedAt">UTC time when the active turn state was last updated.</param>
/// <param name="LastEvent">Most recent timeline event for the active turn.</param>
internal sealed record CodexActiveTurnStateVm(
    string ThreadId,
    string TurnId,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    CodexTimelineEntryVm? LastEvent);

/// <summary>
/// Mutable request payload for creating or updating Codex thread context.
/// </summary>
internal class CodexThreadContextSubmission
{
    /// <summary>
    /// Gets or sets the requested Codex thread name.
    /// </summary>
    public string? ThreadName { get; set; }

    /// <summary>
    /// Gets or sets the local working directory for the thread.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets base instructions for the thread.
    /// </summary>
    public string? BaseInstructions { get; set; }

    /// <summary>
    /// Gets or sets developer instructions for the thread.
    /// </summary>
    public string? DeveloperInstructions { get; set; }

    /// <summary>
    /// Gets or sets the model ID.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets the model provider.
    /// </summary>
    public string? ModelProvider { get; set; }

    /// <summary>
    /// Gets or sets the Codex personality.
    /// </summary>
    public string? Personality { get; set; }

    /// <summary>
    /// Gets or sets the sandbox mode.
    /// </summary>
    public string? Sandbox { get; set; }

    /// <summary>
    /// Gets or sets the service tier.
    /// </summary>
    public string? ServiceTier { get; set; }

    /// <summary>
    /// Gets or sets the approval mode.
    /// </summary>
    public string? ApprovalMode { get; set; }

    /// <summary>
    /// Gets or sets the approval reviewer mode.
    /// </summary>
    public string? ApprovalsReviewer { get; set; }

    /// <summary>
    /// Gets or sets the reasoning effort.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Gets or sets the reasoning summary mode.
    /// </summary>
    public string? ReasoningSummary { get; set; }

    /// <summary>
    /// Gets or sets the web-search mode.
    /// </summary>
    public string? WebSearchMode { get; set; }

    /// <summary>
    /// Gets or sets whether network access is enabled.
    /// </summary>
    public bool? NetworkAccessEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether web search is enabled.
    /// </summary>
    public bool? WebSearchEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether Codex should skip the Git repository check.
    /// </summary>
    public bool? SkipGitRepoCheck { get; set; }

    /// <summary>
    /// Gets or sets whether the thread is ephemeral.
    /// </summary>
    public bool? Ephemeral { get; set; }

    /// <summary>
    /// Gets additional directories exposed to the thread.
    /// </summary>
    public List<string> AdditionalDirectories { get; set; } = [];
}

/// <summary>
/// Request payload for sending a prompt to Codex.
/// </summary>
internal sealed class CodexTurnSubmission : CodexThreadContextSubmission
{
    /// <summary>
    /// Gets or sets the prompt text.
    /// </summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    /// Gets selected Codex file IDs attached to the prompt.
    /// </summary>
    public List<string> SelectedFileIds { get; set; } = [];
}

/// <summary>
/// Query options used when listing Codex threads.
/// </summary>
/// <param name="SearchTerm">Optional text search term.</param>
/// <param name="ArchiveFilter">Archive filter name.</param>
/// <param name="SortKey">Sort key.</param>
/// <param name="Descending">Whether results sort descending.</param>
/// <param name="WorkingDirectory">Optional working-directory filter.</param>
/// <param name="Limit">Maximum result count.</param>
internal sealed record CodexThreadListQuery(
    string? SearchTerm,
    string ArchiveFilter,
    string SortKey,
    bool Descending,
    string? WorkingDirectory,
    int Limit);
