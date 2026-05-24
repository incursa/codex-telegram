using System.Text.Json;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramBotClientMessageSenderTests
{
    [Fact]
    public async Task SendTextMessageAsync_WhenDisabledDoesNotCallTelegram()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = new(
            new TelegramBotOptions
            {
                Enabled = false,
            },
            NullLogger<TelegramBotClientMessageSender>.Instance,
            client);

        await sender.SendTextMessageAsync(
            new TelegramConversationScope(1234, null),
            "ignored",
            null,
            CancellationToken.None);

        Assert.Empty(client.SentMessages);
    }

    [Fact]
    public async Task SendTextMessageAndGetIdAsync_FullCaptureRecordsTelegramApiSuccess()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        TelegramDebugTraceStore traceStore = CreateTraceStore(dataRoot.Path);
        traceStore.EnableFullCapture(TimeSpan.FromMinutes(30));
        string traceId = traceStore.CreateTraceId();
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = CreateSender(client, traceStore: traceStore);

        int? messageId = await sender.SendTextMessageAndGetIdAsync(
            new TelegramConversationScope(1234, 55),
            "hello from bot",
            null,
            CancellationToken.None,
            new TelegramDebugMessageContext("test", TraceId: traceId));

        Assert.Equal(1001, messageId);
        string[] lines = ReadAllTraceLines(dataRoot.Path);
        Assert.Contains(lines, line => line.Contains("\"kind\":\"telegram.api.send.attempt\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"kind\":\"telegram.api.send.succeeded\"", StringComparison.Ordinal));
        Assert.All(lines, line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal("hello from bot", document.RootElement.GetProperty("textBody").GetString());
        });
    }

    [Fact]
    public async Task SendTextMessageAndGetIdAsync_FullCaptureRecordsTelegramApiFailure()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        TelegramDebugTraceStore traceStore = CreateTraceStore(dataRoot.Path);
        traceStore.EnableFullCapture(TimeSpan.FromMinutes(30));
        string traceId = traceStore.CreateTraceId();
        FakeTelegramBotApiClient client = new();
        client.SendFailures.Enqueue(new InvalidOperationException("failed with token=super-secret"));
        TelegramBotClientMessageSender sender = CreateSender(client, traceStore: traceStore);

        int? messageId = await sender.SendTextMessageAndGetIdAsync(
            new TelegramConversationScope(1234, 55),
            "hello from bot",
            null,
            CancellationToken.None,
            new TelegramDebugMessageContext("test", TraceId: traceId));

        Assert.Null(messageId);
        string[] lines = ReadAllTraceLines(dataRoot.Path);
        Assert.Contains(lines, line => line.Contains("\"kind\":\"telegram.api.send.failed\"", StringComparison.Ordinal));
        string combined = string.Join(Environment.NewLine, lines);
        Assert.Contains("redacted", combined);
        Assert.DoesNotContain("super-secret", combined);
    }

    [Fact]
    public async Task OutboundSendTextMessageAsync_WhenDisabledDoesNotCallTelegram()
    {
        FakeTelegramBotApiClient client = new();
        IOutboundTelegramMessageSender sender = new TelegramBotClientMessageSender(
            new TelegramBotOptions
            {
                Enabled = false,
            },
            NullLogger<TelegramBotClientMessageSender>.Instance,
            client);

        await sender.SendTextMessageAsync(new TelegramConversationScope(1234, null), "ignored", CancellationToken.None);

        Assert.Empty(client.SentMessages);
    }

    [Fact]
    public async Task EditTextMessageAsync_WhenDisabledDoesNotCallTelegram()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = new(
            new TelegramBotOptions
            {
                Enabled = false,
            },
            NullLogger<TelegramBotClientMessageSender>.Instance,
            client);

        await sender.EditTextMessageAsync(
            new TelegramConversationScope(1234, null),
            42,
            "ignored",
            null,
            CancellationToken.None);

        Assert.Empty(client.EditedMessages);
        Assert.Empty(client.SentMessages);
    }

    [Fact]
    public async Task AnswerCallbackQueryAsync_WhenDisabledDoesNotCallTelegram()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = new(
            new TelegramBotOptions
            {
                Enabled = false,
            },
            NullLogger<TelegramBotClientMessageSender>.Instance,
            client);

        await sender.AnswerCallbackQueryAsync("callback-1", "ignored", CancellationToken.None);

        Assert.Empty(client.CallbackAnswers);
    }

    [Fact]
    public async Task AcknowledgeMessageAsync_NormalMessageSendsTypingAction()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.AcknowledgeMessageAsync(
            new TelegramMessageAcknowledgement(new TelegramConversationScope(1234, 55), 42, null),
            CancellationToken.None);

        TelegramChatAction action = Assert.Single(client.ChatActions);
        Assert.Equal(1234, action.ChatId);
        Assert.Equal(55, action.MessageThreadId);
        Assert.Equal(TelegramChatActivity.Typing, action.Activity);
        TelegramReaction reaction = Assert.Single(client.Reactions);
        Assert.Equal(42, reaction.MessageId);
        Assert.Equal("\U0001F440", reaction.Emoji);
    }

    [Fact]
    public async Task ReactToMessageAsync_MetadataCaptureRecordsReactionAttemptAndSuccess()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        TelegramDebugTraceStore traceStore = CreateTraceStore(dataRoot.Path);
        traceStore.EnableMetadataCapture();
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = CreateSender(client, traceStore: traceStore);

        await sender.ReactToMessageAsync(
            new TelegramMessageReaction(new TelegramConversationScope(1234, 55), 42, TelegramMessageReactionKind.Completed),
            CancellationToken.None);

        string[] lines = ReadAllTraceLines(dataRoot.Path);
        Assert.Contains(lines, line => line.Contains("\"kind\":\"telegram.api.reaction.attempt\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"kind\":\"telegram.api.reaction.succeeded\"", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("textBody", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcknowledgeMessageAsync_BusinessMessageMarksMessageRead()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.AcknowledgeMessageAsync(
            new TelegramMessageAcknowledgement(new TelegramConversationScope(1234, null), 42, "business-1"),
            CancellationToken.None);

        BusinessRead read = Assert.Single(client.BusinessReads);
        Assert.Equal("business-1", read.BusinessConnectionId);
        Assert.Equal(1234, read.ChatId);
        Assert.Equal(42, read.MessageId);
        Assert.Empty(client.ChatActions);
        Assert.Single(client.Reactions);
    }

    [Fact]
    public async Task SendTypingActionAsync_NormalMessageSendsTypingAction()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.SendTypingActionAsync(new TelegramConversationScope(1234, 55), CancellationToken.None);

        TelegramChatAction action = Assert.Single(client.ChatActions);
        Assert.Equal(1234, action.ChatId);
        Assert.Equal(55, action.MessageThreadId);
        Assert.Equal(TelegramChatActivity.Typing, action.Activity);
    }

    [Fact]
    public async Task OutboundSendFileMessageAsync_PhotoSendsUploadActionAndRecordsContext()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string filePath = Path.Combine(temp.Path, "screenshot.png");
        await File.WriteAllBytesAsync(filePath, [0x89, 0x50, 0x4e, 0x47], CancellationToken.None);
        FakeTelegramBotApiClient client = new();
        TelegramMessageContextStore contextStore = new();
        IOutboundTelegramMessageSender sender = CreateSender(client, messageContextStore: contextStore);
        TelegramConversationScope conversation = new(1234, 55);

        await sender.SendFileMessageAsync(
            conversation,
            new OutboundTelegramFile
            {
                Kind = TelegramOutboundFileKind.Photo,
                Path = filePath,
                FileName = "screenshot.png",
                Caption = "Codex artifact: screenshot.png",
            },
            CancellationToken.None);

        TelegramChatAction action = Assert.Single(client.ChatActions);
        Assert.Equal(TelegramChatActivity.UploadPhoto, action.Activity);
        SentTelegramFile photo = Assert.Single(client.Photos);
        Assert.Equal(1234, photo.ChatId);
        Assert.Equal(55, photo.MessageThreadId);
        Assert.Equal(filePath, photo.FilePath);
        Assert.Equal("screenshot.png", photo.FileName);
        Assert.Equal("Codex artifact: screenshot.png", photo.Caption);
        Assert.Empty(client.Documents);

        TelegramReplyContext? context = await contextStore.ResolveReplyContextAsync(
            conversation,
            photo.MessageId,
            TelegramMessageAuthor.Bot,
            null,
            CancellationToken.None);
        Assert.NotNull(context);
        Assert.Equal("Codex artifact: screenshot.png", context.Text);
    }

    [Fact]
    public async Task OutboundSendFileMessageAsync_DocumentSendsUploadDocumentAction()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string filePath = Path.Combine(temp.Path, "capture.gif");
        await File.WriteAllBytesAsync(filePath, [0x47, 0x49, 0x46], CancellationToken.None);
        FakeTelegramBotApiClient client = new();
        IOutboundTelegramMessageSender sender = CreateSender(client);

        await sender.SendFileMessageAsync(
            new TelegramConversationScope(1234, null),
            new OutboundTelegramFile
            {
                Kind = TelegramOutboundFileKind.Document,
                Path = filePath,
                FileName = "capture.gif",
                Caption = "Codex artifact: capture.gif",
            },
            CancellationToken.None);

        Assert.Equal(TelegramChatActivity.UploadDocument, Assert.Single(client.ChatActions).Activity);
        SentTelegramFile document = Assert.Single(client.Documents);
        Assert.Equal(filePath, document.FilePath);
        Assert.Equal("capture.gif", document.FileName);
        Assert.Empty(client.Photos);
    }

    [Fact]
    public async Task OutboundSendFileMessageAsync_WhenFileIsMissingSendsFallbackText()
    {
        FakeTelegramBotApiClient client = new();
        IOutboundTelegramMessageSender sender = CreateSender(client);

        await sender.SendFileMessageAsync(
            new TelegramConversationScope(1234, null),
            new OutboundTelegramFile
            {
                Kind = TelegramOutboundFileKind.Photo,
                Path = "C:\\missing\\screenshot.png",
                FileName = "screenshot.png",
                Caption = "Codex artifact: screenshot.png",
            },
            CancellationToken.None);

        SentTelegramApiMessage sent = Assert.Single(client.SentMessages);
        Assert.Contains("file is no longer available", sent.Text);
        Assert.Contains("screenshot.png", sent.Text);
        Assert.Empty(client.ChatActions);
        Assert.Empty(client.Photos);
        Assert.Empty(client.Documents);
    }

    [Fact]
    public async Task ReactToMessageAsync_MapsSimpleStatusReactions()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = CreateSender(client);
        TelegramConversationScope conversation = new(1234, null);

        await sender.ReactToMessageAsync(new TelegramMessageReaction(conversation, 10, TelegramMessageReactionKind.Accepted), CancellationToken.None);
        await sender.ReactToMessageAsync(new TelegramMessageReaction(conversation, 11, TelegramMessageReactionKind.Completed), CancellationToken.None);
        await sender.ReactToMessageAsync(new TelegramMessageReaction(conversation, 12, TelegramMessageReactionKind.Failed), CancellationToken.None);

        Assert.Collection(
            client.Reactions,
            reaction => Assert.Equal("\U0001F440", reaction.Emoji),
            reaction => Assert.Equal("\u2705", reaction.Emoji),
            reaction => Assert.Equal("\U0001F628", reaction.Emoji));
    }

    [Fact]
    public async Task SendTextMessageAsync_MainChatSendKeepsThreadNull()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.SendTextMessageAsync(
            new TelegramConversationScope(1234, null),
            "main chat status",
            null,
            CancellationToken.None);

        SentTelegramApiMessage sent = Assert.Single(client.SentMessages);
        Assert.Equal(1234, sent.ChatId);
        Assert.Null(sent.MessageThreadId);
        Assert.Equal("main chat status", sent.Text);
    }

    [Fact]
    public async Task SendTextMessageAsync_RecordsMessageContext()
    {
        FakeTelegramBotApiClient client = new();
        TelegramMessageContextStore contextStore = new();
        TelegramBotClientMessageSender sender = CreateSender(client, messageContextStore: contextStore);
        TelegramConversationScope conversation = new(1234, null);

        await sender.SendTextMessageAsync(conversation, "main chat status", null, CancellationToken.None);

        TelegramReplyContext? context = await contextStore.ResolveReplyContextAsync(
            conversation,
            1001,
            TelegramMessageAuthor.Bot,
            null,
            CancellationToken.None);
        Assert.NotNull(context);
        Assert.Equal("main chat status", context.Text);
        Assert.Equal(TelegramMessageAuthor.Bot, context.Author);
    }

    [Fact]
    public async Task SendTextMessageAsync_WhenDebugPreambleIsEnabledPrependsDiagnosticsAndRecordsCleanContext()
    {
        FakeTelegramBotApiClient client = new();
        TelegramMessageContextStore contextStore = new();
        TelegramBotClientMessageSender sender = CreateSender(
            client,
            messageContextStore: contextStore,
            debugPreambleMode: new TestTelegramDebugPreambleMode { IsEnabled = true });
        TelegramConversationScope conversation = new(1234, 55);

        await sender.SendTextMessageAsync(
            conversation,
            "body text",
            null,
            CancellationToken.None,
            new TelegramDebugMessageContext("reply", "thread-1", "turn-1", "turn-2", "Update", "msg-1", 2));

        SentTelegramApiMessage sent = Assert.Single(client.SentMessages);
        Assert.StartsWith("[codex-debug source=reply chat=1234 topic=55 session=thread-1 turn=turn-1 activeTurn=turn-2 kind=Update items=2 msg=msg-1]", sent.Text, StringComparison.Ordinal);
        Assert.EndsWith("body text", sent.Text, StringComparison.Ordinal);

        TelegramReplyContext? context = await contextStore.ResolveReplyContextAsync(
            conversation,
            1001,
            TelegramMessageAuthor.Bot,
            null,
            CancellationToken.None);
        Assert.NotNull(context);
        Assert.Equal("body text", context.Text);
    }

    [Fact]
    public async Task SendTextMessageAsync_WithButtonsSendsInlineKeyboardRows()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.SendTextMessageAsync(
            new TelegramConversationScope(1234, 55),
            "choose",
            [
                [new TelegramReplyButton("Status", "status:1")],
                [new TelegramReplyButton("Tail", "tail:1"), new TelegramReplyButton("Stop", "stop:1")],
            ],
            CancellationToken.None);

        SentTelegramApiMessage sent = Assert.Single(client.SentMessages);
        Assert.NotNull(sent.ReplyMarkup);
        InlineKeyboardMarkup replyMarkup = sent.ReplyMarkup;
        Assert.Collection(
            replyMarkup.InlineKeyboard,
            row =>
            {
                InlineKeyboardButton button = Assert.Single(row);
                Assert.Equal("Status", button.Text);
                Assert.Equal("status:1", button.CallbackData);
            },
            row =>
            {
                InlineKeyboardButton[] buttons = row.ToArray();
                Assert.Equal("Tail", buttons[0].Text);
                Assert.Equal("tail:1", buttons[0].CallbackData);
                Assert.Equal("Stop", buttons[1].Text);
                Assert.Equal("stop:1", buttons[1].CallbackData);
            });
    }

    [Fact]
    public async Task SendTextMessageAsync_TopicThreadFailureDoesNotRetryInMainChat()
    {
        FakeTelegramBotApiClient client = new();
        client.SendFailures.Enqueue(new ApiRequestException("Bad Request: message thread not found", 400));
        TestLogger<TelegramBotClientMessageSender> logger = new();
        TelegramBotClientMessageSender sender = CreateSender(client, logger);

        await sender.SendTextMessageAsync(
            new TelegramConversationScope(-100123456, 55),
            "topic-scoped status",
            null,
            CancellationToken.None);

        SentTelegramApiMessage sent = Assert.Single(client.SentMessages);
        Assert.Equal(-100123456, sent.ChatId);
        Assert.Equal(55, sent.MessageThreadId);
        Assert.Equal("topic-scoped status", sent.Text);
        LogEntry entry = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("not retried in the main chat", entry.Message);
        Assert.IsType<ApiRequestException>(entry.Exception);
    }

    [Fact]
    public async Task SendTextMessageAsync_GenericFailureLogsAndContinues()
    {
        FakeTelegramBotApiClient client = new();
        client.SendFailures.Enqueue(new InvalidOperationException("telegram transport failed"));
        TestLogger<TelegramBotClientMessageSender> logger = new();
        TelegramBotClientMessageSender sender = CreateSender(client, logger);

        await sender.SendTextMessageAsync(
            new TelegramConversationScope(1234, null),
            "status",
            null,
            CancellationToken.None);

        Assert.Single(client.SentMessages);
        LogEntry entry = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("Telegram send failed for chat 1234", entry.Message);
        Assert.Contains("will continue running", entry.Message);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public async Task SendTextMessageAsync_WhenCancellationIsRequestedRethrows()
    {
        FakeTelegramBotApiClient client = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        client.SendFailures.Enqueue(new OperationCanceledException(cancellation.Token));
        TelegramBotClientMessageSender sender = CreateSender(client);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sender.SendTextMessageAsync(
                new TelegramConversationScope(1234, null),
                "status",
                null,
                cancellation.Token));
    }

    [Fact]
    public async Task OutboundSendTextMessageAsync_TopicThreadFailureThrowsWithoutRetryingMainChat()
    {
        FakeTelegramBotApiClient client = new();
        client.SendFailures.Enqueue(new ApiRequestException("Bad Request: topic was closed", 400));
        IOutboundTelegramMessageSender sender = CreateSender(client);

        TelegramTopicSendException exception = await Assert.ThrowsAsync<TelegramTopicSendException>(
            () => sender.SendTextMessageAsync(
                new TelegramConversationScope(-100123456, 55),
                "topic-scoped Codex output",
                CancellationToken.None));

        Assert.IsType<ApiRequestException>(exception.InnerException);
        Assert.Contains("chat -100123456 topic 55", exception.Message);
        SentTelegramApiMessage sent = Assert.Single(client.SentMessages);
        Assert.Equal(55, sent.MessageThreadId);
    }

    [Fact]
    public async Task OutboundSendTextMessageAsync_MainChatThreadFailurePropagatesApiException()
    {
        FakeTelegramBotApiClient client = new();
        client.SendFailures.Enqueue(new ApiRequestException("Bad Request: topic was closed", 400));
        IOutboundTelegramMessageSender sender = CreateSender(client);

        ApiRequestException exception = await Assert.ThrowsAsync<ApiRequestException>(
            () => sender.SendTextMessageAsync(
                new TelegramConversationScope(-100123456, null),
                "main chat Codex output",
                CancellationToken.None));

        Assert.Contains("topic was closed", exception.Message);
        SentTelegramApiMessage sent = Assert.Single(client.SentMessages);
        Assert.Null(sent.MessageThreadId);
    }

    [Fact]
    public async Task OutboundSendTextMessageAsync_RateLimitThrowsRetryAfter()
    {
        FakeTelegramBotApiClient client = new();
        client.SendFailures.Enqueue(new ApiRequestException(
            "Too Many Requests: retry after 7",
            429,
            new global::Telegram.Bot.Types.ResponseParameters { RetryAfter = 7 }));
        IOutboundTelegramMessageSender sender = CreateSender(client);

        TelegramOutboundRateLimitException exception = await Assert.ThrowsAsync<TelegramOutboundRateLimitException>(
            () => sender.SendTextMessageAsync(
                new TelegramConversationScope(1234, null),
                "rate limited output",
                CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(7), exception.RetryAfter);
        Assert.Single(client.SentMessages);
    }

    [Theory]
    [InlineData(400, "Too Many Requests: flood control exceeded")]
    [InlineData(400, "Bad Request: retry after 5")]
    public async Task OutboundSendTextMessageAsync_RateLimitTextThrowsWithoutRetryAfter(int errorCode, string message)
    {
        FakeTelegramBotApiClient client = new();
        client.SendFailures.Enqueue(new ApiRequestException(message, errorCode));
        IOutboundTelegramMessageSender sender = CreateSender(client);

        TelegramOutboundRateLimitException exception = await Assert.ThrowsAsync<TelegramOutboundRateLimitException>(
            () => sender.SendTextMessageAsync(
                new TelegramConversationScope(1234, null),
                "rate limited output",
                CancellationToken.None));

        Assert.Null(exception.RetryAfter);
        Assert.Single(client.SentMessages);
    }

    [Fact]
    public async Task OutboundSendTextMessageAsync_ZeroRetryAfterKeepsRetryAfterNull()
    {
        FakeTelegramBotApiClient client = new();
        client.SendFailures.Enqueue(new ApiRequestException(
            "Too Many Requests: retry after 0",
            429,
            new global::Telegram.Bot.Types.ResponseParameters { RetryAfter = 0 }));
        IOutboundTelegramMessageSender sender = CreateSender(client);

        TelegramOutboundRateLimitException exception = await Assert.ThrowsAsync<TelegramOutboundRateLimitException>(
            () => sender.SendTextMessageAsync(
                new TelegramConversationScope(1234, null),
                "rate limited output",
                CancellationToken.None));

        Assert.Null(exception.RetryAfter);
        Assert.Single(client.SentMessages);
    }

    [Fact]
    public async Task EditTextMessageAsync_SuccessUsesEditWithoutFallbackSend()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.EditTextMessageAsync(
            new TelegramConversationScope(1234, null),
            42,
            "updated card",
            null,
            CancellationToken.None);

        EditedTelegramApiMessage edit = Assert.Single(client.EditedMessages);
        Assert.Equal(1234, edit.ChatId);
        Assert.Equal(42, edit.MessageId);
        Assert.Equal("updated card", edit.Text);
        Assert.Empty(client.SentMessages);
    }

    [Fact]
    public async Task EditTextMessageAsync_MessageNotModifiedDoesNotFallbackSend()
    {
        FakeTelegramBotApiClient client = new();
        client.EditFailures.Enqueue(new ApiRequestException("Bad Request: message is not modified", 400));
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.EditTextMessageAsync(
            new TelegramConversationScope(1234, null),
            42,
            "same card",
            null,
            CancellationToken.None);

        Assert.Single(client.EditedMessages);
        Assert.Empty(client.SentMessages);
    }

    [Fact]
    public async Task EditTextMessageAsync_WhenCancellationIsRequestedRethrows()
    {
        FakeTelegramBotApiClient client = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        client.EditFailures.Enqueue(new OperationCanceledException(cancellation.Token));
        TelegramBotClientMessageSender sender = CreateSender(client);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sender.EditTextMessageAsync(
                new TelegramConversationScope(1234, null),
                42,
                "status",
                null,
                cancellation.Token));
    }

    [Fact]
    public async Task EditTextMessageAsync_GenericFailureFallsBackToSend()
    {
        FakeTelegramBotApiClient client = new();
        client.EditFailures.Enqueue(new InvalidOperationException("telegram edit transport failed"));
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.EditTextMessageAsync(
            new TelegramConversationScope(1234, 55),
            42,
            "replacement card",
            null,
            CancellationToken.None);

        Assert.Single(client.EditedMessages);
        SentTelegramApiMessage sent = Assert.Single(client.SentMessages);
        Assert.Equal(55, sent.MessageThreadId);
        Assert.Equal("replacement card", sent.Text);
    }

    [Fact]
    public async Task EditTextMessageOrSendReplacementAsync_GenericFailureReturnsReplacementMessageId()
    {
        FakeTelegramBotApiClient client = new();
        client.EditFailures.Enqueue(new InvalidOperationException("telegram edit transport failed"));
        TelegramBotClientMessageSender sender = CreateSender(client);

        int? messageId = await sender.EditTextMessageOrSendReplacementAsync(
            new TelegramConversationScope(1234, 55),
            42,
            "replacement card",
            null,
            CancellationToken.None);

        Assert.Equal(1001, messageId);
        Assert.Single(client.EditedMessages);
        Assert.Single(client.SentMessages);
    }

    [Fact]
    public async Task EditTextMessageAsync_GenericFailureLogsFallbackReason()
    {
        FakeTelegramBotApiClient client = new();
        client.EditFailures.Enqueue(new InvalidOperationException("telegram edit transport failed"));
        TestLogger<TelegramBotClientMessageSender> logger = new();
        TelegramBotClientMessageSender sender = CreateSender(client, logger);

        await sender.EditTextMessageAsync(
            new TelegramConversationScope(1234, 55),
            42,
            "replacement card",
            null,
            CancellationToken.None);

        LogEntry entry = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("falling back to a new message", entry.Message);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public async Task AnswerCallbackQueryAsync_SuccessCallsTelegram()
    {
        FakeTelegramBotApiClient client = new();
        TelegramBotClientMessageSender sender = CreateSender(client);

        await sender.AnswerCallbackQueryAsync("callback-1", "Updated", CancellationToken.None);

        CallbackAnswer answer = Assert.Single(client.CallbackAnswers);
        Assert.Equal("callback-1", answer.CallbackQueryId);
        Assert.Equal("Updated", answer.Text);
    }

    [Fact]
    public async Task AnswerCallbackQueryAsync_GenericFailureLogsAndContinues()
    {
        FakeTelegramBotApiClient client = new();
        client.AnswerFailures.Enqueue(new InvalidOperationException("telegram callback failed"));
        TestLogger<TelegramBotClientMessageSender> logger = new();
        TelegramBotClientMessageSender sender = CreateSender(client, logger);

        await sender.AnswerCallbackQueryAsync("callback-1", "Updated", CancellationToken.None);

        Assert.Single(client.CallbackAnswers);
        LogEntry entry = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("callback callback-1", entry.Message);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public async Task AnswerCallbackQueryAsync_WhenCancellationIsRequestedRethrows()
    {
        FakeTelegramBotApiClient client = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        client.AnswerFailures.Enqueue(new OperationCanceledException(cancellation.Token));
        TelegramBotClientMessageSender sender = CreateSender(client);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sender.AnswerCallbackQueryAsync("callback-1", "Updated", cancellation.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task SendTextMessageAsync_PublicConstructorWithMissingTokenLogsConfigurationFailure(string? token)
    {
        TestLogger<TelegramBotClientMessageSender> logger = new();
        TelegramBotClientMessageSender sender = new(
            Microsoft.Extensions.Options.Options.Create(new TelegramBotOptions
            {
                Enabled = true,
                Token = token,
            }),
            logger);

        await sender.SendTextMessageAsync(
            new TelegramConversationScope(1234, null),
            "status",
            null,
            CancellationToken.None);

        LogEntry entry = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("TelegramBot:Token must be configured", entry.Exception?.Message);
    }

    private static TelegramBotClientMessageSender CreateSender(
        FakeTelegramBotApiClient client,
        ILogger<TelegramBotClientMessageSender>? logger = null,
        ITelegramMessageContextStore? messageContextStore = null,
        ITelegramDebugPreambleMode? debugPreambleMode = null,
        ITelegramDebugTraceStore? traceStore = null)
        => new(
            new TelegramBotOptions
            {
                Enabled = true,
                Token = "123:token",
            },
            logger ?? NullLogger<TelegramBotClientMessageSender>.Instance,
            client,
            messageContextStore,
            debugPreambleMode,
            traceStore);

    private static TelegramDebugTraceStore CreateTraceStore(string dataRoot)
        => new(
            Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
            {
                Workspace = new CodexWorkspaceOptions
                {
                    DataRoot = dataRoot,
                },
            }),
            Microsoft.Extensions.Options.Options.Create(new TelegramDebugTraceOptions()));

    private static string[] ReadAllTraceLines(string dataRoot)
    {
        string root = Path.Combine(dataRoot, "telegram-traces");
        string file = Assert.Single(Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories));
        return File.ReadAllLines(file);
    }

    private sealed class FakeTelegramBotApiClient : ITelegramBotApiClient
    {
        public List<SentTelegramApiMessage> SentMessages { get; } = [];

        public List<EditedTelegramApiMessage> EditedMessages { get; } = [];

        public List<CallbackAnswer> CallbackAnswers { get; } = [];

        public List<TelegramChatAction> ChatActions { get; } = [];

        public List<BusinessRead> BusinessReads { get; } = [];

        public List<SentTelegramFile> Photos { get; } = [];

        public List<SentTelegramFile> Documents { get; } = [];

        public List<TelegramReaction> Reactions { get; } = [];

        public Queue<Exception> SendFailures { get; } = new();

        public Queue<Exception> EditFailures { get; } = new();

        public Queue<Exception> AnswerFailures { get; } = new();

        public Task<int> SendMessageAsync(
            long chatId,
            string text,
            InlineKeyboardMarkup? replyMarkup,
            int? messageThreadId,
            CancellationToken cancellationToken)
        {
            int messageId = 1000 + SentMessages.Count + 1;
            SentMessages.Add(new SentTelegramApiMessage(messageId, chatId, text, messageThreadId, replyMarkup));
            if (SendFailures.Count > 0)
            {
                throw SendFailures.Dequeue();
            }

            return Task.FromResult(messageId);
        }

        public Task EditMessageTextAsync(
            long chatId,
            int messageId,
            string text,
            InlineKeyboardMarkup? replyMarkup,
            CancellationToken cancellationToken)
        {
            EditedMessages.Add(new EditedTelegramApiMessage(chatId, messageId, text, replyMarkup));
            if (EditFailures.Count > 0)
            {
                throw EditFailures.Dequeue();
            }

            return Task.CompletedTask;
        }

        public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
        {
            CallbackAnswers.Add(new CallbackAnswer(callbackQueryId, text));
            if (AnswerFailures.Count > 0)
            {
                throw AnswerFailures.Dequeue();
            }

            return Task.CompletedTask;
        }

        public Task SendChatActionAsync(long chatId, int? messageThreadId, TelegramChatActivity activity, CancellationToken cancellationToken)
        {
            ChatActions.Add(new TelegramChatAction(chatId, messageThreadId, activity));
            return Task.CompletedTask;
        }

        public Task<int> SendPhotoAsync(
            long chatId,
            string filePath,
            string fileName,
            string? caption,
            int? messageThreadId,
            CancellationToken cancellationToken)
        {
            int messageId = 2000 + Photos.Count + 1;
            Photos.Add(new SentTelegramFile(messageId, chatId, filePath, fileName, caption, messageThreadId));
            return Task.FromResult(messageId);
        }

        public Task<int> SendDocumentAsync(
            long chatId,
            string filePath,
            string fileName,
            string? caption,
            int? messageThreadId,
            CancellationToken cancellationToken)
        {
            int messageId = 3000 + Documents.Count + 1;
            Documents.Add(new SentTelegramFile(messageId, chatId, filePath, fileName, caption, messageThreadId));
            return Task.FromResult(messageId);
        }

        public Task SetMessageReactionAsync(long chatId, int messageId, string emoji, bool isBig, CancellationToken cancellationToken)
        {
            Reactions.Add(new TelegramReaction(chatId, messageId, emoji, isBig));
            return Task.CompletedTask;
        }

        public Task ReadBusinessMessageAsync(string businessConnectionId, long chatId, int messageId, CancellationToken cancellationToken)
        {
            BusinessReads.Add(new BusinessRead(businessConnectionId, chatId, messageId));
            return Task.CompletedTask;
        }
    }

    private sealed record SentTelegramApiMessage(
        int MessageId,
        long ChatId,
        string Text,
        int? MessageThreadId,
        InlineKeyboardMarkup? ReplyMarkup);

    private sealed record EditedTelegramApiMessage(long ChatId, int MessageId, string Text, InlineKeyboardMarkup? ReplyMarkup);

    private sealed record CallbackAnswer(string CallbackQueryId, string? Text);

    private sealed record TelegramChatAction(long ChatId, int? MessageThreadId, TelegramChatActivity Activity);

    private sealed record BusinessRead(string BusinessConnectionId, long ChatId, int MessageId);

    private sealed record SentTelegramFile(int MessageId, long ChatId, string FilePath, string FileName, string? Caption, int? MessageThreadId);

    private sealed record TelegramReaction(long ChatId, int MessageId, string Emoji, bool IsBig);

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class TestTelegramDebugPreambleMode : ITelegramDebugPreambleMode
    {
        public bool IsEnabled { get; init; }

        public bool ConfiguredDefaultEnabled => IsEnabled;

        public bool? RuntimeOverrideEnabled => null;

        public void SetRuntimeOverride(bool enabled)
        {
        }

        public void ClearRuntimeOverride()
        {
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
