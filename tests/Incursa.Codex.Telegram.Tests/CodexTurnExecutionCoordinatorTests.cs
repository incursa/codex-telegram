using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexTurnExecutionCoordinatorTests
{
    [Fact]
    public void UpdateActiveTurnState_RekeysActiveTurnWhenEventReportsNewTurnId()
    {
        CodexTurnExecutionCoordinator coordinator = new(
            new NullCodexRealtimeBroadcaster(),
            new NoopTelegramTurnOutputRelay(),
            new TestApplicationLifetime(),
            NullLogger<CodexTurnExecutionCoordinator>.Instance);

        coordinator.RegisterActiveTurn("thread-1", "turn-original");

        coordinator.UpdateActiveTurnState(
            "thread-1",
            "turn-original",
            new CodexTimelineEntryVm(
                "turn.started",
                "Turn started",
                "turn-active",
                null,
                "info",
                DateTimeOffset.UtcNow,
                "thread-1",
                "turn-active",
                new Dictionary<string, string?>(),
                true));

        Assert.Equal("turn-active", coordinator.GetActiveTurnId("thread-1"));
        Assert.False(coordinator.TryClearActiveTurn("thread-1", "turn-original"));
        Assert.True(coordinator.TryClearActiveTurn("thread-1", "turn-active"));
    }

    private sealed class NoopTelegramTurnOutputRelay : ITelegramTurnOutputRelay
    {
        public Task PublishTurnEventAsync(CodexTimelineEntryVm entry, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
