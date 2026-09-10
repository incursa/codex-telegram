using System.Text.Json.Nodes;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramPlanInputCoordinatorTests
{
    [Fact]
    public async Task HandleApprovalRequest_PublishesPlanQuestionAndReturnsTelegramAnswer()
    {
        TelegramThreadFollowRegistry followRegistry = new();
        TelegramConversationScope conversation = new(1234, 56);
        followRegistry.FollowThread(conversation, "thread-plan");
        TestTelegramBotMessageSender sender = new();
        TelegramPlanInputCoordinator coordinator = new(
            followRegistry,
            sender,
            new TestApplicationLifetime(),
            NullLogger<TelegramPlanInputCoordinator>.Instance);

        Task<JsonObject?> responseTask = Task.Run(() => coordinator.HandleApprovalRequest(
            "item/tool/requestUserInput",
            new JsonObject
            {
                ["threadId"] = "thread-plan",
                ["turnId"] = "turn-plan",
                ["itemId"] = "item-question",
                ["questions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "confirm_scope",
                        ["header"] = "Scope",
                        ["question"] = "Should I include tests?",
                        ["options"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["label"] = "Yes",
                                ["description"] = "Include focused tests.",
                            },
                            new JsonObject
                            {
                                ["label"] = "No",
                                ["description"] = "Implementation only.",
                            },
                        },
                    },
                },
            }));

        await WaitUntilAsync(() => sender.Sent.Count == 1);
        Assert.Contains("Plan mode: input needed", sender.Sent[0].Text);
        Assert.Contains("Should I include tests?", sender.Sent[0].Text);
        Assert.NotNull(sender.Sent[0].Buttons);

        Assert.True(await coordinator.TryAnswerPendingAsync(conversation, "Yes", CancellationToken.None));
        JsonObject response = Assert.IsType<JsonObject>(await responseTask.WaitAsync(TimeSpan.FromSeconds(5)));
        JsonObject answers = Assert.IsType<JsonObject>(response["answers"]);
        JsonObject answer = Assert.IsType<JsonObject>(answers["confirm_scope"]);
        JsonArray values = Assert.IsType<JsonArray>(answer["answers"]);
        Assert.Equal("Yes", values[0]!.GetValue<string>());
    }

    [Fact]
    public async Task TryAnswerCallbackAsync_RejectsCallbackFromAnotherConversation()
    {
        TelegramThreadFollowRegistry followRegistry = new();
        TelegramConversationScope origin = new(1234, 56);
        followRegistry.FollowThread(origin, "thread-plan");
        TestTelegramBotMessageSender sender = new();
        TelegramPlanInputCoordinator coordinator = new(
            followRegistry,
            sender,
            new TestApplicationLifetime(),
            NullLogger<TelegramPlanInputCoordinator>.Instance);

        Task<JsonObject?> responseTask = Task.Run(() => coordinator.HandleApprovalRequest(
            "item/tool/requestUserInput",
            CreateRequest()));
        await WaitUntilAsync(() => sender.Sent.Count == 1);
        string token = sender.Sent[0].Buttons!.SelectMany(row => row).First().CallbackData[5..];

        Assert.True(await coordinator.TryAnswerCallbackAsync(
            token,
            new TelegramConversationScope(1234, 57),
            "callback-other-scope",
            CancellationToken.None));
        Assert.Contains(sender.CallbackAnswers, answer => answer.Text == "That plan answer is not for this conversation.");
        Assert.False(responseTask.IsCompleted);

        Assert.True(await coordinator.TryAnswerPendingAsync(origin, "Yes", CancellationToken.None));
        Assert.NotNull(await responseTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TryAnswerCallbackAsync_RejectsExpiredPlanQuestion()
    {
        TelegramThreadFollowRegistry followRegistry = new();
        TelegramConversationScope conversation = new(1234, 56);
        followRegistry.FollowThread(conversation, "thread-plan");
        TestTelegramBotMessageSender sender = new();
        ManualTimeProvider clock = new(DateTimeOffset.Parse("2026-05-23T10:00:00Z"));
        TelegramPlanInputCoordinator coordinator = new(
            followRegistry,
            sender,
            new TestApplicationLifetime(),
            NullLogger<TelegramPlanInputCoordinator>.Instance,
            clock);

        Task<JsonObject?> responseTask = Task.Run(() => coordinator.HandleApprovalRequest(
            "item/tool/requestUserInput",
            CreateRequest()));
        await WaitUntilAsync(() => sender.Sent.Count == 1);
        string token = sender.Sent[0].Buttons!.SelectMany(row => row).First().CallbackData[5..];
        clock.Advance(TimeSpan.FromMinutes(31));

        Assert.True(await coordinator.TryAnswerCallbackAsync(token, conversation, "callback-expired", CancellationToken.None));
        Assert.Contains(sender.CallbackAnswers, answer => answer.Text == "That plan answer is no longer pending.");
        Assert.NotNull(await responseTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static JsonObject CreateRequest()
        => new()
        {
            ["threadId"] = "thread-plan",
            ["turnId"] = "turn-plan",
            ["questions"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "confirm_scope",
                    ["question"] = "Should I include tests?",
                    ["options"] = new JsonArray
                    {
                        new JsonObject { ["label"] = "Yes" },
                        new JsonObject { ["label"] = "No" },
                    },
                },
            },
        };

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token).ConfigureAwait(false);
        }
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

    private sealed class TestTelegramBotMessageSender : ITelegramBotMessageSender
    {
        public List<SentTelegramMessage> Sent { get; } = [];

        public List<CallbackAnswer> CallbackAnswers { get; } = [];

        public Task SendTextMessageAsync(
            TelegramConversationScope conversation,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
        {
            Sent.Add(new SentTelegramMessage(conversation, text, buttons));
            return Task.CompletedTask;
        }

        public Task EditTextMessageAsync(
            TelegramConversationScope conversation,
            int messageId,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
            => Task.CompletedTask;

        public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
        {
            CallbackAnswers.Add(new CallbackAnswer(callbackQueryId, text));
            return Task.CompletedTask;
        }

        public Task AcknowledgeMessageAsync(TelegramMessageAcknowledgement acknowledgement, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ReactToMessageAsync(TelegramMessageReaction reaction, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SendTypingActionAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed record SentTelegramMessage(
        TelegramConversationScope Conversation,
        string Text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? Buttons);

    private sealed record CallbackAnswer(string CallbackQueryId, string? Text);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
            => _utcNow;

        public void Advance(TimeSpan delta)
            => _utcNow = _utcNow.Add(delta);
    }
}
