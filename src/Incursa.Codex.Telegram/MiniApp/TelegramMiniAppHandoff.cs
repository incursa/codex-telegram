using System.Text;
using Incursa.Codex.Telegram.Services;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Builds a bounded, copyable handoff from the same redacted review packet shown
/// by task detail. It contains evidence metadata, never raw worker paths or files.
/// </summary>
internal static class TelegramMiniAppHandoff
{
    private const int MaximumTextChanges = 24;
    private const int MaximumTextArtifacts = 24;

    public static TelegramMiniAppHandoffVm Create(
        CodexSupervisionTaskSnapshot task,
        TelegramMiniAppReviewPacketVm? packet)
    {
        ArgumentNullException.ThrowIfNull(task);

        StringBuilder text = new();
        CodexSupervisionRunSnapshot? run = task.LatestRun;
        string command = $"/handoff {task.CodexThreadId}";
        text.AppendLine("Codex handoff");
        text.AppendLine($"Session: {Limit(task.SessionName, 240) ?? "Unnamed session"}");
        text.AppendLine($"Task: {task.TaskId}");
        text.AppendLine($"Run state: {run?.State.ToString() ?? "not_started"}");
        text.AppendLine($"Codex thread: {task.CodexThreadId}");
        text.AppendLine($"Codex turn: {run?.TurnId ?? "(none)"}");
        text.AppendLine($"Command: {run?.CommandId ?? "(none)"}");
        text.AppendLine($"Telegram command: {command}");
        text.AppendLine($"Review packet: {packet?.PacketId ?? "not available"}");

        if (packet is null)
        {
            text.AppendLine("Review evidence: unavailable from the current Codex runtime.");
        }
        else
        {
            text.AppendLine($"Review evidence: {packet.Changes.Count} changed-file preview(s), {packet.Artifacts.Count} artifact record(s), {packet.ReviewStatus}.");
            AppendChanges(text, packet.Changes);
            AppendArtifacts(text, packet.Artifacts);
        }

        text.Append("This is a bounded context handoff, not a workspace transfer. Continue, approve, or correct the work in this Telegram conversation.");

        return new TelegramMiniAppHandoffVm(
            command,
            text.ToString(),
            packet?.PacketId,
            packet?.CodexTurnId,
            packet?.ReviewStatus ?? "unavailable",
            packet is not null,
            packet?.Changes.Count ?? 0,
            packet?.Artifacts.Count ?? 0,
            packet?.Changes
                .Select(change => new TelegramMiniAppHandoffChangeVm(
                    change.Path,
                    change.Kind,
                    change.EvidenceState,
                    change.DiffTruncated))
                .ToArray() ?? [],
            packet?.Artifacts.ToArray() ?? []);
    }

    private static void AppendChanges(StringBuilder text, IReadOnlyList<TelegramMiniAppReviewChangeVm> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }

        text.AppendLine("Changed files:");
        foreach (TelegramMiniAppReviewChangeVm change in changes.Take(MaximumTextChanges))
        {
            text.Append("- ")
                .Append(change.Path)
                .Append(" (")
                .Append(change.Kind)
                .Append(", ")
                .Append(change.EvidenceState)
                .AppendLine(change.DiffTruncated ? ", diff truncated)" : ")");
        }

        AppendOverflow(text, changes.Count, MaximumTextChanges, "changed-file preview(s)");
    }

    private static void AppendArtifacts(StringBuilder text, IReadOnlyList<TelegramMiniAppArtifactVm> artifacts)
    {
        if (artifacts.Count == 0)
        {
            return;
        }

        text.AppendLine("Artifacts:");
        foreach (TelegramMiniAppArtifactVm artifact in artifacts.Take(MaximumTextArtifacts))
        {
            text.Append("- ")
                .Append(artifact.Kind)
                .Append(": ")
                .Append(artifact.Title)
                .Append(" (")
                .Append(artifact.Status ?? "status unknown")
                .AppendLine(")");
        }

        AppendOverflow(text, artifacts.Count, MaximumTextArtifacts, "artifact record(s)");
    }

    private static void AppendOverflow(StringBuilder text, int count, int shown, string label)
    {
        if (count > shown)
        {
            text.Append("- … ").Append(count - shown).Append(" additional ").Append(label).AppendLine(" omitted from this bounded handoff.");
        }
    }

    private static string? Limit(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value) || value.Length <= maxLength
            ? value
            : value[..maxLength].TrimEnd() + "…";
}

internal sealed record TelegramMiniAppHandoffVm(
    string TelegramCommand,
    string Text,
    string? PacketId,
    string? CodexTurnId,
    string ReviewStatus,
    bool EvidenceAvailable,
    int ChangeCount,
    int ArtifactCount,
    IReadOnlyList<TelegramMiniAppHandoffChangeVm> Changes,
    IReadOnlyList<TelegramMiniAppArtifactVm> Artifacts);

internal sealed record TelegramMiniAppHandoffChangeVm(
    string Path,
    string Kind,
    string EvidenceState,
    bool DiffTruncated);
