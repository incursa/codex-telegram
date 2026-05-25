using System.Text.Json;
using System.Threading.Channels;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class OutboundTelegramQueueTests
{
    private static readonly DateTimeOffset TestNow = DateTimeOffset.Parse("2026-05-04T00:00:00Z");

    [Fact]
    public async Task EnqueueAsync_DropsProgressWhenProgressMessagesAreDisabled()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            IncludeProgressMessages = false,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Progress, "working"), CancellationToken.None);

        TelegramOutboundQueueStatus status = await scheduler.GetStatusAsync(CancellationToken.None);

        Assert.Equal(0, status.PendingMessageCount);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task EnqueueAsync_DropsMessagesWhenOutboundIsDisabledOrTextIsBlank()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler disabledScheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            Enabled = false,
        });

        await disabledScheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "disabled"), CancellationToken.None);

        OutboundTelegramScheduler blankTextScheduler = CreateScheduler(sender, new TelegramOutboundOptions());

        await blankTextScheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "   "), CancellationToken.None);

        Assert.Equal(0, (await disabledScheduler.GetStatusAsync(CancellationToken.None)).PendingMessageCount);
        Assert.Equal(0, (await blankTextScheduler.GetStatusAsync(CancellationToken.None)).PendingMessageCount);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task EnqueueAsync_AllowsBlankTextWhenFilePayloadIsPresent()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
        });

        await scheduler.EnqueueAsync(CreateFileMessage(
            new OutboundTelegramFile
            {
                Kind = TelegramOutboundFileKind.Photo,
                Path = " C:\\temp\\codex.png ",
                FileName = " codex.png ",
                Caption = " shown screenshot ",
            },
            text: "   "),
            CancellationToken.None);

        Assert.Equal(1, (await scheduler.GetStatusAsync(CancellationToken.None)).PendingMessageCount);
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        SentTelegramFileMessage sent = Assert.Single(sender.SentFiles);
        Assert.Equal(TelegramOutboundFileKind.Photo, sent.File.Kind);
        Assert.Equal("C:\\temp\\codex.png", sent.File.Path);
        Assert.Equal("codex.png", sent.File.FileName);
        Assert.Equal("shown screenshot", sent.File.Caption);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task EnqueueAsync_WhenCancelledThrowsAndDoesNotQueue()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions());
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await scheduler.EnqueueAsync(
                CreateMessage(CodexOutboundMessageKind.Update, "cancelled"),
                cancellation.Token));

        Assert.Equal(0, (await scheduler.GetStatusAsync(CancellationToken.None)).PendingMessageCount);
    }

    [Fact]
    public async Task GetStatusAsync_WhenCancelledThrows()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions());
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => scheduler.GetStatusAsync(cancellation.Token));
    }

    [Fact]
    public async Task EnqueueAsync_UsesUpdatedOutboundOptionsFromMonitor()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        MutableOptionsMonitor<TelegramOutboundOptions> options = new(new TelegramOutboundOptions
        {
            Enabled = false,
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
        });
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, options, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "dropped"), CancellationToken.None);
        options.Update(new TelegramOutboundOptions
        {
            Enabled = true,
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
        });
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "sent"), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.Equal("sent", Assert.Single(sender.Sent).Text);
    }

    [Fact]
    public async Task ExecuteAsync_WakesImmediatelyWhenWorkIsEnqueuedDuringLongFlushDelay()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
            FlushIntervalMilliseconds = TelegramOutboundLimits.MaxFlushIntervalMilliseconds,
        });

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(200);
            await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "wake now"), CancellationToken.None);

            SentTelegramMessage sent = await sender.NextSend.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("wake now", sent.Text);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ObserveDeliveries_EmitsOnlyReadyDeliveriesAndKeepsQueueStateUpstream()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 120,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
            FlushIntervalMilliseconds = TelegramOutboundLimits.MinFlushIntervalMilliseconds,
        });
        TestDeliveryObserver observer = new();
        using IDisposable subscription = scheduler.ObserveDeliveries().Subscribe(observer);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "normal waits", chatId: 1, omitCreatedUtc: true), CancellationToken.None);

        Assert.Null(await observer.ReadOrDefaultAsync(TimeSpan.FromMilliseconds(200)));

        await scheduler.EnqueueAsync(
            CreateMessage(CodexOutboundMessageKind.Error, "urgent now", chatId: 2, priority: OutboundPriority.High),
            CancellationToken.None);

        OutboundTelegramDelivery delivery = await observer.ReadRequiredAsync();

        Assert.Equal(2, delivery.Conversation.ChatId);
        Assert.Equal("urgent now", delivery.Text);

        TelegramOutboundQueueStatus inFlight = await scheduler.GetStatusAsync(CancellationToken.None);
        Assert.Contains(inFlight.Destinations, destination => destination.ChatId == 1 && destination.PendingMessageCount == 1);
        Assert.Contains(inFlight.Destinations, destination => destination.ChatId == 2 && destination.PendingChunkCount == 1);

        delivery.Complete();

        await WaitForConditionAsync(async () =>
        {
            TelegramOutboundQueueStatus status = await scheduler.GetStatusAsync(CancellationToken.None);
            return status.Destinations.Any(destination => destination.ChatId == 1 && destination.PendingMessageCount == 1)
                && status.Destinations.All(destination => destination.ChatId != 2);
        });
    }

    [Fact]
    public async Task ObserveDeliveries_KeepsDispatchedChunkPendingUntilSubscriberCompletes()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
            FlushIntervalMilliseconds = TelegramOutboundLimits.MinFlushIntervalMilliseconds,
        });
        TestDeliveryObserver observer = new();
        using IDisposable subscription = scheduler.ObserveDeliveries().Subscribe(observer);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "observable send"), CancellationToken.None);

        OutboundTelegramDelivery delivery = await observer.ReadRequiredAsync();
        TelegramOutboundQueueStatus inFlight = await scheduler.GetStatusAsync(CancellationToken.None);

        Assert.Equal("observable send", delivery.Text);
        Assert.Equal(0, inFlight.PendingMessageCount);
        Assert.Equal(1, inFlight.PendingChunkCount);

        delivery.Complete();

        await WaitForConditionAsync(async () =>
            (await scheduler.GetStatusAsync(CancellationToken.None)).PendingChunkCount == 0);
    }

    [Fact]
    public async Task ObserveDeliveries_AppliesBackoffWhenSubscriberReportsRateLimit()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
            FlushIntervalMilliseconds = TelegramOutboundLimits.MinFlushIntervalMilliseconds,
        }, timeProvider);
        TestDeliveryObserver observer = new();
        using IDisposable subscription = scheduler.ObserveDeliveries().Subscribe(observer);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "retry later", chatId: 9), CancellationToken.None);

        OutboundTelegramDelivery delivery = await observer.ReadRequiredAsync();
        delivery.Fail(new TelegramOutboundRateLimitException("limited", TimeSpan.FromSeconds(12)));

        await WaitForConditionAsync(async () =>
        {
            TelegramOutboundQueueStatus status = await scheduler.GetStatusAsync(CancellationToken.None);
            TelegramOutboundDestinationStatus? destination = status.Destinations.SingleOrDefault(item => item.ChatId == 9);
            return destination is { PendingChunkCount: 1 }
                && destination.ChatBackoffUntilUtc == TestNow + TimeSpan.FromSeconds(12);
        });
    }

    [Fact]
    public async Task DeliveryHostedService_SendsObservableDeliveriesAndCompletesQueueItem()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
            FlushIntervalMilliseconds = TelegramOutboundLimits.MinFlushIntervalMilliseconds,
        });
        OutboundTelegramDeliveryHostedService service = new(
            scheduler,
            sender,
            NullLogger<OutboundTelegramDeliveryHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "sent by subscriber"), CancellationToken.None);

            SentTelegramMessage sent = await sender.NextSend.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal("sent by subscriber", sent.Text);
            await WaitForConditionAsync(async () =>
                (await scheduler.GetStatusAsync(CancellationToken.None)).PendingDestinationCount == 0);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeliveryHostedService_WhenSendFailsReportsFailureBackToQueue()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new() { ThrowOnSend = true };
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            GroupMinimumSendIntervalSeconds = 7,
            PrivateMinimumSendIntervalSeconds = 0,
            FlushIntervalMilliseconds = TelegramOutboundLimits.MinFlushIntervalMilliseconds,
        }, timeProvider);
        OutboundTelegramDeliveryHostedService service = new(
            scheduler,
            sender,
            NullLogger<OutboundTelegramDeliveryHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "still queued", chatId: -100), CancellationToken.None);

            await WaitForConditionAsync(async () =>
            {
                TelegramOutboundQueueStatus status = await scheduler.GetStatusAsync(CancellationToken.None);
                TelegramOutboundDestinationStatus? destination = status.Destinations.SingleOrDefault(item => item.ChatId == -100);
                return destination is { PendingChunkCount: 1 }
                    && destination.ChatBackoffUntilUtc == TestNow + TimeSpan.FromSeconds(7);
            });
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ProcessNextAsync_SendsNormalUpdateWithDefaultBatchWindow()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
        }, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "ready now"), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.Equal("ready now", Assert.Single(sender.Sent).Text);
    }

    [Fact]
    public async Task ProcessNextAsync_FullCaptureRecordsOutboundQueuedAndSentText()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        TelegramDebugTraceStore traceStore = CreateTraceStore(dataRoot.Path);
        traceStore.EnableFullCapture(TimeSpan.FromMinutes(30));
        string traceId = traceStore.CreateTraceId();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
        }, timeProvider, traceStore);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Completion, "final chunk", turnId: "turn-1", traceId: traceId), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        string[] lines = await File.ReadAllLinesAsync(traceStore.GetTracePath(traceId, TestNow), CancellationToken.None);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal("final chunk", document.RootElement.GetProperty("textBody").GetString());
        });
        Assert.Contains(lines, line => line.Contains("\"kind\":\"telegram.outbound.enqueue\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"kind\":\"telegram.outbound.sent\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessNextAsync_RespectsBatchWindowAfterWakeSignal()
    {
        TestTelegramSender sender = new();
        TestTimeProvider timeProvider = new(TestNow);
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 120,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
        }, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "not ready yet"), CancellationToken.None);

        Assert.False(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task EnqueueAsync_TrimsTextAndUsesCurrentTimeWhenCreatedUtcIsMissing()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
        }, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "  trimmed text  ", omitCreatedUtc: true), CancellationToken.None);

        TelegramOutboundQueueStatus status = await scheduler.GetStatusAsync(CancellationToken.None);
        bool processed = await scheduler.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(TestNow, status.OldestFirstPendingUtc);
        Assert.True(processed);
        Assert.Equal("trimmed text", Assert.Single(sender.Sent).Text);
    }

    [Fact]
    public async Task GetStatusAsync_OrdersDestinationsAndReportsOldestPendingWork()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            IncludeProgressMessages = true,
        }, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "newer", chatId: 3, messageThreadId: 2, createdUtc: TestNow.AddSeconds(20)), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "oldest", chatId: 2, messageThreadId: 5, sessionId: "thread-old", createdUtc: TestNow.AddSeconds(10)), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "tie by chat", chatId: 1, messageThreadId: 7, createdUtc: TestNow.AddSeconds(20)), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "tie by topic", chatId: 1, messageThreadId: null, createdUtc: TestNow.AddSeconds(20)), CancellationToken.None);

        TelegramOutboundQueueStatus status = await scheduler.GetStatusAsync(CancellationToken.None);

        Assert.Equal(4, status.PendingDestinationCount);
        Assert.Equal(4, status.PendingMessageCount);
        Assert.Equal(0, status.PendingChunkCount);
        Assert.Equal("neweroldesttie by chattie by topic".Length, status.PendingCharacterCount);
        Assert.Equal(new TelegramDestinationKey(2, 5), status.OldestWaitingDestination);
        Assert.Equal(TestNow.AddSeconds(10), status.OldestFirstPendingUtc);
        Assert.Collection(
            status.Destinations,
            destination =>
            {
                Assert.Equal(2, destination.ChatId);
                Assert.Equal(5, destination.MessageThreadId);
                Assert.Equal("thread-old", destination.SessionId);
            },
            destination =>
            {
                Assert.Equal(1, destination.ChatId);
                Assert.Null(destination.MessageThreadId);
            },
            destination =>
            {
                Assert.Equal(1, destination.ChatId);
                Assert.Equal(7, destination.MessageThreadId);
            },
            destination =>
            {
                Assert.Equal(3, destination.ChatId);
                Assert.Equal(2, destination.MessageThreadId);
            });
    }

    [Fact]
    public async Task ProcessNextAsync_SendsSameDestinationMessagesIndividually()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
            MaxMessageChars = 3500,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "first update"), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Completion, "second update"), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        Assert.Collection(
            sender.Sent,
            message =>
            {
                Assert.Equal(1234, message.Conversation.ChatId);
                Assert.Equal("first update", message.Text);
            },
            message =>
            {
                Assert.Equal(1234, message.Conversation.ChatId);
                Assert.Equal("second update", message.Text);
            });
    }

    [Fact]
    public async Task ProcessNextAsync_SendsFilePayloadAsStandaloneItemBetweenTextMessages()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
        });
        OutboundTelegramFile file = new()
        {
            Kind = TelegramOutboundFileKind.Document,
            Path = "C:\\temp\\capture.gif",
            FileName = "capture.gif",
            Caption = "Codex artifact: capture.gif",
        };

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "before file"), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateFileMessage(file), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "after file"), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.Equal("before file", Assert.Single(sender.Sent).Text);
        Assert.Empty(sender.SentFiles);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        SentTelegramFileMessage sentFile = Assert.Single(sender.SentFiles);
        Assert.Equal(file, sentFile.File);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal("after file", sender.Sent[1].Text);
    }

    [Fact]
    public async Task ProcessNextAsync_PassesDebugContextForIndividualMessages()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
            MaxMessageChars = 3500,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "first update", sessionId: "thread-1", turnId: "turn-1"), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "second update", sessionId: "thread-1", turnId: "turn-1"), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        TelegramDebugMessageContext context = Assert.Single(sender.Sent).DebugContext!;
        Assert.Equal("outbound", context.Source);
        Assert.Equal("thread-1", context.SessionId);
        Assert.Equal("turn-1", context.TurnId);
        Assert.Equal("Update", context.Kind);
        Assert.Equal(1, context.ItemCount);
    }

    [Fact]
    public async Task ProcessNextAsync_SendsFinishedMarkerAsStandaloneMessageAfterContent()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
            MaxMessageChars = 3500,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "first update"), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Completion, "second update"), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Completion, "~~ turn complete ~~"), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        Assert.Collection(
            sender.Sent,
            message => Assert.Equal("first update", message.Text),
            message => Assert.Equal("second update", message.Text),
            message => Assert.Equal("~~ turn complete ~~", message.Text));
    }

    [Fact]
    public async Task ProcessNextAsync_IndividualReleaseSectionsPreserveNumberedLists()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
            MaxMessageChars = 3500,
        });

        string humanWork = string.Join(Environment.NewLine, [
            "Human Work",
            string.Empty,
            "1. Decide support boundary.",
            "2. Run and record live Telegram private-chat checks at minimum.",
            "3. Confirm BotFather settings, privacy mode, and workspace roots."
        ]);
        string notDone = string.Join(Environment.NewLine, [
            "Not Done",
            string.Empty,
            "- Push current head and capture workflow URLs.",
            "- Record owner evidence."
        ]);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, humanWork), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Completion, notDone), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        Assert.Contains("1. Decide support boundary.", sender.Sent[0].Text);
        Assert.Contains("2. Run and record live Telegram private-chat checks at minimum.", sender.Sent[0].Text);
        Assert.Contains("3. Confirm BotFather settings, privacy mode, and workspace roots.", sender.Sent[0].Text);
        Assert.Contains("- Push current head and capture workflow URLs.", sender.Sent[1].Text);
        Assert.Contains("- Record owner evidence.", sender.Sent[1].Text);
        Assert.DoesNotContain(Environment.NewLine + "---" + Environment.NewLine, string.Join(Environment.NewLine, sender.Sent.Select(message => message.Text)));
    }

    [Fact]
    public async Task ProcessNextAsync_IndividualMessagesWithoutSessionIdStartWithContent()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "first", sessionId: " "), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "second", sessionId: " "), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        string text = Assert.Single(sender.Sent).Text;
        Assert.StartsWith("first", text, StringComparison.Ordinal);
        Assert.DoesNotContain("second", text);
        Assert.DoesNotContain("[Codex]", text);
        Assert.DoesNotContain("2 updates", text);
    }

    [Fact]
    public async Task ProcessNextAsync_PreservesIndividualMultilineItemsWithoutSessionHeader()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
        });
        string longLine = new('x', 260);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "  first line  \r\nsecond line", sessionId: "thread-abcdefghijklmnop"), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, longLine, sessionId: "thread-abcdefghijklmnop"), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        string text = sender.Sent[0].Text;
        Assert.StartsWith("first line", text, StringComparison.Ordinal);
        Assert.Contains("first line", text);
        Assert.Contains("second line", text);
        Assert.Equal(longLine, sender.Sent[1].Text);
        Assert.DoesNotContain("[thread-a]", text);
    }

    [Fact]
    public async Task ProcessNextAsync_SelectsHigherPriorityWhenBatchTimesMatch()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "normal first", chatId: 1, priority: OutboundPriority.Normal), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Error, "critical second", chatId: 2, priority: OutboundPriority.Critical), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        Assert.Equal("critical second", Assert.Single(sender.Sent).Text);
    }

    [Fact]
    public async Task ProcessNextAsync_SelectsNeverSentDestinationBeforeRecentlySentDestination()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
        }, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "chat one first", chatId: 1), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "chat one second", chatId: 1), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "chat two first", chatId: 2), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        Assert.Equal(["chat one first", "chat two first"], sender.Sent.Select(message => message.Text));
    }

    [Fact]
    public async Task ProcessNextAsync_WaitsForBatchWindowUnlessMessageIsHighPriority()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 30,
            PrivateMinimumSendIntervalSeconds = 0,
        }, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "normal update"), CancellationToken.None);

        Assert.False(await scheduler.ProcessNextAsync(CancellationToken.None));

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Error, "urgent update", priority: OutboundPriority.High), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(["normal update", "urgent update"], sender.Sent.Select(message => message.Text));
    }

    [Fact]
    public async Task ProcessNextAsync_HonorsGroupSendIntervalBetweenMessages()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            GroupMinimumSendIntervalSeconds = 30,
            PrivateMinimumSendIntervalSeconds = 0,
        }, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "first", chatId: -100), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "second", chatId: -100), CancellationToken.None);

        Assert.False(await scheduler.ProcessNextAsync(CancellationToken.None));

        timeProvider.Advance(TimeSpan.FromSeconds(29));

        Assert.False(await scheduler.ProcessNextAsync(CancellationToken.None));

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(["first", "second"], sender.Sent.Select(message => message.Text));
    }

    [Fact]
    public async Task ProcessNextAsync_HonorsPrivateSendIntervalBetweenMessages()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 30,
            GroupMinimumSendIntervalSeconds = 0,
        }, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "first", chatId: 100), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "second", chatId: 100), CancellationToken.None);

        Assert.False(await scheduler.ProcessNextAsync(CancellationToken.None));

        timeProvider.Advance(TimeSpan.FromSeconds(30));

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(["first", "second"], sender.Sent.Select(message => message.Text));
    }

    [Fact]
    public async Task ProcessNextAsync_HonorsGlobalMessagesPerSecondLimit()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            GlobalMaxMessagesPerSecond = 1,
            PrivateMinimumSendIntervalSeconds = 0,
        }, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "first", chatId: 1), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "second", chatId: 2), CancellationToken.None);

        Assert.False(await scheduler.ProcessNextAsync(CancellationToken.None));

        timeProvider.Advance(TimeSpan.FromMilliseconds(999));

        Assert.False(await scheduler.ProcessNextAsync(CancellationToken.None));

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(["first", "second"], sender.Sent.Select(message => message.Text));
    }

    [Fact]
    public async Task ProcessNextAsync_ExpiresChatBackoffAtRetryBoundary()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        sender.Exceptions.Enqueue(new TelegramOutboundRateLimitException("too many requests", TimeSpan.FromSeconds(5)));
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
        }, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "retry me"), CancellationToken.None);

        Assert.False(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(TestNow + TimeSpan.FromSeconds(5), Assert.Single((await scheduler.GetStatusAsync(CancellationToken.None)).Destinations).ChatBackoffUntilUtc);

        timeProvider.Advance(TimeSpan.FromSeconds(5));

        Assert.Null(Assert.Single((await scheduler.GetStatusAsync(CancellationToken.None)).Destinations).ChatBackoffUntilUtc);
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProcessNextAsync_DefaultsRateLimitBackoffWhenRetryAfterIsMissing()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        sender.Exceptions.Enqueue(new TelegramOutboundRateLimitException("too many requests", retryAfter: null));
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
        }, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "retry me"), CancellationToken.None);

        Assert.False(await scheduler.ProcessNextAsync(CancellationToken.None));

        TelegramOutboundQueueStatus blockedStatus = await scheduler.GetStatusAsync(CancellationToken.None);

        Assert.Equal(TestNow + TimeSpan.FromSeconds(5), Assert.Single(blockedStatus.Destinations).ChatBackoffUntilUtc);
    }

    [Fact]
    public async Task ProcessNextAsync_AppliesRetryAfterBackoffWhenTelegramRateLimitsSend()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new();
        sender.Exceptions.Enqueue(new TelegramOutboundRateLimitException("too many requests", TimeSpan.FromSeconds(12)));
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
        }, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "retry me"), CancellationToken.None);

        Assert.False(await scheduler.ProcessNextAsync(CancellationToken.None));

        TelegramOutboundQueueStatus blockedStatus = await scheduler.GetStatusAsync(CancellationToken.None);

        Assert.Equal(TestNow + TimeSpan.FromSeconds(12), Assert.Single(blockedStatus.Destinations).ChatBackoffUntilUtc);
        Assert.False(await scheduler.ProcessNextAsync(CancellationToken.None));

        timeProvider.Advance(TimeSpan.FromSeconds(12));

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.Equal("retry me", Assert.Single(sender.Sent).Text);
        Assert.Null((await scheduler.GetStatusAsync(CancellationToken.None)).GlobalBackoffUntilUtc);
    }

    [Fact]
    public async Task ProcessNextAsync_AppliesConfiguredBackoffAfterGenericSendFailure()
    {
        TestTimeProvider timeProvider = new(TestNow);
        TestTelegramSender sender = new() { ThrowOnSend = true };
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            GroupMinimumSendIntervalSeconds = 7,
        }, timeProvider);

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "still pending", chatId: -100), CancellationToken.None);

        Assert.False(await scheduler.ProcessNextAsync(CancellationToken.None));

        TelegramOutboundQueueStatus status = await scheduler.GetStatusAsync(CancellationToken.None);

        Assert.Equal(TestNow + TimeSpan.FromSeconds(7), Assert.Single(status.Destinations).ChatBackoffUntilUtc);
    }

    [Fact]
    public async Task ProcessNextAsync_SplitsLongMessageAndRetainsPreparedChunksUntilSent()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            MaxMessageChars = 10,
            PrivateMinimumSendIntervalSeconds = 0,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "abcdefghijklmno"), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        TelegramOutboundQueueStatus midSendStatus = await scheduler.GetStatusAsync(CancellationToken.None);

        Assert.Equal(0, midSendStatus.PendingMessageCount);
        Assert.Equal(1, midSendStatus.PendingChunkCount);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(["abcdefghij", "klmno"], sender.Sent.Select(message => message.Text));
        Assert.Equal(0, (await scheduler.GetStatusAsync(CancellationToken.None)).PendingChunkCount);
    }

    [Fact]
    public async Task EnqueueAsync_CompactsOlderNormalUpdatesWithoutDroppingTheirText()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            MaxBufferedMessagesPerDestination = 2,
            MaxBufferedCharsPerDestination = 5000,
            PrivateMinimumSendIntervalSeconds = 0,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "normal update", priority: OutboundPriority.Normal), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "high update", priority: OutboundPriority.High), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Error, "critical update", priority: OutboundPriority.Critical), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        string text = string.Join(Environment.NewLine, sender.Sent.Select(message => message.Text));
        Assert.Contains("older outbound updates compacted", text);
        Assert.Contains("normal update", text);
        Assert.Contains("high update", text);
        Assert.Contains("critical update", text);
    }

    [Fact]
    public async Task EnqueueAsync_CompactsOlderProgressWithoutDroppingTheirText()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            IncludeProgressMessages = true,
            MaxBufferedMessagesPerDestination = 2,
            MaxBufferedCharsPerDestination = 5000,
            PrivateMinimumSendIntervalSeconds = 0,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Progress, "progress one", priority: OutboundPriority.Low), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "useful update", priority: OutboundPriority.Normal), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Error, "critical update", priority: OutboundPriority.Critical), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        string text = string.Join(Environment.NewLine, sender.Sent.Select(message => message.Text));
        Assert.Contains("older outbound updates compacted", text);
        Assert.Contains("progress one", text);
        Assert.Contains("useful update", text);
        Assert.Contains("critical update", text);
    }

    [Fact]
    public async Task EnqueueAsync_CompactsByCharacterBudgetWithoutDroppingContent()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            MaxBufferedMessagesPerDestination = 10,
            MaxBufferedCharsPerDestination = 20,
            PrivateMinimumSendIntervalSeconds = 0,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "normal update one"), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "normal update two"), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Error, "critical update", priority: OutboundPriority.Critical), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        string text = string.Join(Environment.NewLine, sender.Sent.Select(message => message.Text));
        Assert.Contains("older outbound updates compacted", text);
        Assert.Contains("normal update one", text);
        Assert.Contains("normal update two", text);
        Assert.Contains("critical update", text);
    }

    [Fact]
    public async Task EnqueueAsync_DoesNotCompactWhenOnlyHighPriorityMessagesRemain()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            MaxBufferedMessagesPerDestination = 1,
            MaxBufferedCharsPerDestination = 10,
            PrivateMinimumSendIntervalSeconds = 0,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Error, "high one", priority: OutboundPriority.High), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Error, "critical two", priority: OutboundPriority.Critical), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        string text = string.Join(Environment.NewLine, sender.Sent.Select(message => message.Text));
        Assert.DoesNotContain("older outbound updates compacted", text);
        Assert.Contains("high one", text);
        Assert.Contains("critical two", text);
    }

    [Fact]
    public async Task EnqueueAsync_DoesNotCompactFilePayloads()
    {
        TestTelegramSender sender = new();
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            MaxBufferedMessagesPerDestination = 1,
            MaxBufferedCharsPerDestination = 10,
            PrivateMinimumSendIntervalSeconds = 0,
        });
        OutboundTelegramFile file = new()
        {
            Kind = TelegramOutboundFileKind.Photo,
            Path = "C:\\temp\\screenshot.png",
            FileName = "screenshot.png",
            Caption = "Codex artifact: screenshot.png",
        };

        await scheduler.EnqueueAsync(CreateFileMessage(file, priority: OutboundPriority.Normal), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Error, "critical two", priority: OutboundPriority.Critical), CancellationToken.None);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.Equal(file, Assert.Single(sender.SentFiles).File);
        Assert.Empty(sender.Sent);

        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));
        string text = Assert.Single(sender.Sent).Text;
        Assert.DoesNotContain("older outbound updates compacted", text);
        Assert.Contains("critical two", text);
    }

    [Fact]
    public async Task ProcessNextAsync_KeepsMessagesQueuedAfterSendFailure()
    {
        TestTelegramSender sender = new() { ThrowOnSend = true };
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "still pending"), CancellationToken.None);

        bool processed = await scheduler.ProcessNextAsync(CancellationToken.None);
        TelegramOutboundQueueStatus status = await scheduler.GetStatusAsync(CancellationToken.None);

        Assert.False(processed);
        Assert.Equal(1, status.PendingChunkCount);
    }

    [Fact]
    public async Task ProcessNextAsync_TimedOutSendDoesNotBlockOtherDestinations()
    {
        TestTelegramSender sender = new();
        sender.HangingTexts.Add("stuck send");
        OutboundTelegramScheduler scheduler = CreateScheduler(sender, new TelegramOutboundOptions
        {
            BatchWindowSeconds = 0,
            PrivateMinimumSendIntervalSeconds = 0,
            GroupMinimumSendIntervalSeconds = 0,
            SendTimeoutSeconds = 1,
        });

        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "stuck send", chatId: 1), CancellationToken.None);
        await scheduler.EnqueueAsync(CreateMessage(CodexOutboundMessageKind.Update, "other send", chatId: 2), CancellationToken.None);

        Assert.False(await scheduler.ProcessNextAsync(CancellationToken.None));
        Assert.True(await scheduler.ProcessNextAsync(CancellationToken.None));

        SentTelegramMessage sent = Assert.Single(sender.Sent);
        Assert.Equal(2, sent.Conversation.ChatId);
        Assert.Equal("other send", sent.Text);
        TelegramOutboundQueueStatus status = await scheduler.GetStatusAsync(CancellationToken.None);
        TelegramOutboundDestinationStatus stuck = Assert.Single(status.Destinations, destination => destination.ChatId == 1);
        Assert.Equal(1, stuck.PendingChunkCount);
    }

    private static OutboundTelegramScheduler CreateScheduler(
        TestTelegramSender sender,
        TelegramOutboundOptions options,
        TimeProvider? timeProvider = null,
        ITelegramDebugTraceStore? traceStore = null)
        => new(
            sender,
            new TelegramMessageChunker(),
            timeProvider ?? TimeProvider.System,
            new StaticOptionsMonitor<TelegramOutboundOptions>(options),
            NullLogger<OutboundTelegramScheduler>.Instance,
            traceStore);

    private static OutboundTelegramScheduler CreateScheduler(
        TestTelegramSender sender,
        IOptionsMonitor<TelegramOutboundOptions> options,
        TimeProvider? timeProvider = null,
        ITelegramDebugTraceStore? traceStore = null)
        => new(
            sender,
            new TelegramMessageChunker(),
            timeProvider ?? TimeProvider.System,
            options,
            NullLogger<OutboundTelegramScheduler>.Instance,
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

    private static OutboundTelegramMessage CreateMessage(
        CodexOutboundMessageKind kind,
        string text,
        long chatId = 1234,
        int? messageThreadId = null,
        string sessionId = "thread-1234567890",
        string? turnId = null,
        DateTimeOffset? createdUtc = null,
        OutboundPriority priority = OutboundPriority.Normal,
        bool omitCreatedUtc = false,
        string? traceId = null)
        => new()
        {
            MessageId = Guid.NewGuid().ToString("n"),
            ChatId = chatId,
            MessageThreadId = messageThreadId,
            SessionId = sessionId,
            TurnId = turnId,
            TraceId = traceId,
            Kind = kind,
            Text = text,
            CreatedUtc = omitCreatedUtc ? default : createdUtc ?? TestNow,
            Priority = priority,
        };

    private static OutboundTelegramMessage CreateFileMessage(
        OutboundTelegramFile file,
        string text = "Codex artifact",
        long chatId = 1234,
        int? messageThreadId = null,
        string sessionId = "thread-1234567890",
        string? turnId = null,
        DateTimeOffset? createdUtc = null,
        OutboundPriority priority = OutboundPriority.Normal)
        => new()
        {
            MessageId = Guid.NewGuid().ToString("n"),
            ChatId = chatId,
            MessageThreadId = messageThreadId,
            SessionId = sessionId,
            TurnId = turnId,
            Kind = CodexOutboundMessageKind.Update,
            Text = text,
            File = file,
            CreatedUtc = createdUtc ?? TestNow,
            Priority = priority,
        };

    private static async Task WaitForConditionAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(2);
        TimeSpan effectivePollInterval = pollInterval ?? TimeSpan.FromMilliseconds(20);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + effectiveTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(effectivePollInterval).ConfigureAwait(false);
        }

        Assert.True(await condition().ConfigureAwait(false), "Timed out waiting for the expected condition.");
    }

    private sealed class TestDeliveryObserver : IObserver<OutboundTelegramDelivery>
    {
        private readonly Channel<OutboundTelegramDelivery> _deliveries = Channel.CreateUnbounded<OutboundTelegramDelivery>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        public void OnNext(OutboundTelegramDelivery value)
            => _deliveries.Writer.TryWrite(value);

        public void OnError(Exception error)
            => _deliveries.Writer.TryComplete(error);

        public void OnCompleted()
            => _deliveries.Writer.TryComplete();

        public async Task<OutboundTelegramDelivery?> ReadOrDefaultAsync(TimeSpan timeout)
        {
            using CancellationTokenSource cancellation = new(timeout);
            try
            {
                return await _deliveries.Reader.ReadAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public async Task<OutboundTelegramDelivery> ReadRequiredAsync()
            => await ReadOrDefaultAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)
                ?? throw new TimeoutException("Timed out waiting for an observable Telegram delivery.");
    }

    private sealed class TestTelegramSender : IOutboundTelegramMessageSender
    {
        private readonly TaskCompletionSource<SentTelegramMessage> _nextSend = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<SentTelegramMessage> Sent { get; } = [];

        public List<SentTelegramFileMessage> SentFiles { get; } = [];

        public Task<SentTelegramMessage> NextSend => _nextSend.Task;

        public Queue<Exception> Exceptions { get; } = [];

        public HashSet<string> HangingTexts { get; } = new(StringComparer.Ordinal);

        public bool ThrowOnSend { get; init; }

        public Task SendTextMessageAsync(
            TelegramConversationScope conversation,
            string text,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
        {
            if (Exceptions.TryDequeue(out Exception? exception))
            {
                throw exception;
            }

            if (ThrowOnSend)
            {
                throw new InvalidOperationException("send failed");
            }

            if (HangingTexts.Contains(text))
            {
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            SentTelegramMessage sent = new(conversation, text, debugContext);
            Sent.Add(sent);
            _nextSend.TrySetResult(sent);
            return Task.CompletedTask;
        }

        public Task SendFileMessageAsync(
            TelegramConversationScope conversation,
            OutboundTelegramFile file,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
        {
            if (Exceptions.TryDequeue(out Exception? exception))
            {
                throw exception;
            }

            if (ThrowOnSend)
            {
                throw new InvalidOperationException("send failed");
            }

            SentFiles.Add(new SentTelegramFileMessage(conversation, file, debugContext));
            return Task.CompletedTask;
        }
    }

    private sealed record SentTelegramMessage(TelegramConversationScope Conversation, string Text, TelegramDebugMessageContext? DebugContext);

    private sealed record SentTelegramFileMessage(TelegramConversationScope Conversation, OutboundTelegramFile File, TelegramDebugMessageContext? DebugContext);

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
            => _utcNow;

        public void Advance(TimeSpan value)
            => _utcNow += value;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        private readonly List<Action<T, string?>> _listeners = [];

        public T CurrentValue { get; private set; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return null;
        }

        public void Update(T value)
        {
            CurrentValue = value;
            foreach (Action<T, string?> listener in _listeners)
            {
                listener(value, null);
            }
        }
    }
}
