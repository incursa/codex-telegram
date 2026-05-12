using System.Globalization;
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
    public async Task HandleMessageAsync_AllowsUnauthorizedSharedChatWhoamiMessages()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [],
        });

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(9999, -1005555, "supergroup", "/whoami@codex_bot"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Telegram user ID: 9999", sent.Text);
        Assert.Contains("Chat ID: -1005555", sent.Text);
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
    public async Task HandleMessageAsync_ExplainsGroupCommandsWhenChatIsNotTrusted()
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

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("not trusted", sent.Text);
        Assert.Contains("/trust", sent.Text);
        Assert.Empty(harness.Sender.Edited);
    }

    [Fact]
    public async Task HandleMessageAsync_TrustAllowsGroupCommandsWithoutConfiguredChatAllowlist()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [],
        });

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, -1005555, "supergroup", "/trust"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage trustReply = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Trusted this chat", trustReply.Text);
        Assert.True(await harness.StateStore.IsChatTrustedAsync(-1005555, CancellationToken.None));

        harness.Sender.Sent.Clear();
        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, -1005555, "supergroup", "/sessions"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sessionsReply = Assert.Single(harness.Sender.Sent);
        Assert.Contains("No Codex sessions are known yet.", sessionsReply.Text);
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
    public async Task HandleMessageAsync_RoutesTrustedGroupRootPlainTextToChatRootSession()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [-1005555],
        });
        string projectPath = harness.Temp.CreateDirectory("repo");
        TelegramConversationScope conversation = new(-1005555, null);
        harness.ProjectCatalog.Projects.Add(new CodexProjectCatalogRecord
        {
            WorkingDirectory = projectPath,
            AddedAt = DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
        });
        await harness.StateStore.SetActiveProjectWorkingDirectoryAsync(conversation, projectPath, CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, -1005555, "supergroup", "please look at this"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        CreateCodexSessionRequest request = Assert.Single(harness.SessionManager.CreateRequests);
        Assert.StartsWith("repo session ", request.Name, StringComparison.Ordinal);
        Assert.Equal(projectPath, request.WorkingDirectory);
        Assert.Equal("please look at this", Assert.Single(harness.SessionManager.SendRequests));
        Assert.Equal("thread-1", await harness.StateStore.GetActiveSessionIdAsync(conversation, CancellationToken.None));
        Assert.Contains("Sent to repo session", sent.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_RoutesTrustedGroupRootAttachment()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [-1005555],
        });
        string projectPath = harness.Temp.CreateDirectory("repo");
        TelegramConversationScope conversation = new(-1005555, null);
        harness.ProjectCatalog.Projects.Add(new CodexProjectCatalogRecord
        {
            WorkingDirectory = projectPath,
            AddedAt = DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
        });
        await harness.StateStore.SetActiveProjectWorkingDirectoryAsync(conversation, projectPath, CancellationToken.None);
        string photoPath = Path.Combine(harness.Temp.Path, "photo.png");
        await File.WriteAllBytesAsync(photoPath, [1, 2, 3]);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(
                1234,
                -1005555,
                "supergroup",
                null,
                Attachments:
                [
                    new TelegramAttachmentDescriptor(
                        photoPath,
                        "photo.png",
                        "image/png",
                        IsImage: true),
                ]),
            harness.Sender,
            CancellationToken.None);

        IReadOnlyList<CodexInputItem> input = Assert.IsAssignableFrom<IReadOnlyList<CodexInputItem>>(Assert.Single(harness.SessionManager.SendRequests));
        CodexLocalImageInput image = Assert.IsType<CodexLocalImageInput>(Assert.Single(input));
        Assert.Equal(photoPath, image.Path);
        Assert.True(File.Exists(photoPath));
        Assert.Contains("Sent to repo session", Assert.Single(harness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_RoutesTrustedGroupRootAudio()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [-1005555],
        });
        string projectPath = harness.Temp.CreateDirectory("repo");
        TelegramConversationScope conversation = new(-1005555, null);
        harness.ProjectCatalog.Projects.Add(new CodexProjectCatalogRecord
        {
            WorkingDirectory = projectPath,
            AddedAt = DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
        });
        await harness.StateStore.SetActiveProjectWorkingDirectoryAsync(conversation, projectPath, CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(
                1234,
                -1005555,
                "supergroup",
                null,
                AudioFilePath: "not-created.ogg"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal("transcribed text", Assert.Single(harness.SessionManager.SendRequests));
        Assert.Collection(
            harness.Sender.Sent,
            sent => Assert.Contains("Here's what I transcribed:", sent.Text),
            sent => Assert.Contains("Sent to repo session", sent.Text));
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
    public async Task HandleMessageAsync_DoctorExplainsPrivateChatSetupAndNextAction()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/doctor"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Codex Telegram doctor", sent.Text);
        Assert.Contains("Effective access: allowed", sent.Text);
        Assert.Contains("Routing: Plain text, audio, and attachments can auto-route", sent.Text);
        Assert.Contains("Active project: <none>", sent.Text);
        Assert.Contains("Next: use /projects or /project add", sent.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_DoctorExplainsGroupRootRouting()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [-1005555],
        });

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, -1005555, "supergroup", "/doctor"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("supergroup root", sent.Text);
        Assert.Contains("Plain text, audio, and attachments can auto-route", sent.Text);
        Assert.Contains("Next: use /projects or /project add", sent.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_DoctorExplainsReadyConversation()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        string projectPath = harness.Temp.CreateDirectory("repo");
        TelegramConversationScope conversation = new(5555, null);
        harness.ProjectCatalog.Projects.Add(new CodexProjectCatalogRecord
        {
            WorkingDirectory = projectPath,
            AddedAt = DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
        });
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", projectPath));
        await harness.StateStore.SetActiveProjectWorkingDirectoryAsync(conversation, projectPath, CancellationToken.None);
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/doctor"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Active project: repo", sent.Text);
        Assert.Contains("Active session: Demo session", sent.Text);
        Assert.Contains("Next: send a normal message", sent.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_NewSessionReplyDoesNotShowRedundantSessionControls()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        string projectPath = harness.Temp.CreateDirectory("repo");
        TelegramConversationScope conversation = new(5555, null);
        SetUsageWindows(harness);
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
        AssertCompactUsageSummary(sent.Text);
        Assert.Equal(["Sessions", "Projects", "Help"], FlattenButtonLabels(sent));
        Assert.DoesNotContain(FlattenButtonLabels(sent), label => IsSessionControlLabel(label));
        Assert.DoesNotContain(FlattenButtonLabels(sent), label => label.StartsWith("Use", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HandleMessageAsync_NewWithoutNameUsesProjectBasedDefaultName()
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
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/new"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        CreateCodexSessionRequest request = Assert.Single(harness.SessionManager.CreateRequests);
        Assert.StartsWith("repo session ", request.Name, StringComparison.Ordinal);
        Assert.Equal(projectPath, request.WorkingDirectory);
        Assert.Contains("Created and selected repo session", sent.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_HelpListsCoreCommandsAndNavigationButtons()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/help"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("/projects", sent.Text);
        Assert.Contains("/sessions", sent.Text);
        Assert.Contains("/model", sent.Text);
        Assert.Contains("/version", sent.Text);
        Assert.Contains("/queue", sent.Text);
        Assert.Contains("/outbound", sent.Text);
        Assert.Contains("configured OpenAI transcription model", sent.Text);
        Assert.Equal(["Sessions", "Projects", "Help"], FlattenButtonLabels(sent));
    }

    [Fact]
    public async Task HandleMessageAsync_VersionShowsRunningBinaryVersion()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/version"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Incursa Codex Telegram", sent.Text);
        Assert.Contains("older binary", sent.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_UnknownCommandPointsToHelp()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/doesnotexist"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal("Unknown command. Send /help for the supported commands.", Assert.Single(harness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_ProjectAddSelectsAbsoluteWorkspacePath()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        string projectPath = harness.Temp.CreateDirectory("repo");

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/project add " + projectPath),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Added and selected project repo.", sent.Text);
        Assert.Contains(projectPath, sent.Text);
        Assert.Equal(projectPath, await harness.StateStore.GetActiveProjectWorkingDirectoryAsync(new TelegramConversationScope(5555, null), CancellationToken.None));
    }

    [Fact]
    public async Task HandleMessageAsync_ProjectCurrentExplainsMissingSelection()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/project current"),
            harness.Sender,
            CancellationToken.None);

        Assert.Contains("Select a project before creating a session", Assert.Single(harness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_ProjectsUsesUnnumberedUseButtonForSingleProject()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        string projectPath = harness.Temp.CreateDirectory("repo");
        harness.ProjectCatalog.Projects.Add(new CodexProjectCatalogRecord
        {
            WorkingDirectory = projectPath,
            AddedAt = DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
        });

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/projects"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Projects:", sent.Text);
        Assert.Equal(["Use", "Sessions", "Projects", "Help"], FlattenButtonLabels(sent));
    }

    [Fact]
    public async Task HandleMessageAsync_UseSelectsSessionByNumber()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "First session", harness.Temp.Path));
        harness.SessionManager.Sessions.Add(CreateSession("thread-2", "Second session", harness.Temp.Path));

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/use thread-2"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Selected Second session.", sent.Text);
        Assert.Equal("thread-2", await harness.StateStore.GetActiveSessionIdAsync(conversation, CancellationToken.None));
        Assert.Equal(["Sessions", "Projects", "Help"], FlattenButtonLabels(sent));
    }

    [Fact]
    public async Task HandleMessageAsync_SendWithoutTextShowsUsage()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/send"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal("Usage: /send <text>", Assert.Single(harness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_PrivateTextCreatesSessionWhenProjectIsSelected()
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
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "please inspect the repo"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal("please inspect the repo", Assert.Single(harness.SessionManager.SendRequests));
        Assert.Equal("thread-1", await harness.StateStore.GetActiveSessionIdAsync(conversation, CancellationToken.None));
        Assert.Contains("Sent to", Assert.Single(harness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_TracksTypingWhileWaitingForCodexSendToStart()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        harness.SessionManager.PendingSend = new TaskCompletionSource<CodexThreadExecutionVm>(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        Task handleTask = harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "please inspect the repo"),
            harness.Sender,
            CancellationToken.None);
        await harness.SessionManager.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains(conversation, harness.TypingIndicatorRegistry.GetTargets());

        harness.SessionManager.PendingSend.SetResult(new CodexThreadExecutionVm("thread-1", "turn-1", "running", null));
        await handleTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(harness.TypingIndicatorRegistry.GetTargets());
    }

    [Fact]
    public async Task HandleMessageAsync_QueuesTextWhenSelectedSessionIsReportedRunning()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession(
            "thread-1",
            "Goal session",
            harness.Temp.Path,
            CodexSessionStatus.Running));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "tell me about the goal"),
            harness.Sender,
            CancellationToken.None);

        Assert.Empty(harness.SessionManager.SendRequests);
        TelegramQueuedPrompt queued = Assert.Single(await harness.StateStore.ListQueuedPromptsAsync(1234, conversation, CancellationToken.None));
        Assert.Equal("thread-1", queued.SessionId);
        Assert.Equal("tell me about the goal", queued.Text);
        Assert.Contains("Queued for Goal session", Assert.Single(harness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_ReplyPlainTextQueuesTelegramContextWhenSessionIsRunning()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession(
            "thread-1",
            "Goal session",
            harness.Temp.Path,
            CodexSessionStatus.Running));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);
        TelegramReplyContext replyContext = new(
            12,
            TelegramMessageAuthor.Bot,
            "I am going to delete the stale file.",
            [
                new TelegramMessageContextRecord(conversation, 10, TelegramMessageAuthor.Bot, "First progress update.", DateTimeOffset.Parse("2026-05-10T12:00:00Z")),
                new TelegramMessageContextRecord(conversation, 11, TelegramMessageAuthor.Bot, "Second progress update.", DateTimeOffset.Parse("2026-05-10T12:01:00Z")),
            ]);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "do not delete that", ReplyContext: replyContext),
            harness.Sender,
            CancellationToken.None);

        TelegramQueuedPrompt queued = Assert.Single(await harness.StateStore.ListQueuedPromptsAsync(1234, conversation, CancellationToken.None));
        Assert.Contains("Telegram reply context:", queued.Text);
        Assert.Contains("First progress update.", queued.Text);
        Assert.Contains("I am going to delete the stale file.", queued.Text);
        Assert.Contains("Operator reply:", queued.Text);
        Assert.Contains("do not delete that", queued.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_SteerReplyIncludesTelegramContext()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Goal session", harness.Temp.Path));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);
        TelegramReplyContext replyContext = new(
            12,
            TelegramMessageAuthor.Bot,
            "I am going to delete the stale file.",
            []);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/steer do not delete that", ReplyContext: replyContext),
            harness.Sender,
            CancellationToken.None);

        (string sessionId, object input) = Assert.Single(harness.SessionManager.SteerRequests);
        Assert.Equal("thread-1", sessionId);
        string text = Assert.IsType<string>(input);
        Assert.Contains("Telegram reply context:", text);
        Assert.Contains("I am going to delete the stale file.", text);
        Assert.Contains("Operator reply:", text);
        Assert.Contains("do not delete that", text);
    }

    [Fact]
    public async Task HandleMessageAsync_AudioTranscribesRoutesAndDeletesTemporaryFile()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        string projectPath = harness.Temp.CreateDirectory("repo");
        string audioPath = Path.Combine(harness.Temp.Path, "voice.ogg");
        await File.WriteAllBytesAsync(audioPath, [1, 2, 3]);
        TelegramConversationScope conversation = new(5555, null);
        harness.ProjectCatalog.Projects.Add(new CodexProjectCatalogRecord
        {
            WorkingDirectory = projectPath,
            AddedAt = DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
        });
        await harness.StateStore.SetActiveProjectWorkingDirectoryAsync(conversation, projectPath, CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", null, AudioFilePath: audioPath),
            harness.Sender,
            CancellationToken.None);

        Assert.False(File.Exists(audioPath));
        Assert.Equal("transcribed text", Assert.Single(harness.SessionManager.SendRequests));
        Assert.Collection(
            harness.Sender.Sent,
            sent => Assert.Contains("Here's what I transcribed:", sent.Text),
            sent => Assert.Contains("Sent to", sent.Text));
    }

    [Fact]
    public async Task HandleMessageAsync_AudioCreatesFreshSessionWhenSelectedThreadStoreIsUnreadable()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        string projectPath = harness.Temp.CreateDirectory("repo");
        string audioPath = Path.Combine(harness.Temp.Path, "voice.ogg");
        TelegramConversationScope conversation = new(5555, null);
        await File.WriteAllBytesAsync(audioPath, [1, 2, 3]);
        harness.ProjectCatalog.Projects.Add(new CodexProjectCatalogRecord
        {
            WorkingDirectory = projectPath,
            AddedAt = DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
        });
        harness.SessionManager.Sessions.Add(CreateSession("thread-stale", "Stale session", projectPath));
        harness.SessionManager.SendExceptions.Enqueue(new InvalidOperationException(
            @"failed to read thread: thread-store internal error: failed to read thread C:\Users\you\.codex\sessions\2026\05\05\rollout-2026-05-05T13-35-46-thread-stale.jsonl: rollout at C:\Users\you\.codex\sessions\2026\05\05\rollout-2026-05-05T13-35-46-thread-stale.jsonl is empty"));
        await harness.StateStore.SetActiveProjectWorkingDirectoryAsync(conversation, projectPath, CancellationToken.None);
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-stale", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", null, AudioFilePath: audioPath),
            harness.Sender,
            CancellationToken.None);

        Assert.False(File.Exists(audioPath));
        Assert.Equal("thread-1", await harness.StateStore.GetActiveSessionIdAsync(conversation, CancellationToken.None));
        Assert.Equal(projectPath, Assert.Single(harness.SessionManager.CreateRequests).WorkingDirectory);
        Assert.Equal(["thread-1"], harness.SessionManager.SendSessionIds);
        Assert.Equal("transcribed text", Assert.Single(harness.SessionManager.SendRequests));
        Assert.Collection(
            harness.Sender.Sent,
            sent => Assert.Contains("Here's what I transcribed:", sent.Text),
            sent => Assert.Contains("could not be resumed", sent.Text),
            sent => Assert.Contains("Sent to repo session", sent.Text));
    }

    [Fact]
    public async Task HandleMessageAsync_AudioFailureAndEmptyTranscriptDoNotRouteToCodex()
    {
        using CommandHandlerHarness failureHarness = CommandHandlerHarness.Create();
        string failureAudioPath = Path.Combine(failureHarness.Temp.Path, "failure.ogg");
        await File.WriteAllBytesAsync(failureAudioPath, [1, 2, 3]);
        failureHarness.AudioTranscription.Exception = new InvalidOperationException("speech unavailable");

        await failureHarness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", null, AudioFilePath: failureAudioPath),
            failureHarness.Sender,
            CancellationToken.None);

        Assert.False(File.Exists(failureAudioPath));
        Assert.Contains("Audio transcription failed: speech unavailable", Assert.Single(failureHarness.Sender.Sent).Text);
        Assert.Empty(failureHarness.SessionManager.SendRequests);

        using CommandHandlerHarness emptyHarness = CommandHandlerHarness.Create();
        string emptyAudioPath = Path.Combine(emptyHarness.Temp.Path, "empty.ogg");
        await File.WriteAllBytesAsync(emptyAudioPath, [1, 2, 3]);
        emptyHarness.AudioTranscription.Transcript = " ";

        await emptyHarness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", null, AudioFilePath: emptyAudioPath),
            emptyHarness.Sender,
            CancellationToken.None);

        Assert.False(File.Exists(emptyAudioPath));
        Assert.Contains("couldn't transcribe", Assert.Single(emptyHarness.Sender.Sent).Text);
        Assert.Empty(emptyHarness.SessionManager.SendRequests);
    }

    [Fact]
    public async Task HandleMessageAsync_TopicNewCreatesForumTopicAndSessionWhenPathIsProvided()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [-1005555],
        });
        string projectPath = harness.Temp.CreateDirectory("repo");

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, -1005555, "supergroup", "/topic new Demo lane | " + projectPath, MessageThreadId: 1),
            harness.Sender,
            CancellationToken.None);

        CreateCodexSessionRequest request = Assert.Single(harness.SessionManager.CreateRequests);
        Assert.Equal("Demo lane", request.Name);
        Assert.Equal(projectPath, request.WorkingDirectory);
        Assert.Equal("thread-1", await harness.StateStore.GetActiveSessionIdAsync(new TelegramConversationScope(-1005555, 123), CancellationToken.None));
        Assert.Contains("Created topic Demo lane and session Demo lane", harness.Sender.Sent[0].Text);
        Assert.Contains("Created and selected Demo lane", harness.Sender.Sent[1].Text);
    }

    [Fact]
    public async Task HandleMessageAsync_TopicNewInPrivateChatExplainsUnsupportedCreation()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        string projectPath = harness.Temp.CreateDirectory("repo");

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/topic new Demo lane | " + projectPath),
            harness.Sender,
            CancellationToken.None);

        string text = Assert.Single(harness.Sender.Sent).Text;
        Assert.Contains("private chat", text);
        Assert.Contains("/new [name]", text);
    }

    [Fact]
    public async Task HandleMessageAsync_TopicAttachAndCurrentUseTopicScopedSession()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [-1005555],
        });
        TelegramConversationScope topic = new(-1005555, 77);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Topic session", harness.Temp.Path));

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, topic.ChatId, "supergroup", "/topic attach thread-1", topic.MessageThreadId),
            harness.Sender,
            CancellationToken.None);
        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, topic.ChatId, "supergroup", "/topic current", topic.MessageThreadId),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal("thread-1", await harness.StateStore.GetActiveSessionIdAsync(topic, CancellationToken.None));
        Assert.Collection(
            harness.Sender.Sent,
            sent => Assert.Contains("Attached this topic to Topic session.", sent.Text),
            sent =>
            {
                Assert.Contains("Topic thread ID: 77", sent.Text);
                Assert.Contains("Topic session", sent.Text);
            });
    }

    [Fact]
    public async Task HandleMessageAsync_TopicListShowsRegisteredTopicSessions()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [-1005555],
        });
        TelegramConversationScope topic = new(-1005555, 77);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Topic session", harness.Temp.Path));
        await harness.StateStore.SetActiveSessionIdAsync(topic, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, topic.ChatId, "supergroup", "/topics", topic.MessageThreadId),
            harness.Sender,
            CancellationToken.None);

        string text = Assert.Single(harness.Sender.Sent).Text;
        Assert.Contains("Telegram threads in this chat", text);
        Assert.Contains("topic 77", text);
        Assert.Contains("Topic session", text);
    }

    [Fact]
    public async Task HandleMessageAsync_SessionsAllShowsRecentHistoryAndLimitGuidance()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        for (int index = 0; index < 3; index++)
        {
            harness.SessionManager.Sessions.Add(CreateSession($"thread-{index}", $"Session {index}", harness.Temp.Path));
        }

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/sessions all 2"),
            harness.Sender,
            CancellationToken.None);

        string text = Assert.Single(harness.Sender.Sent).Text;
        Assert.Contains("Recent Codex sessions:", text);
        Assert.Contains("Showing 2 of 3", text);
        Assert.Equal(["Use 1", "Use 2", "Sessions", "Projects", "Help"], FlattenButtonLabels(harness.Sender.Sent.Single()));
    }

    [Fact]
    public async Task HandleMessageAsync_ModelMenuRequiresActiveSession()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/model"),
            harness.Sender,
            CancellationToken.None);

        Assert.Contains("No active session is selected", Assert.Single(harness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_ModelMenuShowsModelButtonsForActiveSession()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/model"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Model settings:", sent.Text);
        Assert.Contains("Voice phrase:", sent.Text);
        Assert.Contains("[x] GPT-5.4 Mini", FlattenButtonLabels(sent));
        Assert.Contains("Back", FlattenButtonLabels(sent));
    }

    [Fact]
    public async Task HandleMessageAsync_ModelUpdateAcceptsThinkingArgument()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        SetUsageWindows(harness);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/model gpt-5.4-mini thinking medium"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(("thread-1", (string?)"gpt-5.4-mini", (string?)"medium"), Assert.Single(harness.SessionManager.UpdateRequests));
        string text = Assert.Single(harness.Sender.Sent).Text;
        Assert.Contains("Updated model settings:", text);
        AssertCompactUsageSummary(text);
    }

    [Fact]
    public async Task HandleMessageAsync_ModelInvalidArgumentsShowUsage()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/model thinking"),
            harness.Sender,
            CancellationToken.None);

        Assert.Contains("Usage: /model", Assert.Single(harness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_ThinkingMenuAndUpdateUseActiveSession()
    {
        using CommandHandlerHarness menuHarness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        menuHarness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", menuHarness.Temp.Path));
        await menuHarness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await menuHarness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/thinking"),
            menuHarness.Sender,
            CancellationToken.None);

        Assert.Contains("Thinking settings:", Assert.Single(menuHarness.Sender.Sent).Text);
        Assert.Contains("[x] High", FlattenButtonLabels(menuHarness.Sender.Sent.Single()));

        using CommandHandlerHarness updateHarness = CommandHandlerHarness.Create();
        updateHarness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", updateHarness.Temp.Path));
        await updateHarness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await updateHarness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/thinking xhigh"),
            updateHarness.Sender,
            CancellationToken.None);

        Assert.Equal(("thread-1", (string?)null, (string?)"xhigh"), Assert.Single(updateHarness.SessionManager.UpdateRequests));
        Assert.Contains("Updated model settings:", Assert.Single(updateHarness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_GoalShowsAndSetsActiveSessionGoal()
    {
        using CommandHandlerHarness showHarness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        showHarness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", showHarness.Temp.Path));
        showHarness.SessionManager.Goals["thread-1"] = CreateGoal("thread-1", "Ship the Telegram goal command");
        await showHarness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await showHarness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/goal"),
            showHarness.Sender,
            CancellationToken.None);

        string showText = Assert.Single(showHarness.Sender.Sent).Text;
        Assert.Contains("Session goal:", showText);
        Assert.Contains("Ship the Telegram goal command", showText);
        Assert.Contains("Status: active", showText);

        using CommandHandlerHarness setHarness = CommandHandlerHarness.Create();
        setHarness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", setHarness.Temp.Path));
        await setHarness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await setHarness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/goal set Get /goal working --budget 12000"),
            setHarness.Sender,
            CancellationToken.None);

        Assert.Equal(("thread-1", "Get /goal working", (long?)12000), Assert.Single(setHarness.SessionManager.SetGoalRequests));
        string setText = Assert.Single(setHarness.Sender.Sent).Text;
        Assert.Contains("Updated session goal:", setText);
        Assert.Contains("Get /goal working", setText);
        Assert.Contains("12,000", setText);
    }

    [Fact]
    public async Task HandleMessageAsync_GoalControlCommandsUseActiveSession()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        harness.SessionManager.Goals["thread-1"] = CreateGoal("thread-1", "Keep working");
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/goal pause"),
            harness.Sender,
            CancellationToken.None);
        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/goal resume"),
            harness.Sender,
            CancellationToken.None);
        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/goal complete"),
            harness.Sender,
            CancellationToken.None);
        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/goal clear"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(
            [
                ("thread-1", CodexThreadGoalStatus.Paused),
                ("thread-1", CodexThreadGoalStatus.Active),
                ("thread-1", CodexThreadGoalStatus.Complete),
            ],
            harness.SessionManager.SetGoalStatusRequests);
        Assert.Equal(["thread-1"], harness.SessionManager.ClearGoalRequests);
        Assert.Contains("Paused session goal:", harness.Sender.Sent[0].Text);
        Assert.Contains("Resumed session goal:", harness.Sender.Sent[1].Text);
        Assert.Contains("Completed session goal:", harness.Sender.Sent[2].Text);
        Assert.Equal("Cleared the session goal.", harness.Sender.Sent[3].Text);
    }

    [Fact]
    public async Task HandleMessageAsync_StatusAndTailUseActiveSession()
    {
        using CommandHandlerHarness statusHarness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        SetUsageWindows(statusHarness);
        statusHarness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", statusHarness.Temp.Path));
        await statusHarness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await statusHarness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/status"),
            statusHarness.Sender,
            CancellationToken.None);

        Assert.Contains("Demo session", Assert.Single(statusHarness.Sender.Sent).Text);
        Assert.Contains("Status: idle", statusHarness.Sender.Sent.Single().Text);
        AssertCompactUsageSummary(statusHarness.Sender.Sent.Single().Text);

        using CommandHandlerHarness cachedMissHarness = CommandHandlerHarness.Create();
        cachedMissHarness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", cachedMissHarness.Temp.Path));
        await cachedMissHarness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);
        cachedMissHarness.AccountUsage.ExceptionToThrow = new OperationCanceledException();

        await cachedMissHarness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/model"),
            cachedMissHarness.Sender,
            CancellationToken.None);

        cachedMissHarness.Sender.Sent.Clear();
        cachedMissHarness.AccountUsage.ExceptionToThrow = null;
        SetUsageWindows(cachedMissHarness);

        await cachedMissHarness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/status"),
            cachedMissHarness.Sender,
            CancellationToken.None);

        AssertCompactUsageSummary(Assert.Single(cachedMissHarness.Sender.Sent).Text);

        using CommandHandlerHarness tailHarness = CommandHandlerHarness.Create();
        tailHarness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", tailHarness.Temp.Path));
        await tailHarness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await tailHarness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/tail 42"),
            tailHarness.Sender,
            CancellationToken.None);

        Assert.Equal(("thread-1", 42), Assert.Single(tailHarness.SessionManager.TailRequests));
        Assert.Equal("tail output", Assert.Single(tailHarness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_TailWithoutCountUsesDefaultLineCount()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/tail"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(("thread-1", 40), Assert.Single(harness.SessionManager.TailRequests));
        Assert.Equal("tail output", Assert.Single(harness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_UsageShowsAccountWindows()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.AccountUsage.Usage = new CodexAccountUsageVm(
            DateTimeOffset.Parse("2026-05-06T12:00:00Z", CultureInfo.InvariantCulture),
            [
                new CodexRateLimitSnapshotVm(
                    "codex",
                    null,
                    "pro",
                    null,
                    new CodexRateLimitWindowVm(
                        17,
                        DateTimeOffset.Parse("2026-05-06T14:30:00Z", CultureInfo.InvariantCulture),
                        300),
                    new CodexRateLimitWindowVm(
                        48,
                        DateTimeOffset.Parse("2026-05-10T12:00:00Z", CultureInfo.InvariantCulture),
                        10080)),
            ]);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/usage"),
            harness.Sender,
            CancellationToken.None);

        string text = Assert.Single(harness.Sender.Sent).Text;
        Assert.Contains("Codex usage", text);
        Assert.Contains("Plan: pro", text);
        Assert.Contains("5-hour window: 83% remaining (17% used)", text);
        Assert.Contains("Weekly window: 52% remaining (48% used)", text);
        Assert.Contains("resets in 2h 30m", text);
    }

    [Fact]
    public async Task HandleMessageAsync_UsageExplainsMissingCodexExecutable()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.AccountUsage.ExceptionToThrow = new FileNotFoundException("missing codex");

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/usage"),
            harness.Sender,
            CancellationToken.None);

        string text = Assert.Single(harness.Sender.Sent).Text;
        Assert.Contains("Codex usage unavailable", text);
        Assert.Contains("Codex:CodexPathOverride", text);
        Assert.Contains("PATH", text);
    }

    [Fact]
    public async Task HandleMessageAsync_OutboundStatusShowsQueueDetails()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.OutboundQueue.Status = new TelegramOutboundQueueStatus(
            1,
            2,
            3,
            100,
            new TelegramDestinationKey(5555, null),
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddSeconds(5),
            [
                new TelegramOutboundDestinationStatus(
                    5555,
                    null,
                    "thread-1",
                    PendingMessageCount: 2,
                    PendingChunkCount: 3,
                    PendingCharacterCount: 100,
                    FirstPendingUtc: DateTimeOffset.UtcNow.AddMinutes(-2),
                    LastEnqueuedUtc: DateTimeOffset.UtcNow.AddMinutes(-1),
                    ChatBackoffUntilUtc: DateTimeOffset.UtcNow.AddSeconds(10),
                    LastSentUtc: DateTimeOffset.UtcNow.AddSeconds(-30)),
            ]);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/outbound"),
            harness.Sender,
            CancellationToken.None);

        string text = Assert.Single(harness.Sender.Sent).Text;
        Assert.Contains("Outbound Telegram queue", text);
        Assert.Contains("Pending messages: 2", text);
        Assert.Contains("This chat:", text);
    }

    [Fact]
    public async Task HandleMessageAsync_QueueListsConversationPromptsWithControls()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        await harness.StateStore.EnqueueQueuedPromptAsync(
            CreateQueuedPrompt("aaaaaaaa11111111", 1234, conversation, "first line\nsecond line"),
            CancellationToken.None);
        await harness.StateStore.EnqueueQueuedPromptAsync(
            CreateQueuedPrompt("bbbbbbbb22222222", 1234, new TelegramConversationScope(5555, 77), "other topic"),
            CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/queue"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Queued prompts:", sent.Text);
        Assert.Contains("id aaaaaaaa", sent.Text);
        Assert.Contains("first line second line", sent.Text);
        Assert.DoesNotContain("other topic", sent.Text);
        Assert.Equal(["Send now", "Edit", "Delete", "Sessions", "Projects", "Help"], FlattenButtonLabels(sent));
    }

    [Fact]
    public async Task HandleMessageAsync_QueueMentionListsConversationPrompts()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        await harness.StateStore.EnqueueQueuedPromptAsync(
            CreateQueuedPrompt("aaaaaaaa11111111", 1234, conversation, "queued text"),
            CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/queue@codex_bot"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Queued prompts:", sent.Text);
        Assert.Contains("queued text", sent.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_QueueEditUpdatesOwnedPromptText()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        await harness.StateStore.EnqueueQueuedPromptAsync(
            CreateQueuedPrompt("aaaaaaaa11111111", 1234, conversation, "old text"),
            CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/queue edit aaaaaaaa new steering text"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Updated queued item aaaaaaaa.", sent.Text);
        Assert.Contains("new steering text", sent.Text);
        TelegramQueuedPrompt updated = Assert.Single(await harness.StateStore.ListQueuedPromptsAsync(1234, conversation, CancellationToken.None));
        Assert.Equal("new steering text", updated.Text);
    }

    [Fact]
    public async Task HandleCallbackAsync_QueueSendNowSteersAndRemovesPrompt()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        await harness.StateStore.EnqueueQueuedPromptAsync(
            CreateQueuedPrompt("aaaaaaaa11111111", 1234, conversation, "queued steering"),
            CancellationToken.None);

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-queue", 1234, conversation.ChatId, "private", "qnow:aaaaaaaa11111111", SourceMessageId: 42),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal("Sending queued item.", Assert.Single(harness.Sender.CallbackAnswers).Text);
        Assert.Equal(("thread-1", (object)"queued steering"), Assert.Single(harness.SessionManager.SteerRequests));
        Assert.Empty(await harness.StateStore.ListQueuedPromptsAsync(1234, conversation, CancellationToken.None));
        EditedTelegramMessage edited = Assert.Single(harness.Sender.Edited);
        Assert.Equal(42, edited.MessageId);
        Assert.Contains("Sent queued item aaaaaaaa", edited.Text);
        Assert.Contains("No queued prompts", edited.Text);
    }

    [Fact]
    public async Task HandleCallbackAsync_QueueSendNowWithAttachmentRetainsTemporaryFileAfterCodexHandoff()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        string attachmentPath = Path.Combine(harness.Temp.Path, "queued.png");
        await File.WriteAllTextAsync(attachmentPath, "image");
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        await harness.StateStore.EnqueueQueuedPromptAsync(
            CreateQueuedPrompt(
                "aaaaaaaa11111111",
                1234,
                conversation,
                "queued steering",
                [
                    new TelegramAttachmentDescriptor(attachmentPath, "queued.png", "image/png", IsImage: true),
                ]),
            CancellationToken.None);

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-queue", 1234, conversation.ChatId, "private", "qnow:aaaaaaaa11111111", SourceMessageId: 42),
            harness.Sender,
            CancellationToken.None);

        (string sessionId, object input) = Assert.Single(harness.SessionManager.SteerRequests);
        Assert.Equal("thread-1", sessionId);
        IReadOnlyList<CodexInputItem> items = Assert.IsAssignableFrom<IReadOnlyList<CodexInputItem>>(input);
        Assert.Collection(
            items,
            item => Assert.Equal("queued steering", Assert.IsType<CodexTextInput>(item).Text),
            item => Assert.Equal(attachmentPath, Assert.IsType<CodexLocalImageInput>(item).Path));
        Assert.True(File.Exists(attachmentPath));
        Assert.Empty(await harness.StateStore.ListQueuedPromptsAsync(1234, conversation, CancellationToken.None));
    }

    [Fact]
    public async Task HandleCallbackAsync_QueueSendNowRequeuesWhenSteerFails()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        harness.SessionManager.SteerExceptions.Enqueue(new InvalidOperationException("No active turn is currently running for this session."));
        await harness.StateStore.EnqueueQueuedPromptAsync(
            CreateQueuedPrompt("aaaaaaaa11111111", 1234, conversation, "queued steering"),
            CancellationToken.None);

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-queue", 1234, conversation.ChatId, "private", "qnow:aaaaaaaa11111111", SourceMessageId: 42),
            harness.Sender,
            CancellationToken.None);

        TelegramQueuedPrompt queued = Assert.Single(await harness.StateStore.ListQueuedPromptsAsync(1234, conversation, CancellationToken.None));
        Assert.Equal("aaaaaaaa11111111", queued.Id);
        EditedTelegramMessage edited = Assert.Single(harness.Sender.Edited);
        Assert.Contains("It is still queued.", edited.Text);
        Assert.Contains("queued steering", edited.Text);
    }

    [Fact]
    public async Task HandleCallbackAsync_QueueDeleteRemovesPromptAndTemporaryAttachments()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        string attachmentPath = Path.Combine(harness.Temp.Path, "queued.png");
        await File.WriteAllTextAsync(attachmentPath, "image");
        await harness.StateStore.EnqueueQueuedPromptAsync(
            CreateQueuedPrompt(
                "aaaaaaaa11111111",
                1234,
                conversation,
                "queued with attachment",
                [
                    new TelegramAttachmentDescriptor(attachmentPath, "queued.png", "image/png", IsImage: true),
                ]),
            CancellationToken.None);

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-queue", 1234, conversation.ChatId, "private", "qdel:aaaaaaaa11111111", SourceMessageId: 42),
            harness.Sender,
            CancellationToken.None);

        Assert.False(File.Exists(attachmentPath));
        Assert.Empty(await harness.StateStore.ListQueuedPromptsAsync(1234, conversation, CancellationToken.None));
        EditedTelegramMessage edited = Assert.Single(harness.Sender.Edited);
        Assert.Contains("Deleted queued item aaaaaaaa.", edited.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_LifecycleCommandsResolveSessions()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(new TelegramInboundMessage(1234, conversation.ChatId, "private", "/stop"), harness.Sender, CancellationToken.None);
        await harness.Handler.HandleMessageAsync(new TelegramInboundMessage(1234, conversation.ChatId, "private", "/kill thread-1"), harness.Sender, CancellationToken.None);
        await harness.Handler.HandleMessageAsync(new TelegramInboundMessage(1234, conversation.ChatId, "private", "/kill thread-1 confirm"), harness.Sender, CancellationToken.None);
        await harness.Handler.HandleMessageAsync(new TelegramInboundMessage(1234, conversation.ChatId, "private", "/rename thread-1 Renamed session"), harness.Sender, CancellationToken.None);
        await harness.Handler.HandleMessageAsync(new TelegramInboundMessage(1234, conversation.ChatId, "private", "/forget thread-1"), harness.Sender, CancellationToken.None);

        Assert.Collection(
            harness.Sender.Sent,
            sent => Assert.Contains("Stopped Demo session", sent.Text),
            sent => Assert.Contains("Usage: /kill <sessionId> confirm", sent.Text),
            sent => Assert.Contains("Killed Demo session", sent.Text),
            sent => Assert.Contains("Renamed to Renamed session.", sent.Text),
            sent => Assert.Contains("Forgot Renamed session", sent.Text));
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

    [Fact]
    public async Task HandleCallbackAsync_AllowsTrustedGroupCallbacks()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [],
        });
        await harness.StateStore.TrustChatAsync(-1005555, CancellationToken.None);

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-1", 1234, -1005555, "supergroup", "nav:sessions"),
            harness.Sender,
            CancellationToken.None);

        CallbackAnswer answer = Assert.Single(harness.Sender.CallbackAnswers);
        Assert.Equal("Opening menu.", answer.Text);
        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("No Codex sessions are known yet.", sent.Text);
    }

    [Fact]
    public async Task HandleCallbackAsync_RejectsMalformedAndUnknownActions()
    {
        using CommandHandlerHarness malformedHarness = CommandHandlerHarness.Create();

        await malformedHarness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-1", 1234, 5555, "private", "malformed"),
            malformedHarness.Sender,
            CancellationToken.None);

        Assert.Equal("Unsupported action.", Assert.Single(malformedHarness.Sender.CallbackAnswers).Text);

        using CommandHandlerHarness unknownHarness = CommandHandlerHarness.Create();

        await unknownHarness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-2", 1234, 5555, "private", "unknown:value"),
            unknownHarness.Sender,
            CancellationToken.None);

        Assert.Equal("Unsupported action.", Assert.Single(unknownHarness.Sender.CallbackAnswers).Text);
    }

    [Fact]
    public async Task HandleCallbackAsync_NavigationProjectsOpensProjectMenu()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        string projectPath = harness.Temp.CreateDirectory("repo");
        harness.ProjectCatalog.Projects.Add(new CodexProjectCatalogRecord
        {
            WorkingDirectory = projectPath,
            AddedAt = DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
        });

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-1", 1234, 5555, "private", "nav:projects"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal("Opening menu.", Assert.Single(harness.Sender.CallbackAnswers).Text);
        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Projects:", sent.Text);
        Assert.Equal(["Use", "Sessions", "Projects", "Help"], FlattenButtonLabels(sent));
    }

    [Fact]
    public async Task HandleCallbackAsync_NavigationSessionsHelpAndUnknownTargetsRespond()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        await harness.StateStore.TrackSessionAsync("thread-1", CancellationToken.None);

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-sessions", 1234, 5555, "private", "nav:sessions"),
            harness.Sender,
            CancellationToken.None);
        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-help", 1234, 5555, "private", "nav:help"),
            harness.Sender,
            CancellationToken.None);
        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-unknown", 1234, 5555, "private", "nav:missing"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(["Opening menu.", "Opening menu.", "Opening menu."], harness.Sender.CallbackAnswers.Select(answer => answer.Text));
        Assert.Collection(
            harness.Sender.Sent,
            sent => Assert.Contains("Demo session", sent.Text),
            sent => Assert.Contains("Commands:", sent.Text),
            sent => Assert.Equal("Unsupported navigation action.", sent.Text));
    }

    [Fact]
    public async Task HandleCallbackAsync_UseAndProjectSelectionEditSourceMessage()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        string projectPath = harness.Temp.CreateDirectory("repo");
        TelegramConversationScope conversation = new(5555, null);
        harness.ProjectCatalog.Projects.Add(new CodexProjectCatalogRecord
        {
            WorkingDirectory = projectPath,
            AddedAt = DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
        });
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", projectPath));

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-use", 1234, conversation.ChatId, "private", "use:thread-1", SourceMessageId: 42),
            harness.Sender,
            CancellationToken.None);
        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-project", 1234, conversation.ChatId, "private", "project:1", SourceMessageId: 43),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(["Selected.", "Selected project."], harness.Sender.CallbackAnswers.Select(answer => answer.Text));
        Assert.Equal("thread-1", await harness.StateStore.GetActiveSessionIdAsync(conversation, CancellationToken.None));
        Assert.Equal(projectPath, await harness.StateStore.GetActiveProjectWorkingDirectoryAsync(conversation, CancellationToken.None));
        Assert.Empty(harness.Sender.Sent);
        Assert.Collection(
            harness.Sender.Edited,
            edited =>
            {
                Assert.Equal(42, edited.MessageId);
                Assert.Contains("Selected Demo session.", edited.Text);
            },
            edited =>
            {
                Assert.Equal(43, edited.MessageId);
                Assert.Contains("Selected project repo.", edited.Text);
            });
    }

    [Fact]
    public async Task HandleCallbackAsync_StatusBackTailAndStopUseRequestedSession()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-status", 1234, 5555, "private", "status:thread-1", SourceMessageId: 10),
            harness.Sender,
            CancellationToken.None);
        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-back", 1234, 5555, "private", "back:thread-1", SourceMessageId: 11),
            harness.Sender,
            CancellationToken.None);
        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-tail", 1234, 5555, "private", "tail:thread-1", SourceMessageId: 12),
            harness.Sender,
            CancellationToken.None);
        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-stop", 1234, 5555, "private", "stop:thread-1", SourceMessageId: 13),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(["Status.", "Back.", "Tail.", "Stopping."], harness.Sender.CallbackAnswers.Select(answer => answer.Text));
        Assert.Equal([("thread-1", 40)], harness.SessionManager.TailRequests);
        Assert.Equal(["thread-1"], harness.SessionManager.StopRequests);
        Assert.Empty(harness.Sender.Sent);
        Assert.Collection(
            harness.Sender.Edited,
            edited => Assert.Contains("Demo session", edited.Text),
            edited => Assert.Contains("Demo session", edited.Text),
            edited => Assert.Equal("tail output", edited.Text),
            edited => Assert.Contains("Stopped Demo session.", edited.Text));
    }

    [Fact]
    public async Task HandleCallbackAsync_ModelAndThinkingMenusEditSourceMessage()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-model", 1234, 5555, "private", "model:thread-1", SourceMessageId: 10),
            harness.Sender,
            CancellationToken.None);
        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-thinking", 1234, 5555, "private", "thinking:thread-1", SourceMessageId: 11),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(["Model settings.", "Thinking settings."], harness.Sender.CallbackAnswers.Select(answer => answer.Text));
        Assert.Empty(harness.Sender.Sent);
        Assert.Collection(
            harness.Sender.Edited,
            edited =>
            {
                Assert.Equal(10, edited.MessageId);
                Assert.Equal("Loading model settings...", edited.Text);
                Assert.Null(edited.Buttons);
            },
            edited =>
            {
                Assert.Contains("Model settings:", edited.Text);
                Assert.Contains("[x] GPT-5.4 Mini", FlattenButtonLabels(edited));
                Assert.Contains("Back", FlattenButtonLabels(edited));
            },
            edited =>
            {
                Assert.Equal(11, edited.MessageId);
                Assert.Equal("Loading thinking settings...", edited.Text);
                Assert.Null(edited.Buttons);
            },
            edited =>
            {
                Assert.Contains("Thinking settings:", edited.Text);
                Assert.Contains("[x] High", FlattenButtonLabels(edited));
                Assert.Contains("Back", FlattenButtonLabels(edited));
            });
    }

    [Fact]
    public async Task HandleCallbackAsync_ModelSelectionAnswersBeforeSettingsUpdateFinishes()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        TaskCompletionSource<CodexSessionModelSettings> updateCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.SessionManager.UpdateModelSettingsCompletion = updateCompletion;

        Task operation = harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-1", 1234, 5555, "private", "modelset:thread-1|gpt-5.4-mini", SourceMessageId: 22),
            harness.Sender,
            CancellationToken.None);

        await WaitUntilAsync(() =>
            harness.Sender.CallbackAnswers.Count == 1
            && harness.Sender.Edited.Count == 1
            && harness.SessionManager.UpdateRequests.Count == 1);
        Assert.Equal("Updated model.", Assert.Single(harness.Sender.CallbackAnswers).Text);
        Assert.False(operation.IsCompleted);
        Assert.Empty(harness.Sender.Sent);
        Assert.Equal("Updating model settings...", Assert.Single(harness.Sender.Edited).Text);
        Assert.Equal(("thread-1", (string?)"gpt-5.4-mini", (string?)null), Assert.Single(harness.SessionManager.UpdateRequests));

        updateCompletion.SetResult(CreateModelSettings("thread-1", "Demo session", "gpt-5.4-mini", "high"));
        await operation;

        Assert.Empty(harness.Sender.Sent);
        Assert.Collection(
            harness.Sender.Edited,
            edited => Assert.Equal("Updating model settings...", edited.Text),
            edited => Assert.Contains("Model settings:", edited.Text));
    }

    [Fact]
    public async Task HandleCallbackAsync_ThinkingSelectionAnswersBeforeSettingsUpdateFinishes()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        TaskCompletionSource<CodexSessionModelSettings> updateCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.SessionManager.UpdateModelSettingsCompletion = updateCompletion;

        Task operation = harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-1", 1234, 5555, "private", "thinkingset:thread-1|xhigh", SourceMessageId: 23),
            harness.Sender,
            CancellationToken.None);

        await WaitUntilAsync(() =>
            harness.Sender.CallbackAnswers.Count == 1
            && harness.Sender.Edited.Count == 1
            && harness.SessionManager.UpdateRequests.Count == 1);
        Assert.Equal("Updated thinking.", Assert.Single(harness.Sender.CallbackAnswers).Text);
        Assert.False(operation.IsCompleted);
        Assert.Empty(harness.Sender.Sent);
        Assert.Equal("Updating thinking settings...", Assert.Single(harness.Sender.Edited).Text);
        Assert.Equal(("thread-1", (string?)null, (string?)"xhigh"), Assert.Single(harness.SessionManager.UpdateRequests));

        updateCompletion.SetResult(CreateModelSettings("thread-1", "Demo session", "gpt-5.4-mini", "XHigh"));
        await operation;

        Assert.Empty(harness.Sender.Sent);
        Assert.Collection(
            harness.Sender.Edited,
            edited => Assert.Equal("Updating thinking settings...", edited.Text),
            edited => Assert.Contains("Thinking settings:", edited.Text));
    }

    [Fact]
    public async Task HandleCallbackAsync_StaleModelIndexShowsRefreshGuidance()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-1", 1234, 5555, "private", "modelset:thread-1|99"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal("Updated model.", Assert.Single(harness.Sender.CallbackAnswers).Text);
        Assert.Contains("model button is stale", Assert.Single(harness.Sender.Sent).Text);
        Assert.Empty(harness.SessionManager.UpdateRequests);
    }

    private static CodexSessionSummary CreateSession(
        string id,
        string name,
        string? workingDirectory,
        CodexSessionStatus status = CodexSessionStatus.Exited)
        => new(
            id,
            name,
            status,
            workingDirectory,
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            null,
            null);

    private static TelegramQueuedPrompt CreateQueuedPrompt(
        string id,
        long userId,
        TelegramConversationScope conversation,
        string text,
        IReadOnlyList<TelegramAttachmentDescriptor>? attachments = null)
        => new(
            id,
            userId,
            conversation.ChatId,
            "thread-1",
            "Demo session",
            text,
            DateTimeOffset.Parse("2026-05-06T12:00:00Z", CultureInfo.InvariantCulture),
            conversation.MessageThreadId,
            attachments);

    private static CodexSessionModelSettings CreateModelSettings(string sessionId, string sessionName, string model, string effort)
        => new(
            sessionId,
            sessionName,
            model,
            effort,
            [
                new CodexModelVm(
                    "gpt-5.4-mini",
                    "GPT-5.4 Mini",
                    "Fast model for Telegram tests.",
                    CodexReasoningEffort.High,
                    [CodexReasoningEffort.Low, CodexReasoningEffort.Medium, CodexReasoningEffort.High, CodexReasoningEffort.XHigh],
                    IsDefault: true,
                    Hidden: false,
                    SupportsPersonality: false,
                    AvailabilityMessage: null),
            ],
            [CodexReasoningEffort.Low, CodexReasoningEffort.Medium, CodexReasoningEffort.High, CodexReasoningEffort.XHigh]);

    private static CodexThreadGoalVm CreateGoal(
        string threadId,
        string objective,
        CodexThreadGoalStatus status = CodexThreadGoalStatus.Active,
        long? tokenBudget = null)
        => new(
            threadId,
            objective,
            status,
            tokenBudget,
            tokenBudget.HasValue ? 1000 : 0,
            95,
            DateTimeOffset.Parse("2026-05-06T12:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-05-06T12:05:00Z", CultureInfo.InvariantCulture));

    private static void SetUsageWindows(CommandHandlerHarness harness)
        => harness.AccountUsage.Usage = new CodexAccountUsageVm(
            DateTimeOffset.Parse("2026-05-06T12:00:00Z", CultureInfo.InvariantCulture),
            [
                new CodexRateLimitSnapshotVm(
                    "codex",
                    null,
                    "pro",
                    null,
                    new CodexRateLimitWindowVm(
                        17,
                        DateTimeOffset.Parse("2026-05-06T14:30:00Z", CultureInfo.InvariantCulture),
                        300),
                    new CodexRateLimitWindowVm(
                        48,
                        DateTimeOffset.Parse("2026-05-10T12:00:00Z", CultureInfo.InvariantCulture),
                        10080)),
            ]);

    private static void AssertCompactUsageSummary(string text)
    {
        Assert.Contains("Rate limits (pro): 5-hour block: 83%, resets", text);
        Assert.Contains("weekly block: 52%, resets", text);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Timed out waiting for the expected callback-side effect.");
            await Task.Delay(10);
        }
    }

    private static IReadOnlyList<string> FlattenButtonLabels(SentTelegramMessage message)
        => message.Buttons?.SelectMany(row => row.Select(button => button.Text)).ToArray() ?? [];

    private static IReadOnlyList<string> FlattenButtonLabels(EditedTelegramMessage message)
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
            FakeCodexAccountUsageService accountUsage,
            FakeProjectCatalogStore projectCatalog,
            TelegramBotStateStore stateStore,
            FakeOutboundTelegramQueue outboundQueue,
            TelegramTypingIndicatorRegistry typingIndicatorRegistry,
            FakeTelegramForumTopicService topicService,
            FakeAudioTranscriptionService audioTranscription,
            TestTelegramBotMessageSender sender,
            TelegramCodexBotCommandHandler handler)
        {
            Temp = temp;
            SessionManager = sessionManager;
            AccountUsage = accountUsage;
            ProjectCatalog = projectCatalog;
            StateStore = stateStore;
            OutboundQueue = outboundQueue;
            TypingIndicatorRegistry = typingIndicatorRegistry;
            TopicService = topicService;
            AudioTranscription = audioTranscription;
            Sender = sender;
            Handler = handler;
        }

        public TemporaryDirectory Temp { get; }

        public FakeCodexSessionManager SessionManager { get; }

        public FakeCodexAccountUsageService AccountUsage { get; }

        public FakeProjectCatalogStore ProjectCatalog { get; }

        public TelegramBotStateStore StateStore { get; }

        public FakeOutboundTelegramQueue OutboundQueue { get; }

        public TelegramTypingIndicatorRegistry TypingIndicatorRegistry { get; }

        public FakeTelegramForumTopicService TopicService { get; }

        public FakeAudioTranscriptionService AudioTranscription { get; }

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
            FakeCodexAccountUsageService accountUsage = new();
            FakeProjectCatalogStore projectCatalog = new();
            TelegramBotStateStore stateStore = new(codexOptions);
            FakeOutboundTelegramQueue outboundQueue = new();
            TelegramTypingIndicatorRegistry typingIndicatorRegistry = new();
            FakeTelegramForumTopicService topicService = new();
            FakeAudioTranscriptionService audioTranscription = new();
            TestTelegramBotMessageSender sender = new();
            TelegramCodexBotCommandHandler handler = new(
                new TelegramCommandParser(),
                new TelegramMessageChunker(),
                sessionManager,
                accountUsage,
                projectCatalog,
                new CodexWorkspaceBrowser(codexOptions),
                stateStore,
                new FakeTurnExecutionCoordinator(),
                new TelegramThreadFollowRegistry(),
                typingIndicatorRegistry,
                topicService,
                audioTranscription,
                outboundQueue,
                Microsoft.Extensions.Options.Options.Create(botOptions ?? new TelegramBotOptions
                {
                    AllowedUserIds = [1234],
                }),
                NullLogger<TelegramCodexBotCommandHandler>.Instance);

            return new CommandHandlerHarness(temp, sessionManager, accountUsage, projectCatalog, stateStore, outboundQueue, typingIndicatorRegistry, topicService, audioTranscription, sender, handler);
        }

        public void Dispose()
            => Temp.Dispose();
    }

    private sealed class FakeCodexSessionManager : ICodexSessionManager
    {
        public List<CodexSessionSummary> Sessions { get; } = [];

        public List<CreateCodexSessionRequest> CreateRequests { get; } = [];

        public List<object> SendRequests { get; } = [];

        public List<string> SendSessionIds { get; } = [];

        public Queue<Exception> SendExceptions { get; } = [];

        public List<(string SessionId, string? Model, string? ReasoningEffort)> UpdateRequests { get; } = [];

        public Dictionary<string, CodexThreadGoalVm> Goals { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string SessionId, string Objective, long? TokenBudget)> SetGoalRequests { get; } = [];

        public List<(string SessionId, CodexThreadGoalStatus Status)> SetGoalStatusRequests { get; } = [];

        public List<string> ClearGoalRequests { get; } = [];

        public List<(string SessionId, int LineCount)> TailRequests { get; } = [];

        public List<string> StopRequests { get; } = [];

        public List<(string SessionId, object Input)> SteerRequests { get; } = [];

        public Queue<Exception> SteerExceptions { get; } = [];

        public TaskCompletionSource<CodexSessionModelSettings>? UpdateModelSettingsCompletion { get; set; }

        public TaskCompletionSource<CodexThreadExecutionVm>? PendingSend { get; set; }

        public TaskCompletionSource<bool> SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            ThrowNextSendExceptionIfPresent();
            SendSessionIds.Add(sessionId);
            SendRequests.Add(input);
            SendStarted.TrySetResult(true);
            if (PendingSend is not null)
            {
                return PendingSend.Task;
            }

            return Task.FromResult(new CodexThreadExecutionVm(sessionId, "turn-1", "running", null));
        }

        public Task<CodexThreadExecutionVm> SendAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
        {
            ThrowNextSendExceptionIfPresent();
            SendSessionIds.Add(sessionId);
            SendRequests.Add(input);
            SendStarted.TrySetResult(true);
            if (PendingSend is not null)
            {
                return PendingSend.Task;
            }

            return Task.FromResult(new CodexThreadExecutionVm(sessionId, "turn-1", "running", null));
        }

        private void ThrowNextSendExceptionIfPresent()
        {
            if (SendExceptions.TryDequeue(out Exception? exception))
            {
                throw exception;
            }
        }

        public Task SteerAsync(string sessionId, string input, CancellationToken cancellationToken)
        {
            ThrowNextSteerExceptionIfPresent();
            SteerRequests.Add((sessionId, input));
            return Task.CompletedTask;
        }

        public Task SteerAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
        {
            ThrowNextSteerExceptionIfPresent();
            SteerRequests.Add((sessionId, input));
            return Task.CompletedTask;
        }

        private void ThrowNextSteerExceptionIfPresent()
        {
            if (SteerExceptions.TryDequeue(out Exception? exception))
            {
                throw exception;
            }
        }

        public Task<CodexSessionModelSettings> GetModelSettingsAsync(string sessionId, CancellationToken cancellationToken)
        {
            CodexSessionSummary? session = Sessions.FirstOrDefault(candidate => string.Equals(candidate.Id, sessionId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(CreateModelSettings(sessionId, session?.Name ?? sessionId, "gpt-5.4-mini", "high"));
        }

        public Task<CodexSessionModelSettings> UpdateModelSettingsAsync(
            string sessionId,
            string? model,
            string? reasoningEffort,
            CancellationToken cancellationToken)
        {
            UpdateRequests.Add((sessionId, model, reasoningEffort));
            if (UpdateModelSettingsCompletion is not null)
            {
                return UpdateModelSettingsCompletion.Task;
            }

            return Task.FromResult(CreateModelSettings(
                sessionId,
                Sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase))?.Name ?? sessionId,
                model ?? "gpt-5.4-mini",
                reasoningEffort ?? "high"));
        }

        public Task<CodexThreadGoalVm?> GetGoalAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult(Goals.GetValueOrDefault(sessionId));

        public Task<CodexThreadGoalVm> SetGoalAsync(
            string sessionId,
            string objective,
            long? tokenBudget,
            CancellationToken cancellationToken)
        {
            SetGoalRequests.Add((sessionId, objective, tokenBudget));
            CodexThreadGoalVm goal = CreateGoal(sessionId, objective, CodexThreadGoalStatus.Active, tokenBudget);
            Goals[sessionId] = goal;
            return Task.FromResult(goal);
        }

        public Task<CodexThreadGoalVm> SetGoalStatusAsync(
            string sessionId,
            CodexThreadGoalStatus status,
            CancellationToken cancellationToken)
        {
            SetGoalStatusRequests.Add((sessionId, status));
            CodexThreadGoalVm goal = Goals.GetValueOrDefault(sessionId) ?? CreateGoal(sessionId, "Existing goal");
            goal = goal with { Status = status };
            Goals[sessionId] = goal;
            return Task.FromResult(goal);
        }

        public Task<bool> ClearGoalAsync(string sessionId, CancellationToken cancellationToken)
        {
            ClearGoalRequests.Add(sessionId);
            return Task.FromResult(Goals.Remove(sessionId));
        }

        public Task<string> TailAsync(string sessionId, int lineCount, CancellationToken cancellationToken)
        {
            TailRequests.Add((sessionId, lineCount));
            return Task.FromResult("tail output");
        }

        public Task StopAsync(string sessionId, CancellationToken cancellationToken)
        {
            StopRequests.Add(sessionId);
            return Task.CompletedTask;
        }

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

    private sealed class FakeCodexAccountUsageService : ICodexAccountUsageService
    {
        public CodexAccountUsageVm Usage { get; set; } = new(DateTimeOffset.Parse("2026-05-06T12:00:00Z", CultureInfo.InvariantCulture), []);

        public Exception? ExceptionToThrow { get; set; }

        public Task<CodexAccountUsageVm> GetUsageAsync(CancellationToken cancellationToken)
            => ExceptionToThrow is null ? Task.FromResult(Usage) : Task.FromException<CodexAccountUsageVm>(ExceptionToThrow);
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
        public List<(long ChatId, string Name)> CreateRequests { get; } = [];

        public Task<TelegramForumTopicCreationResult> CreateForumTopicAsync(long chatId, string name, CancellationToken cancellationToken)
        {
            CreateRequests.Add((chatId, name));
            return Task.FromResult(new TelegramForumTopicCreationResult(123, name));
        }
    }

    private sealed class FakeAudioTranscriptionService : IAudioTranscriptionService
    {
        public string Transcript { get; set; } = "transcribed text";

        public Exception? Exception { get; set; }

        public Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Transcript);
        }
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

        public Task<int?> SendStatusMessageAsync(
            TelegramConversationScope conversation,
            string text,
            CancellationToken cancellationToken)
        {
            Sent.Add(new SentTelegramMessage(conversation, text, null));
            return Task.FromResult<int?>(Sent.Count);
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

        public Task AcknowledgeMessageAsync(TelegramMessageAcknowledgement acknowledgement, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SendTypingActionAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
            => Task.CompletedTask;
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
