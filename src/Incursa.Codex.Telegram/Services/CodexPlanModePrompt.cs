namespace Incursa.Codex.Telegram.Services;

internal static class CodexPlanModePrompt
{
    public static string Wrap(string input)
        => string.Join(
            Environment.NewLine,
            [
                "Plan mode request:",
                "Work in planning and clarification mode. Do not make code changes or run destructive commands unless the operator explicitly asks you to proceed. Ask concise clarification questions when needed, using request_user_input if available. Produce a concrete plan and wait for operator direction before implementation.",
                string.Empty,
                "Operator request:",
                input.Trim(),
            ]);
}
