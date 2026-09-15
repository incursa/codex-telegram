using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Models;

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
