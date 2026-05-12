using System.Text.Json.Nodes;
using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;

namespace Incursa.Codex.Telegram.Services;

internal static class CodexViewModelMapper
{
    public static CodexThreadListItemVm ToThreadListItemVm(
        CodexThreadSummary summary,
        CodexThreadManifestRecord? manifest)
        => new(
            summary.Id,
            CodexTextFormatting.ResolveDisplayName(manifest?.ThreadName, RepairTextOrNull(summary.Name)),
            CodexTextFormatting.TruncatePreview(RepairTextOrNull(summary.Preview)),
            DescribeThreadStatus(summary.Status),
            RepairText(summary.ModelProvider),
            summary.CreatedAt,
            summary.UpdatedAt,
            summary.Ephemeral,
            RepairTextOrNull(summary.Path),
            RepairTextOrNull(summary.AgentRole),
            RepairTextOrNull(summary.AgentNickname),
            RepairTextOrNull(summary.GitInfo?.Branch),
            RepairTextOrNull(summary.GitInfo?.Sha),
            manifest?.IsArchived == true,
            manifest?.WorkingDirectory);

    public static CodexProjectCatalogEntryVm ToProjectCatalogEntryVm(CodexProjectCatalogRecord project)
        => new(
            project.WorkingDirectory,
            CodexTextFormatting.ResolveProjectName(project.WorkingDirectory),
            project.AddedAt);

    public static CodexTurnVm ToTurnVm(CodexTurnRecord turn)
        => new(
            turn.Id,
            turn.Status.ToString(),
            RepairTextOrNull(turn.Error?.Message),
            SelectFinalResponse(turn.Items),
            turn.Usage is null ? null : ToUsageVm(turn.Usage),
            turn.Items.Select(ToTurnItemVm).ToArray());

    public static CodexTimelineEntryVm ToTimelineEntryVm(CodexThreadEvent evt, string? fallbackThreadId = null)
    {
        return evt switch
        {
            CodexThreadStartedEvent started => new CodexTimelineEntryVm(
                evt.Type,
                "Thread started",
                CodexTextFormatting.ResolveDisplayName(RepairTextOrNull(started.Thread.Name), started.Thread.Id),
                CodexTextFormatting.TruncatePreview(RepairTextOrNull(started.Thread.Preview)),
                "success",
                DateTimeOffset.UtcNow,
                ResolveThreadId(started.Thread.Id, fallbackThreadId),
                null,
                new Dictionary<string, string?>
                {
                    ["modelProvider"] = RepairTextOrNull(started.Thread.ModelProvider),
                    ["path"] = RepairTextOrNull(started.Thread.Path),
                },
                false),
            CodexTurnStartedEvent startedTurn => new CodexTimelineEntryVm(
                evt.Type,
                "Turn started",
                startedTurn.Turn.Id,
                startedTurn.Turn.Status.ToString(),
                "info",
                DateTimeOffset.UtcNow,
                ResolveThreadId(null, fallbackThreadId),
                startedTurn.Turn.Id,
                CreateMetadata("status", startedTurn.Turn.Status.ToString()),
                true),
            CodexTurnCompletedEvent completedTurn => ToTurnTerminalEventVm(evt.Type, "Turn completed", completedTurn.Turn, "success", fallbackThreadId),
            CodexTurnFailedEvent failedTurn => ToTurnTerminalEventVm(evt.Type, "Turn failed", failedTurn.Turn, "danger", fallbackThreadId),
            CodexItemStartedEvent startedItem => ToItemEventVm(evt.Type, "Item started", ResolveThreadId(startedItem.ThreadId, fallbackThreadId), startedItem.TurnId, startedItem.Item, "info"),
            CodexItemUpdatedEvent updatedItem => ToItemEventVm(evt.Type, "Item updated", ResolveThreadId(updatedItem.ThreadId, fallbackThreadId), updatedItem.TurnId, updatedItem.Item, "info"),
            CodexItemCompletedEvent completedItem => ToItemEventVm(evt.Type, "Item completed", ResolveThreadId(completedItem.ThreadId, fallbackThreadId), completedItem.TurnId, completedItem.Item, "success"),
            CodexThreadErrorEvent threadError => new CodexTimelineEntryVm(
                evt.Type,
                "Thread error",
                RepairTextOrNull(threadError.Error.Message),
                threadError.WillRetry ? "Will retry." : null,
                "danger",
                DateTimeOffset.UtcNow,
                ResolveThreadId(threadError.ThreadId, fallbackThreadId),
                threadError.TurnId,
                CreateMetadata("willRetry", threadError.WillRetry.ToString()),
                false),
            CodexUnknownThreadEvent unknown => ToUnknownThreadEventVm(unknown, fallbackThreadId),
            _ => ToInternalEventVm(evt.Type, fallbackThreadId, null),
        };
    }

    public static CodexTimelineEntryVm ToTurnItemVm(CodexThreadItem item)
        => new(
            item.Type,
            DescribeItem(item),
            null,
            null,
            "neutral",
            DateTimeOffset.UtcNow,
            null,
            item.Id,
            DescribeItemMetadata(item),
            IsInternalItem(item));

    public static CodexModelVm ToModelVm(CodexModel model)
    {
        string? displayName = RepairTextOrNull(model.DisplayName);
        return new CodexModelVm(
            model.Model,
            string.IsNullOrWhiteSpace(displayName) ? model.Model : displayName,
            RepairTextOrNull(model.Description) ?? string.Empty,
            model.DefaultReasoningEffort,
            model.SupportedReasoningEfforts.Select(option => option.ReasoningEffort).ToArray(),
            model.IsDefault,
            model.Hidden,
            model.SupportsPersonality == true,
            RepairTextOrNull(model.AvailabilityNux?.Message));
    }

    public static CodexThreadFileVm ToThreadFileVm(CodexThreadFileRecord file, string threadRoot)
        => new(
            file.Id,
            file.Name,
            Path.GetFullPath(Path.Combine(threadRoot, file.RelativePath)),
            file.Length,
            file.ContentType,
            file.UploadedAt,
            file.Selected,
            file.IsImage);

    public static CodexWorkspaceEntryVm ToWorkspaceEntryVm(string root, string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
        FileInfo? info = isDirectory ? null : new FileInfo(path);
        string? contentType = null;
        if (!isDirectory)
        {
            contentType = GetContentType(path);
        }

        return new CodexWorkspaceEntryVm(
            path,
            Path.GetFileName(path),
            root,
            isDirectory,
            info?.Length,
            info?.LastWriteTimeUtc,
            contentType);
    }

    public static CodexUsageVm ToUsageVm(CodexUsage usage)
        => new(
            usage.Total.CachedInputTokens,
            usage.Total.InputTokens,
            usage.Total.OutputTokens,
            usage.Total.ReasoningOutputTokens,
            usage.Total.TotalTokens,
            usage.ModelContextWindow);

    public static CodexThreadGoalVm ToThreadGoalVm(CodexThreadGoal goal)
        => new(
            goal.ThreadId,
            RepairText(goal.Objective),
            goal.Status,
            goal.TokenBudget,
            goal.TokensUsed,
            goal.TimeUsedSeconds,
            goal.CreatedAt,
            goal.UpdatedAt);

    public static CodexRuntimeStateVm ToRuntimeVm(CodexRuntimeState runtimeState)
        => runtimeState.ToViewModel();

    public static CodexThreadDetailVm ToThreadDetailVm(
        CodexThreadSnapshot snapshot,
        CodexThreadManifestRecord manifest,
        IReadOnlyList<CodexThreadFileVm> files,
        IReadOnlyList<CodexModelVm> models,
        IReadOnlyList<CodexWorkspaceEntryVm> workspaceEntries,
        CodexRuntimeStateVm runtime,
        string? activeTurnId)
        => new(
            ToThreadListItemVm(snapshot, manifest),
            snapshot.Turns.Select(ToTurnVm).ToArray(),
            files,
            workspaceEntries,
            models,
            runtime,
            activeTurnId,
            manifest.Model,
            manifest.WorkingDirectory,
            manifest.BaseInstructions,
            manifest.DeveloperInstructions,
            manifest.AdditionalDirectories.ToArray());

    private static string? SelectFinalResponse(IReadOnlyList<CodexThreadItem> items)
    {
        CodexAgentMessageItem? finalAnswer = items
            .OfType<CodexAgentMessageItem>()
            .LastOrDefault(item => item.Phase == CodexMessagePhase.FinalAnswer && !string.IsNullOrWhiteSpace(item.Text));

        if (finalAnswer is not null)
        {
            return RepairTextOrNull(finalAnswer.Text);
        }

        CodexAgentMessageItem? phaseLess = items
            .OfType<CodexAgentMessageItem>()
            .LastOrDefault(item => item.Phase is null && !string.IsNullOrWhiteSpace(item.Text));

        return RepairTextOrNull(phaseLess?.Text);
    }

    private static CodexTimelineEntryVm ToTurnTerminalEventVm(
        string type,
        string title,
        CodexTurnRecord turn,
        string severity,
        string? threadId)
    {
        string? body = RepairTextOrNull(turn.Error?.Message);
        if (string.IsNullOrWhiteSpace(body))
        {
            body = SelectFinalResponse(turn.Items);
        }

        return new CodexTimelineEntryVm(
            type,
            title,
            turn.Id,
            body,
            severity,
            DateTimeOffset.UtcNow,
            ResolveThreadId(threadId, null),
            turn.Id,
            new Dictionary<string, string?>
            {
                ["status"] = turn.Status.ToString(),
                ["usage"] = turn.Usage?.Total.TotalTokens.ToString(),
            },
            false);
    }

    private static CodexTimelineEntryVm ToItemEventVm(
        string type,
        string title,
        string? threadId,
        string turnId,
        CodexThreadItem item,
        string severity)
        => new(
            type,
            title,
            DescribeItem(item),
            null,
            severity,
            DateTimeOffset.UtcNow,
            threadId,
            turnId,
            DescribeItemMetadata(item),
            IsInternalItem(item));

    private static CodexTimelineEntryVm ToUnknownThreadEventVm(CodexUnknownThreadEvent evt, string? fallbackThreadId)
    {
        string? threadId = ResolveThreadId(GetString(evt.RawPayload, "threadId"), fallbackThreadId);
        string? turnId = GetString(evt.RawPayload, "turnId");

        if (string.Equals(evt.UnknownType, "item.agentMessage.delta", StringComparison.OrdinalIgnoreCase))
        {
            return new CodexTimelineEntryVm(
                evt.Type,
                "Assistant response delta",
                null,
                RepairTextOrNull(ExtractAgentMessageDelta(evt.RawPayload)),
                "neutral",
                DateTimeOffset.UtcNow,
                threadId,
                turnId,
                new Dictionary<string, string?>(),
                true);
        }

        return ToInternalEventVm(evt.Type, threadId, turnId);
    }

    private static CodexTimelineEntryVm ToInternalEventVm(string type, string? threadId, string? turnId)
        => new(
            type,
            type,
            null,
            null,
            "neutral",
            DateTimeOffset.UtcNow,
            threadId,
            turnId,
            new Dictionary<string, string?>(),
            true);

    private static string DescribeThreadStatus(CodexThreadStatus status)
        => status switch
        {
            CodexActiveThreadStatus active when active.ActiveFlags.Count > 0 => $"active ({string.Join(", ", active.ActiveFlags)})",
            CodexActiveThreadStatus => "active",
            CodexIdleThreadStatus => "idle",
            CodexSystemErrorThreadStatus => "system error",
            CodexNotLoadedThreadStatus => "not loaded",
            _ => status.Type,
        };

    private static string DescribeItem(CodexThreadItem item)
        => item switch
        {
            CodexUserMessageItem userMessage => Truncate(string.Join(' ', userMessage.Content.OfType<CodexTextInput>().Select(content => RepairText(content.Text)))),
            CodexAgentMessageItem agentMessage => Truncate(RepairText(agentMessage.Text)),
            CodexPlanItem plan => Truncate(RepairText(plan.Text)),
            CodexReasoningItem reasoning => reasoning.Summary is { Count: > 0 } ? Truncate(string.Join(' ', reasoning.Summary.Select(RepairText))) : $"Reasoning ({reasoning.Content?.Count ?? 0} items)",
            CodexCommandExecutionItem command => $"{RepairText(command.Command)} [{command.Status}]",
            CodexFileChangeItem fileChange => $"{fileChange.Changes.Count} file changes [{fileChange.Status}]",
            CodexMcpToolCallItem mcp => $"{RepairText(mcp.Server)}/{RepairText(mcp.Tool)} [{mcp.Status}]",
            CodexDynamicToolCallItem dynamicToolCall => $"{RepairText(dynamicToolCall.Tool)} [{dynamicToolCall.Status}]",
            CodexCollabAgentToolCallItem collab => $"{RepairText(collab.Tool.ToString())} [{collab.Status}]",
            CodexWebSearchItem webSearch => RepairText(webSearch.Query),
            CodexImageViewItem imageView => RepairText(imageView.Path),
            CodexImageGenerationItem imageGeneration => RepairText(imageGeneration.Status),
            CodexUnknownThreadItem unknown when IsUnknownImageViewItem(unknown) => RepairText(GetStringAny(unknown.RawPayload, "path", "url", "imageUrl", "image_url", "filePath", "file_path")),
            CodexUnknownThreadItem unknown when IsUnknownImageGenerationItem(unknown) => RepairText(GetStringAny(unknown.RawPayload, "status", "result")),
            CodexEnteredReviewModeItem enteredReview => RepairText(enteredReview.Review),
            CodexExitedReviewModeItem exitedReview => RepairText(exitedReview.Review),
            CodexTodoListItem todo => $"{todo.Items.Count} todo items",
            CodexErrorItem error => RepairText(error.Message),
            CodexContextCompactionItem => "Context compaction",
            _ => item.Type,
        };

    private static IReadOnlyDictionary<string, string?> DescribeItemMetadata(CodexThreadItem item)
        => item switch
        {
            CodexCommandExecutionItem command => new Dictionary<string, string?>
            {
                ["command"] = RepairTextOrNull(command.Command),
                ["cwd"] = RepairTextOrNull(command.Cwd),
                ["exitCode"] = command.ExitCode?.ToString(),
                ["status"] = command.Status.ToString(),
                ["durationMs"] = command.DurationMs?.ToString(),
            },
            CodexFileChangeItem fileChange => new Dictionary<string, string?>
            {
                ["changeCount"] = fileChange.Changes.Count.ToString(),
                ["status"] = fileChange.Status.ToString(),
            },
            CodexMcpToolCallItem mcp => new Dictionary<string, string?>
            {
                ["server"] = RepairTextOrNull(mcp.Server),
                ["tool"] = RepairTextOrNull(mcp.Tool),
                ["status"] = mcp.Status.ToString(),
            },
            CodexDynamicToolCallItem dynamicToolCall => new Dictionary<string, string?>
            {
                ["tool"] = RepairTextOrNull(dynamicToolCall.Tool),
                ["status"] = dynamicToolCall.Status.ToString(),
            },
            CodexWebSearchItem webSearch => new Dictionary<string, string?>
            {
                ["query"] = RepairTextOrNull(webSearch.Query),
            },
            CodexImageViewItem imageView => new Dictionary<string, string?>
            {
                ["explicitMediaKind"] = "image-view",
                ["path"] = RepairTextOrNull(imageView.Path),
            },
            CodexImageGenerationItem imageGeneration => new Dictionary<string, string?>
            {
                ["explicitMediaKind"] = "image-generation",
                ["result"] = RepairTextOrNull(imageGeneration.Result),
                ["status"] = RepairTextOrNull(imageGeneration.Status),
            },
            CodexUnknownThreadItem unknown when IsUnknownImageViewItem(unknown) => new Dictionary<string, string?>
            {
                ["explicitMediaKind"] = "image-view",
                ["path"] = RepairTextOrNull(GetStringAny(unknown.RawPayload, "path", "url", "imageUrl", "image_url", "filePath", "file_path")),
                ["id"] = RepairTextOrNull(unknown.Id),
            },
            CodexUnknownThreadItem unknown when IsUnknownImageGenerationItem(unknown) => new Dictionary<string, string?>
            {
                ["explicitMediaKind"] = "image-generation",
                ["result"] = RepairTextOrNull(GetStringAny(unknown.RawPayload, "result", "image", "imageData", "image_data", "b64_json", "base64")),
                ["status"] = RepairTextOrNull(GetStringAny(unknown.RawPayload, "status")),
                ["id"] = RepairTextOrNull(unknown.Id),
                ["contentType"] = RepairTextOrNull(GetStringAny(unknown.RawPayload, "contentType", "content_type", "mimeType", "mime_type")),
            },
            _ => new Dictionary<string, string?>(),
        };

    private static bool IsInternalItem(CodexThreadItem item)
        => item switch
        {
            CodexUserMessageItem => false,
            CodexErrorItem => false,
            _ => true,
        };

    private static Dictionary<string, string?> CreateMetadata(string key, string? value)
        => new()
        {
            [key] = value,
        };

    private static string GetContentType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".gif" => "image/gif",
            ".jpeg" or ".jpg" => "image/jpeg",
            ".json" => "application/json",
            ".md" => "text/markdown",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".txt" => "text/plain",
            ".webp" => "image/webp",
            ".xml" => "application/xml",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };

    private static string Truncate(string? value, int length = 160)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return CodexTextFormatting.TruncatePreview(value, length);
    }

    private static string? ResolveThreadId(string? threadId, string? fallbackThreadId)
        => string.IsNullOrWhiteSpace(threadId) ? fallbackThreadId : threadId;

    private static string? ExtractAgentMessageDelta(JsonObject? payload)
        => GetString(payload, "delta")
            ?? GetString(payload, "text")
            ?? GetString(payload, "content")
            ?? GetString(GetObject(payload, "item"), "delta")
            ?? GetString(GetObject(payload, "item"), "text")
            ?? GetString(GetObject(payload, "message"), "delta")
            ?? GetString(GetObject(payload, "message"), "text")
            ?? GetString(GetObject(payload, "agentMessage"), "delta")
            ?? GetString(GetObject(payload, "agentMessage"), "text");

    private static JsonObject? GetObject(JsonObject? payload, string name)
        => payload is not null
            && payload.TryGetPropertyValue(name, out JsonNode? node)
            && node is JsonObject obj
                ? obj
                : null;

    private static bool IsUnknownImageViewItem(CodexUnknownThreadItem item)
        => IsUnknownItemType(item, "imageView", "image_view", "image_view_item");

    private static bool IsUnknownImageGenerationItem(CodexUnknownThreadItem item)
        => IsUnknownItemType(item, "imageGeneration", "image_generation", "image_generation_call", "imageGenerationCall");

    private static bool IsUnknownItemType(CodexUnknownThreadItem item, params string[] names)
    {
        string? payloadType = GetString(item.RawPayload, "type");
        return names.Any(name => string.Equals(item.UnknownType, name, StringComparison.OrdinalIgnoreCase))
            || names.Any(name => string.Equals(payloadType, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetString(JsonObject? payload, string name)
    {
        if (payload is null || !payload.TryGetPropertyValue(name, out JsonNode? node) || node is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text)
            ? text
            : null;
    }

    private static string? GetStringAny(JsonObject? payload, params string[] names)
    {
        foreach (string name in names)
        {
            string? value = GetString(payload, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string RepairText(string? value)
        => RepairTextOrNull(value) ?? string.Empty;

    private static string? RepairTextOrNull(string? value)
        => CodexTextFormatting.RepairUtf8Mojibake(value);
}
