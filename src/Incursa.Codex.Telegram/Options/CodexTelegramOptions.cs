namespace Incursa.Codex.Telegram.Options;

/// <summary>
/// Default values for local Codex workspace browsing and persistence.
/// </summary>
public static class CodexWorkspaceDefaults
{
    /// <summary>
    /// Default maximum file count retained in one thread manifest.
    /// </summary>
    public const int MaxFilesPerThread = 200;

    /// <summary>
    /// Default maximum workspace entries shown while browsing local folders.
    /// </summary>
    public const int MaxWorkspaceEntries = 200;

    /// <summary>
    /// Default directory traversal depth while browsing workspace roots.
    /// </summary>
    public const int WorkspaceSearchDepth = 3;
}

/// <summary>
/// Root configuration for the local Codex host integration.
/// </summary>
public sealed class CodexTelegramOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the Codex runtime should be initialized during startup.
    /// </summary>
    public bool InitializeOnStart { get; set; } = true;

    /// <summary>
    /// Gets or sets the default Codex session context applied to new sessions.
    /// </summary>
    public CodexContextOptions Context { get; set; } = new();

    /// <summary>
    /// Gets or sets local workspace and state-storage options.
    /// </summary>
    public CodexWorkspaceOptions Workspace { get; set; } = new();
}

/// <summary>
/// Default context values used when creating or continuing Codex sessions.
/// </summary>
public sealed class CodexContextOptions
{
    /// <summary>
    /// Gets or sets the default working directory for new Codex sessions.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets base instructions passed to Codex for new sessions.
    /// </summary>
    public string? BaseInstructions { get; set; }

    /// <summary>
    /// Gets or sets developer instructions passed to Codex for new sessions.
    /// </summary>
    public string? DeveloperInstructions { get; set; }

    /// <summary>
    /// Gets or sets the preferred Codex model ID.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets the preferred model provider, when Codex exposes provider selection.
    /// </summary>
    public string? ModelProvider { get; set; }

    /// <summary>
    /// Gets or sets the optional Codex personality preset.
    /// </summary>
    public string? Personality { get; set; }

    /// <summary>
    /// Gets or sets the Codex sandbox mode override.
    /// </summary>
    public string? Sandbox { get; set; }

    /// <summary>
    /// Gets or sets the Codex service tier override.
    /// </summary>
    public string? ServiceTier { get; set; }

    /// <summary>
    /// Gets or sets the Codex approval mode override.
    /// </summary>
    public string? ApprovalMode { get; set; }

    /// <summary>
    /// Gets or sets the Codex approvals reviewer override.
    /// </summary>
    public string? ApprovalsReviewer { get; set; }

    /// <summary>
    /// Gets or sets the requested Codex reasoning effort.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Gets or sets the requested Codex reasoning summary mode.
    /// </summary>
    public string? ReasoningSummary { get; set; }

    /// <summary>
    /// Gets or sets the requested Codex web-search mode.
    /// </summary>
    public string? WebSearchMode { get; set; }

    /// <summary>
    /// Gets or sets whether Codex network access should be enabled.
    /// </summary>
    public bool? NetworkAccessEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether Codex web search should be enabled.
    /// </summary>
    public bool? WebSearchEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether Codex should skip the Git repository check.
    /// </summary>
    public bool? SkipGitRepoCheck { get; set; }

    /// <summary>
    /// Gets or sets whether new Codex sessions should be ephemeral.
    /// </summary>
    public bool? Ephemeral { get; set; }

    /// <summary>
    /// Gets additional local directories exposed to Codex for new sessions.
    /// </summary>
    public List<string> AdditionalDirectories { get; set; } = [];
}

/// <summary>
/// Configuration for local state storage and workspace allowlisting.
/// </summary>
public sealed class CodexWorkspaceOptions
{
    /// <summary>
    /// Gets or sets the local data root for projects, Telegram state, and thread manifests.
    /// </summary>
    public string? DataRoot { get; set; }

    /// <summary>
    /// Gets the local directory roots that Telegram users may add as projects.
    /// </summary>
    public List<string> WorkspaceRoots { get; set; } = [];

    /// <summary>
    /// Gets or sets the maximum file count retained in one thread manifest.
    /// </summary>
    public int MaxFilesPerThread { get; set; } = CodexWorkspaceDefaults.MaxFilesPerThread;

    /// <summary>
    /// Gets or sets the maximum workspace entry count returned while browsing local folders.
    /// </summary>
    public int MaxWorkspaceEntries { get; set; } = CodexWorkspaceDefaults.MaxWorkspaceEntries;

    /// <summary>
    /// Gets or sets the directory traversal depth used while discovering workspace entries.
    /// </summary>
    public int WorkspaceSearchDepth { get; set; } = CodexWorkspaceDefaults.WorkspaceSearchDepth;
}
