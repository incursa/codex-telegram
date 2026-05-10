using Telegram.Bot.Types;

namespace Incursa.Codex.Telegram.Telegram;

internal sealed record TelegramBotCommandDefinition(string Name, string Description);

internal static class TelegramBotMetadata
{
    internal static readonly IReadOnlyList<TelegramBotCommandDefinition> PublicCommands =
    [
        new("help", "show this help"),
        new("whoami", "show Telegram user, chat, and topic thread IDs"),
        new("version", "show the running Codex Telegram app version"),
        new("trust", "trust the current group or forum chat for allowlisted users"),
        new("projects", "list known project directories"),
        new("project", "add or select a project"),
        new("topics", "list Telegram topics/sessions in this chat"),
        new("topic", "manage forum-topic sessions"),
        new("launchpad", "arm or disarm root-chat launch mode for plain-text and audio launches"),
        new("launch", "create a detached git worktree-backed forum topic and session while launchpad is armed"),
        new("sessions", "show active and Telegram-managed sessions"),
        new("new", "create and select a Codex session in the active project"),
        new("use", "select the active session for this conversation"),
        new("send", "send text to the active session"),
        new("steer", "steer the active turn in the selected session"),
        new("queue", "view, edit, delete, or send queued prompts now"),
        new("model", "show or change the selected session model"),
        new("thinking", "change the selected session thinking effort"),
        new("goal", "show or change the selected session goal"),
        new("tail", "show recent output and keep following the session live"),
        new("status", "show session status"),
        new("usage", "show Codex account usage remaining and reset times"),
        new("doctor", "explain authorization, routing, active project/session, workspace roots, and queue state"),
        new("outbound", "show outbound Telegram queue status"),
        new("stop", "gracefully stop a session"),
        new("restart", "show restart guidance"),
    ];

    internal static string Description
        => "Controls a local Codex installation from Telegram and routes prompts to sessions.";

    internal static string ShortDescription
        => "Control local Codex sessions from Telegram.";

    internal static ChatAdministratorRights DefaultGroupAdministratorRights
        => new()
        {
            CanManageTopics = true,
        };

    internal static IReadOnlyList<global::Telegram.Bot.Types.BotCommand> BuildPublicBotCommands()
        => PublicCommands.Select(command => new global::Telegram.Bot.Types.BotCommand(command.Name, command.Description)).ToArray();

    internal static IReadOnlyList<string> BuildHelpLines()
        => [
            "Commands:",
            "Use the buttons below for quick navigation between sessions, projects, and help.",
            ..PublicCommands.Select(command => $"/{command.Name} - {command.Description}"),
            "/project add <path> - add and select a project",
            "/project <number|name|path> - select a project",
            "/project current - show the active project for the current conversation",
            "/topic list - list Telegram topics/sessions in this chat",
            "/topic new <name> [| <absolute directory path>] - create a new Telegram forum topic and session in a forum-enabled supergroup",
            "/topic attach [sessionId] - bind the current Telegram forum topic to an existing Codex session",
            "/topic current - show the active topic/session in this conversation",
            "/launchpad on|off|status - arm or disarm root-chat launch mode for repeated plain-text and audio launches",
            "/launch <name> [| <absolute directory path>] - create a detached git worktree-backed forum topic and session while launchpad is armed",
            "/sessions all [count] - show recent Codex history",
            "/model [model] [thinking <effort>] - show or change the selected session model",
            "/thinking <minimal|low|medium|high|xhigh> - change the selected session thinking effort",
            "/goal [objective|set <objective>|clear|pause|resume|complete] - show or change the selected session goal",
            "/tail [count] - show recent output and keep following the session live",
            "/status [sessionId] - show session status",
            "/queue - view, edit, delete, or send queued prompts now",
            "/usage - show Codex account usage remaining and reset times",
            "/doctor - explain authorization, routing, active project/session, workspace roots, and queue state",
            "/outbound - show outbound Telegram queue status",
            "/stop [sessionId] - gracefully stop a session",
            "/restart confirm - explain how to restart this standalone process",
            "/kill <sessionId> confirm - hard-stop a session",
            "/rename <sessionId> <new name> - rename a session",
            "/forget <sessionId> - hide a stopped/exited session without deleting logs",
            "Plain text and audio in a private chat, trusted group, or topic stay on that conversation's session; if the conversation has none yet, the first message starts one and live output follows automatically.",
            "In forum topics, if plain text gets no response, Telegram bot privacy is likely hiding non-command messages; use /send <text> or disable privacy for this bot.",
            "Launchpad mode arms a root supergroup for repeated plain-text or audio launch messages that create detached git worktree-backed forum-topic sessions and seed the new session. It expires after 10 minutes of inactivity.",
            "Images, documents, and other attachments are forwarded to Codex; voice notes are transcribed with the configured OpenAI transcription model first.",
            "Voice/text control phrase: Codex settings model gpt-5.4-mini thinking high: <prompt>",
        ];
}
