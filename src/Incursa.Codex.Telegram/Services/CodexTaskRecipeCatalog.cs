using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Immutable snapshot of a configured task recipe. The version is part of the
/// identity so a task records the exact policy selected at creation time.
/// </summary>
internal sealed record CodexTaskRecipeSnapshot(
    string Id,
    string Version,
    string DisplayName,
    string Objective,
    string? BaseInstructions,
    string? DeveloperInstructions,
    string? Model,
    string? ReasoningEffort,
    IReadOnlyList<string> ExpectedOutputs,
    IReadOnlyList<string> RequiredCapabilities)
{
    public string RecipeKey => $"{Id}@{Version}";
}

internal interface ICodexTaskRecipeCatalog
{
    IReadOnlyList<CodexTaskRecipeSnapshot> List();

    CodexTaskRecipeSnapshot? Find(string id);
}

internal sealed class CodexTaskRecipeCatalog : ICodexTaskRecipeCatalog
{
    private static readonly IReadOnlyList<CodexTaskRecipeSnapshot> BuiltIns =
    [
        new(
            "investigate-tests",
            "1",
            "Investigate failing tests",
            "Investigate the failing tests, identify the root cause, and propose or implement a minimal fix with evidence.",
            null,
            "Keep the investigation bounded to the selected workspace. Report the failing test, root cause, changed files, and verification results.",
            null,
            null,
            ["Root cause", "Changed files", "Test command and result"],
            ["codex-execution", "task-workspaces"]),
        new(
            "review-branch",
            "1",
            "Review this branch",
            "Review the selected branch for correctness, regressions, security concerns, and missing tests. Do not modify files unless explicitly asked.",
            null,
            "Prioritize actionable findings with file and line evidence. End with a concise risk summary and verification limits.",
            null,
            null,
            ["Findings", "Risk summary", "Verification limits"],
            ["codex-execution", "task-workspaces"]),
        new(
            "implement-issue",
            "1",
            "Implement an issue",
            "Implement the requested issue in the selected workspace, preserve existing behavior outside the scope, and verify the result.",
            null,
            "State assumptions before editing. Keep the change focused, run the repository's normal tests, and report any unverified external behavior.",
            null,
            null,
            ["Implementation summary", "Tests", "Remaining risks"],
            ["codex-execution", "task-workspaces"]),
    ];

    private readonly IReadOnlyList<CodexTaskRecipeSnapshot> _recipes;

    public CodexTaskRecipeCatalog(IOptions<CodexTelegramOptions> options)
    {
        _recipes = options.Value.Recipes.Count == 0
            ? BuiltIns
            : options.Value.Recipes.Select(ToSnapshot).ToArray();
        EnsureUniqueIds(_recipes);
    }

    public IReadOnlyList<CodexTaskRecipeSnapshot> List() => _recipes;

    public CodexTaskRecipeSnapshot? Find(string id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : _recipes.FirstOrDefault(recipe => recipe.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    private static CodexTaskRecipeSnapshot ToSnapshot(CodexTaskRecipeDefinition definition)
        => new(
            definition.Id.Trim(),
            definition.Version.Trim(),
            definition.DisplayName.Trim(),
            definition.Objective.Trim(),
            Normalize(definition.BaseInstructions),
            Normalize(definition.DeveloperInstructions),
            Normalize(definition.Model),
            Normalize(definition.ReasoningEffort),
            definition.ExpectedOutputs.Select(item => item.Trim()).Where(item => item.Length > 0).ToArray(),
            definition.RequiredCapabilities.Select(item => item.Trim()).Where(item => item.Length > 0).ToArray());

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureUniqueIds(IReadOnlyList<CodexTaskRecipeSnapshot> recipes)
    {
        if (recipes.Select(recipe => recipe.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != recipes.Count)
        {
            throw new InvalidOperationException("CodexTelegram:Recipes must contain unique IDs.");
        }
    }
}
