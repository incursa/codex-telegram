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
        Assert.Contains("Plain text and attachments do not auto-route", sent.Text);
        Assert.Contains("Next: use /send <text>", sent.Text);
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
        Assert.Contains("/outbound", sent.Text);
        Assert.Equal(["Sessions", "Projects", "Help"], FlattenButtonLabels(sent));
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
        Assert.Contains("/new <name>", text);
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
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/model gpt-5.4-mini thinking medium"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(("thread-1", (string?)"gpt-5.4-mini", (string?)"medium"), Assert.Single(harness.SessionManager.UpdateRequests));
        Assert.Contains("Updated model settings:", Assert.Single(harness.Sender.Sent).Text);
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
    public async Task HandleMessageAsync_StatusAndTailUseActiveSession()
    {
        using CommandHandlerHarness statusHarness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        statusHarness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", statusHarness.Temp.Path));
        await statusHarness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await statusHarness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/status"),
            statusHarness.Sender,
            CancellationToken.None);

        Assert.Contains("Demo session", Assert.Single(statusHarness.Sender.Sent).Text);
        Assert.Contains("Status: idle", statusHarness.Sender.Sent.Single().Text);

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
    public async Task HandleCallbackAsync_ModelSelectionAnswersBeforeSettingsUpdateFinishes()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        TaskCompletionSource<CodexSessionModelSettings> updateCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.SessionManager.UpdateModelSettingsCompletion = updateCompletion;

        Task operation = harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-1", 1234, 5555, "private", "modelset:thread-1|gpt-5.4-mini"),
            harness.Sender,
            CancellationToken.None);

        await WaitUntilAsync(() => harness.Sender.CallbackAnswers.Count == 1);
        Assert.Equal("Updated model.", Assert.Single(harness.Sender.CallbackAnswers).Text);
        Assert.False(operation.IsCompleted);
        Assert.Empty(harness.Sender.Sent);
        Assert.Equal(("thread-1", (string?)"gpt-5.4-mini", (string?)null), Assert.Single(harness.SessionManager.UpdateRequests));

        updateCompletion.SetResult(CreateModelSettings("thread-1", "Demo session", "gpt-5.4-mini", "high"));
        await operation;

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Model settings:", sent.Text);
    }

    [Fact]
    public async Task HandleCallbackAsync_ThinkingSelectionAnswersBeforeSettingsUpdateFinishes()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        TaskCompletionSource<CodexSessionModelSettings> updateCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.SessionManager.UpdateModelSettingsCompletion = updateCompletion;

        Task operation = harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-1", 1234, 5555, "private", "thinkingset:thread-1|xhigh"),
            harness.Sender,
            CancellationToken.None);

        await WaitUntilAsync(() => harness.Sender.CallbackAnswers.Count == 1);
        Assert.Equal("Updated thinking.", Assert.Single(harness.Sender.CallbackAnswers).Text);
        Assert.False(operation.IsCompleted);
        Assert.Empty(harness.Sender.Sent);
        Assert.Equal(("thread-1", (string?)null, (string?)"xhigh"), Assert.Single(harness.SessionManager.UpdateRequests));

        updateCompletion.SetResult(CreateModelSettings("thread-1", "Demo session", "gpt-5.4-mini", "XHigh"));
        await operation;

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Thinking settings:", sent.Text);
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
            FakeOutboundTelegramQueue outboundQueue,
            FakeTelegramForumTopicService topicService,
            FakeAudioTranscriptionService audioTranscription,
            TestTelegramBotMessageSender sender,
            TelegramCodexBotCommandHandler handler)
        {
            Temp = temp;
            SessionManager = sessionManager;
            ProjectCatalog = projectCatalog;
            StateStore = stateStore;
            OutboundQueue = outboundQueue;
            TopicService = topicService;
            AudioTranscription = audioTranscription;
            Sender = sender;
            Handler = handler;
        }

        public TemporaryDirectory Temp { get; }

        public FakeCodexSessionManager SessionManager { get; }

        public FakeProjectCatalogStore ProjectCatalog { get; }

        public TelegramBotStateStore StateStore { get; }

        public FakeOutboundTelegramQueue OutboundQueue { get; }

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
            FakeProjectCatalogStore projectCatalog = new();
            TelegramBotStateStore stateStore = new(codexOptions);
            FakeOutboundTelegramQueue outboundQueue = new();
            FakeTelegramForumTopicService topicService = new();
            FakeAudioTranscriptionService audioTranscription = new();
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
                topicService,
                audioTranscription,
                outboundQueue,
                Microsoft.Extensions.Options.Options.Create(botOptions ?? new TelegramBotOptions
                {
                    AllowedUserIds = [1234],
                }),
                NullLogger<TelegramCodexBotCommandHandler>.Instance);

            return new CommandHandlerHarness(temp, sessionManager, projectCatalog, stateStore, outboundQueue, topicService, audioTranscription, sender, handler);
        }

        public void Dispose()
            => Temp.Dispose();
    }

    private sealed class FakeCodexSessionManager : ICodexSessionManager
    {
        public List<CodexSessionSummary> Sessions { get; } = [];

        public List<CreateCodexSessionRequest> CreateRequests { get; } = [];

        public List<object> SendRequests { get; } = [];

        public List<(string SessionId, string? Model, string? ReasoningEffort)> UpdateRequests { get; } = [];

        public List<(string SessionId, int LineCount)> TailRequests { get; } = [];

        public TaskCompletionSource<CodexSessionModelSettings>? UpdateModelSettingsCompletion { get; set; }

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

        public Task<string> TailAsync(string sessionId, int lineCount, CancellationToken cancellationToken)
        {
            TailRequests.Add((sessionId, lineCount));
            return Task.FromResult("tail output");
        }

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
