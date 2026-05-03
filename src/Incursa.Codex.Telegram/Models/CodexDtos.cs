using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Models;

public sealed record CodexRuntimeStateVm(
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
    bool SupportsListModels);

public sealed record CodexThreadListItemVm(
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

public sealed record CodexThreadProjectGroupVm(
    string DisplayName,
    string? WorkingDirectory,
    IReadOnlyList<CodexThreadListItemVm> Threads);

public sealed record CodexProjectCatalogEntryVm(
    string WorkingDirectory,
    string DisplayName,
    DateTimeOffset AddedAt);

public sealed record CodexUsageVm(
    int CachedInputTokens,
    int InputTokens,
    int OutputTokens,
    int ReasoningOutputTokens,
    int TotalTokens,
    int? ModelContextWindow);

public sealed record CodexTimelineEntryVm(
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

public sealed record CodexTurnVm(
    string Id,
    string Status,
    string? ErrorMessage,
    string? FinalResponse,
    CodexUsageVm? Usage,
    IReadOnlyList<CodexTimelineEntryVm> Items);

public sealed record CodexThreadFileVm(
    string Id,
    string Name,
    string Path,
    long Length,
    string? ContentType,
    DateTimeOffset UploadedAt,
    bool Selected,
    bool IsImage);

public sealed record CodexWorkspaceEntryVm(
    string Path,
    string Name,
    string Root,
    bool IsDirectory,
    long? Length,
    DateTimeOffset? LastModified,
    string? ContentType);

public sealed record CodexWorkspaceRootVm(
    string Path,
    bool Exists,
    bool IsDirectory,
    string Status,
    string Message);

public sealed record CodexWorkspaceOverviewVm(
    string ServerPlatform,
    string PathFormatHint,
    string CurrentWorkingDirectory,
    string? ConfiguredWorkingDirectory,
    IReadOnlyList<CodexWorkspaceRootVm> WorkspaceRoots);

public sealed record CodexWorkspaceValidationVm(
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

public sealed record CodexModelVm(
    string Id,
    string DisplayName,
    string Description,
    CodexReasoningEffort DefaultReasoningEffort,
    IReadOnlyList<CodexReasoningEffort> SupportedEfforts,
    bool IsDefault,
    bool Hidden,
    bool SupportsPersonality,
    string? AvailabilityMessage);

public sealed record CodexThreadDetailVm(
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

public sealed record CodexThreadExecutionVm(
    string ThreadId,
    string TurnId,
    string Status,
    string? Message);

public sealed record CodexActiveTurnStateVm(
    string ThreadId,
    string TurnId,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    CodexTimelineEntryVm? LastEvent);

public class CodexThreadContextSubmission
{
    public string? ThreadName { get; set; }

    public string? WorkingDirectory { get; set; }

    public string? BaseInstructions { get; set; }

    public string? DeveloperInstructions { get; set; }

    public string? Model { get; set; }

    public string? ModelProvider { get; set; }

    public string? Personality { get; set; }

    public string? Sandbox { get; set; }

    public string? ServiceTier { get; set; }

    public string? ApprovalMode { get; set; }

    public string? ApprovalsReviewer { get; set; }

    public string? ReasoningEffort { get; set; }

    public string? ReasoningSummary { get; set; }

    public string? WebSearchMode { get; set; }

    public bool? NetworkAccessEnabled { get; set; }

    public bool? WebSearchEnabled { get; set; }

    public bool? SkipGitRepoCheck { get; set; }

    public bool? Ephemeral { get; set; }

    public List<string> AdditionalDirectories { get; set; } = [];
}

public sealed class CodexTurnSubmission : CodexThreadContextSubmission
{
    public string Prompt { get; set; } = string.Empty;

    public List<string> SelectedFileIds { get; set; } = [];
}

public sealed record CodexThreadListQuery(
    string? SearchTerm,
    string ArchiveFilter,
    string SortKey,
    bool Descending,
    string? WorkingDirectory,
    int Limit);
