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

        CreateCodexSessionRequest request = Assert.Single(harness.SessionManager.CreateRequests);
        Assert.StartsWith("repo session ", request.Name, StringComparison.Ordinal);
        Assert.Equal(projectPath, request.WorkingDirectory);
        Assert.Equal("please look at this", Assert.Single(harness.SessionManager.SendRequests));
        Assert.Equal("thread-1", await harness.StateStore.GetActiveSessionIdAsync(conversation, CancellationToken.None));
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task HandleMessageAsync_RegistersSourceMessageForTurnReaction()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "please inspect this", SourceMessageId: 77),
            harness.Sender,
            CancellationToken.None);

        TelegramTurnReactionTarget? target = harness.TurnReactionRegistry.TryTake("thread-1", "turn-1");
        Assert.NotNull(target);
        Assert.Equal(new TelegramConversationScope(5555, null), target.Conversation);
        Assert.Equal(77, target.MessageId);
    }

    [Fact]
    public async Task HandleMessageAsync_WhenSendFailsReactsToSourceMessage()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.SessionManager.SendExceptions.Enqueue(new InvalidOperationException("send failed"));

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "please inspect this", SourceMessageId: 77),
            harness.Sender,
            CancellationToken.None);

        TelegramMessageReaction reaction = Assert.Single(harness.Sender.Reactions);
        Assert.Equal(TelegramMessageReactionKind.Failed, reaction.Kind);
        Assert.Equal(77, reaction.MessageId);
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
        Assert.Empty(harness.Sender.Sent);
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
        Assert.Contains("Here's what I transcribed:", Assert.Single(harness.Sender.Sent).Text);
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
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task HandleMessageAsync_WhenActiveTurnUsesEditableInputBundleAndSteerButton()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(
            inputOptionsOverride: new TelegramInputOptions
            {
                DefaultCaptureMode = TelegramInputCaptureMode.BundleWhenActiveOrMedia,
            });
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path, CodexSessionStatus.Running));
        harness.TurnCoordinator.ActiveThreadIds.Add("thread-1");
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "voice transcript note", SourceMessageId: 10),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage card = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Input ready", card.Text);
        Assert.Contains("voice transcript note", card.Text);
        string steerCallback = Assert.Single(card.Buttons!.SelectMany(row => row), button => button.Text == "Steer current turn").CallbackData;
        string imagePath = Path.Combine(harness.Temp.Path, "screen.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(
                1234,
                conversation.ChatId,
                "private",
                "extra screenshot context",
                Attachments:
                [
                    new TelegramAttachmentDescriptor(imagePath, "screen.png", "image/png", IsImage: true),
                ],
                SourceMessageId: 11),
            harness.Sender,
            CancellationToken.None);

        EditedTelegramMessage edited = Assert.Single(harness.Sender.Edited);
        Assert.Contains("extra screenshot context", edited.Text);
        Assert.Contains("Attachments: 1", edited.Text);
        Assert.Empty(harness.SessionManager.SendRequests);
        steerCallback = Assert.Single(edited.Buttons!.SelectMany(row => row), button => button.Text == "Steer current turn").CallbackData;

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("bundle-steer", 1234, conversation.ChatId, "private", steerCallback, SourceMessageId: edited.MessageId),
            harness.Sender,
            CancellationToken.None);

        (string sessionId, object inputObject) = Assert.Single(harness.SessionManager.SteerRequests);
        Assert.Equal("thread-1", sessionId);
        IReadOnlyList<CodexInputItem> input = Assert.IsAssignableFrom<IReadOnlyList<CodexInputItem>>(inputObject);
        Assert.Contains(input, item => item is CodexTextInput text && text.Text.Contains("voice transcript note", StringComparison.Ordinal));
        CodexLocalImageInput imageInput = Assert.IsType<CodexLocalImageInput>(Assert.Single(input, item => item is CodexLocalImageInput));
        Assert.Contains("telegram-attachments", imageInput.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(imageInput.Path));
        Assert.False(File.Exists(imagePath));
    }

    [Fact]
    public async Task HandleMessageAsync_DefaultCaptureModeCapturesShortPlainTextBeforeStartingTurn()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(
            inputOptionsOverride: new TelegramInputOptions());
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "short prompt", SourceMessageId: 9),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Input ready", sent.Text);
        Assert.Contains("short prompt", sent.Text);
        Assert.Empty(harness.SessionManager.SendRequests);

        TelegramInputBundle bundle = Assert.Single(await harness.InputBundleStore.ListAsync(conversation, CancellationToken.None));
        Assert.Equal(TelegramInputBundleStatus.Capturing, bundle.Status);
        Assert.Equal("short prompt", bundle.CombinedText);
    }

    [Fact]
    public async Task HandleMessageAsync_LongPlainTextCapturesBundleBeforeStartingTurn()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(
            inputOptionsOverride: new TelegramInputOptions
            {
                DefaultCaptureMode = TelegramInputCaptureMode.ImmediateText,
            });
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        string firstChunk = new('a', TelegramBotDefaults.MaxTelegramMessageLength + 1);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", firstChunk, SourceMessageId: 10),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Input ready", sent.Text);
        Assert.Contains("Text: 1 part", sent.Text);
        Assert.Empty(harness.SessionManager.SendRequests);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "second chunk", SourceMessageId: 11),
            harness.Sender,
            CancellationToken.None);

        EditedTelegramMessage edited = Assert.Single(harness.Sender.Edited);
        Assert.Contains("Text: 2 parts", edited.Text);
        Assert.Empty(harness.SessionManager.SendRequests);

        TelegramInputBundle bundle = Assert.Single(await harness.InputBundleStore.ListAsync(conversation, CancellationToken.None));
        Assert.Equal(2, bundle.TextParts.Count);
        Assert.Equal("second chunk", bundle.TextParts[1].Text);
        Assert.Contains("second chunk", bundle.CombinedText);
    }

    [Fact]
    public async Task HandleMessageAsync_WhenBundleCardEditFailsStoresReplacementMessageId()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(
            inputOptionsOverride: new TelegramInputOptions
            {
                DefaultCaptureMode = TelegramInputCaptureMode.BundleAlways,
            });
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "first", SourceMessageId: 10),
            harness.Sender,
            CancellationToken.None);
        harness.Sender.EditFailures.Enqueue(new InvalidOperationException("message to edit not found"));

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "second", SourceMessageId: 11),
            harness.Sender,
            CancellationToken.None);
        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "third", SourceMessageId: 12),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(2, harness.Sender.Sent.Count);
        Assert.Collection(
            harness.Sender.Edited,
            edited => Assert.Equal(1, edited.MessageId),
            edited => Assert.Equal(2, edited.MessageId));
        TelegramInputBundle bundle = Assert.Single(await harness.InputBundleStore.ListAsync(conversation, CancellationToken.None));
        Assert.Equal(2, bundle.StatusCardMessageId);
    }

    [Fact]
    public async Task HandleCallbackAsync_BundleClearClearsContentAndLeavesBundleOpen()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(
            inputOptionsOverride: new TelegramInputOptions
            {
                DefaultCaptureMode = TelegramInputCaptureMode.BundleAlways,
            });
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);
        string imagePath = Path.Combine(harness.Temp.Path, "clear.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(
                1234,
                conversation.ChatId,
                "private",
                "clear me",
                Attachments: [new TelegramAttachmentDescriptor(imagePath, "clear.png", "image/png", IsImage: true)],
                SourceMessageId: 15),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage card = Assert.Single(harness.Sender.Sent);
        string clearCallback = Assert.Single(card.Buttons!.SelectMany(row => row), button => button.Text == "Clear").CallbackData;
        TelegramInputBundle bundleBefore = Assert.Single(await harness.InputBundleStore.ListAsync(conversation, CancellationToken.None));
        string durablePath = Assert.Single(bundleBefore.Attachments).FilePath;
        Assert.True(File.Exists(durablePath));

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("bundle-clear", 1234, conversation.ChatId, "private", clearCallback, SourceMessageId: 1),
            harness.Sender,
            CancellationToken.None);

        TelegramInputBundle bundleAfter = Assert.Single(await harness.InputBundleStore.ListAsync(conversation, CancellationToken.None));
        Assert.Equal(TelegramInputBundleStatus.Capturing, bundleAfter.Status);
        Assert.Empty(bundleAfter.TextParts);
        Assert.Empty(bundleAfter.Attachments);
        Assert.False(File.Exists(durablePath));
        EditedTelegramMessage edited = Assert.Single(harness.Sender.Edited);
        Assert.Contains("Add input", edited.Text);
        Assert.DoesNotContain("Attachments:", edited.Text);
    }

    [Fact]
    public async Task HandleMessageAsync_QueuedAttachmentIsCopiedToDurableStateDirectory()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path, CodexSessionStatus.Running));
        harness.TurnCoordinator.ActiveThreadIds.Add("thread-1");
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);
        string sourcePath = Path.Combine(harness.Temp.Path, "queued.png");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(
                1234,
                conversation.ChatId,
                "private",
                "/send queued attachment",
                Attachments: [new TelegramAttachmentDescriptor(sourcePath, "queued.png", "image/png", IsImage: true)],
                SourceMessageId: 21),
            harness.Sender,
            CancellationToken.None);

        TelegramQueuedPrompt prompt = Assert.Single(await harness.StateStore.ListQueuedPromptsAsync(1234, conversation, CancellationToken.None));
        TelegramAttachmentDescriptor attachment = Assert.Single(prompt.Attachments!);
        Assert.Contains("telegram-attachments", attachment.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(attachment.FilePath));
        Assert.False(File.Exists(sourcePath));
    }

    [Fact]
    public async Task HandleMessageAsync_BundleAlwaysCapturesVoiceTranscriptWithoutImmediateSend()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(
            inputOptionsOverride: new TelegramInputOptions
            {
                DefaultCaptureMode = TelegramInputCaptureMode.BundleAlways,
            });
        harness.AudioTranscription.Transcript = "transcribed bundle text";

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(
                1234,
                5555,
                "private",
                null,
                AudioFilePath: "not-created.ogg",
                SourceMessageId: 20),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage card = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Input ready", card.Text);
        Assert.Contains("transcribed bundle text", card.Text);
        Assert.Equal(
            ["Send now", "Add more", "Clear", "Cancel"],
            card.Buttons!.SelectMany(row => row.Select(button => button.Text)).ToArray());
        Assert.Empty(harness.SessionManager.SendRequests);
    }

    [Fact]
    public async Task ProcessDueInputBundleAsync_IdleVoiceBundleAutoSendsAfterInactivity()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(
            inputOptionsOverride: new TelegramInputOptions
            {
                DefaultCaptureMode = TelegramInputCaptureMode.BundleWhenActiveOrMedia,
                AutoDispatchAfterSeconds = 5,
            });
        TelegramConversationScope conversation = new(5555, null);
        string projectPath = harness.Temp.CreateDirectory("repo");
        string audioPath = Path.Combine(harness.Temp.Path, "voice.ogg");
        await File.WriteAllBytesAsync(audioPath, [1, 2, 3]);
        harness.AudioTranscription.Transcript = "auto send transcript";
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

        TelegramInputBundle bundle = Assert.Single(await harness.InputBundleStore.ListAsync(conversation, CancellationToken.None));
        Assert.Equal(TelegramInputBundleStatus.Capturing, bundle.Status);
        Assert.Empty(harness.SessionManager.SendRequests);
        await MarkBundleIdleAsync(harness, bundle, secondsAgo: 10);

        Assert.True(await harness.Handler.ProcessDueInputBundleAsync(harness.Sender, CancellationToken.None));

        Assert.Equal("auto send transcript", Assert.Single(harness.SessionManager.SendRequests));
        TelegramInputBundle completed = Assert.Single(await harness.InputBundleStore.ListAsync(conversation, CancellationToken.None));
        Assert.Equal(TelegramInputBundleStatus.Sent, completed.Status);
        Assert.Contains(harness.Sender.Edited, edited =>
            edited.Text.Contains("Bundle sent to Codex", StringComparison.Ordinal)
            && edited.Text.Contains("Live updates will stream here", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessDueInputBundleAsync_ActiveTurnAutoQueuesAfterInactivity()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(
            inputOptionsOverride: new TelegramInputOptions
            {
                DefaultCaptureMode = TelegramInputCaptureMode.BundleWhenActiveOrMedia,
                AutoDispatchAfterSeconds = 5,
            });
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path, CodexSessionStatus.Running));
        harness.TurnCoordinator.ActiveThreadIds.Add("thread-1");
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "queue after idle", SourceMessageId: 24),
            harness.Sender,
            CancellationToken.None);

        TelegramInputBundle bundle = Assert.Single(await harness.InputBundleStore.ListAsync(conversation, CancellationToken.None));
        await MarkBundleIdleAsync(harness, bundle, secondsAgo: 10);

        Assert.True(await harness.Handler.ProcessDueInputBundleAsync(harness.Sender, CancellationToken.None));

        TelegramQueuedPrompt prompt = Assert.Single(await harness.StateStore.ListQueuedPromptsAsync(1234, conversation, CancellationToken.None));
        Assert.Equal("queue after idle", prompt.Text);
        Assert.Empty(harness.SessionManager.SendRequests);
        Assert.Empty(harness.SessionManager.SteerRequests);
        TelegramInputBundle completed = Assert.Single(await harness.InputBundleStore.ListAsync(conversation, CancellationToken.None));
        Assert.Equal(TelegramInputBundleStatus.Queued, completed.Status);
    }

    [Fact]
    public async Task HandleCallbackAsync_WhenBundleSteerFails_KeepsBundleOpenForRetry()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(
            inputOptionsOverride: new TelegramInputOptions
            {
                DefaultCaptureMode = TelegramInputCaptureMode.BundleWhenActiveOrMedia,
            });
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path, CodexSessionStatus.Running));
        harness.TurnCoordinator.ActiveThreadIds.Add("thread-1");
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "retry this bundle", SourceMessageId: 25),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage card = Assert.Single(harness.Sender.Sent);
        string steerCallback = Assert.Single(card.Buttons!.SelectMany(row => row), button => button.Text == "Steer current turn").CallbackData;
        harness.SessionManager.SteerExceptions.Enqueue(new InvalidOperationException("steer failed"));

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("bundle-steer-fail", 1234, conversation.ChatId, "private", steerCallback, SourceMessageId: 1),
            harness.Sender,
            CancellationToken.None);

        Assert.Empty(harness.SessionManager.SteerRequests);
        Assert.Contains(harness.Sender.Edited, edited => edited.Text.Contains("could not be steered", StringComparison.Ordinal));
        steerCallback = harness.Sender.Edited[^1]
            .Buttons!
            .SelectMany(row => row)
            .Single(button => button.Text == "Steer current turn")
            .CallbackData;

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("bundle-steer-retry", 1234, conversation.ChatId, "private", steerCallback, SourceMessageId: 1),
            harness.Sender,
            CancellationToken.None);

        (string sessionId, object input) = Assert.Single(harness.SessionManager.SteerRequests);
        Assert.Equal("thread-1", sessionId);
        Assert.Contains(
            Assert.IsAssignableFrom<IReadOnlyList<CodexInputItem>>(input),
            item => item is CodexTextInput text && text.Text == "retry this bundle");
    }

    [Fact]
    public async Task HandleCallbackAsync_WhenBundleSteerAcceptanceIsSlow_ReportsPendingThenCompletes()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(
            inputOptionsOverride: new TelegramInputOptions
            {
                DefaultCaptureMode = TelegramInputCaptureMode.BundleWhenActiveOrMedia,
            },
            steerStartTimeout: TimeSpan.FromMilliseconds(10));
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path, CodexSessionStatus.Running));
        harness.TurnCoordinator.ActiveThreadIds.Add("thread-1");
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "slow steer bundle", SourceMessageId: 26),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage card = Assert.Single(harness.Sender.Sent);
        string steerCallback = Assert.Single(card.Buttons!.SelectMany(row => row), button => button.Text == "Steer current turn").CallbackData;
        harness.SessionManager.PendingSteer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("bundle-steer-slow", 1234, conversation.ChatId, "private", steerCallback, SourceMessageId: 1),
            harness.Sender,
            CancellationToken.None);

        Assert.Contains(harness.Sender.CallbackAnswers, answer => answer.Text == "Steering.");
        Assert.Contains(harness.Sender.Sent, sent => sent.Text.Contains("Steering the input bundle", StringComparison.Ordinal));
        TelegramInputBundle pending = Assert.Single(await harness.InputBundleStore.ListAsync(conversation, CancellationToken.None));
        Assert.Equal(TelegramInputBundleStatus.Submitted, pending.Status);

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("bundle-steer-slow-duplicate", 1234, conversation.ChatId, "private", steerCallback, SourceMessageId: 1),
            harness.Sender,
            CancellationToken.None);

        Assert.Single(harness.SessionManager.SteerRequests);
        Assert.Contains(harness.Sender.CallbackAnswers, answer => answer.Text == "Bundle is no longer active.");

        harness.SessionManager.PendingSteer.SetResult(true);
        await WaitUntilAsync(() => HasSentOrEditedText(harness, "Steered Demo session with the input bundle."));

        TelegramInputBundle completed = Assert.Single(await harness.InputBundleStore.ListAsync(conversation, CancellationToken.None));
        Assert.Equal(TelegramInputBundleStatus.Steered, completed.Status);
    }

    [Fact]
    public async Task HandleCallbackAsync_WhenSlowBundleSteerEventuallyFails_KeepsBundleOpenForRetry()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(
            inputOptionsOverride: new TelegramInputOptions
            {
                DefaultCaptureMode = TelegramInputCaptureMode.BundleWhenActiveOrMedia,
            },
            steerStartTimeout: TimeSpan.FromMilliseconds(10));
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path, CodexSessionStatus.Running));
        harness.TurnCoordinator.ActiveThreadIds.Add("thread-1");
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "slow failed steer bundle", SourceMessageId: 27),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage card = Assert.Single(harness.Sender.Sent);
        string steerCallback = Assert.Single(card.Buttons!.SelectMany(row => row), button => button.Text == "Steer current turn").CallbackData;
        harness.SessionManager.PendingSteer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("bundle-steer-slow-fail", 1234, conversation.ChatId, "private", steerCallback, SourceMessageId: 1),
            harness.Sender,
            CancellationToken.None);

        Assert.Contains(harness.Sender.Sent, sent => sent.Text.Contains("Steering the input bundle", StringComparison.Ordinal));

        harness.SessionManager.PendingSteer.SetException(new InvalidOperationException("steer transport hung"));
        await WaitUntilAsync(() => HasSentOrEditedText(harness, "could not be steered"));

        TelegramInputBundle stillOpen = Assert.Single(await harness.InputBundleStore.ListAsync(conversation, CancellationToken.None));
        Assert.Equal(TelegramInputBundleStatus.Capturing, stillOpen.Status);
        Assert.Contains(harness.Sender.Edited, edited => edited.Text.Contains("steer transport hung", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleCallbackAsync_WhenAttachmentSteeringDisabled_TextOnlySteerLeavesBundleOpen()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(
            inputOptionsOverride: new TelegramInputOptions
            {
                DefaultCaptureMode = TelegramInputCaptureMode.BundleWhenActiveOrMedia,
                AllowAttachmentSteering = false,
            });
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path, CodexSessionStatus.Running));
        harness.TurnCoordinator.ActiveThreadIds.Add("thread-1");
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);
        string imagePath = Path.Combine(harness.Temp.Path, "screen.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(
                1234,
                conversation.ChatId,
                "private",
                "voice transcript plus screenshot",
                Attachments:
                [
                    new TelegramAttachmentDescriptor(imagePath, "screen.png", "image/png", IsImage: true),
                ],
                SourceMessageId: 31),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage card = Assert.Single(harness.Sender.Sent);
        Assert.Equal(
            ["Queue next", "Text-only steer", "Add more", "Clear", "Cancel"],
            FlattenButtonLabels(card));
        string steerTextCallback = Assert.Single(card.Buttons!.SelectMany(row => row), button => button.Text == "Text-only steer").CallbackData;

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("bundle-steer-text", 1234, conversation.ChatId, "private", steerTextCallback, SourceMessageId: 1),
            harness.Sender,
            CancellationToken.None);

        (string sessionId, object input) = Assert.Single(harness.SessionManager.SteerRequests);
        Assert.Equal("thread-1", sessionId);
        IReadOnlyList<CodexInputItem> items = Assert.IsAssignableFrom<IReadOnlyList<CodexInputItem>>(input);
        Assert.Single(items);
        Assert.Equal("voice transcript plus screenshot", Assert.IsType<CodexTextInput>(items[0]).Text);
        TelegramInputBundle bundle = Assert.Single(await harness.InputBundleStore.ListAsync(conversation, CancellationToken.None));
        Assert.Equal(TelegramInputBundleStatus.Capturing, bundle.Status);
        Assert.NotEmpty(bundle.Attachments);
        Assert.Contains(harness.Sender.Edited, edited => edited.Text.Contains("Attachment steering is not supported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleCallbackAsync_WhenBundleCallbackRevisionIsStale_RefreshesWithoutSteering()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(
            inputOptionsOverride: new TelegramInputOptions
            {
                DefaultCaptureMode = TelegramInputCaptureMode.BundleWhenActiveOrMedia,
            });
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path, CodexSessionStatus.Running));
        harness.TurnCoordinator.ActiveThreadIds.Add("thread-1");
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "first note", SourceMessageId: 40),
            harness.Sender,
            CancellationToken.None);
        SentTelegramMessage originalCard = Assert.Single(harness.Sender.Sent);
        string staleSteerCallback = Assert.Single(originalCard.Buttons!.SelectMany(row => row), button => button.Text == "Steer current turn").CallbackData;

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "second note", SourceMessageId: 41),
            harness.Sender,
            CancellationToken.None);
        EditedTelegramMessage refreshedCard = Assert.Single(harness.Sender.Edited);

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("bundle-stale", 1234, conversation.ChatId, "private", staleSteerCallback, SourceMessageId: refreshedCard.MessageId),
            harness.Sender,
            CancellationToken.None);

        Assert.Empty(harness.SessionManager.SteerRequests);
        Assert.Contains(harness.Sender.CallbackAnswers, answer => answer.Text == "Card refreshed.");
        Assert.Contains(harness.Sender.Edited, edited => edited.Text.Contains("second note", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleMessageAsync_StatusWithoutSelectedSessionShowsMobileSessionActions()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/status"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("No active session", sent.Text);
        Assert.Equal(["Create session", "Sessions", "Projects", "Help"], FlattenButtonLabels(sent));
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
        Assert.Contains("/debug", sent.Text);
        Assert.Contains("/output", sent.Text);
        Assert.Contains("/turn", sent.Text);
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
    public async Task HandleMessageAsync_DebugCommandTogglesRuntimePreambleMode()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/debug on"),
            harness.Sender,
            CancellationToken.None);

        Assert.True(harness.DebugPreambleMode.RuntimeOverrideEnabled);
        Assert.Contains("Effective: on", Assert.Single(harness.Sender.Sent).Text);

        harness.Sender.Sent.Clear();
        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/debug reset"),
            harness.Sender,
            CancellationToken.None);

        Assert.Null(harness.DebugPreambleMode.RuntimeOverrideEnabled);
        Assert.Contains("reset to configuration", Assert.Single(harness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_DebugCaptureCommandsToggleMetadataAndFullModes()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/debug capture status"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(TelegramDebugCaptureMode.Off, harness.TraceStore.CaptureStatus.Mode);
        Assert.Contains("Mode: Off", Assert.Single(harness.Sender.Sent).Text);

        harness.Sender.Sent.Clear();
        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/debug capture on"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(TelegramDebugCaptureMode.Metadata, harness.TraceStore.CaptureStatus.Mode);
        Assert.Contains("Mode: Metadata", Assert.Single(harness.Sender.Sent).Text);

        harness.Sender.Sent.Clear();
        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/debug capture full on 30m"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(TelegramDebugCaptureMode.Full, harness.TraceStore.CaptureStatus.Mode);
        Assert.NotNull(harness.TraceStore.CaptureStatus.FullCaptureExpiresUtc);
        Assert.Contains("FULL CAPTURE IS ENABLED", Assert.Single(harness.Sender.Sent).Text);

        harness.Sender.Sent.Clear();
        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/debug capture full off"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(TelegramDebugCaptureMode.Metadata, harness.TraceStore.CaptureStatus.Mode);
        Assert.Contains("Full bodies: off", Assert.Single(harness.Sender.Sent).Text);

        harness.Sender.Sent.Clear();
        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/debug capture off"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(TelegramDebugCaptureMode.Off, harness.TraceStore.CaptureStatus.Mode);
        Assert.Contains("Mode: Off", Assert.Single(harness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_OutputModeCommandSetsRuntimeMode()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/output mode final"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(TelegramOutputPresentationMode.FinalOnly, harness.OutputModeState.CurrentMode);
        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Output mode set to FinalOnly.", sent.Text);
        Assert.Contains("Output mode: FinalOnly", sent.Text);
        Assert.Equal(["Verbose", "LiveCard", "FinalOnly", "Reset"], FlattenButtonLabels(sent));
    }

    [Fact]
    public async Task HandleCallbackAsync_OutputModeButtonSetsRuntimeMode()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-output", 1234, 5555, "private", "outmode:verbose"),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal(TelegramOutputPresentationMode.Verbose, harness.OutputModeState.CurrentMode);
        Assert.Contains("Output mode set to Verbose.", Assert.Single(harness.Sender.Sent).Text);
    }

    [Fact]
    public async Task HandleMessageAsync_TurnCommandShowsOperationalHistory()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        harness.EventLog.Record(new CodexSessionEventRecord(
            "thread-1",
            "turn-1",
            DateTimeOffset.UtcNow,
            "item.message",
            CodexSessionEventKind.Other,
            CodexSessionEventLane.Timeline,
            "Updated Telegram output projection",
            "Live card refreshed."));

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/turn updates thread-1 turn-1"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Turn updates", sent.Text);
        Assert.Contains("Demo session", sent.Text);
        Assert.Contains("Updated Telegram output projection: Live card refreshed.", sent.Text);
        Assert.Contains(FlattenButtonLabels(sent), label => label == "Show Full Turn");
        Assert.Contains(FlattenButtonLabels(sent), label => label == "Trace");
        Assert.All(sent.Buttons!.SelectMany(row => row), button => Assert.True(button.CallbackData.Length <= 64, button.CallbackData));
    }

    [Fact]
    public async Task HandleMessageAsync_TurnFinalShowsFinalResponseHistory()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create();
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        harness.EventLog.Record(new CodexSessionEventRecord(
            "thread-1",
            "turn-1",
            DateTimeOffset.UtcNow,
            "turn.finalResponse",
            CodexSessionEventKind.AssistantOutput,
            CodexSessionEventLane.Timeline,
            "Final response",
            "The final answer is durable."));

        await harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, 5555, "private", "/turn final thread-1 turn-1"),
            harness.Sender,
            CancellationToken.None);

        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Turn final response", sent.Text);
        Assert.Contains("Final response: The final answer is durable.", sent.Text);
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
    public async Task HandleMessageAsync_PlanCommandStartsPlanModeRequest()
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
            new TelegramInboundMessage(1234, 5555, "private", "/plan clarify the implementation approach"),
            harness.Sender,
            CancellationToken.None);

        CreateCodexSessionRequest request = Assert.Single(harness.SessionManager.CreateRequests);
        Assert.StartsWith("repo session ", request.Name, StringComparison.Ordinal);
        PlanSendRequest send = Assert.IsType<PlanSendRequest>(Assert.Single(harness.SessionManager.SendRequests));
        Assert.Equal("clarify the implementation approach", send.Input);
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
        Assert.Empty(harness.Sender.Sent);
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

        Assert.Empty(harness.Sender.Sent);
        Assert.Contains(conversation, harness.TypingIndicatorRegistry.GetTargets());

        harness.SessionManager.PendingSend.SetResult(new CodexThreadExecutionVm("thread-1", "turn-1", "running", null));
        await handleTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(harness.Sender.Sent);
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
    public async Task HandleMessageAsync_WhenSteerAcceptanceIsSlow_ReportsPendingThenCompletion()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(steerStartTimeout: TimeSpan.FromMilliseconds(10));
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Goal session", harness.Temp.Path));
        harness.SessionManager.PendingSteer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        Task handleTask = harness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/steer keep going"),
            harness.Sender,
            CancellationToken.None);

        await harness.SessionManager.SteerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await handleTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Contains(harness.Sender.Sent, sent => sent.Text.Contains("is taking longer than usual", StringComparison.Ordinal));
        Assert.Equal(("thread-1", (object)"keep going"), Assert.Single(harness.SessionManager.SteerRequests));

        harness.SessionManager.PendingSteer.SetResult(true);
        await WaitUntilAsync(() => HasSentOrEditedText(harness, "Steered Goal session."));
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
        Assert.Contains("Here's what I transcribed:", Assert.Single(harness.Sender.Sent).Text);
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
            sent => Assert.Contains("could not be resumed", sent.Text));
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

        using CommandHandlerHarness closeoutHarness = CommandHandlerHarness.Create();
        closeoutHarness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", closeoutHarness.Temp.Path) with
        {
            LastTurnCloseout = new CodexTurnCloseoutSummary(
                "turn-1",
                "completed",
                DateTimeOffset.UtcNow,
                AssistantTextSeen: true,
                FinalResponseSeen: false,
                Warning: true,
                "Codex streamed assistant text but ended the turn without a final response item."),
        });
        await closeoutHarness.StateStore.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);

        await closeoutHarness.Handler.HandleMessageAsync(
            new TelegramInboundMessage(1234, conversation.ChatId, "private", "/status"),
            closeoutHarness.Sender,
            CancellationToken.None);

        string closeoutStatus = Assert.Single(closeoutHarness.Sender.Sent).Text;
        Assert.Contains("Last turn: completed", closeoutStatus);
        Assert.Contains("warning", closeoutStatus);
        Assert.Contains("Closeout: Codex streamed assistant text", closeoutStatus);

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
    public async Task HandleCallbackAsync_WhenQueuedSteerAcceptanceIsSlow_ReportsPendingThenCompletes()
    {
        using CommandHandlerHarness harness = CommandHandlerHarness.Create(steerStartTimeout: TimeSpan.FromMilliseconds(10));
        TelegramConversationScope conversation = new(5555, null);
        harness.SessionManager.Sessions.Add(CreateSession("thread-1", "Demo session", harness.Temp.Path));
        harness.SessionManager.PendingSteer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.StateStore.EnqueueQueuedPromptAsync(
            CreateQueuedPrompt("aaaaaaaa11111111", 1234, conversation, "queued steering"),
            CancellationToken.None);

        await harness.Handler.HandleCallbackAsync(
            new TelegramInboundCallback("callback-queue-slow", 1234, conversation.ChatId, "private", "qnow:aaaaaaaa11111111", SourceMessageId: 42),
            harness.Sender,
            CancellationToken.None);

        Assert.Equal("Sending queued item.", Assert.Single(harness.Sender.CallbackAnswers).Text);
        Assert.Contains(harness.Sender.Edited, edited => edited.Text.Contains("is taking longer than usual", StringComparison.Ordinal));
        Assert.Empty(await harness.StateStore.ListQueuedPromptsAsync(1234, conversation, CancellationToken.None));

        harness.SessionManager.PendingSteer.SetResult(true);
        await WaitUntilAsync(() => HasSentOrEditedText(harness, "Sent queued item aaaaaaaa"));
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

    private static async Task MarkBundleIdleAsync(
        CommandHandlerHarness harness,
        TelegramInputBundle bundle,
        int secondsAgo)
    {
        TelegramInputBundle? updated = await harness.InputBundleStore.TryUpdateBundleAsync(
            bundle.Id,
            bundle.UserId,
            current => current with
            {
                UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(-secondsAgo),
            },
            CancellationToken.None);
        Assert.NotNull(updated);
    }

    private static IReadOnlyList<string> FlattenButtonLabels(SentTelegramMessage message)
        => message.Buttons?.SelectMany(row => row.Select(button => button.Text)).ToArray() ?? [];

    private static IReadOnlyList<string> FlattenButtonLabels(EditedTelegramMessage message)
        => message.Buttons?.SelectMany(row => row.Select(button => button.Text)).ToArray() ?? [];

    private static bool HasSentOrEditedText(CommandHandlerHarness harness, string expected)
        => harness.Sender.Sent.Any(sent => sent.Text.Contains(expected, StringComparison.Ordinal))
            || harness.Sender.Edited.Any(edited => edited.Text.Contains(expected, StringComparison.Ordinal));

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
            FakeTurnExecutionCoordinator turnCoordinator,
            TelegramInputBundleStore inputBundleStore,
            TelegramTypingIndicatorRegistry typingIndicatorRegistry,
            TelegramTurnReactionRegistry turnReactionRegistry,
            TestTelegramDebugPreambleMode debugPreambleMode,
            TestTelegramOutputModeState outputModeState,
            TelegramDebugTraceStore traceStore,
            CodexSessionEventLog eventLog,
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
            TurnCoordinator = turnCoordinator;
            InputBundleStore = inputBundleStore;
            TypingIndicatorRegistry = typingIndicatorRegistry;
            TurnReactionRegistry = turnReactionRegistry;
            DebugPreambleMode = debugPreambleMode;
            OutputModeState = outputModeState;
            TraceStore = traceStore;
            EventLog = eventLog;
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

        public FakeTurnExecutionCoordinator TurnCoordinator { get; }

        public TelegramInputBundleStore InputBundleStore { get; }

        public TelegramTypingIndicatorRegistry TypingIndicatorRegistry { get; }

        public TelegramTurnReactionRegistry TurnReactionRegistry { get; }

        public TestTelegramDebugPreambleMode DebugPreambleMode { get; }

        public TestTelegramOutputModeState OutputModeState { get; }

        public TelegramDebugTraceStore TraceStore { get; }

        public CodexSessionEventLog EventLog { get; }

        public FakeTelegramForumTopicService TopicService { get; }

        public FakeAudioTranscriptionService AudioTranscription { get; }

        public TestTelegramBotMessageSender Sender { get; }

        public TelegramCodexBotCommandHandler Handler { get; }

        public static CommandHandlerHarness Create(
            TelegramBotOptions? botOptions = null,
            TelegramInputOptions? inputOptionsOverride = null,
            TimeSpan? steerStartTimeout = null)
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
            TelegramTurnReactionRegistry turnReactionRegistry = new();
            TestTelegramDebugPreambleMode debugPreambleMode = new();
            TestTelegramOutputModeState outputModeState = new(TelegramOutputPresentationMode.LiveCard);
            FakeTelegramForumTopicService topicService = new();
            FakeAudioTranscriptionService audioTranscription = new();
            TestTelegramBotMessageSender sender = new();
            IOptions<TelegramInputOptions> inputOptions = Microsoft.Extensions.Options.Options.Create(inputOptionsOverride ?? new TelegramInputOptions
            {
                DefaultCaptureMode = TelegramInputCaptureMode.ImmediateText,
            });
            TelegramInputBundleStore inputBundleStore = new(codexOptions, inputOptions, TimeProvider.System);
            TelegramInputBundleCardRenderer inputBundleCardRenderer = new(inputOptions);
            TelegramDebugTraceStore traceStore = new(
                codexOptions,
                Microsoft.Extensions.Options.Options.Create(new TelegramDebugTraceOptions()));
            CodexSessionEventLog eventLog = new(Microsoft.Extensions.Options.Options.Create(new TelegramOutputOptions()));
            TelegramAttachmentStore attachmentStore = new(codexOptions);
            FakeTurnExecutionCoordinator turnCoordinator = new();
            TelegramCodexBotCommandHandler handler = new(
                new TelegramCommandParser(),
                new TelegramMessageChunker(),
                sessionManager,
                accountUsage,
                projectCatalog,
                new CodexWorkspaceBrowser(codexOptions),
                stateStore,
                turnCoordinator,
                new TelegramThreadFollowRegistry(),
                typingIndicatorRegistry,
                turnReactionRegistry,
                new FakeTelegramPlanInputCoordinator(),
                debugPreambleMode,
                outputModeState,
                topicService,
                audioTranscription,
                outboundQueue,
                attachmentStore,
                inputBundleStore,
                inputBundleCardRenderer,
                traceStore,
                eventLog,
                Microsoft.Extensions.Options.Options.Create(botOptions ?? new TelegramBotOptions
                {
                    AllowedUserIds = [1234],
                }),
                inputOptions,
                NullLogger<TelegramCodexBotCommandHandler>.Instance,
                steerStartTimeout);

            return new CommandHandlerHarness(temp, sessionManager, accountUsage, projectCatalog, stateStore, outboundQueue, turnCoordinator, inputBundleStore, typingIndicatorRegistry, turnReactionRegistry, debugPreambleMode, outputModeState, traceStore, eventLog, topicService, audioTranscription, sender, handler);
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

        public TaskCompletionSource<bool>? PendingSteer { get; set; }

        public TaskCompletionSource<bool> SteerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        public Task<CodexThreadExecutionVm> SendPlanAsync(string sessionId, string input, CancellationToken cancellationToken)
        {
            ThrowNextSendExceptionIfPresent();
            SendSessionIds.Add(sessionId);
            SendRequests.Add(new PlanSendRequest(input));
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
            SteerStarted.TrySetResult(true);
            return PendingSteer?.Task ?? Task.CompletedTask;
        }

        public Task SteerAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
        {
            ThrowNextSteerExceptionIfPresent();
            SteerRequests.Add((sessionId, input));
            SteerStarted.TrySetResult(true);
            return PendingSteer?.Task ?? Task.CompletedTask;
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

    private sealed record PlanSendRequest(string Input);

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
        public HashSet<string> ActiveThreadIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasActiveTurn => ActiveThreadIds.Count > 0;

        public IReadOnlyCollection<string> GetActiveThreadIds() => ActiveThreadIds.ToArray();

        public bool HasActiveTurnForThread(string threadId) => ActiveThreadIds.Contains(threadId);

        public string? GetActiveTurnId(string threadId) => HasActiveTurnForThread(threadId) ? "turn-1" : null;

        public CodexActiveTurnStateVm? TryGetActiveTurnState(string threadId)
            => HasActiveTurnForThread(threadId)
                ? new CodexActiveTurnStateVm(threadId, "turn-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)
                : null;

        public void RegisterActiveTurn(string threadId, string turnId, ICodexTurnHandle? turn = null, CodexTimelineEntryVm? lastEvent = null)
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

    private sealed class FakeTelegramPlanInputCoordinator : ITelegramPlanInputCoordinator
    {
        public bool HandleNextPlainText { get; set; }

        public List<string> Answers { get; } = [];

        public System.Text.Json.Nodes.JsonObject? HandleApprovalRequest(string action, System.Text.Json.Nodes.JsonObject? request)
            => null;

        public Task<bool> TryAnswerPendingAsync(
            TelegramConversationScope conversation,
            string text,
            CancellationToken cancellationToken)
        {
            if (!HandleNextPlainText)
            {
                return Task.FromResult(false);
            }

            Answers.Add(text);
            HandleNextPlainText = false;
            return Task.FromResult(true);
        }

        public Task<bool> TryAnswerCallbackAsync(
            string token,
            TelegramConversationScope conversation,
            string callbackQueryId,
            CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    private sealed class TestTelegramDebugPreambleMode : ITelegramDebugPreambleMode
    {
        public bool ConfiguredDefaultEnabled { get; set; }

        public bool? RuntimeOverrideEnabled { get; private set; }

        public bool IsEnabled => RuntimeOverrideEnabled ?? ConfiguredDefaultEnabled;

        public void SetRuntimeOverride(bool enabled)
            => RuntimeOverrideEnabled = enabled;

        public void ClearRuntimeOverride()
            => RuntimeOverrideEnabled = null;
    }

    private sealed class TestTelegramOutputModeState : ITelegramOutputModeState
    {
        public TestTelegramOutputModeState(TelegramOutputPresentationMode mode)
        {
            CurrentMode = mode;
        }

        public TelegramOutputPresentationMode CurrentMode { get; private set; }

        public bool HasRuntimeOverride { get; private set; }

        public void SetRuntimeMode(TelegramOutputPresentationMode mode)
        {
            CurrentMode = mode;
            HasRuntimeOverride = true;
        }

        public void ClearRuntimeMode()
        {
            HasRuntimeOverride = false;
            CurrentMode = TelegramOutputPresentationMode.LiveCard;
        }
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

        public Queue<Exception> EditFailures { get; } = new();

        public List<CallbackAnswer> CallbackAnswers { get; } = [];

        public List<TelegramMessageReaction> Reactions { get; } = [];

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

        public Task<int?> SendTextMessageAndGetIdAsync(
            TelegramConversationScope conversation,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
        {
            Sent.Add(new SentTelegramMessage(conversation, text, buttons));
            return Task.FromResult<int?>(Sent.Count);
        }

        public Task EditTextMessageAsync(
            TelegramConversationScope conversation,
            int messageId,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
        {
            Edited.Add(new EditedTelegramMessage(conversation, messageId, text, buttons));
            if (EditFailures.TryDequeue(out Exception? exception))
            {
                throw exception;
            }

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

        public Task ReactToMessageAsync(TelegramMessageReaction reaction, CancellationToken cancellationToken)
        {
            Reactions.Add(reaction);
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
