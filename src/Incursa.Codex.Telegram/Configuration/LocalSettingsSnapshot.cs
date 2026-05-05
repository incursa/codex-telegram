namespace Incursa.Codex.Telegram.Configuration;

internal sealed record LocalSettingsSnapshot(
    bool TelegramEnabled,
    bool TelegramTokenConfigured,
    IReadOnlyList<long> AllowedUserIds,
    IReadOnlyList<long> AllowedChatIds,
    bool OpenAiApiKeyConfigured,
    string? OpenAiModel,
    string? OpenAiFfmpegPath,
    int? MinAudioDurationSeconds,
    int? MaxAudioDurationSeconds,
    string? CodexPathOverride,
    string? TelegramBotCodexExecutablePath,
    bool InitializeOnStart,
    string? WorkingDirectory,
    string? DataRoot,
    IReadOnlyList<string> WorkspaceRoots,
    string? CodexModel,
    string? ReasoningEffort,
    string? Sandbox,
    string? ApprovalMode,
    bool? NetworkAccessEnabled);
