using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramCommandHandlerTests
{
    [Fact]
    public async Task HandleMessageAsync_IgnoresUnauthorizedNonWhoamiMessages()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
        });

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(9999, 5555, "private", "/sessions"),
            harness.Sender,
            CancellationToken.None);

        Assert.Empty(harness.Sender.Sent);
        Assert.Empty(harness.Sender.Edited);
    }

    [Fact]
    public async Task HandleMessageAsync_AllowsUnauthorizedWhoamiMessages()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
        });

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(9999, 5555, "private", "/whoami"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Telegram user ID: 9999", sent.Text);
        Assert.Contains("Chat ID: 5555", sent.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_SessionsListUsesOnlyUseSelectionButtons()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "First session", harness.Temp.Path));
        harness.SessionManager.Sessions.Add(CreateSession("thread-2", "Second session", harness.Temp.Path));
        await harness.StateStore.TrackSessionAsync("thread-1", CancellationToken.None);
        await harness.StateStore.TrackSessionAsync("thread-2", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/sessions"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Equal(["Use 1", "Use 2", "Sessions", "Projects", "Help"], FlattenButtonLabels(sent));
        Assert.DoesNotContain(FlattenButtonLabels(sent), label => IsSessionControlLabel(label));
    }

    [Fact]
    public async Task HandleMessageAsync_IgnoresGroupCommandsWhenChatIsNotAllowed()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [],
        });

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, -1005555, "supergroup", "/sessions"),
            harness.Sender,
            CancellationToken.None);

        Assert.Empty(harness.Sender.Sent);
        Assert.Empty(harness.Sender.Edited);
    }

    [Fact]
    public async Task HandleMessageAsync_AllowsGroupCommandsWhenUserAndChatAreAllowed()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [-1005555],
        });

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, -1005555, "supergroup", "/sessions"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("No Codex sessions are known yet.", sent.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_ExplainsGroupRootPlainTextInsteadOfSilentlyIgnoringIt()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [-1005555],
        });

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, -1005555, "supergroup", "please look at this"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("group root", sent.Text);
        Assert.Contains("/send <text>", sent.Text);
        Assert.Contains("forum topics", sent.Text);
        Assert.Empty(harness.SessionManager.SendRequests);
    }

    [Fact]
    public async Task HandleMessageAsync_ExplainsGroupRootAttachmentInsteadOfRoutingIt()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [-1005555],
        });

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(
                1234,
                -1005555,
                "supergroup",
                null,
                Attachments:
                [
                    new TelegramAttachmentDescriptor(
                        Path.Combine(harness.Temp.Path, "photo.png"),
                        "photo.png",
                        "image/png",
                        IsImage: true),
                ]),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("group root", sent.Text);
        Assert.Contains("did not send it to Codex", sent.Text);
        Assert.Empty(harness.SessionManager.SendRequests);
    }

    [Fact]
    public async Task HandleMessageAsync_RoutesTopicAttachmentAndEmojiCaptionToActiveSession()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [-1005555],
        });
        TelegramConversationScope conversation = new(-1005555, 77);
        harness.SessionManager.Sessions.Add(CreateSession("thread-topic", "Topic session", harness.Temp.Path));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-topic", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(
                1234,
                conversation.ChatId,
                "supergroup",
                "inspect this image 🚀",
                conversation.MessageThreadId,
                Attachments:
                [
                    new TelegramAttachmentDescriptor(
                        Path.Combine(harness.Temp.Path, "photo.png"),
                        "photo.png",
                        "image/png",
                        IsImage: true),
                ]),
            harness.Sender,
            CancellationToken.None);

        IReadOnlyList<CodexInputItem> input = Assert.IsAssignableFrom<IReadOnlyList<CodexInputItem>>(Assert.Single(harness.SessionManager.SendRequests));
        Assert.Collection(
            input,
            item => Assert.Equal("inspect this image 🚀", Assert.IsType<CodexTextInput>(item).Text),
            item => Assert.EndsWith("photo.png", Assert.IsType<CodexLocalImageInput>(item).Path, StringComparison.Ordinal));
        Assert.Contains("Sent to Topic session.", Assert.Single(harness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_NewSessionReplyDoesNotShowRedundantSessionControls()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        string projectPath = harness.Temp.CreateDirectory("repo");
        TelegramConversationScope conversation = new(5555, null);
        harness.ProjectCatalog.Projects.Add(new CodexProjectCatalogRecord
        {
            WorkingDirectory = projectPath,
            AddedAt = DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
        });
        await harness.StateStore.SetActiveProjectWorkingDirectoryAsync(conversation, projectPath, CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/new Release smoke"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        CreateCodexSessionRequest request = Assert.Single(harness.SessionManager.CreateRequests);
        Assert.Equal("Release smoke", request.Name);
        Assert.Equal(projectPath, request.WorkingDirectory);
        Assert.Contains("Created and selected Release smoke.", sent.Text);
        Assert.Equal(["Sessions", "Projects", "Help"], FlattenButtonLabels(sent));
        Assert.DoesNotContain(FlattenButtonLabels(sent), label => IsSessionControlLabel(label));
        Assert.DoesNotContain(FlattenButtonLabels(sent), label => label.StartsWith("Use", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleMessageAsync_RestartConfirmExplainsExternalRestart()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/restart confirm"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Restart is managed outside this standalone process.", sent.Text);
        Assert.Contains("service manager", sent.Text);
    }

    [Fact]
    public async Task HandleCallbackAsync_IgnoresUnauthorizedCallbacks()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
        });

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-1", 9999, 5555, "private", "nav:sessions"),
            harness.Sender,
            CancellationToken.None);

        CallbackAnswer answer = Assert.Single(harness.Sender.CallbackAnswers);
        Assert.Equal("callback-1", answer.CallbackQueryId);
        Assert.Null(answer.Text);
        Assert.Empty(harness.Sender.Sent);
        Assert.Empty(harness.Sender.Edited);
    }

    private static CodexSessionSummary CreateSession(string id, string name, string? workingDirectory)
        => new(
            id,
            name,
            CodexSessionStatus.Exited,
            workingDirectory,
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            null,
            null);

    private static IReadOnlyList<string> FlattenButtonLabels(SentTelegramMessage message)
        => message.Buttons?.SelectMany(row => row.Select(button => button.Text)).ToArray() ?? [];

    private static bool IsSessionControlLabel(string label)
        => label.StartsWith("Tail", StringComparison.OrdinalIgnoreCase)
            || label.StartsWith("Status", StringComparison.OrdinalIgnoreCase)
            || label.StartsWith("Model", StringComparison.OrdinalIgnoreCase)
            || label.StartsWith("Thinking", StringComparison.OrdinalIgnoreCase);

    private sealed class CommandHandlerHarness : IDisposable
    {
        private CommandHandlerHarness(
            TemporaryDirectory temp,
            FakeCodexSessionManager sessionManager,
            FakeProjectCatalogStore projectCatalog,
            TelegramBotStateStore stateStore,
            TestTelegramBotMessageSender sender,
            TelegramCodexBotCommandHandler handler)
        {
            Temp = temp;
            SessionManager = sessionManager;
            ProjectCatalog = projectCatalog;
            StateStore = stateStore;
            Sender = sender;
            Handler = handler;
        }

        public TemporaryDirectory Temp { get; }

        public FakeCodexSessionManager SessionManager { get; }

        public FakeProjectCatalogStore ProjectCatalog { get; }

        public TelegramBotStateStore StateStore { get; }

        public TestTelegramBotMessageSender Sender { get; }

        public TelegramCodexBotCommandHandler Handler { get; }

        public static CommandHandlerHarness Create(TelegramBotOptions? botOptions = null)
        {
            TemporaryDirectory temp = TemporaryDirectory.Create();
            IOptions<CodexTelegramOptions> codexOptions = Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
            {
                Workspace = new CodexWorkspaceOptions
                {
                    DataRoot = Path.Combine(temp.Path, "state"),
                    WorkspaceRoots = [temp.Path],
                },
            });

            FakeCodexSessionManager sessionManager = new();
            FakeProjectCatalogStore projectCatalog = new();
            TelegramBotStateStore stateStore = new(codexOptions);
            TestTelegramBotMessageSender sender = new();
            TelegramCodexBotCommandHandler handler = new(
                new TelegramCommandParser(),
                new TelegramMessageChunker(),
                sessionManager,
                projectCatalog,
                new CodexWorkspaceBrowser(codexOptions),
                stateStore,
                new FakeTurnExecutionCoordinator(),
                new TelegramThreadFollowRegistry(),
                new FakeTelegramForumTopicService(),
                new FakeAudioTranscriptionService(),
                new FakeOutboundTelegramQueue(),
                Microsoft.Extensions.Options.Options.Create(botOptions ?? new TelegramBotOptions
                {
                    AllowedUserIds = [1234],
                }),
                NullLogger<TelegramCodexBotCommandHandler>.Instance);

            return new CommandHandlerHarness(temp, sessionManager, projectCatalog, stateStore, sender, handler);
        }

        public void Dispose()
            => Temp.Dispose();
    }

    private sealed class FakeCodexSessionManager : ICodexSessionManager
    {
        public List<CodexSessionSummary> Sessions { get; } = [];

        public List<CreateCodexSessionRequest> CreateRequests { get; } = [];

        public List<object> SendRequests { get; } = [];

        public Task<IReadOnlyCollection<CodexSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<CodexSessionSummary>>(Sessions.ToArray());

        public Task<CodexSessionSummary> CreateSessionAsync(CreateCodexSessionRequest request, CancellationToken cancellationToken)
        {
            CreateRequests.Add(request);
            CodexSessionSummary session = CreateSession($"thread-{CreateRequests.Count}", request.Name, request.WorkingDirectory);
            Sessions.Add(session);
            return Task.FromResult(session);
        }

        public Task<CodexSessionSummary?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult(Sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase)));

        public Task<CodexThreadExecutionVm> SendAsync(string sessionId, string input, CancellationToken cancellationToken)
        {
            SendRequests.Add(input);
            return Task.FromResult(new CodexThreadExecutionVm(sessionId, "turn-1", "running", null));
        }

        public Task<CodexThreadExecutionVm> SendAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
        {
            SendRequests.Add(input);
            return Task.FromResult(new CodexThreadExecutionVm(sessionId, "turn-1", "running", null));
        }

        public Task SteerAsync(string sessionId, string input, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<CodexSessionModelSettings> GetModelSettingsAsync(string sessionId, CancellationToken cancellationToken)
        {
            CodexSessionSummary? session = Sessions.FirstOrDefault(candidate => string.Equals(candidate.Id, sessionId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new CodexSessionModelSettings(
                sessionId,
                session?.Name ?? sessionId,
                "gpt-5.4-mini",
                "high",
                [],
                [CodexReasoningEffort.High]));
        }

        public Task<CodexSessionModelSettings> UpdateModelSettingsAsync(
            string sessionId,
            string? model,
            string? reasoningEffort,
            CancellationToken cancellationToken)
            => Task.FromResult(new CodexSessionModelSettings(
                sessionId,
                Sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase))?.Name ?? sessionId,
                model,
                reasoningEffort,
                [],
                [CodexReasoningEffort.High]));

        public Task<string> TailAsync(string sessionId, int lineCount, CancellationToken cancellationToken)
            => Task.FromResult("tail output");

        public Task StopAsync(string sessionId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task KillAsync(string sessionId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RenameAsync(string sessionId, string name, CancellationToken cancellationToken)
        {
            int index = Sessions.FindIndex(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                CodexSessionSummary current = Sessions[index];
                Sessions[index] = current with { Name = name };
            }

            return Task.CompletedTask;
        }

        public Task ForgetAsync(string sessionId, CancellationToken cancellationToken)
        {
            Sessions.RemoveAll(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProjectCatalogStore : ICodexProjectCatalogStore
    {
        public List<CodexProjectCatalogRecord> Projects { get; } = [];

        public Task<IReadOnlyList<CodexProjectCatalogRecord>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CodexProjectCatalogRecord>>(Projects.ToArray());

        public Task<CodexProjectCatalogRecord> AddAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            CodexProjectCatalogRecord record = new()
            {
                WorkingDirectory = Path.GetFullPath(workingDirectory),
                AddedAt = DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            };
            Projects.Add(record);
            return Task.FromResult(record);
        }

        public Task<bool> RemoveAsync(string workingDirectory, CancellationToken cancellationToken)
            => Task.FromResult(Projects.RemoveAll(project => string.Equals(project.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase)) > 0);
    }

    private sealed class FakeTurnExecutionCoordinator : ICodexTurnExecutionCoordinator
    {
        public bool HasActiveTurn => false;

        public IReadOnlyCollection<string> GetActiveThreadIds() => [];

        public bool HasActiveTurnForThread(string threadId) => false;

        public string? GetActiveTurnId(string threadId) => null;

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

    private sealed class FakeTelegramForumTopicService : ITelegramForumTopicService
    {
        public Task<TelegramForumTopicCreationResult> CreateForumTopicAsync(long chatId, string name, CancellationToken cancellationToken)
            => Task.FromResult(new TelegramForumTopicCreationResult(123, name));
    }

    private sealed class FakeAudioTranscriptionService : IAudioTranscriptionService
    {
        public Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken)
            => Task.FromResult("transcribed text");
    }

    private sealed class FakeOutboundTelegramQueue : IOutboundTelegramQueue
    {
        public ValueTask EnqueueAsync(OutboundTelegramMessage message, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public Task<TelegramOutboundQueueStatus> GetStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(new TelegramOutboundQueueStatus(0, 0, 0, 0, null, null, null, []));
    }

    private sealed class TestTelegramBotMessageSender : ITelegramBotMessageSender
    {
        public List<SentTelegramMessage> Sent { get; } = [];

        public List<EditedTelegramMessage> Edited { get; } = [];

        public List<CallbackAnswer> CallbackAnswers { get; } = [];

        public Task SendTextMessageAsync(
            TelegramConversationScope conversation,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken)
        {
            Sent.Add(new SentTelegramMessage(conversation, text, buttons));
            return Task.CompletedTask;
        }

        public Task EditTextMessageAsync(
            TelegramConversationScope conversation,
            int messageId,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken)
        {
            Edited.Add(new EditedTelegramMessage(conversation, messageId, text, buttons));
            return Task.CompletedTask;
        }

        public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
        {
            CallbackAnswers.Add(new CallbackAnswer(callbackQueryId, text));
            return Task.CompletedTask;
        }
    }

    private sealed record SentTelegramMessage(
        TelegramConversationScope Conversation,
        string Text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? Buttons);

    private sealed record EditedTelegramMessage(
        TelegramConversationScope Conversation,
        int MessageId,
        string Text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? Buttons);

    private sealed record CallbackAnswer(string CallbackQueryId, string? Text);
}
