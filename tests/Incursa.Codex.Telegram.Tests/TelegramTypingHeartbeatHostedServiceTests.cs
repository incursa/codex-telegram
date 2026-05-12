using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Incursa.OpenAI.Codex;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramTypingHeartbeatHostedServiceTests
{
    [Fact]
    public async Task SendHeartbeatAsync_SendsTypingToActiveFollowedConversations()
    {
        FakeTurnCoordinator turnCoordinator = new("thread-1", "thread-2");
        TelegramThreadFollowRegistry followRegistry = new();
        TelegramConversationScope first = new(1234, null);
        TelegramConversationScope second = new(1234, 77);
        followRegistry.FollowThread(first, "thread-1");
        followRegistry.FollowThread(second, "thread-2");
        TelegramTypingIndicatorRegistry typingIndicatorRegistry = new();
        TestTelegramBotMessageSender sender = new();
        TelegramTypingHeartbeatHostedService service = CreateService(turnCoordinator, followRegistry, typingIndicatorRegistry, sender);

        int sent = await service.SendHeartbeatAsync(CancellationToken.None);

        Assert.Equal(2, sent);
        Assert.Contains(first, sender.TypingActions);
        Assert.Contains(second, sender.TypingActions);
    }

    [Fact]
    public async Task SendHeartbeatAsync_WhenNoActiveFollowedConversationDoesNothing()
    {
        FakeTurnCoordinator turnCoordinator = new("thread-1");
        TelegramThreadFollowRegistry followRegistry = new();
        TelegramTypingIndicatorRegistry typingIndicatorRegistry = new();
        TestTelegramBotMessageSender sender = new();
        TelegramTypingHeartbeatHostedService service = CreateService(turnCoordinator, followRegistry, typingIndicatorRegistry, sender);

        int sent = await service.SendHeartbeatAsync(CancellationToken.None);

        Assert.Equal(0, sent);
        Assert.Empty(sender.TypingActions);
    }

    [Fact]
    public async Task SendHeartbeatAsync_SendsTypingWhileWaitingForCodexBeforeActiveTurnExists()
    {
        FakeTurnCoordinator turnCoordinator = new();
        TelegramThreadFollowRegistry followRegistry = new();
        TelegramTypingIndicatorRegistry typingIndicatorRegistry = new();
        TelegramConversationScope conversation = new(1234, 77);
        using IDisposable registration = typingIndicatorRegistry.Track(conversation);
        TestTelegramBotMessageSender sender = new();
        TelegramTypingHeartbeatHostedService service = CreateService(turnCoordinator, followRegistry, typingIndicatorRegistry, sender);

        int sent = await service.SendHeartbeatAsync(CancellationToken.None);

        Assert.Equal(1, sent);
        Assert.Equal([conversation], sender.TypingActions);
    }

    [Fact]
    public async Task WaitForChangeAsync_ReturnsImmediatelyWhenCodexWaitWasRegisteredBeforeWaitStarted()
    {
        TelegramTypingIndicatorRegistry typingIndicatorRegistry = new();
        long observedVersion = typingIndicatorRegistry.ChangeVersion;

        using IDisposable registration = typingIndicatorRegistry.Track(new TelegramConversationScope(1234, 77));

        await typingIndicatorRegistry
            .WaitForChangeAsync(TimeSpan.FromSeconds(30), observedVersion, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SendHeartbeatAsync_WhenDisabledDoesNotSend()
    {
        FakeTurnCoordinator turnCoordinator = new("thread-1");
        TelegramThreadFollowRegistry followRegistry = new();
        TelegramTypingIndicatorRegistry typingIndicatorRegistry = new();
        TelegramConversationScope conversation = new(1234, null);
        followRegistry.FollowThread(conversation, "thread-1");
        TestTelegramBotMessageSender sender = new();
        TelegramTypingHeartbeatHostedService service = CreateService(
            turnCoordinator,
            followRegistry,
            typingIndicatorRegistry,
            sender,
            enabled: false);

        int sent = await service.SendHeartbeatAsync(CancellationToken.None);

        Assert.Equal(0, sent);
        Assert.Empty(sender.TypingActions);
    }

    private static TelegramTypingHeartbeatHostedService CreateService(
        FakeTurnCoordinator turnCoordinator,
        TelegramThreadFollowRegistry followRegistry,
        TelegramTypingIndicatorRegistry? typingIndicatorRegistry,
        TestTelegramBotMessageSender sender,
        bool enabled = true)
        => new(
            Microsoft.Extensions.Options.Options.Create(new TelegramBotOptions { Enabled = enabled }),
            turnCoordinator,
            followRegistry,
            typingIndicatorRegistry ?? new TelegramTypingIndicatorRegistry(),
            sender,
            NullLogger<TelegramTypingHeartbeatHostedService>.Instance);

    private sealed class FakeTurnCoordinator : ICodexTurnExecutionCoordinator
    {
        private readonly IReadOnlyCollection<string> _activeThreadIds;

        public FakeTurnCoordinator(params string[] activeThreadIds)
        {
            _activeThreadIds = activeThreadIds;
        }

        public bool HasActiveTurn => _activeThreadIds.Count > 0;

        public IReadOnlyCollection<string> GetActiveThreadIds() => _activeThreadIds;

        public bool HasActiveTurnForThread(string threadId)
            => _activeThreadIds.Contains(threadId, StringComparer.Ordinal);

        public string? GetActiveTurnId(string threadId)
            => HasActiveTurnForThread(threadId) ? "turn-1" : null;

        public CodexActiveTurnStateVm? TryGetActiveTurnState(string threadId) => null;

        public void RegisterActiveTurn(string threadId, string turnId, CodexTurn? turn = null, CodexTimelineEntryVm? lastEvent = null)
        {
        }

        public void UpdateActiveTurnState(string threadId, string turnId, CodexTimelineEntryVm? lastEvent = null)
        {
        }

        public bool TryClearActiveTurn(string threadId, string turnId) => false;

        public Task SteerAsync(string threadId, string turnId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task InterruptAsync(string threadId, string turnId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class TestTelegramBotMessageSender : ITelegramBotMessageSender
    {
        public List<TelegramConversationScope> TypingActions { get; } = [];

        public Task SendTextMessageAsync(
            TelegramConversationScope conversation,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
            => Task.CompletedTask;

        public Task EditTextMessageAsync(
            TelegramConversationScope conversation,
            int messageId,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
            => Task.CompletedTask;

        public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AcknowledgeMessageAsync(TelegramMessageAcknowledgement acknowledgement, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SendTypingActionAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
        {
            TypingActions.Add(conversation);
            return Task.CompletedTask;
        }

        public Task ReactToMessageAsync(TelegramMessageReaction reaction, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
