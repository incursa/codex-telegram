using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Services;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Builds the small, read-only projection used by the Mini App. Codex thread and
/// turn surfaces remain authoritative for execution detail; the bounded
/// supervision ledger adds explicit application task/run lifecycle metadata.
/// </summary>
internal static class TelegramMiniAppProjection
{
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
            .Where(thread => thread.NeedsAttention)
            .Select(thread => thread.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (TelegramMiniAppSupervisionTaskVm task in supervisionTasks ?? [])
        {
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

        IReadOnlyList<TelegramMiniAppChangeVm> changes = detail.Turns
            .SelectMany(turn => turn.Changes)
            .Select(change => new TelegramMiniAppChangeVm(
                change.Path,
                change.Kind,
                Limit(change.Diff, 16000) ?? string.Empty))
            .OrderBy(change => change.Path, StringComparer.Ordinal)
            .ThenBy(change => change.Kind, StringComparer.Ordinal)
            .ThenBy(change => change.Diff, StringComparer.Ordinal)
            .Take(200)
            .ToArray();

        IReadOnlyList<TelegramMiniAppArtifactVm> artifacts = detail.Turns
            .SelectMany(turn => turn.Items)
            .Where(item => item.Metadata.ContainsKey("explicitMediaKind"))
            .Select(item => new TelegramMiniAppArtifactVm(
                item.Metadata.TryGetValue("itemId", out string? itemId) && !string.IsNullOrWhiteSpace(itemId)
                    ? itemId
                    : $"{item.Type}:{item.Timestamp:O}",
                item.Metadata.TryGetValue("explicitMediaKind", out string? kind) ? kind ?? "artifact" : "artifact",
                Limit(item.Title, 240) ?? "Artifact",
                item.Metadata.TryGetValue("status", out string? status) ? Limit(status, 80) : null,
                item.Timestamp))
            .OrderByDescending(artifact => artifact.Timestamp)
            .ThenBy(artifact => artifact.Id, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.Kind, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.Title, StringComparer.Ordinal)
            .Take(100)
            .ToArray();

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
            retrievedAtUtc);
    }

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

internal sealed record TelegramMiniAppTimelineEntryVm(
    string Type,
    string Title,
    string? Subtitle,
    string? Body,
    string Severity,
    DateTimeOffset Timestamp);
