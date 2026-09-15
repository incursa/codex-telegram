using System.Security.Cryptography;
using System.Text;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Services;
using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Builds the small, read-only projection used by the Mini App. Codex thread and
/// turn surfaces remain authoritative for execution detail; the bounded
/// supervision ledger adds explicit application task/run lifecycle metadata.
/// </summary>
internal static class TelegramMiniAppProjection
{
    public static TelegramMiniAppCurrentSessionVm ToCurrentSessionViewModel(
        TelegramMiniAppThreadVm thread,
        CodexActiveTurnStateVm? activeTurn,
        CodexSessionModelSettings? settings,
        CodexThreadGoalVm? goal,
        bool disconnected,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(thread);

        string? effectiveModel = ResolveEffectiveModel(settings);
        string? effectiveEffort = ResolveEffectiveEffort(settings, effectiveModel);
        (string activityStatus, string? description) = ResolveActivity(thread, activeTurn, disconnected);
        return new TelegramMiniAppCurrentSessionVm(
            thread.Id,
            thread.Name ?? "Unnamed session",
            DirectoryLabel(thread.WorkingDirectory),
            thread.WorkingDirectory,
            thread.LifecycleState,
            activityStatus,
            description,
            activeTurn?.StartedAt,
            activeTurn is null ? 0 : Math.Max(0, (int)(observedAtUtc - activeTurn.StartedAt).TotalSeconds),
            activeTurn?.Model ?? effectiveModel,
            activeTurn?.ReasoningEffort ?? effectiveEffort,
            effectiveModel,
            effectiveEffort,
            settings?.AvailableModels.Select(ToModelOption).ToArray() ?? [],
            goal);
    }

    private static (string Status, string? Description) ResolveActivity(
        TelegramMiniAppThreadVm thread,
        CodexActiveTurnStateVm? activeTurn,
        bool disconnected)
    {
        if (disconnected)
        {
            return ("Disconnected", "Communication with the Codex runtime is unavailable.");
        }

        CodexTimelineEntryVm? entry = activeTurn?.LastEvent;
        if (activeTurn is null)
        {
            return (thread.AttentionKind == "waiting-for-input" ? "Waiting for you" : "Ready", null);
        }

        string type = entry?.Type ?? string.Empty;
        string status = type.Contains("requestUserInput", StringComparison.OrdinalIgnoreCase)
            ? "Waiting for you"
            : type.Contains("commandExecution", StringComparison.OrdinalIgnoreCase)
                || type.Contains("shell", StringComparison.OrdinalIgnoreCase)
                || type.Contains("terminal", StringComparison.OrdinalIgnoreCase)
                ? "Running a command"
                : type.Contains("agentMessage", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("finalResponse", StringComparison.OrdinalIgnoreCase)
                    ? "Generating response"
                    : type.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
                        ? "Thinking"
                        : "Working";
        string? description = entry?.Body ?? entry?.Subtitle ?? entry?.Title;
        return (status, Limit(description, 240));
    }

    private static TelegramMiniAppModelOptionVm ToModelOption(CodexModelVm model)
        => new(
            model.Id,
            model.DisplayName,
            model.Description,
            model.DefaultReasoningEffort.ToString(),
            model.SupportedEfforts
                .Where(effort => effort is not CodexReasoningEffort.None)
                .Select(effort => effort.ToString())
                .ToArray(),
            model.IsDefault,
            model.Hidden,
            model.AvailabilityMessage);

    private static string? ResolveEffectiveModel(CodexSessionModelSettings? settings)
        => settings?.Model
            ?? settings?.AvailableModels.FirstOrDefault(model => model.IsDefault)?.Id
            ?? settings?.AvailableModels.FirstOrDefault()?.Id;

    private static string? ResolveEffectiveEffort(CodexSessionModelSettings? settings, string? model)
    {
        if (!string.IsNullOrWhiteSpace(settings?.ReasoningEffort))
        {
            return settings.ReasoningEffort;
        }

        CodexModelVm? selected = settings?.AvailableModels.FirstOrDefault(candidate => string.Equals(candidate.Id, model, StringComparison.OrdinalIgnoreCase));
        if (selected?.DefaultReasoningEffort is { } effort && effort is not CodexReasoningEffort.None)
        {
            return effort.ToString();
        }

        return settings?.AvailableReasoningEfforts.FirstOrDefault() is { } available
            ? available.ToString()
            : null;
    }

    public static TelegramMiniAppThreadVm ToThreadViewModel(
        CodexThreadListItemVm thread,
        CodexActiveTurnStateVm? activeTurn)
    {
        ArgumentNullException.ThrowIfNull(thread);

        string lifecycleState = ResolveLifecycleState(thread, activeTurn);
        string? attentionKind = ResolveAttentionKind(thread, activeTurn, lifecycleState);
        return new TelegramMiniAppThreadVm(
            thread.Id,
            thread.Name,
            thread.Preview,
            thread.Status,
            thread.ModelProvider,
            thread.CreatedAt,
            thread.UpdatedAt,
            thread.Archived,
            DirectoryLabel(thread.WorkingDirectory),
            lifecycleState,
            attentionKind is not null,
            attentionKind,
            ResolveAttentionTitle(attentionKind),
            activeTurn?.LastEvent?.Title);
    }

    public static IReadOnlyList<TelegramMiniAppAttentionVm> BuildNeedsAttention(
        IReadOnlyList<TelegramMiniAppThreadVm> threads,
        string? runtimeError,
        string? threadsError = null,
        string? projectsError = null,
        string? stateError = null,
        DateTimeOffset? now = null,
        IReadOnlyList<TelegramMiniAppSupervisionTaskVm>? supervisionTasks = null)
    {
        ArgumentNullException.ThrowIfNull(threads);

        DateTimeOffset observedAt = now ?? DateTimeOffset.UtcNow;
        List<TelegramMiniAppAttentionVm> attention = new();
        AddUnavailable(attention, "runtime", "Codex runtime unavailable", runtimeError, 100, observedAt);
        AddUnavailable(attention, "threads", "Codex sessions unavailable", threadsError, 95, observedAt);
        AddUnavailable(attention, "projects", "Workspace context unavailable", projectsError, 90, observedAt);
        AddUnavailable(attention, "state", "Conversation context unavailable", stateError, 90, observedAt);

        foreach (TelegramMiniAppThreadVm thread in threads)
        {
            if ((supervisionTasks ?? []).Any(task => task.AttentionAcknowledged
                && string.Equals(task.CodexThreadId, thread.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!thread.NeedsAttention || string.IsNullOrWhiteSpace(thread.AttentionKind))
            {
                continue;
            }

            attention.Add(new TelegramMiniAppAttentionVm(
                thread.Id,
                thread.AttentionKind,
                thread.AttentionTitle ?? "Needs attention",
                ResolveAttentionDetail(thread),
                thread.Id,
                DirectoryLabel(thread.WorkingDirectory),
                thread.LifecycleState,
                thread.UpdatedAt,
                ResolveAttentionPriority(thread.AttentionKind)));
        }

        HashSet<string> representedSessions = threads
            .Where(thread => thread.NeedsAttention
                && !(supervisionTasks ?? []).Any(task => task.AttentionAcknowledged
                    && string.Equals(task.CodexThreadId, thread.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(thread => thread.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (TelegramMiniAppSupervisionTaskVm task in supervisionTasks ?? [])
        {
            if (task.AttentionAcknowledged)
            {
                continue;
            }

            string? attentionKind = ResolveSupervisionAttentionKind(task.State);
            if (attentionKind is null || representedSessions.Contains(task.CodexThreadId))
            {
                continue;
            }

            attention.Add(new TelegramMiniAppAttentionVm(
                $"task:{task.TaskId}",
                attentionKind,
                ResolveSupervisionAttentionTitle(attentionKind),
                $"{task.SessionName} · durable run state is {task.State}.",
                task.CodexThreadId,
                null,
                task.State,
                task.LastRunUpdatedAt,
                ResolveAttentionPriority(attentionKind)));
        }

        return attention
            .OrderByDescending(item => item.Priority)
            .ThenByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddUnavailable(
        ICollection<TelegramMiniAppAttentionVm> attention,
        string id,
        string title,
        string? detail,
        int priority,
        DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        attention.Add(new TelegramMiniAppAttentionVm(
            id,
            $"{id}-unavailable",
            title,
            detail,
            null,
            null,
            "unavailable",
            observedAt,
            priority));
    }

    public static TelegramMiniAppThreadDetailVm ToThreadDetailViewModel(
        CodexThreadDetailVm detail,
        CodexActiveTurnStateVm? activeTurn,
        DateTimeOffset retrievedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(detail);

        TelegramMiniAppThreadVm thread = ToThreadViewModel(detail.Summary, activeTurn);
        IReadOnlyList<TelegramMiniAppTurnVm> turns = detail.Turns
            .TakeLast(12)
            .Select(turn => new TelegramMiniAppTurnVm(
                turn.Id,
                turn.Status,
                Limit(turn.ErrorMessage, 4000),
                Limit(turn.FinalResponse, 12000),
                turn.Usage,
                turn.Items.Count == 0 ? null : turn.Items.Max(item => item.Timestamp),
                turn.Changes
                    .Select(change => new TelegramMiniAppChangeVm(
                        change.Path,
                        change.Kind,
                        Limit(change.Diff, 16000) ?? string.Empty))
                    .ToArray()))
            .ToArray();

        IReadOnlyList<TelegramMiniAppTimelineEntryVm> timeline = detail.Turns
            .SelectMany(turn => turn.Items)
            .OrderByDescending(item => item.Timestamp)
            .Take(80)
            .OrderBy(item => item.Timestamp)
            .Select(item => new TelegramMiniAppTimelineEntryVm(
                item.Type,
                Limit(item.Title, 240) ?? "Codex event",
                Limit(item.Subtitle, 1000),
                Limit(item.Body, 8000),
                item.Severity,
                item.Timestamp))
            .ToArray();

        IReadOnlyList<TelegramMiniAppChangeVm> changes = BuildChanges(detail.Turns);
        IReadOnlyList<TelegramMiniAppArtifactVm> artifacts = BuildArtifacts(detail.Turns);

        return new TelegramMiniAppThreadDetailVm(
            thread,
            turns,
            timeline,
            detail.Runtime,
            detail.ActiveTurnId,
            detail.ThreadModel,
            DirectoryLabel(detail.ThreadWorkingDirectory),
            changes,
            artifacts,
            retrievedAtUtc)
        {
            ReviewPacket = BuildReviewPacket(detail, null, retrievedAtUtc),
        };
    }

    public static TelegramMiniAppSupervisionTaskVm ToSupervisionTaskViewModel(
        CodexSupervisionTaskSnapshot task,
        bool attentionAcknowledged = false)
    {
        ArgumentNullException.ThrowIfNull(task);

        CodexSupervisionRunSnapshot? run = task.LatestRun;
        return new TelegramMiniAppSupervisionTaskVm(
            task.TaskId,
            task.CodexThreadId,
            task.SessionName,
            run?.State.ToString().ToLowerInvariant() ?? "not_started",
            run?.RunId,
            run?.CommandId,
            run?.TurnId,
            task.CreatedAt,
            task.UpdatedAt,
            run?.UpdatedAt ?? task.UpdatedAt,
            task.RecipeId,
            task.RecipeVersion,
            task.RecipeDisplayName,
            task.WorkerId,
            task.LeaseId,
            task.WorkspaceId,
            attentionAcknowledged);
    }

    public static TelegramMiniAppReviewPacketVm BuildReviewPacket(
        CodexThreadDetailVm detail,
        TelegramMiniAppSupervisionTaskVm? supervision,
        DateTimeOffset retrievedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(detail);

        TelegramMiniAppReviewChangeVm[] changes = detail.Turns
            .SelectMany(turn => turn.Changes.Select(change => ToReviewChange(turn, change)))
            .OrderBy(change => change.Path, StringComparer.Ordinal)
            .ThenBy(change => change.Kind, StringComparer.Ordinal)
            .ThenBy(change => change.TurnId, StringComparer.Ordinal)
            .ThenBy(change => change.Diff, StringComparer.Ordinal)
            .Take(200)
            .ToArray();
        TelegramMiniAppArtifactVm[] artifacts = BuildArtifacts(detail.Turns).ToArray();
        string? turnId = detail.Turns.LastOrDefault()?.Id;
        string canonical = string.Join(
            "\n",
            detail.Summary.Id,
            supervision?.TaskId,
            supervision?.RunId,
            turnId,
            string.Join("\n", changes.Select(change => string.Join("|", change.TurnId, change.Path, change.Kind, change.EvidenceState, change.Diff))),
            string.Join("\n", artifacts.Select(artifact => string.Join("|", artifact.Id, artifact.Kind, artifact.Title, artifact.Status, artifact.Timestamp.ToString("O")))));
        string packetId = "review:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..16];
        bool hasUnsupportedEvidence = changes.Any(change => change.EvidenceState is "binary" or "unsupported");
        string reviewStatus = changes.Length == 0 && artifacts.Length == 0
            ? "no-reviewable-evidence"
            : hasUnsupportedEvidence
                ? "partial"
                : "ready";

        return new TelegramMiniAppReviewPacketVm(
            packetId,
            supervision?.TaskId,
            supervision?.RunId,
            detail.Summary.Id,
            turnId,
            supervision?.State ?? detail.Summary.Status,
            reviewStatus,
            retrievedAtUtc,
            changes,
            artifacts);
    }

    private static IReadOnlyList<TelegramMiniAppChangeVm> BuildChanges(IReadOnlyList<CodexTurnVm> turns)
        => turns
            .SelectMany(turn => turn.Changes)
            .Select(change => new TelegramMiniAppChangeVm(
                NormalizeReviewPath(change.Path),
                Limit(change.Kind, 80) ?? "change",
                Limit(change.Diff, 16000) ?? string.Empty))
            .OrderBy(change => change.Path, StringComparer.Ordinal)
            .ThenBy(change => change.Kind, StringComparer.Ordinal)
            .ThenBy(change => change.Diff, StringComparer.Ordinal)
            .Take(200)
            .ToArray();

    private static IReadOnlyList<TelegramMiniAppArtifactVm> BuildArtifacts(IReadOnlyList<CodexTurnVm> turns)
        => turns
            .SelectMany(turn => turn.Items)
            .Where(item => item.Metadata.ContainsKey("explicitMediaKind"))
            .Select(item => new TelegramMiniAppArtifactVm(
                item.Metadata.TryGetValue("itemId", out string? itemId) && !string.IsNullOrWhiteSpace(itemId)
                    ? Limit(itemId, 160) ?? "artifact"
                    : $"{item.Type}:{item.Timestamp:O}",
                item.Metadata.TryGetValue("explicitMediaKind", out string? kind) ? Limit(kind, 80) ?? "artifact" : "artifact",
                Limit(item.Title, 240) ?? "Artifact",
                item.Metadata.TryGetValue("status", out string? status) ? Limit(status, 80) : null,
                item.Timestamp))
            .OrderByDescending(artifact => artifact.Timestamp)
            .ThenBy(artifact => artifact.Id, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.Kind, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.Title, StringComparer.Ordinal)
            .Take(100)
            .ToArray();

    private static TelegramMiniAppReviewChangeVm ToReviewChange(CodexTurnVm turn, CodexFileChangePreviewVm change)
    {
        string diff = change.Diff ?? string.Empty;
        bool truncated = diff.Length > 16000;
        string evidenceState = IsBinaryOrUnsupportedDiff(diff)
            ? "binary"
            : string.IsNullOrWhiteSpace(diff)
                ? "unsupported"
                : "text";
        string displayDiff = evidenceState switch
        {
            "binary" => "Binary change reported by Codex; text diff is not available.",
            "unsupported" => "Codex reported this change without a text diff.",
            _ => Limit(diff, 16000) ?? string.Empty,
        };

        return new TelegramMiniAppReviewChangeVm(
            turn.Id,
            NormalizeReviewPath(change.Path),
            NormalizeChangeKind(change.Kind),
            evidenceState,
            displayDiff,
            truncated);
    }

    private static string NormalizeChangeKind(string? kind)
        => kind?.Trim().ToLowerInvariant() switch
        {
            "add" => "added",
            "delete" => "deleted",
            "update" => "updated",
            _ => "unsupported",
        };

    private static string NormalizeReviewPath(string? path)
    {
        string normalized = string.IsNullOrWhiteSpace(path) ? "[unnamed file]" : path.Trim().Replace('\\', '/');
        bool absolute = normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':');
        if (absolute || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
        {
            string leaf = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            return string.IsNullOrWhiteSpace(leaf) ? "[external file]" : $"…/{Limit(leaf, 120)}";
        }

        return Limit(normalized, 240) ?? "[unnamed file]";
    }

    private static bool IsBinaryOrUnsupportedDiff(string diff)
        => diff.Contains("Binary files", StringComparison.OrdinalIgnoreCase)
            || diff.Contains("GIT binary patch", StringComparison.OrdinalIgnoreCase)
            || diff.Contains('\0');

    private static string ResolveLifecycleState(
        CodexThreadListItemVm thread,
        CodexActiveTurnStateVm? activeTurn)
    {
        if (thread.Archived)
        {
            return "archived";
        }

        if (thread.Interrupted)
        {
            return "interrupted";
        }

        if (activeTurn is not null)
        {
            return IsWaitingForOperator(activeTurn.LastEvent) ? "waiting" : "running";
        }

        string status = thread.Status.Trim();
        if (ContainsAny(status, "system error", "error", "failed"))
        {
            return "failed";
        }

        if (ContainsAny(status, "not loaded", "unavailable"))
        {
            return "unavailable";
        }

        if (ContainsAny(status, "active", "running", "starting"))
        {
            return "running";
        }

        if (ContainsAny(status, "queued", "pending"))
        {
            return "queued";
        }

        return "completed";
    }

    private static string? DirectoryLabel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string trimmed = path.TrimEnd('/', '\\');
        if (trimmed.Length == 0)
        {
            return "Workspace";
        }

        int separatorIndex = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        string label = separatorIndex >= 0 ? trimmed[(separatorIndex + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(label) ? "Workspace" : Limit(label, 120);
    }

    private static string? ResolveAttentionKind(
        CodexThreadListItemVm thread,
        CodexActiveTurnStateVm? activeTurn,
        string lifecycleState)
    {
        if (thread.Archived)
        {
            return null;
        }

        if (thread.Interrupted)
        {
            return "interrupted";
        }

        if (activeTurn is not null && IsWaitingForOperator(activeTurn.LastEvent))
        {
            return "waiting-for-input";
        }

        if (lifecycleState == "failed")
        {
            return "failed";
        }

        if (lifecycleState == "unavailable")
        {
            return "unavailable";
        }

        return null;
    }

    private static bool IsWaitingForOperator(CodexTimelineEntryVm? entry)
    {
        if (entry is null)
        {
            return false;
        }

        return ContainsAny(
            entry.Type,
            "requestUserInput",
            "approval",
            "plan.question",
            "plan_input",
            "input.request")
            || ContainsAny(entry.Title, "approval", "question", "waiting for input", "needs your input")
            || ContainsAny(entry.Subtitle, "approval", "question", "waiting for input", "needs your input");
    }

    private static string? ResolveAttentionTitle(string? attentionKind)
        => attentionKind switch
        {
            "waiting-for-input" => "Needs your answer",
            "failed" => "Run failed",
            "interrupted" => "Run interrupted",
            "unavailable" => "Session unavailable",
            _ => null,
        };

    private static string? ResolveSupervisionAttentionKind(string state)
        => state switch
        {
            "waitingforinput" or "waiting_for_input" => "waiting-for-input",
            "readyforreview" or "ready_for_review" => "ready-for-review",
            "failed" => "failed",
            "interrupted" => "interrupted",
            "unknown" => "reconciliation-required",
            _ => null,
        };

    private static string ResolveSupervisionAttentionTitle(string attentionKind)
        => attentionKind switch
        {
            "ready-for-review" => "Ready for review",
            "reconciliation-required" => "Run needs reconciliation",
            _ => ResolveAttentionTitle(attentionKind) ?? "Needs attention",
        };

    private static string ResolveAttentionDetail(TelegramMiniAppThreadVm thread)
        => !string.IsNullOrWhiteSpace(thread.Preview)
            ? thread.Preview
            : thread.AttentionTitle ?? thread.Status;

    private static int ResolveAttentionPriority(string attentionKind)
        => attentionKind switch
        {
            "waiting-for-input" => 80,
            "ready-for-review" => 75,
            "failed" => 70,
            "interrupted" => 65,
            "unavailable" => 60,
            "reconciliation-required" => 55,
            _ => 10,
        };

    private static bool ContainsAny(string? value, params string[] values)
        => !string.IsNullOrWhiteSpace(value)
            && values.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static string? Limit(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength].TrimEnd() + "…";
    }
}

internal sealed record TelegramMiniAppAttentionVm(
    string Id,
    string Kind,
    string Title,
    string Detail,
    string? ThreadId,
    string? WorkingDirectory,
    string LifecycleState,
    DateTimeOffset UpdatedAt,
    int Priority);

internal sealed record TelegramMiniAppThreadDetailVm(
    TelegramMiniAppThreadVm Thread,
    IReadOnlyList<TelegramMiniAppTurnVm> Turns,
    IReadOnlyList<TelegramMiniAppTimelineEntryVm> Timeline,
    CodexRuntimeStateVm Runtime,
    string? ActiveTurnId,
    string? ThreadModel,
    string? ThreadWorkingDirectory,
    IReadOnlyList<TelegramMiniAppChangeVm> Changes,
    IReadOnlyList<TelegramMiniAppArtifactVm> Artifacts,
    DateTimeOffset RetrievedAtUtc)
{
    public TelegramMiniAppSupervisionTaskVm? Supervision { get; init; }

    public TelegramMiniAppReviewPacketVm? ReviewPacket { get; init; }
}

internal sealed record TelegramMiniAppTurnVm(
    string Id,
    string Status,
    string? ErrorMessage,
    string? FinalResponse,
    CodexUsageVm? Usage,
    DateTimeOffset? LastEventAtUtc,
    IReadOnlyList<TelegramMiniAppChangeVm> Changes);

internal sealed record TelegramMiniAppChangeVm(
    string Path,
    string Kind,
    string Diff);

internal sealed record TelegramMiniAppArtifactVm(
    string Id,
    string Kind,
    string Title,
    string? Status,
    DateTimeOffset Timestamp);

internal sealed record TelegramMiniAppReviewPacketVm(
    string PacketId,
    string? TaskId,
    string? RunId,
    string CodexThreadId,
    string? CodexTurnId,
    string State,
    string ReviewStatus,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<TelegramMiniAppReviewChangeVm> Changes,
    IReadOnlyList<TelegramMiniAppArtifactVm> Artifacts)
{
    public bool ReadOnly { get; init; } = true;

    public bool RequiresTelegramApproval { get; init; } = true;

    public bool Acknowledged { get; init; }

    public DateTimeOffset? AcknowledgedAtUtc { get; init; }
}

internal sealed record TelegramMiniAppReviewChangeVm(
    string TurnId,
    string Path,
    string Kind,
    string EvidenceState,
    string Diff,
    bool DiffTruncated);

internal sealed record TelegramMiniAppTimelineEntryVm(
    string Type,
    string Title,
    string? Subtitle,
    string? Body,
    string Severity,
    DateTimeOffset Timestamp);

internal sealed record TelegramMiniAppCurrentSessionVm(
    string Id,
    string Name,
    string? Repository,
    string? WorkingDirectory,
    string LifecycleState,
    string ActivityStatus,
    string? ActivityDescription,
    DateTimeOffset? ActivityStartedAtUtc,
    int ActivityElapsedSeconds,
    string? CurrentModel,
    string? CurrentThinkingEffort,
    string? NextModel,
    string? NextThinkingEffort,
    IReadOnlyList<TelegramMiniAppModelOptionVm> Models,
    CodexThreadGoalVm? Goal);

internal sealed record TelegramMiniAppModelOptionVm(
    string Id,
    string DisplayName,
    string Description,
    string DefaultThinkingEffort,
    IReadOnlyList<string> SupportedThinkingEfforts,
    bool IsDefault,
    bool Hidden,
    string? AvailabilityMessage);

internal sealed record TelegramMiniAppGlobalInstructionsVm(
    string Text,
    DateTimeOffset? UpdatedAtUtc,
    bool HasSavedValue,
    string ApplicationState);
