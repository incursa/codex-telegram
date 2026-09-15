using Incursa.Codex.Telegram.Options;
using Telegram.Bot.Types;

namespace Incursa.Codex.Telegram.Configuration;

/// <summary>Canonical app-owned Telegram command definitions.</summary>
internal sealed record TelegramCommandDefinition(
    string Command,
    string Description,
    bool IncludeInPicker = true,
    CodexTelegramMode? Mode = null);

/// <summary>
/// Owns the command definitions used by setup registration and the command
/// help surface. The picker intentionally includes only the everyday subset.
/// </summary>
internal static class TelegramCommandCatalog
{
    private static readonly IReadOnlyList<TelegramCommandDefinition> Definitions =
    [
        Definition("help", "Show supported commands"),
        Definition("home", "Show the bot home and active session"),
        Definition("whoami", "Show Telegram user, chat, and topic IDs"),
        Definition("version", "Show the running app version", includeInPicker: false),
        Definition("trust", "Trust the current group or forum chat"),
        Definition("doctor", "Diagnose authorization, routing, project, session, and queue state"),
        Definition("projects", "List known local projects", mode: CodexTelegramMode.GeneralPurpose),
        Definition("project", "Select, add, or show the current project", mode: CodexTelegramMode.GeneralPurpose),
        Definition("repo", "Show repository status and guidance", mode: CodexTelegramMode.Repository),
        Definition("new", "Create and select a Codex session"),
        Definition("task", "Create or inspect an isolated task workspace"),
        Definition("pair", "Approve or revoke a read-only browser session"),
        Definition("resume", "Resume a Codex session"),
        Definition("sessions", "List active and managed sessions"),
        Definition("use", "Select an existing session"),
        Definition("send", "Send text to the active session"),
        Definition("steer", "Steer the active turn"),
        Definition("queue", "View, edit, send, or delete queued prompts"),
        Definition("status", "Show session status and compact usage"),
        Definition("stop", "Stop the active or selected session"),
        Definition("topic", "Manage forum-topic sessions"),
        Definition("topics", "List topic and session bindings", includeInPicker: false),
        Definition("model", "Show or change model settings", includeInPicker: false),
        Definition("thinking", "Show or change thinking effort", includeInPicker: false),
        Definition("goal", "Show or change the session goal", includeInPicker: false),
        Definition("tail", "Show recent session output", includeInPicker: false),
        Definition("handoff", "Emit a bounded task and review handoff"),
        Definition("usage", "Show five-hour and weekly Codex usage", includeInPicker: false),
        Definition("debug", "Toggle diagnostic message preambles", includeInPicker: false),
        Definition("outbound", "Show outbound Telegram queue status", includeInPicker: false),
        Definition("restart", "Show restart guidance", includeInPicker: false),
    ];

    /// <summary>Gets the canonical definitions, including advanced slash commands.</summary>
    internal static IReadOnlyList<TelegramCommandDefinition> GetDefinitions() => Definitions;

    /// <summary>Gets a canonical description for one parser/help command.</summary>
    internal static string GetDescription(string command)
        => Definitions.FirstOrDefault(definition => definition.Command.Equals(command, StringComparison.OrdinalIgnoreCase))?.Description
            ?? "Run this command";

    /// <summary>Gets a fresh app-owned command list for one setup pass.</summary>
    internal static IReadOnlyList<BotCommand> Commands => CreateDefaultCommands();

    /// <summary>Creates a fresh compact command list for the selected mode.</summary>
    internal static IReadOnlyList<BotCommand> CreateDefaultCommands(CodexTelegramMode mode = CodexTelegramMode.GeneralPurpose)
        => Definitions
            .Where(definition => definition.IncludeInPicker && (definition.Mode is null || definition.Mode == mode))
            .Select(definition => new BotCommand { Command = definition.Command, Description = definition.Description })
            .ToArray();

    /// <summary>Alias retained for setup callers that prefer a catalog verb.</summary>
    internal static IReadOnlyList<BotCommand> GetCommands() => CreateDefaultCommands();

    private static TelegramCommandDefinition Definition(
        string command,
        string description,
        bool includeInPicker = true,
        CodexTelegramMode? mode = null)
        => new(command, description, includeInPicker, mode);
}
