namespace Incursa.Codex.Telegram.Configuration;

/// <summary>
/// Read-only view of the local settings file used by the interactive bootstrap menu.
/// </summary>
/// <param name="TelegramEnabled">Whether Telegram polling is enabled.</param>
/// <param name="TelegramTokenConfigured">Whether a Telegram bot token is configured.</param>
/// <param name="AllowedUserIds">Allowlisted Telegram user IDs.</param>
/// <param name="AllowedChatIds">Allowlisted Telegram group or supergroup chat IDs.</param>
/// <param name="OpenAiApiKeyConfigured">Whether an OpenAI API key is configured.</param>
/// <param name="OpenAiModel">Configured OpenAI transcription model.</param>
/// <param name="OpenAiFfmpegPath">Configured ffmpeg executable path.</param>
/// <param name="MinAudioDurationSeconds">Minimum accepted Telegram audio duration.</param>
/// <param name="MaxAudioDurationSeconds">Maximum accepted Telegram audio duration.</param>
/// <param name="CodexPathOverride">Configured Codex executable override from the Codex section.</param>
/// <param name="TelegramBotCodexExecutablePath">Legacy Codex executable override from the Telegram section.</param>
/// <param name="InitializeOnStart">Whether Codex runtime warm-up is enabled at startup.</param>
/// <param name="WorkingDirectory">Default Codex working directory.</param>
/// <param name="DataRoot">Local state data root.</param>
/// <param name="WorkspaceRoots">Configured project allowlist roots.</param>
/// <param name="CodexModel">Default Codex model.</param>
/// <param name="ReasoningEffort">Default Codex reasoning effort.</param>
/// <param name="PlanModeReasoningEffort">Default Codex Plan mode reasoning effort.</param>
/// <param name="Sandbox">Default Codex sandbox mode.</param>
/// <param name="ApprovalMode">Default Codex approval mode.</param>
/// <param name="NetworkAccessEnabled">Default Codex network-access setting.</param>
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
    string? PlanModeReasoningEffort,
    string? Sandbox,
    string? ApprovalMode,
    bool? NetworkAccessEnabled);
