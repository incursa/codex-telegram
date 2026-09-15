using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramMiniAppHandoffTests
{
    [Fact]
    public void HandoffCarriesBoundedReviewAndArtifactEvidence()
    {
        CodexSupervisionTaskSnapshot task = new(
            "task:1",
            "thread:1",
            "Implement feature",
            new TelegramConversationScope(42, null),
            DateTimeOffset.Parse("2026-09-15T12:00:00Z"),
            DateTimeOffset.Parse("2026-09-15T12:05:00Z"),
            new CodexSupervisionRunSnapshot(
                "run:1",
                "command:1",
                "prompt",
                CodexSupervisionRunState.Completed,
                "turn:1",
                DateTimeOffset.Parse("2026-09-15T12:00:00Z"),
                DateTimeOffset.Parse("2026-09-15T12:05:00Z"),
                null,
                null,
                "completed"));
        TelegramMiniAppReviewPacketVm packet = new(
            "review:packet",
            task.TaskId,
            "run:1",
            task.CodexThreadId,
            "turn:1",
            "completed",
            "ready",
            DateTimeOffset.Parse("2026-09-15T12:05:00Z"),
            [new TelegramMiniAppReviewChangeVm("turn:1", "src/Feature.cs", "Update", "supported", "diff", false)],
            [new TelegramMiniAppArtifactVm("artifact:1", "screenshot", "Result preview", "ready", DateTimeOffset.Parse("2026-09-15T12:04:00Z"))]);

        TelegramMiniAppHandoffVm result = TelegramMiniAppHandoff.Create(task, packet);

        Assert.True(result.EvidenceAvailable);
        Assert.Equal("review:packet", result.PacketId);
        Assert.Equal(1, result.ChangeCount);
        Assert.Equal(1, result.ArtifactCount);
        Assert.Equal("src/Feature.cs", Assert.Single(result.Changes).Path);
        Assert.Equal("Result preview", Assert.Single(result.Artifacts).Title);
        Assert.Contains("Changed files:", result.Text, StringComparison.Ordinal);
        Assert.Contains("Artifacts:", result.Text, StringComparison.Ordinal);
        Assert.Contains("/handoff thread:1", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void HandoffWithoutRuntimeEvidenceRemainsBoundedAndExplicit()
    {
        CodexSupervisionTaskSnapshot task = new(
            "task:1",
            "thread:1",
            "Task",
            new TelegramConversationScope(42, null),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null);

        TelegramMiniAppHandoffVm result = TelegramMiniAppHandoff.Create(task, null);

        Assert.False(result.EvidenceAvailable);
        Assert.Equal("unavailable", result.ReviewStatus);
        Assert.Empty(result.Changes);
        Assert.Empty(result.Artifacts);
        Assert.Contains("unavailable from the current Codex runtime", result.Text, StringComparison.Ordinal);
        Assert.Contains("not a workspace transfer", result.Text, StringComparison.Ordinal);
    }
}
