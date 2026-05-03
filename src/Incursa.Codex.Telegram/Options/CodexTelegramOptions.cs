namespace Incursa.Codex.Telegram.Options;

public sealed class CodexTelegramOptions
{
    public bool InitializeOnStart { get; set; } = true;

    public CodexContextOptions Context { get; set; } = new();

    public CodexWorkspaceOptions Workspace { get; set; } = new();
}

public sealed class CodexContextOptions
{
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

public sealed class CodexWorkspaceOptions
{
    public string? DataRoot { get; set; }

    public List<string> WorkspaceRoots { get; set; } = [];

    public int MaxFilesPerThread { get; set; } = 200;

    public int MaxWorkspaceEntries { get; set; } = 200;

    public int WorkspaceSearchDepth { get; set; } = 3;
}
