namespace Incursa.Codex.Telegram.Options;

/// <summary>
/// Defines the host's workspace operating mode.
/// </summary>
public enum CodexTelegramMode
{
    /// <summary>
    /// Use the existing general-purpose workspace behavior.
    /// </summary>
    GeneralPurpose,

    /// <summary>
    /// Pin the host to one explicitly configured repository root.
    /// </summary>
    Repository,
}

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
/// Default timing values used while streaming Codex turn output.
/// </summary>
public static class CodexTurnStreamingDefaults
{
    /// <summary>
    /// Default grace period for delaying terminal turn publication while late events arrive.
    /// </summary>
    public const int TerminalEventHoldMilliseconds = 3000;

    /// <summary>
    /// Minimum supported grace period for delaying terminal turn publication.
    /// </summary>
    public const int MinTerminalEventHoldMilliseconds = 0;

    /// <summary>
    /// Maximum supported grace period for delaying terminal turn publication.
    /// </summary>
    public const int MaxTerminalEventHoldMilliseconds = 30000;
}

/// <summary>
/// Root configuration for the local Codex host integration.
/// </summary>
public sealed class CodexTelegramOptions
{
    /// <summary>
    /// Gets or sets the workspace operating mode.
    /// </summary>
    public CodexTelegramMode Mode { get; set; } = CodexTelegramMode.GeneralPurpose;

    /// <summary>
    /// Gets or sets the repository root used by <see cref="CodexTelegramMode.Repository"/>.
    /// </summary>
    public string? RepositoryRoot { get; set; }

    /// <summary>
    /// Gets or sets the optional operator-facing repository label.
    /// </summary>
    public string? RepositoryDisplayLabel { get; set; }

    /// <summary>
    /// Gets or sets an optional instance identifier used to partition the default local data root.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Codex runtime should be initialized during startup.
    /// </summary>
    public bool InitializeOnStart { get; set; } = true;

    /// <summary>
    /// Gets or sets the default Codex session context applied to new sessions.
    /// </summary>
    public CodexContextOptions Context { get; set; } = new();

    /// <summary>
    /// Gets or sets how long Codex terminal turn events may be held before the bot publishes them.
    /// </summary>
    public int TerminalEventHoldMilliseconds { get; set; } = CodexTurnStreamingDefaults.TerminalEventHoldMilliseconds;

    /// <summary>
    /// Gets or sets local workspace and state-storage options.
    /// </summary>
    public CodexWorkspaceOptions Workspace { get; set; } = new();

    /// <summary>
    /// Gets or sets the local worker registration and scheduling options.
    /// </summary>
    public CodexWorkerOptions Worker { get; set; } = new();

    /// <summary>
    /// Gets the operator-defined task recipes. Empty configuration uses the built-in recipes.
    /// </summary>
    public List<CodexTaskRecipeDefinition> Recipes { get; set; } = [];

    /// <summary>
    /// Gets or sets the optional authenticated coordinator control-plane options.
    /// </summary>
    public CodexCoordinatorOptions Coordinator { get; set; } = new();

    /// <summary>
    /// Gets or sets the operator-controlled worker update options.
    /// </summary>
    public CodexWorkerUpdateOptions Updates { get; set; } = new();
}

/// <summary>
/// Identity and bounded lease options for the local Codex worker.
/// </summary>
public sealed class CodexWorkerOptions
{
    /// <summary>
    /// Gets or sets an optional stable worker identifier. When empty, one is generated and persisted locally.
    /// </summary>
    public string? WorkerId { get; set; }

    /// <summary>
    /// Gets or sets the operator-facing worker name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of concurrent task leases this worker may hold.
    /// </summary>
    public int MaxConcurrentTasks { get; set; } = 8;

    /// <summary>
    /// Gets or sets the private HTTP(S) endpoint that an authenticated coordinator may use for lease handoff.
    /// </summary>
    public string? ControlPlaneUrl { get; set; }
}

/// <summary>
/// Declarative, inspectable policy for creating a Codex task session.
/// </summary>
public sealed class CodexTaskRecipeDefinition
{
    /// <summary>Gets or sets the stable recipe identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the immutable policy version.</summary>
    public string Version { get; set; } = "1";

    /// <summary>Gets or sets the operator-facing recipe name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the bounded task objective shown to the operator.</summary>
    public string Objective { get; set; } = string.Empty;

    /// <summary>Gets or sets optional Codex base instructions.</summary>
    public string? BaseInstructions { get; set; }

    /// <summary>Gets or sets optional Codex developer instructions.</summary>
    public string? DeveloperInstructions { get; set; }

    /// <summary>Gets or sets an optional Codex model override.</summary>
    public string? Model { get; set; }

    /// <summary>Gets or sets an optional Codex reasoning-effort override.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Gets the operator-visible outputs expected from the recipe.</summary>
    public List<string> ExpectedOutputs { get; set; } = [];

    /// <summary>Gets the worker capabilities required by the recipe.</summary>
    public List<string> RequiredCapabilities { get; set; } = [];
}

/// <summary>
/// Controls optional worker heartbeat registration with a self-hosted coordinator.
/// </summary>
public sealed class CodexCoordinatorOptions
{
    /// <summary>Gets or sets whether this host accepts authenticated worker heartbeats.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets whether this host sends its worker heartbeat to a coordinator.</summary>
    public bool WorkerRegistrationEnabled { get; set; }

    /// <summary>Gets or sets the coordinator base URL used for worker heartbeats.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets the shared private bearer token for this self-hosted control plane.</summary>
    public string? AuthenticationToken { get; set; }

    /// <summary>Gets or sets the heartbeat interval in seconds.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 15;

    /// <summary>Gets or sets the coordinator request timeout in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>Gets or sets the maximum number of registered remote workers.</summary>
    public int MaximumRegisteredWorkers { get; set; } = 32;

    /// <summary>Gets or sets the worker IDs permitted to register; empty permits any holder of the token.</summary>
    public List<string> AllowedWorkerIds { get; set; } = [];
}

/// <summary>
/// Describes a package that may be staged for an explicitly controlled worker update.
/// </summary>
public sealed class CodexWorkerUpdateOptions
{
    /// <summary>Gets or sets whether worker update staging is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the exact local package path supplied by the operator.</summary>
    public string? PackagePath { get; set; }

    /// <summary>Gets or sets the expected release version of the package.</summary>
    public string? TargetVersion { get; set; }

    /// <summary>Gets or sets the expected SHA-256 digest of the package.</summary>
    public string? ExpectedSha256 { get; set; }

    /// <summary>Gets or sets the operator-owned staging directory.</summary>
    public string? StageRoot { get; set; }

    /// <summary>Gets or sets the private token used by the external installer to report post-install health.</summary>
    public string? InstallerAuthenticationToken { get; set; }

    /// <summary>Gets the worker capabilities required before staging is allowed.</summary>
    public List<string> RequiredCapabilities { get; set; } = [];
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

    /// <summary>
    /// Gets or sets the operator-owned root where task worktrees are provisioned.
    /// When empty, a <c>task-workspaces</c> directory below <see cref="DataRoot"/> is used.
    /// </summary>
    public string? TaskWorktreeRoot { get; set; }

    /// <summary>
    /// Gets or sets the first TCP port reserved for task development environments.
    /// </summary>
    public int TaskDevelopmentPortRangeStart { get; set; } = 43000;

    /// <summary>
    /// Gets or sets the inclusive final TCP port reserved for task development environments.
    /// </summary>
    public int TaskDevelopmentPortRangeEnd { get; set; } = 43999;
}
