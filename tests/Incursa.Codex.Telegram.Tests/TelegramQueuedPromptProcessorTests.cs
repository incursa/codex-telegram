using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Incursa.OpenAI.Codex;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramQueuedPromptProcessorTests
{
    [Fact]
    public async Task ProcessNextAsync_ReturnsFalseWhenNoPromptIsQueued()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.False(processed);
        Assert.Empty(harness.SessionManager.TextSends);
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task ProcessNextAsync_StartsQueuedTextPromptAndFollowsThread()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        TelegramQueuedPrompt prompt = CreatePrompt("prompt-1", "thread-1", "queued text");
        harness.SessionManager.Sessions.Add(CreateSession("thread-1"));
        await EnqueueSelectedPromptAsync(harness, prompt);

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.True(processed);
        Assert.Equal([("thread-1", "queued text")], harness.SessionManager.TextSends);
        Assert.Contains(prompt.ConversationScope, harness.FollowRegistry.GetTargets("thread-1"));
        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Starting queued message for thread-1", sent.Text);
        TelegramConversationState state = Assert.Single(await harness.StateStore.ListConversationStatesAsync(CancellationToken.None));
        Assert.Equal(0, state.QueuedPromptCount);
    }

    [Fact]
    public async Task ProcessNextAsync_AnnouncesQueuedPromptBeforeWaitingForTurnStart()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        TelegramQueuedPrompt prompt = CreatePrompt("prompt-1", "thread-1", "queued text");
        harness.SessionManager.Sessions.Add(CreateSession("thread-1"));
        harness.SessionManager.PendingTextSend = new TaskCompletionSource<CodexThreadExecutionVm>(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueSelectedPromptAsync(harness, prompt);

        Task<bool> processTask = harness.Processor.ProcessNextAsync(CancellationToken.None);
        await harness.SessionManager.TextSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Starting queued message for thread-1", sent.Text);

        harness.SessionManager.PendingTextSend.SetResult(new CodexThreadExecutionVm("thread-1", "turn-1", "running", null));
        Assert.True(await processTask.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ProcessNextAsync_FollowsReturnedExecutionThreadWhenCodexAliasesSessionId()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        TelegramQueuedPrompt prompt = CreatePrompt("prompt-1", "thread-1", "queued text");
        harness.SessionManager.Sessions.Add(CreateSession("thread-1"));
        harness.SessionManager.ExecutionThreadId = "thread-returned";
        await EnqueueSelectedPromptAsync(harness, prompt);

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.True(processed);
        Assert.DoesNotContain(prompt.ConversationScope, harness.FollowRegistry.GetTargets("thread-1"));
        Assert.Contains(prompt.ConversationScope, harness.FollowRegistry.GetTargets("thread-returned"));
    }

    [Fact]
    public async Task ProcessNextAsync_StartsQueuedAttachmentPromptAndRetainsTemporaryFiles()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        string imagePath = Path.Combine(harness.Temp.Path, "image.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
        TelegramQueuedPrompt prompt = CreatePrompt(
            "prompt-1",
            "thread-1",
            "inspect this",
            attachments:
            [
                new TelegramAttachmentDescriptor(imagePath, "image.png", "image/png", IsImage: true),
            ]);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1"));
        await EnqueueSelectedPromptAsync(harness, prompt);

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.True(processed);
        IReadOnlyList<CodexInputItem> sent = Assert.Single(harness.SessionManager.AttachmentSends).Input;
        Assert.Collection(
            sent,
            item => Assert.Equal("inspect this", Assert.IsType<CodexTextInput>(item).Text),
            item => Assert.Equal(imagePath, Assert.IsType<CodexLocalImageInput>(item).Path));
        Assert.True(File.Exists(imagePath));
    }

    [Fact]
    public async Task ProcessNextAsync_WhenQueuedAttachmentIsMissingFailsWithoutDroppingItFromDiagnostics()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        string imagePath = Path.Combine(harness.Temp.Path, "missing-image.png");
        TelegramQueuedPrompt prompt = CreatePrompt(
            "prompt-1",
            "thread-1",
            "inspect this",
            attachments:
            [
                new TelegramAttachmentDescriptor(imagePath, "missing-image.png", "image/png", IsImage: true),
            ]);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1"));
        await EnqueueSelectedPromptAsync(harness, prompt);

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.True(processed);
        Assert.Empty(harness.SessionManager.AttachmentSends);
        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("attachment is no longer available", sent.Text);
        TelegramConversationState state = Assert.Single(await harness.StateStore.ListConversationStatesAsync(CancellationToken.None));
        Assert.Equal(0, state.QueuedPromptCount);
    }

    [Fact]
    public async Task ProcessNextAsync_SkipsMissingSessionAndDeletesAttachments()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        string documentPath = Path.Combine(harness.Temp.Path, "missing.bin");
        await File.WriteAllBytesAsync(documentPath, [1, 2, 3]);
        TelegramQueuedPrompt prompt = CreatePrompt(
            "prompt-1",
            "missing-thread",
            "queued text",
            attachments:
            [
                new TelegramAttachmentDescriptor(documentPath, "missing.bin", "application/octet-stream", IsImage: false),
            ]);
        await harness.StateStore.EnqueueQueuedPromptAsync(prompt, CancellationToken.None);

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.True(processed);
        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Equal(prompt.ConversationScope, sent.Conversation);
        Assert.Contains("session is no longer available", sent.Text);
        Assert.False(File.Exists(documentPath));
    }

    [Fact]
    public async Task ProcessNextAsync_DefersPromptWhenSessionAlreadyHasActiveTurn()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        TelegramQueuedPrompt prompt = CreatePrompt("prompt-1", "thread-1", "queued text");
        harness.SessionManager.Sessions.Add(CreateSession("thread-1"));
        harness.TurnCoordinator.ActiveThreadIds.Add("thread-1");
        await EnqueueSelectedPromptAsync(harness, prompt);

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.False(processed);
        Assert.Empty(harness.SessionManager.TextSends);
        TelegramConversationState state = Assert.Single(await harness.StateStore.ListConversationStatesAsync(CancellationToken.None));
        Assert.Equal(1, state.QueuedPromptCount);
    }

    [Fact]
    public async Task ProcessNextAsync_DefersPromptWhenSessionIsReportedRunning()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        TelegramQueuedPrompt prompt = CreatePrompt("prompt-1", "thread-1", "queued text");
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", status: CodexSessionStatus.Running));
        await EnqueueSelectedPromptAsync(harness, prompt);

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.False(processed);
        Assert.Empty(harness.SessionManager.TextSends);
        Assert.Empty(harness.Sender.Sent);
        TelegramConversationState state = Assert.Single(await harness.StateStore.ListConversationStatesAsync(CancellationToken.None));
        Assert.Equal(1, state.QueuedPromptCount);
    }

    [Fact]
    public async Task ProcessNextAsync_SkipsPromptWhenConversationMovedToAnotherSession()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        TelegramQueuedPrompt prompt = CreatePrompt("prompt-1", "thread-1", "queued text");
        harness.SessionManager.Sessions.Add(CreateSession("thread-1"));
        harness.SessionManager.Sessions.Add(CreateSession("thread-2"));
        await harness.StateStore.SetActiveSessionIdAsync(prompt.ConversationScope, "thread-2", CancellationToken.None);
        await harness.StateStore.EnqueueQueuedPromptAsync(prompt, CancellationToken.None);

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.True(processed);
        Assert.Empty(harness.SessionManager.TextSends);
        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("conversation now points at another session", sent.Text);
        TelegramConversationState state = Assert.Single(await harness.StateStore.ListConversationStatesAsync(CancellationToken.None));
        Assert.Equal("thread-2", state.ActiveSessionId);
        Assert.Equal(0, state.QueuedPromptCount);
    }

    [Fact]
    public async Task ProcessNextAsync_DefersPromptWhenConversationHasPendingOutboundWork()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        TelegramQueuedPrompt prompt = CreatePrompt("prompt-1", "thread-1", "queued text");
        harness.SessionManager.Sessions.Add(CreateSession("thread-1"));
        harness.OutboundQueue.Status = new TelegramOutboundQueueStatus(
            1,
            1,
            0,
            11,
            new TelegramDestinationKey(prompt.ChatId, prompt.MessageThreadId),
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            null,
            [
                new TelegramOutboundDestinationStatus(
                    prompt.ChatId,
                    prompt.MessageThreadId,
                    prompt.SessionId,
                    PendingMessageCount: 1,
                    PendingChunkCount: 0,
                    PendingCharacterCount: 11,
                    FirstPendingUtc: DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
                    LastEnqueuedUtc: DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
                    ChatBackoffUntilUtc: null,
                    LastSentUtc: null),
            ]);
        await EnqueueSelectedPromptAsync(harness, prompt);

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.False(processed);
        Assert.Empty(harness.SessionManager.TextSends);
        TelegramConversationState state = Assert.Single(await harness.StateStore.ListConversationStatesAsync(CancellationToken.None));
        Assert.Equal(1, state.QueuedPromptCount);
    }

    [Fact]
    public async Task ProcessNextAsync_DefersPromptWhenConversationHasPendingOutboundChunk()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        TelegramQueuedPrompt prompt = CreatePrompt("prompt-1", "thread-1", "queued text", messageThreadId: 7);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1"));
        harness.OutboundQueue.Status = new TelegramOutboundQueueStatus(
            1,
            0,
            1,
            11,
            new TelegramDestinationKey(prompt.ChatId, prompt.MessageThreadId),
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            null,
            [
                new TelegramOutboundDestinationStatus(
                    prompt.ChatId,
                    prompt.MessageThreadId,
                    prompt.SessionId,
                    PendingMessageCount: 0,
                    PendingChunkCount: 1,
                    PendingCharacterCount: 11,
                    FirstPendingUtc: DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
                    LastEnqueuedUtc: DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
                    ChatBackoffUntilUtc: null,
                    LastSentUtc: null),
            ]);
        await EnqueueSelectedPromptAsync(harness, prompt);

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.False(processed);
        Assert.Empty(harness.SessionManager.TextSends);
        TelegramConversationState state = Assert.Single(await harness.StateStore.ListConversationStatesAsync(CancellationToken.None));
        Assert.Equal(1, state.QueuedPromptCount);
    }

    [Fact]
    public async Task ProcessNextAsync_DoesNotDeferForOutboundWorkInDifferentTopic()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        TelegramQueuedPrompt prompt = CreatePrompt("prompt-1", "thread-1", "queued text", messageThreadId: 7);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1"));
        harness.OutboundQueue.Status = new TelegramOutboundQueueStatus(
            1,
            1,
            0,
            11,
            new TelegramDestinationKey(prompt.ChatId, 8),
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            null,
            [
                new TelegramOutboundDestinationStatus(
                    prompt.ChatId,
                    8,
                    prompt.SessionId,
                    PendingMessageCount: 1,
                    PendingChunkCount: 0,
                    PendingCharacterCount: 11,
                    FirstPendingUtc: DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
                    LastEnqueuedUtc: DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
                    ChatBackoffUntilUtc: null,
                    LastSentUtc: null),
            ]);
        await EnqueueSelectedPromptAsync(harness, prompt);

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.True(processed);
        Assert.Equal([("thread-1", "queued text")], harness.SessionManager.TextSends);
        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Starting queued message for thread-1", sent.Text);
        TelegramConversationState state = Assert.Single(await harness.StateStore.ListConversationStatesAsync(CancellationToken.None));
        Assert.Equal(0, state.QueuedPromptCount);
    }

    [Fact]
    public async Task ProcessNextAsync_RequeuesPromptWhenCodexReportsAnotherTurnStarted()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        TelegramQueuedPrompt prompt = CreatePrompt("prompt-1", "thread-1", "queued text");
        harness.SessionManager.Sessions.Add(CreateSession("thread-1"));
        harness.SessionManager.SendException = new InvalidOperationException("another turn is already active");
        await EnqueueSelectedPromptAsync(harness, prompt);

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.False(processed);
        Assert.Collection(
            harness.Sender.Sent,
            sent => Assert.Contains("Starting queued message for thread-1", sent.Text),
            sent => Assert.Contains("still queued because another Codex turn started first", sent.Text));
        TelegramConversationState state = Assert.Single(await harness.StateStore.ListConversationStatesAsync(CancellationToken.None));
        Assert.Equal(1, state.QueuedPromptCount);
    }

    [Fact]
    public async Task ProcessNextAsync_ReportsFailureAndRemovesPromptWhenSendFails()
    {
        using ProcessorHarness harness = ProcessorHarness.Create();
        string documentPath = Path.Combine(harness.Temp.Path, "failing.bin");
        await File.WriteAllBytesAsync(documentPath, [1, 2, 3]);
        TelegramQueuedPrompt prompt = CreatePrompt(
            "prompt-1",
            "thread-1",
            "queued text",
            attachments:
            [
                new TelegramAttachmentDescriptor(documentPath, "failing.bin", "application/octet-stream", IsImage: false),
            ]);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Failing session"));
        harness.SessionManager.SendException = new ApplicationException("codex unavailable");
        await EnqueueSelectedPromptAsync(harness, prompt);

        bool processed = await harness.Processor.ProcessNextAsync(CancellationToken.None);

        Assert.True(processed);
        Assert.Collection(
            harness.Sender.Sent,
            sent => Assert.Contains("Starting queued message for Failing session", sent.Text),
            sent =>
            {
                Assert.Contains("Queued message for Failing session failed to start", sent.Text);
                Assert.Contains("codex unavailable", sent.Text);
            });
        TelegramConversationState state = Assert.Single(await harness.StateStore.ListConversationStatesAsync(CancellationToken.None));
        Assert.Equal(0, state.QueuedPromptCount);
        Assert.False(File.Exists(documentPath));
    }

    private static async Task EnqueueSelectedPromptAsync(ProcessorHarness harness, TelegramQueuedPrompt prompt)
    {
        await harness.StateStore.SetActiveSessionIdAsync(prompt.ConversationScope, prompt.SessionId, CancellationToken.None);
        await harness.StateStore.EnqueueQueuedPromptAsync(prompt, CancellationToken.None);
    }

    private static TelegramQueuedPrompt CreatePrompt(
        string id,
        string sessionId,
        string text,
        long chatId = 5555,
        int? messageThreadId = null,
        IReadOnlyList<TelegramAttachmentDescriptor>? attachments = null)
        => new(
            id,
            UserId: 1234,
            chatId,
            sessionId,
            SessionName: sessionId,
            text,
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            messageThreadId,
            attachments);

    private static CodexSessionSummary CreateSession(string id, string? name = null, CodexSessionStatus status = CodexSessionStatus.Exited)
        => new(
            id,
            name ?? id,
            status,
            WorkingDirectory: null,
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            ExitCode: null,
            LastError: null);

    private sealed class ProcessorHarness : IDisposable
    {
        private ProcessorHarness(
            TemporaryDirectory temp,
            TelegramBotStateStore stateStore,
            FakeCodexSessionManager sessionManager,
            FakeTurnExecutionCoordinator turnCoordinator,
            TelegramThreadFollowRegistry followRegistry,
            TelegramTypingIndicatorRegistry typingIndicatorRegistry,
            FakeOutboundTelegramQueue outboundQueue,
            TestTelegramBotMessageSender sender,
            TelegramQueuedPromptProcessor processor)
        {
            Temp = temp;
            StateStore = stateStore;
            SessionManager = sessionManager;
            TurnCoordinator = turnCoordinator;
            FollowRegistry = followRegistry;
            TypingIndicatorRegistry = typingIndicatorRegistry;
            OutboundQueue = outboundQueue;
            Sender = sender;
            Processor = processor;
        }

        public TemporaryDirectory Temp { get; }

        public TelegramBotStateStore StateStore { get; }

        public FakeCodexSessionManager SessionManager { get; }

        public FakeTurnExecutionCoordinator TurnCoordinator { get; }

        public TelegramThreadFollowRegistry FollowRegistry { get; }

        public TelegramTypingIndicatorRegistry TypingIndicatorRegistry { get; }

        public FakeOutboundTelegramQueue OutboundQueue { get; }

        public TestTelegramBotMessageSender Sender { get; }

        public TelegramQueuedPromptProcessor Processor { get; }

        public static ProcessorHarness Create()
        {
            TemporaryDirectory temp = TemporaryDirectory.Create();
            TelegramBotStateStore stateStore = new(Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
            {
                Workspace = new CodexWorkspaceOptions
                {
                    DataRoot = Path.Combine(temp.Path, "state"),
                    WorkspaceRoots = [temp.Path],
                },
            }));
            FakeCodexSessionManager sessionManager = new();
            FakeTurnExecutionCoordinator turnCoordinator = new();
            TelegramThreadFollowRegistry followRegistry = new();
            TelegramTypingIndicatorRegistry typingIndicatorRegistry = new();
            FakeOutboundTelegramQueue outboundQueue = new();
            TestTelegramBotMessageSender sender = new();
            TelegramQueuedPromptProcessor processor = new(
                stateStore,
                sessionManager,
                turnCoordinator,
                followRegistry,
                typingIndicatorRegistry,
                outboundQueue,
                sender,
                NullTelegramDebugTraceStore.Instance,
                NullLogger<TelegramQueuedPromptProcessor>.Instance);

            return new ProcessorHarness(
                temp,
                stateStore,
                sessionManager,
                turnCoordinator,
                followRegistry,
                typingIndicatorRegistry,
                outboundQueue,
                sender,
                processor);
        }

        public void Dispose()
            => Temp.Dispose();
    }

    private sealed class FakeCodexSessionManager : ICodexSessionManager
    {
        public List<CodexSessionSummary> Sessions { get; } = [];

        public List<(string SessionId, string Text)> TextSends { get; } = [];

        public List<(string SessionId, string Text)> PlanSends { get; } = [];

        public List<(string SessionId, IReadOnlyList<CodexInputItem> Input)> AttachmentSends { get; } = [];

        public Exception? SendException { get; set; }

        public string? ExecutionThreadId { get; set; }

        public TaskCompletionSource<CodexThreadExecutionVm>? PendingTextSend { get; set; }

        public TaskCompletionSource<bool> TextSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyCollection<CodexSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<CodexSessionSummary>>(Sessions.ToArray());

        public Task<CodexSessionSummary> CreateSessionAsync(CreateCodexSessionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CodexSessionSummary?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult(Sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase)));

        public Task<CodexThreadExecutionVm> SendAsync(string sessionId, string input, CancellationToken cancellationToken)
        {
            if (SendException is not null)
            {
                throw SendException;
            }

            TextSends.Add((sessionId, input));
            TextSendStarted.TrySetResult(true);
            if (PendingTextSend is not null)
            {
                return PendingTextSend.Task;
            }

            return Task.FromResult(new CodexThreadExecutionVm(ExecutionThreadId ?? sessionId, "turn-1", "running", null));
        }

        public Task<CodexThreadExecutionVm> SendPlanAsync(string sessionId, string input, CancellationToken cancellationToken)
        {
            if (SendException is not null)
            {
                throw SendException;
            }

            PlanSends.Add((sessionId, input));
            TextSendStarted.TrySetResult(true);
            if (PendingTextSend is not null)
            {
                return PendingTextSend.Task;
            }

            return Task.FromResult(new CodexThreadExecutionVm(ExecutionThreadId ?? sessionId, "turn-1", "running", null));
        }

        public Task<CodexThreadExecutionVm> SendAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
        {
            if (SendException is not null)
            {
                throw SendException;
            }

            AttachmentSends.Add((sessionId, input));
            return Task.FromResult(new CodexThreadExecutionVm(ExecutionThreadId ?? sessionId, "turn-1", "running", null));
        }

        public Task SteerAsync(string sessionId, string input, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SteerAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CodexSessionModelSettings> GetModelSettingsAsync(string sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CodexSessionModelSettings> UpdateModelSettingsAsync(string sessionId, string? model, string? reasoningEffort, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CodexThreadGoalVm?> GetGoalAsync(string sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CodexThreadGoalVm> SetGoalAsync(string sessionId, string objective, long? tokenBudget, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<CodexThreadGoalVm> SetGoalStatusAsync(string sessionId, CodexThreadGoalStatus status, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> ClearGoalAsync(string sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> TailAsync(string sessionId, int lineCount, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task StopAsync(string sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task KillAsync(string sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task RenameAsync(string sessionId, string name, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task ForgetAsync(string sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakeTurnExecutionCoordinator : ICodexTurnExecutionCoordinator
    {
        public HashSet<string> ActiveThreadIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasActiveTurn => ActiveThreadIds.Count > 0;

        public IReadOnlyCollection<string> GetActiveThreadIds() => ActiveThreadIds.ToArray();

        public bool HasActiveTurnForThread(string threadId) => ActiveThreadIds.Contains(threadId);

        public string? GetActiveTurnId(string threadId) => HasActiveTurnForThread(threadId) ? "turn-1" : null;

        public CodexActiveTurnStateVm? TryGetActiveTurnState(string threadId) => null;

        public void RegisterActiveTurn(string threadId, string turnId, ICodexTurnHandle? turn = null, CodexTimelineEntryVm? lastEvent = null)
        {
            ActiveThreadIds.Add(threadId);
        }

        public void UpdateActiveTurnState(string threadId, string turnId, CodexTimelineEntryVm? lastEvent = null)
        {
        }

        public bool TryClearActiveTurn(string threadId, string turnId) => ActiveThreadIds.Remove(threadId);

        public Task SteerAsync(string threadId, string turnId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task InterruptAsync(string threadId, string turnId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeOutboundTelegramQueue : IOutboundTelegramQueue
    {
        public TelegramOutboundQueueStatus Status { get; set; } = new(0, 0, 0, 0, null, null, null, []);

        public ValueTask EnqueueAsync(OutboundTelegramMessage message, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public Task<TelegramOutboundQueueStatus> GetStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(Status);
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
            Sent.Add(new SentTelegramMessage(conversation, text));
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

        public Task SendTypingActionAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ReactToMessageAsync(TelegramMessageReaction reaction, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed record SentTelegramMessage(TelegramConversationScope Conversation, string Text);
}
