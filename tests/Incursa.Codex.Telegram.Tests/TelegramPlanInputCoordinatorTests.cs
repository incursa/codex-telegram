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
            => Task.CompletedTask;

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
}
