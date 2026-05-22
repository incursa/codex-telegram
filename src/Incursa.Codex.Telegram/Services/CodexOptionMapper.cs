using System.Text;
using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;

namespace Incursa.Codex.Telegram.Services;

internal static class CodexOptionMapper
{
    public static CodexThreadListOptions BuildThreadListOptions(CodexThreadListQuery query)
        => new()
        {
            Archived = query.ArchiveFilter.ToLowerInvariant() switch
            {
                "archived" => true,
                "active" => false,
                _ => null,
            },
            Cursor = null,
            WorkingDirectory = NormalizePath(query.WorkingDirectory),
            Limit = query.Limit,
            SearchTerm = string.IsNullOrWhiteSpace(query.SearchTerm) ? null : query.SearchTerm.Trim(),
            SortKey = query.SortKey.Equals("createdAt", StringComparison.OrdinalIgnoreCase)
                ? CodexThreadSortKey.CreatedAt
                : CodexThreadSortKey.UpdatedAt,
        };

    public static CodexThreadOptions BuildThreadOptions(
        CodexTelegramOptions options,
        CodexThreadContextSubmission submission,
        CodexThreadManifestRecord? manifest = null)
    {
        CodexContextOptions defaults = options.Context;
        string? approvalMode = submission.ApprovalMode ?? manifest?.ApprovalMode ?? defaults.ApprovalMode;
        string? sandbox = submission.Sandbox ?? manifest?.Sandbox ?? defaults.Sandbox;
        bool enableDefaultModeRequestUserInput = submission is CodexTurnSubmission { PlanMode: true };
        return new CodexThreadOptions
        {
            Config = BuildConfigOverrides(sandbox, approvalMode, enableDefaultModeRequestUserInput),
            ApprovalsReviewer = ParseEnum<CodexApprovalsReviewer>(submission.ApprovalsReviewer ?? manifest?.ApprovalsReviewer ?? defaults.ApprovalsReviewer),
            BaseInstructions = submission.BaseInstructions ?? manifest?.BaseInstructions ?? defaults.BaseInstructions,
            DeveloperInstructions = submission.DeveloperInstructions ?? manifest?.DeveloperInstructions ?? defaults.DeveloperInstructions,
            Ephemeral = submission.Ephemeral ?? manifest?.Ephemeral ?? defaults.Ephemeral,
            Model = submission.Model ?? manifest?.Model ?? defaults.Model,
            ModelProvider = submission.ModelProvider ?? manifest?.ModelProvider ?? defaults.ModelProvider,
            Personality = ParseEnum<CodexPersonality>(submission.Personality ?? manifest?.Personality ?? defaults.Personality),
            ServiceTier = ParseEnum<CodexServiceTier>(submission.ServiceTier ?? manifest?.ServiceTier ?? defaults.ServiceTier),
            WorkingDirectory = NormalizePath(submission.WorkingDirectory ?? manifest?.WorkingDirectory ?? defaults.WorkingDirectory),
            ModelReasoningEffort = ParseEnum<CodexReasoningEffort>(submission.ReasoningEffort ?? manifest?.ReasoningEffort ?? defaults.ReasoningEffort),
            NetworkAccessEnabled = submission.NetworkAccessEnabled ?? manifest?.NetworkAccessEnabled ?? defaults.NetworkAccessEnabled,
            WebSearchMode = ParseEnum<CodexWebSearchMode>(submission.WebSearchMode ?? manifest?.WebSearchMode ?? defaults.WebSearchMode),
            WebSearchEnabled = submission.WebSearchEnabled ?? manifest?.WebSearchEnabled ?? defaults.WebSearchEnabled,
            SkipGitRepoCheck = submission.SkipGitRepoCheck ?? manifest?.SkipGitRepoCheck ?? defaults.SkipGitRepoCheck,
            AdditionalDirectories = MergeDirectories(defaults.AdditionalDirectories, manifest?.AdditionalDirectories, submission.AdditionalDirectories),
        };
    }

    public static CodexTurnOptions BuildTurnOptions(
        CodexTelegramOptions options,
        CodexTurnSubmission submission,
        CodexThreadManifestRecord? manifest = null)
    {
        CodexContextOptions defaults = options.Context;
        return new CodexTurnOptions
        {
            ApprovalsReviewer = ParseEnum<CodexApprovalsReviewer>(submission.ApprovalsReviewer ?? manifest?.ApprovalsReviewer ?? defaults.ApprovalsReviewer),
            Effort = ParseEnum<CodexReasoningEffort>(submission.ReasoningEffort ?? manifest?.ReasoningEffort ?? defaults.ReasoningEffort),
            Model = submission.Model ?? manifest?.Model ?? defaults.Model,
            Personality = ParseEnum<CodexPersonality>(submission.Personality ?? manifest?.Personality ?? defaults.Personality),
            ServiceTier = ParseEnum<CodexServiceTier>(submission.ServiceTier ?? manifest?.ServiceTier ?? defaults.ServiceTier),
            Summary = ParseEnum<CodexReasoningSummary>(submission.ReasoningSummary ?? manifest?.ReasoningSummary ?? defaults.ReasoningSummary),
            WorkingDirectory = NormalizePath(submission.WorkingDirectory ?? manifest?.WorkingDirectory ?? defaults.WorkingDirectory),
        };
    }

    public static IReadOnlyList<CodexInputItem> BuildInputItems(
        CodexThreadManifestRecord? manifest,
        CodexTurnSubmission submission,
        Func<CodexThreadFileRecord, string> resolveFilePath)
    {
        List<CodexInputItem> items = new();

        string prompt = submission.Prompt?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            items.Add(new CodexTextInput { Text = prompt });
        }

        if (manifest is not null)
        {
            List<CodexThreadFileRecord> selectedFiles = manifest.Files
                .Where(file => file.Selected && (submission.SelectedFileIds.Count == 0 || submission.SelectedFileIds.Contains(file.Id, StringComparer.OrdinalIgnoreCase)))
                .ToList();

            if (selectedFiles.Count > 0)
            {
                items.Add(new CodexTextInput
                {
                    Text = "Staged context files:\n" + string.Join('\n', selectedFiles.Select(file => $"- {file.Name} ({resolveFilePath(file)})")),
                });

                foreach (CodexThreadFileRecord file in selectedFiles)
                {
                    string path = resolveFilePath(file);
                    if (file.IsImage)
                    {
                        items.Add(new CodexLocalImageInput { Path = path });
                    }
                    else
                    {
                        items.Add(new CodexMentionInput
                        {
                            Name = file.Name,
                            Path = path,
                        });
                    }
                }
            }
        }

        if (items.Count == 0)
        {
            items.Add(new CodexTextInput { Text = "Continue the current thread." });
        }

        return items;
    }

    private static CodexConfigObject? BuildConfigOverrides(string? sandbox, string? approvalMode, bool enableDefaultModeRequestUserInput)
    {
        Dictionary<string, CodexConfigValue> values = new(StringComparer.Ordinal);
        if (ParseEnum<CodexSandboxMode>(sandbox) is { } sandboxMode)
        {
            values["sandbox_mode"] = new CodexConfigStringValue(ToKebabCase(sandboxMode));
        }

        if (ParseEnum<CodexApprovalMode>(approvalMode) is { } mode)
        {
            values["approval_policy"] = new CodexConfigStringValue(ToKebabCase(mode));
        }

        if (enableDefaultModeRequestUserInput)
        {
            values["features"] = new CodexConfigObject
            {
                Values = new Dictionary<string, CodexConfigValue>(StringComparer.Ordinal)
                {
                    ["default_mode_request_user_input"] = new CodexConfigBooleanValue(true),
                },
            };
        }

        return values.Count == 0
            ? null
            : new CodexConfigObject
            {
                Values = values,
            };
    }

    private static string ToKebabCase<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        string name = value.ToString();
        StringBuilder builder = new();
        for (int index = 0; index < name.Length; index++)
        {
            char character = name[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> MergeDirectories(
        IEnumerable<string>? defaults,
        IEnumerable<string>? manifest,
        IEnumerable<string>? submission)
    {
        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        AddRange(defaults);
        AddRange(manifest);
        AddRange(submission);
        return directories.ToArray();

        void AddRange(IEnumerable<string>? values)
        {
            foreach (string? value in values ?? [])
            {
                string? normalized = NormalizePath(value);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    directories.Add(normalized);
                }
            }
        }
    }

    private static string? NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static TEnum? ParseEnum<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse(value, ignoreCase: true, out TEnum parsed))
        {
            return parsed;
        }

        string normalized = NormalizeEnumToken(value);
        foreach (TEnum candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(NormalizeEnumToken(candidate.ToString()), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string NormalizeEnumToken(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
