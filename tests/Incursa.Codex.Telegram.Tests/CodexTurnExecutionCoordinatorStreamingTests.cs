using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Incursa.Codex.Telegram.Tests.TestSupport;
using Incursa.OpenAI.Codex;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexTurnExecutionCoordinatorStreamingTests
{
    [Fact]
    public async Task StartAsync_StreamsLongAssistantResponseThroughRelay()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddDelta("hello world.\nremaining").Complete("hello world.\nremaining");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        CodexThreadExecutionVm execution = await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("thread-1", execution.ThreadId);
        Assert.Equal(script.TurnId, execution.TurnId);
        Assert.Contains(queue.Messages, message => message.Text == "hello world.");
        Assert.Contains(queue.Messages, message => message.Text == "remaining");
    }

    [Fact]
    public async Task StartAsync_LeavesTurnActiveUntilDelayedCompletionGateIsReleased()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddDelta("waiting on Codex").HoldCompletion().Complete("done");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(coordinator.HasActiveTurnForThread("thread-1"));

        script.ReleaseCompletion();
        await WaitForConditionAsync(() => !coordinator.HasActiveTurnForThread("thread-1"));

        Assert.False(coordinator.HasActiveTurnForThread("thread-1"));
    }

    [Fact]
    public async Task StartAsync_PublishesStreamingFaultsAsTurnFailures()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddDelta("context full").Fault(new InvalidOperationException("context full"));

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await WaitForConditionAsync(() =>
            queue.Messages.Any(message =>
                message.Kind == CodexOutboundMessageKind.Error
                && message.Text.Contains("context full", StringComparison.OrdinalIgnoreCase)));

        Assert.Contains(queue.Messages, message =>
            message.Kind == CodexOutboundMessageKind.Error
            && message.Text.Contains("context full", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartAsync_DefersTerminalPublicationUntilLaterEventsDrain()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddCommandProgress("echo before", CodexCommandExecutionStatus.Completed);
        script.AddCompletionEvent("done");
        script.AddCommandProgress("echo after", CodexCommandExecutionStatus.Completed);

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));

        string[] messages = queue.Messages.Select(message => message.Text).ToArray();
        int beforeIndex = Array.FindIndex(messages, text => text.Contains("Command finished: echo before", StringComparison.OrdinalIgnoreCase));
        int afterIndex = Array.FindIndex(messages, text => text.Contains("Command finished: echo after", StringComparison.OrdinalIgnoreCase));
        int completionIndex = Array.FindIndex(messages, text => string.Equals(text, "done", StringComparison.Ordinal));

        Assert.True(beforeIndex >= 0, "Expected the pre-completion command progress to be published.");
        Assert.True(afterIndex >= 0, "Expected the post-completion command progress to be published.");
        Assert.True(completionIndex >= 0, "Expected the terminal completion message to be published.");
        Assert.True(beforeIndex < completionIndex, "Expected completion to be published after earlier turn events.");
        Assert.True(afterIndex < completionIndex, "Expected completion to be published after later turn events.");
    }

    [Fact]
    public async Task StartAsync_SuppressesContextCompactionNoise()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddContextCompaction().Complete("done");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.DoesNotContain(queue.Messages, message => message.Text.Contains("Context compaction", StringComparison.OrdinalIgnoreCase));
    }

    private static CodexTurnExecutionCoordinator CreateCoordinator(TelegramTurnOutputRelay relay)
        => new(
            new NullCodexRealtimeBroadcaster(),
            relay,
            new TestApplicationLifetime(),
            NullLogger<CodexTurnExecutionCoordinator>.Instance);

    private static TelegramTurnOutputRelay CreateRelay(
        FakeOutboundTelegramQueue queue,
        TelegramThreadFollowRegistry followRegistry)
        => new(
            queue,
            followRegistry,
            new TelegramTurnReactionRegistry(),
            new NoopTelegramBotMessageSender(),
            Microsoft.Extensions.Options.Options.Create(new TelegramOutboundOptions
            {
                AgentMessageUpdateMinChars = 5,
                AgentMessageUpdateMaxChars = 12,
            }),
            NullLogger<TelegramTurnOutputRelay>.Instance);

    private static TelegramThreadFollowRegistry FollowThread()
    {
        TelegramThreadFollowRegistry followRegistry = new();
        followRegistry.FollowThread(new TelegramConversationScope(1234, 55), "thread-1");
        return followRegistry;
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(2);
        TimeSpan effectivePollInterval = pollInterval ?? TimeSpan.FromMilliseconds(20);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + effectiveTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(effectivePollInterval);
        }

        Assert.True(condition(), "Timed out waiting for the expected condition.");
    }

    private sealed class FakeOutboundTelegramQueue : IOutboundTelegramQueue
    {
        private readonly object _gate = new();
        private readonly List<OutboundTelegramMessage> _messages = [];

        public IReadOnlyList<OutboundTelegramMessage> Messages
        {
            get
            {
                lock (_gate)
                {
                    return _messages.ToArray();
                }
            }
        }

        public ValueTask EnqueueAsync(OutboundTelegramMessage message, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _messages.Add(message);
            }

            return ValueTask.CompletedTask;
        }

        public Task<TelegramOutboundQueueStatus> GetStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(new TelegramOutboundQueueStatus(0, 0, 0, 0, null, null, null, []));
    }

    private sealed class NoopTelegramBotMessageSender : ITelegramBotMessageSender
    {
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
            => Task.CompletedTask;

        public Task ReactToMessageAsync(TelegramMessageReaction reaction, CancellationToken cancellationToken)
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
