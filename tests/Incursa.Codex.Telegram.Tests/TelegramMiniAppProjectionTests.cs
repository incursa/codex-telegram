using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Models;
using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramMiniAppProjectionTests
{
    [Fact]
    public void WaitingForInputIsAttentionAndHasHigherPriorityThanFailure()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TelegramMiniAppThreadVm waiting = TelegramMiniAppProjection.ToThreadViewModel(
            CreateThread("waiting", "active", now),
            CreateActiveTurn("waiting", CreateEvent("item/tool/requestUserInput", "Question")));
        TelegramMiniAppThreadVm failed = TelegramMiniAppProjection.ToThreadViewModel(
            CreateThread("failed", "system error", now.AddMinutes(-1)),
            null);

        IReadOnlyList<TelegramMiniAppAttentionVm> result = TelegramMiniAppProjection.BuildNeedsAttention(
            [failed, waiting],
            null);

        Assert.Equal(["waiting", "failed"], result.Select(item => item.Id).ToArray());
        Assert.Equal("waiting", waiting.LifecycleState);
        Assert.Equal("waiting-for-input", waiting.AttentionKind);
        Assert.True(waiting.NeedsAttention);
        Assert.Equal("failed", failed.LifecycleState);
    }

    [Fact]
    public void ArchivedAndCompletedThreadsDoNotNeedAttention()
    {
        TelegramMiniAppThreadVm archived = TelegramMiniAppProjection.ToThreadViewModel(
            CreateThread("archived", "idle", DateTimeOffset.UtcNow, archived: true),
            null);
        TelegramMiniAppThreadVm completed = TelegramMiniAppProjection.ToThreadViewModel(
            CreateThread("completed", "idle", DateTimeOffset.UtcNow),
            null);

        IReadOnlyList<TelegramMiniAppAttentionVm> result = TelegramMiniAppProjection.BuildNeedsAttention(
            [archived, completed],
            null);

        Assert.Empty(result);
        Assert.Equal("archived", archived.LifecycleState);
        Assert.Equal("completed", completed.LifecycleState);
    }

    [Fact]
    public void InterruptedLocalTurnIsShownAsAttention()
    {
        TelegramMiniAppThreadVm interrupted = TelegramMiniAppProjection.ToThreadViewModel(
            CreateThread("interrupted", "idle", DateTimeOffset.UtcNow) with { Interrupted = true },
            null);

        IReadOnlyList<TelegramMiniAppAttentionVm> result = TelegramMiniAppProjection.BuildNeedsAttention([interrupted], null);

        TelegramMiniAppAttentionVm item = Assert.Single(result);
        Assert.Equal("interrupted", interrupted.LifecycleState);
        Assert.Equal("interrupted", item.Kind);
        Assert.Equal("Run interrupted", item.Title);
    }

    [Fact]
    public void DurableSupervisionAttentionUsesSpecificPriorities()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TelegramMiniAppSupervisionTaskVm review = CreateSupervisionTask("review", "ready_for_review", now);
        TelegramMiniAppSupervisionTaskVm unknown = CreateSupervisionTask("unknown", "unknown", now.AddMinutes(-1));

        IReadOnlyList<TelegramMiniAppAttentionVm> result = TelegramMiniAppProjection.BuildNeedsAttention(
            [],
            null,
            projectsError: "Projects unavailable",
            now: now,
            supervisionTasks: [unknown, review]);

        Assert.Equal(["projects", "task:task-review", "task:task-unknown"], result.Select(item => item.Id).ToArray());
        Assert.Equal("Ready for review", result[1].Title);
        Assert.Equal("Run needs reconciliation", result[2].Title);
    }

    [Fact]
    public void RuntimeFailureIsRepresentedAsExplicitAttentionItem()
    {
        IReadOnlyList<TelegramMiniAppAttentionVm> result = TelegramMiniAppProjection.BuildNeedsAttention(
            [],
            "Codex runtime status is currently unavailable.");

        TelegramMiniAppAttentionVm item = Assert.Single(result);
        Assert.Equal("runtime", item.Id);
        Assert.Equal("runtime-unavailable", item.Kind);
        Assert.Null(item.ThreadId);
        Assert.Equal("unavailable", item.LifecycleState);
    }

    [Fact]
    public void AttentionOrderingIsDeterministicForEqualPriority()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TelegramMiniAppThreadVm first = TelegramMiniAppProjection.ToThreadViewModel(
            CreateThread("b", "system error", now),
            null);
        TelegramMiniAppThreadVm second = TelegramMiniAppProjection.ToThreadViewModel(
            CreateThread("a", "system error", now),
            null);

        IReadOnlyList<TelegramMiniAppAttentionVm> result = TelegramMiniAppProjection.BuildNeedsAttention(
            [first, second],
            null);

        Assert.Equal(["a", "b"], result.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void UnavailableItemsUseOneObservationTimeAndStableIdTieBreak()
    {
        DateTimeOffset observedAt = DateTimeOffset.Parse("2026-09-14T12:00:00Z");

        IReadOnlyList<TelegramMiniAppAttentionVm> result = TelegramMiniAppProjection.BuildNeedsAttention(
            [],
            null,
            threadsError: null,
            projectsError: "Projects unavailable",
            stateError: "Conversation unavailable",
            now: observedAt);

        Assert.Equal(["projects", "state"], result.Select(item => item.Id).ToArray());
        Assert.All(result, item => Assert.Equal(observedAt, item.UpdatedAt));
    }

    [Fact]
    public void ThreadDirectoryIsReducedToADisplayLabel()
    {
        TelegramMiniAppThreadVm thread = TelegramMiniAppProjection.ToThreadViewModel(
            CreateThread("private", "idle", DateTimeOffset.UtcNow),
            null);

        Assert.Equal("private", thread.WorkingDirectory);
    }

    [Fact]
    public void DetailProjectionSortsAndBoundsChangesAndRedactsArtifactPayloads()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-14T12:00:00Z");
        CodexThreadListItemVm summary = CreateThread("review", "idle", now);
        CodexTimelineEntryVm[] artifacts = Enumerable.Range(0, 101)
            .Select(index => new CodexTimelineEntryVm(
                "image_view",
                $"Artifact {index}",
                null,
                null,
                "info",
                now.AddSeconds(index),
                summary.Id,
                "turn-1",
                new Dictionary<string, string?>
                {
                    ["explicitMediaKind"] = "image-view",
                    ["itemId"] = $"artifact-{index:000}",
                    ["path"] = @"C:\private\secret.png",
                    ["result"] = "base64-secret",
                },
                true))
            .ToArray();
        CodexTurnVm turn = new(
            "turn-1",
            "Completed",
            null,
            "Done",
            null,
            artifacts)
        {
            Changes = Enumerable.Range(0, 198)
                .Select(index => new CodexFileChangePreviewVm($"src/file-{index:000}.cs", "Update", $"diff-{index:000}"))
                .Concat(
                [
                    new CodexFileChangePreviewVm("src/same.cs", "Update", "z"),
                    new CodexFileChangePreviewVm("src/same.cs", "Update", "a"),
                ])
                .ToArray(),
        };
        CodexThreadDetailVm detail = new(
            summary,
            [turn],
            [],
            [],
            [],
            CreateRuntime(),
            null,
            null,
            @"C:\Users\Samuel\private-repo",
            null,
            null,
            []);

        TelegramMiniAppThreadDetailVm result = TelegramMiniAppProjection.ToThreadDetailViewModel(detail, null, now);

        Assert.Equal(200, result.Changes.Count);
        Assert.Equal(["a", "z"], result.Changes.Where(change => change.Path == "src/same.cs").Select(change => change.Diff).ToArray());
        Assert.Equal(100, result.Artifacts.Count);
        Assert.DoesNotContain("C:\\private\\secret.png", System.Text.Json.JsonSerializer.Serialize(result.Artifacts), StringComparison.Ordinal);
        Assert.DoesNotContain("base64-secret", System.Text.Json.JsonSerializer.Serialize(result.Artifacts), StringComparison.Ordinal);
        Assert.Equal("private-repo", result.ThreadWorkingDirectory);
        Assert.NotNull(result.ReviewPacket);
        Assert.Equal(200, result.ReviewPacket.Changes.Count);
        Assert.Equal(100, result.ReviewPacket.Artifacts.Count);
        Assert.Equal("ready", result.ReviewPacket.ReviewStatus);
        Assert.Equal(
            result.ReviewPacket.PacketId,
            TelegramMiniAppProjection.BuildReviewPacket(detail, null, now).PacketId);
    }

    [Fact]
    public void ReviewPacketRedactsAbsolutePathsAndLabelsBinaryOrUnsupportedEvidence()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-14T12:00:00Z");
        CodexThreadListItemVm summary = CreateThread("review-safety", "idle", now);
        CodexTurnVm turn = new(
            "turn-safety",
            "Completed",
            null,
            "Done",
            null,
            [])
        {
            Changes =
            [
                new CodexFileChangePreviewVm(
                    @"C:\private\secrets.txt",
                    "Update",
                    "Binary files a/secrets.txt and b/secrets.txt differ"),
                new CodexFileChangePreviewVm("../outside.txt", "Unknown", string.Empty),
            ],
        };
        CodexThreadDetailVm detail = new(
            summary,
            [turn],
            [],
            [],
            [],
            CreateRuntime(),
            null,
            null,
            @"C:\Users\Samuel\private-repo",
            null,
            null,
            []);

        TelegramMiniAppReviewPacketVm packet = TelegramMiniAppProjection.BuildReviewPacket(detail, null, now);

        Assert.Equal("partial", packet.ReviewStatus);
        TelegramMiniAppReviewChangeVm binary = Assert.Single(packet.Changes, change => change.Path == "…/secrets.txt");
        Assert.Equal("binary", binary.EvidenceState);
        TelegramMiniAppReviewChangeVm unsupported = Assert.Single(packet.Changes, change => change.Path == "…/outside.txt");
        Assert.Equal("unsupported", unsupported.Kind);
        Assert.Equal("unsupported", unsupported.EvidenceState);
    }

    private static CodexThreadListItemVm CreateThread(
        string id,
        string status,
        DateTimeOffset updatedAt,
        bool archived = false)
        => new(
            id,
            id,
            $"Preview for {id}",
            status,
            "OpenAI",
            updatedAt.AddMinutes(-5),
            updatedAt,
            false,
            null,
            null,
            null,
            null,
            null,
            archived,
            $"C:\\work\\{id}");

    private static CodexActiveTurnStateVm CreateActiveTurn(string threadId, CodexTimelineEntryVm lastEvent)
        => new(threadId, "turn-1", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, lastEvent);

    private static TelegramMiniAppSupervisionTaskVm CreateSupervisionTask(
        string threadId,
        string state,
        DateTimeOffset updatedAt)
        => new(
            $"task-{threadId}",
            threadId,
            threadId,
            state,
            $"run-{threadId}",
            $"command-{threadId}",
            $"turn-{threadId}",
            updatedAt.AddMinutes(-1),
            updatedAt,
            updatedAt);

    private static CodexRuntimeStateVm CreateRuntime()
        => new(
            true,
            "ready",
            "Codex",
            "1.0",
            "linux",
            "linux",
            null,
            [],
            [],
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            true);

    private static CodexTimelineEntryVm CreateEvent(string type, string title)
        => new(
            type,
            title,
            null,
            null,
            "warning",
            DateTimeOffset.UtcNow,
            "thread",
            "turn-1",
            new Dictionary<string, string?>(),
            false);
}
