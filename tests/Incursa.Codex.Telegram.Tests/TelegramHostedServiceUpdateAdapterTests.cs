using System.Text.Json;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramHostedServiceUpdateAdapterTests
{
    [Fact]
    public async Task HandleUpdateAsync_DownloadsPhotoDocumentAndCaptionBeforeForwarding()
    {
        using Harness harness = Harness.Create();
        Message telegramMessage = CreateMessage(caption: "inspect this 🚀");
        telegramMessage.Photo =
        [
            new PhotoSize { FileId = "photo-small", FileUniqueId = "photo-small-u", Width = 32, Height = 32 },
            new PhotoSize { FileId = "photo-large", FileUniqueId = "photo-large-u", Width = 2048, Height = 1024 },
        ];
        telegramMessage.Document = new Document
        {
            FileId = "doc-file",
            FileUniqueId = "doc-file-u",
            FileName = "notes.pdf",
            MimeType = "application/pdf",
        };
        Update update = new()
        {
            Id = 10,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.Equal(1234, message.UserId);
        Assert.Equal(5555, message.ChatId);
        Assert.Equal("Private", message.ChatType);
        Assert.Equal("inspect this 🚀", message.Text);
        Assert.Collection(
            message.Attachments ?? [],
            attachment =>
            {
                Assert.Equal("telegram-photo.jpg", attachment.FileName);
                Assert.Equal("image/jpeg", attachment.ContentType);
                Assert.True(attachment.IsImage);
                Assert.EndsWith(".jpg", attachment.FilePath, StringComparison.OrdinalIgnoreCase);
            },
            attachment =>
            {
                Assert.Equal("notes.pdf", attachment.FileName);
                Assert.Equal("application/pdf", attachment.ContentType);
                Assert.False(attachment.IsImage);
                Assert.EndsWith(".pdf", attachment.FilePath, StringComparison.OrdinalIgnoreCase);
            });
        Assert.Equal(["photo-large", "doc-file"], harness.FileClient.DownloadedFileIds);
    }

    [Fact]
    public async Task HandleUpdateAsync_PrefersTextOverCaptionWhenTelegramProvidesBoth()
    {
        using Harness harness = Harness.Create();
        Update update = new()
        {
            Id = 16,
            Message = CreateMessage(text: "use the message text", caption: "ignore caption"),
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.Equal("use the message text", message.Text);
    }

    [Fact]
    public async Task HandleUpdateAsync_FullCaptureRecordsInboundTelegramMessageBody()
    {
        using Harness harness = Harness.Create();
        harness.TraceStore.EnableFullCapture(TimeSpan.FromMinutes(30));
        Update update = new()
        {
            Id = 116,
            Message = CreateMessage(text: "capture this input", messageThreadId: 77, messageId: 42),
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.False(string.IsNullOrWhiteSpace(message.TraceId));
        string file = Assert.Single(Directory.GetFiles(Path.Combine(harness.Temp.Path, "telegram-traces"), "*.jsonl", SearchOption.AllDirectories));
        string line = Assert.Single(await File.ReadAllLinesAsync(file, CancellationToken.None));
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        Assert.Equal("telegram.inbound.message", root.GetProperty("kind").GetString());
        Assert.Equal("TelegramInbound", root.GetProperty("source").GetString());
        Assert.Equal("capture this input", root.GetProperty("textBody").GetString());
        Assert.Equal("116", root.GetProperty("metadata").GetProperty("updateId").GetString());
    }

    [Fact]
    public async Task HandleUpdateAsync_AcknowledgesAuthorizedMessage()
    {
        using Harness harness = Harness.Create();
        Update update = new()
        {
            Id = 17,
            Message = CreateMessage(text: "run this", messageThreadId: 77, messageId: 42),
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramMessageAcknowledgement acknowledgement = Assert.Single(harness.Sender.Acknowledgements);
        Assert.Equal(new TelegramConversationScope(5555, 77), acknowledgement.Conversation);
        Assert.Equal(42, acknowledgement.MessageId);
    }

    [Fact]
    public async Task HandleUpdateAsync_AttachesReplyContextFromStoredTelegramMessages()
    {
        using Harness harness = Harness.Create();
        TelegramConversationScope conversation = new(5555, null);
        await harness.MessageContextStore.RecordAsync(new TelegramMessageContextRecord(conversation, 10, TelegramMessageAuthor.Bot, "First progress update.", DateTimeOffset.Parse("2026-05-10T12:00:00Z")), CancellationToken.None);
        await harness.MessageContextStore.RecordAsync(new TelegramMessageContextRecord(conversation, 11, TelegramMessageAuthor.Bot, "Second progress update.", DateTimeOffset.Parse("2026-05-10T12:01:00Z")), CancellationToken.None);
        await harness.MessageContextStore.RecordAsync(new TelegramMessageContextRecord(conversation, 12, TelegramMessageAuthor.Bot, "I am going to delete the stale file.", DateTimeOffset.Parse("2026-05-10T12:02:00Z")), CancellationToken.None);
        Message message = CreateMessage(text: "do not delete that", messageId: 13);
        message.ReplyToMessage = CreateMessage(text: "fallback reply text", messageId: 12);
        message.ReplyToMessage.From = new User
        {
            Id = 999,
            IsBot = true,
            FirstName = "Codex",
        };
        Update update = new()
        {
            Id = 18,
            Message = message,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramReplyContext? context = Assert.Single(harness.Handler.Messages).ReplyContext;
        Assert.NotNull(context);
        Assert.Equal(12, context.MessageId);
        Assert.Equal(TelegramMessageAuthor.Bot, context.Author);
        Assert.Equal("I am going to delete the stale file.", context.Text);
        Assert.Collection(
            context.PriorMessages,
            prior => Assert.Equal("First progress update.", prior.Text),
            prior => Assert.Equal("Second progress update.", prior.Text));
    }

    [Fact]
    public async Task HandleUpdateAsync_OmitsOperationalBotReplyContextFromStoredTelegramMessages()
    {
        using Harness harness = Harness.Create();
        TelegramConversationScope conversation = new(5555, null);
        string operationalText = """
            Codex failed
            Session: 019e5d81
            Mode: LiveCard
            Updates: 0 captured
            Progress: 0 suppressed
            Artifacts: 0
            Final response: captured
            Telegram delivery: draining (0 messages, 1 chunks)
            Latest: Codex turn was interrupted.
            """;
        await harness.MessageContextStore.RecordAsync(new TelegramMessageContextRecord(conversation, 12, TelegramMessageAuthor.Bot, operationalText, DateTimeOffset.Parse("2026-05-10T12:02:00Z")), CancellationToken.None);

        Message message = CreateMessage(text: "do not delete that", messageId: 13);
        message.ReplyToMessage = CreateMessage(text: operationalText, messageId: 12);
        message.ReplyToMessage.From = new User
        {
            Id = 999,
            IsBot = true,
            FirstName = "Codex",
        };
        Update update = new()
        {
            Id = 19,
            Message = message,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage inbound = Assert.Single(harness.Handler.Messages);
        Assert.Null(inbound.ReplyContext);
        Assert.True(inbound.ReplyContextWasOperationalBotCard);
    }

    [Fact]
    public async Task HandleUpdateAsync_IgnoresEmptyAuthorizedMessageWithoutAttachments()
    {
        using Harness harness = Harness.Create();
        Update update = new()
        {
            Id = 17,
            Message = CreateMessage(),
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        Assert.Empty(harness.Handler.Messages);
        Assert.Empty(harness.Sender.Sent);
        Assert.Empty(harness.FileClient.DownloadedFileIds);
    }

    [Fact]
    public async Task HandleUpdateAsync_IgnoresUnauthorizedTextMessage()
    {
        using Harness harness = Harness.Create();
        Update update = new()
        {
            Id = 18,
            Message = CreateMessage(text: "run this", userId: 9999),
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        Assert.Empty(harness.Handler.Messages);
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task HandleUpdateAsync_AllowsUnauthorizedWhoAmICommandForDiagnostics()
    {
        using Harness harness = Harness.Create();
        Update update = new()
        {
            Id = 19,
            Message = CreateMessage(text: " /whoami@codex_bot details ", userId: 9999),
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.Equal(9999, message.UserId);
        Assert.Equal(" /whoami@codex_bot details ", message.Text);
    }

    [Theory]
    [InlineData(ChatType.Group)]
    [InlineData(ChatType.Supergroup)]
    public async Task HandleUpdateAsync_AllowsUnauthorizedGroupWhoAmICommandForDiagnostics(ChatType chatType)
    {
        using Harness harness = Harness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [],
        });
        Update update = new()
        {
            Id = 35,
            Message = CreateMessage(
                text: "/whoami@codex_bot",
                userId: 9999,
                chatId: -1005555,
                chatType: chatType),
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.Equal(9999, message.UserId);
        Assert.Equal(-1005555, message.ChatId);
        Assert.Equal(chatType.ToString(), message.ChatType);
        Assert.Equal("/whoami@codex_bot", message.Text);
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task HandleUpdateAsync_ForwardsTrustCommandFromAllowedUserInUntrustedGroup()
    {
        using Harness harness = Harness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [],
        });
        Update update = new()
        {
            Id = 33,
            Message = CreateMessage(text: "/trust", chatId: -1005555, chatType: ChatType.Supergroup),
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.Equal("/trust", message.Text);
        Assert.Equal(-1005555, message.ChatId);
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task HandleUpdateAsync_ExplainsUntrustedGroupCommandFromAllowedUser()
    {
        using Harness harness = Harness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            AllowedChatIds = [],
        });
        Update update = new()
        {
            Id = 34,
            Message = CreateMessage(text: "/sessions", chatId: -1005555, chatType: ChatType.Supergroup),
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        Assert.Empty(harness.Handler.Messages);
        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("not trusted", sent.Text);
        Assert.Contains("/trust", sent.Text);
    }

    [Fact]
    public async Task HandleUpdateAsync_IgnoresUnauthorizedAudioWithoutDownloading()
    {
        using Harness harness = Harness.Create();
        Message telegramMessage = CreateMessage(userId: 9999);
        telegramMessage.Voice = new Voice
        {
            FileId = "voice-file",
            FileUniqueId = "voice-file-u",
            Duration = 2,
            MimeType = "audio/ogg",
        };
        Update update = new()
        {
            Id = 20,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        Assert.Empty(harness.Handler.Messages);
        Assert.Empty(harness.FileClient.DownloadedFileIds);
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task HandleUpdateAsync_DownloadsGroupRootAudioForTrustedChat()
    {
        using Harness harness = Harness.Create();
        Message telegramMessage = CreateMessage(chatId: -1005555, chatType: ChatType.Supergroup);
        telegramMessage.Voice = new Voice
        {
            FileId = "voice-file",
            FileUniqueId = "voice-file-u",
            Duration = 2,
            MimeType = "audio/ogg",
        };
        Update update = new()
        {
            Id = 28,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.Equal(-1005555, message.ChatId);
        Assert.Null(message.MessageThreadId);
        Assert.Null(message.Text);
        Assert.NotNull(message.AudioFilePath);
        Assert.Equal(["voice-file"], harness.FileClient.RequestedFileIds);
        Assert.Equal(["voice-file"], harness.FileClient.DownloadedFileIds);
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task HandleUpdateAsync_DownloadsGroupRootAttachmentForTrustedChat()
    {
        using Harness harness = Harness.Create();
        Message telegramMessage = CreateMessage(caption: "read this", chatId: -1005555, chatType: ChatType.Supergroup);
        telegramMessage.Document = new Document
        {
            FileId = "doc-file",
            FileUniqueId = "doc-file-u",
            FileName = "notes.pdf",
            MimeType = "application/pdf",
        };
        Update update = new()
        {
            Id = 29,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.Equal("read this", message.Text);
        TelegramAttachmentDescriptor attachment = Assert.Single(message.Attachments ?? []);
        Assert.Equal("notes.pdf", attachment.FileName);
        Assert.Equal(["doc-file"], harness.FileClient.RequestedFileIds);
        Assert.Equal(["doc-file"], harness.FileClient.DownloadedFileIds);
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task HandleUpdateAsync_ForwardsGroupRootCommandWithoutDownloadingAttachment()
    {
        using Harness harness = Harness.Create();
        Message telegramMessage = CreateMessage(caption: "/projects", chatId: -1005555, chatType: ChatType.Supergroup);
        telegramMessage.Document = new Document
        {
            FileId = "doc-file",
            FileUniqueId = "doc-file-u",
            FileName = "notes.pdf",
            MimeType = "application/pdf",
        };
        Update update = new()
        {
            Id = 30,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.Equal("/projects", message.Text);
        Assert.Null(message.Attachments);
        Assert.Empty(harness.FileClient.RequestedFileIds);
        Assert.Empty(harness.FileClient.DownloadedFileIds);
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task HandleUpdateAsync_ForwardsOddGroupRootSlashCommandWithoutDownloadingAttachment()
    {
        using Harness harness = Harness.Create();
        Message telegramMessage = CreateMessage(caption: "/@codex_bot diagnostics", chatId: -1005555, chatType: ChatType.Supergroup);
        telegramMessage.Document = new Document
        {
            FileId = "doc-file",
            FileUniqueId = "doc-file-u",
            FileName = "notes.pdf",
            MimeType = "application/pdf",
        };
        Update update = new()
        {
            Id = 32,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.Equal("/@codex_bot diagnostics", message.Text);
        Assert.Null(message.Attachments);
        Assert.Empty(harness.FileClient.RequestedFileIds);
        Assert.Empty(harness.FileClient.DownloadedFileIds);
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task HandleUpdateAsync_DownloadsGroupRootSendCommandAttachment()
    {
        using Harness harness = Harness.Create();
        Message telegramMessage = CreateMessage(caption: "/send inspect this", chatId: -1005555, chatType: ChatType.Supergroup);
        telegramMessage.Document = new Document
        {
            FileId = "doc-file",
            FileUniqueId = "doc-file-u",
            FileName = "notes.pdf",
            MimeType = "application/pdf",
        };
        Update update = new()
        {
            Id = 31,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.Equal("/send inspect this", message.Text);
        Assert.Single(message.Attachments ?? []);
        Assert.Equal(["doc-file"], harness.FileClient.RequestedFileIds);
        Assert.Equal(["doc-file"], harness.FileClient.DownloadedFileIds);
    }

    [Fact]
    public async Task HandleUpdateAsync_CollectsMediaGroupIntoSingleInboundMessage()
    {
        using Harness harness = Harness.Create(
            null,
            new TelegramInputOptions
            {
                MediaGroupDebounceMilliseconds = TelegramInputLimits.MinMediaGroupDebounceMilliseconds,
            });
        Message first = CreateMessage(caption: "inspect the album", messageId: 41, mediaGroupId: "album-1");
        first.Photo =
        [
            new PhotoSize { FileId = "photo-small", FileUniqueId = "photo-small-u", Width = 32, Height = 32 },
            new PhotoSize { FileId = "photo-large", FileUniqueId = "photo-large-u", Width = 2048, Height = 1024 },
        ];
        Message second = CreateMessage(messageId: 42, mediaGroupId: "album-1");
        second.Document = new Document
        {
            FileId = "doc-file",
            FileUniqueId = "doc-file-u",
            FileName = "notes.pdf",
            MimeType = "application/pdf",
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, new Update { Id = 40, Message = first }, harness.Sender, CancellationToken.None);
        Assert.Empty(harness.Handler.Messages);

        await harness.Service.HandleUpdateAsync(harness.FileClient, new Update { Id = 41, Message = second }, harness.Sender, CancellationToken.None);
        await WaitUntilAsync(() => harness.Handler.Messages.Count == 1);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.Equal("inspect the album", message.Text);
        Assert.Equal(41, message.SourceMessageId);
        Assert.Equal([41, 42], message.SourceMessageIds);
        Assert.Collection(
            message.Attachments ?? [],
            attachment =>
            {
                Assert.Equal("telegram-photo.jpg", attachment.FileName);
                Assert.True(attachment.IsImage);
            },
            attachment =>
            {
                Assert.Equal("notes.pdf", attachment.FileName);
                Assert.False(attachment.IsImage);
            });
        Assert.Equal(["photo-large", "doc-file"], harness.FileClient.DownloadedFileIds);
        Assert.Equal([41, 42], harness.Sender.Acknowledgements.Select(item => item.MessageId).ToArray());
    }

    [Fact]
    public async Task HandleUpdateAsync_MapsImageDocumentFallbackNameAndImageFlag()
    {
        using Harness harness = Harness.Create();
        Message telegramMessage = CreateMessage(caption: "inspect image document");
        telegramMessage.Document = new Document
        {
            FileId = "image-doc",
            FileUniqueId = "image-doc-u",
            FileName = null,
            MimeType = "image/png",
        };
        Update update = new()
        {
            Id = 21,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramAttachmentDescriptor attachment = Assert.Single(Assert.Single(harness.Handler.Messages).Attachments ?? []);
        Assert.Equal("telegram-document.png", attachment.FileName);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.True(attachment.IsImage);
        Assert.EndsWith(".png", attachment.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleUpdateAsync_MapsStaticStickerAndVideoNoteAttachments()
    {
        using Harness harness = Harness.Create();
        Message telegramMessage = CreateMessage(caption: "compare these", chatId: -1005555, chatType: ChatType.Supergroup, messageThreadId: 77);
        telegramMessage.Sticker = new Sticker
        {
            FileId = "sticker-file",
            FileUniqueId = "sticker-file-u",
            Width = 512,
            Height = 512,
            IsAnimated = false,
            IsVideo = false,
        };
        telegramMessage.VideoNote = new VideoNote
        {
            FileId = "video-note-file",
            FileUniqueId = "video-note-file-u",
            Duration = 3,
            Length = 64,
        };
        Update update = new()
        {
            Id = 11,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.Equal(-1005555, message.ChatId);
        Assert.Equal(77, message.MessageThreadId);
        Assert.Collection(
            message.Attachments ?? [],
            attachment =>
            {
                Assert.Equal("telegram-sticker.webp", attachment.FileName);
                Assert.Equal("image/webp", attachment.ContentType);
                Assert.True(attachment.IsImage);
            },
            attachment =>
            {
                Assert.Equal("telegram-video-note.mp4", attachment.FileName);
                Assert.Equal("video/mp4", attachment.ContentType);
                Assert.False(attachment.IsImage);
            });
        Assert.Equal(["sticker-file", "video-note-file"], harness.FileClient.DownloadedFileIds);
    }

    [Fact]
    public async Task HandleUpdateAsync_MapsAnimatedStickerAsBinaryAttachment()
    {
        using Harness harness = Harness.Create();
        Message telegramMessage = CreateMessage(caption: "animated sticker");
        telegramMessage.Sticker = new Sticker
        {
            FileId = "animated-sticker-file",
            FileUniqueId = "animated-sticker-file-u",
            Width = 512,
            Height = 512,
            IsAnimated = true,
            IsVideo = false,
        };
        Update update = new()
        {
            Id = 22,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramAttachmentDescriptor attachment = Assert.Single(Assert.Single(harness.Handler.Messages).Attachments ?? []);
        Assert.Equal("telegram-sticker.bin", attachment.FileName);
        Assert.Null(attachment.ContentType);
        Assert.False(attachment.IsImage);
    }

    [Fact]
    public async Task HandleUpdateAsync_ForwardsCallbackDataWithSourceMessage()
    {
        using Harness harness = Harness.Create();
        Update update = new()
        {
            Id = 12,
            CallbackQuery = new CallbackQuery
            {
                Id = "callback-1",
                From = CreateUser(1234),
                Data = "model:thread-1",
                Message = CreateMessage(chatId: -1005555, chatType: ChatType.Supergroup, messageThreadId: 77, messageId: 42),
            },
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundCallback callback = Assert.Single(harness.Handler.Callbacks);
        Assert.Equal("callback-1", callback.Id);
        Assert.Equal(1234, callback.UserId);
        Assert.Equal(-1005555, callback.ChatId);
        Assert.Equal("Supergroup", callback.ChatType);
        Assert.Equal("model:thread-1", callback.Data);
        Assert.Equal(77, callback.MessageThreadId);
        Assert.Equal(42, callback.SourceMessageId);
    }

    [Fact]
    public async Task HandleUpdateAsync_IgnoresUnsupportedUpdates()
    {
        using Harness harness = Harness.Create();

        await harness.Service.HandleUpdateAsync(harness.FileClient, new Update { Id = 13 }, harness.Sender, CancellationToken.None);

        Assert.Empty(harness.Handler.Messages);
        Assert.Empty(harness.Handler.Callbacks);
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task HandleUpdateAsync_IgnoresCallbackWithoutDataOrMessage()
    {
        using Harness harness = Harness.Create();
        Update missingData = new()
        {
            Id = 23,
            CallbackQuery = new CallbackQuery
            {
                Id = "callback-missing-data",
                From = CreateUser(1234),
                Data = null,
                Message = CreateMessage(),
            },
        };
        Update missingMessage = new()
        {
            Id = 24,
            CallbackQuery = new CallbackQuery
            {
                Id = "callback-missing-message",
                From = CreateUser(1234),
                Data = "status:1",
                Message = null,
            },
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, missingData, harness.Sender, CancellationToken.None);
        await harness.Service.HandleUpdateAsync(harness.FileClient, missingMessage, harness.Sender, CancellationToken.None);

        Assert.Empty(harness.Handler.Callbacks);
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task HandleUpdateAsync_NotifiesChatWhenAttachmentDownloadFails()
    {
        using Harness harness = Harness.Create();
        harness.FileClient.ThrowOnGetFileId = "doc-file";
        Message telegramMessage = CreateMessage(caption: "read this", chatId: -1005555, chatType: ChatType.Supergroup, messageThreadId: 77);
        telegramMessage.Document = new Document
        {
            FileId = "doc-file",
            FileUniqueId = "doc-file-u",
            FileName = "broken.pdf",
            MimeType = "application/pdf",
        };
        Update update = new()
        {
            Id = 14,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        Assert.Empty(harness.Handler.Messages);
        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Equal(new TelegramConversationScope(-1005555, 77), sent.Conversation);
        Assert.Contains("Attachment could not be processed", sent.Text);
        Assert.Contains("doc-file", sent.Text);
    }

    [Fact]
    public async Task HandleUpdateAsync_SwallowsNotificationFailureWhenAudioDownloadFails()
    {
        using Harness harness = Harness.Create();
        harness.FileClient.ThrowOnGetFileId = "voice-file";
        harness.Sender.SendException = new InvalidOperationException("chat unavailable");
        Message telegramMessage = CreateMessage();
        telegramMessage.Voice = new Voice
        {
            FileId = "voice-file",
            FileUniqueId = "voice-file-u",
            Duration = 2,
            MimeType = "audio/ogg",
        };
        Update update = new()
        {
            Id = 25,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        Assert.Empty(harness.Handler.Messages);
        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("Audio message could not be processed", sent.Text);
        Assert.Contains("voice-file", sent.Text);
    }

    [Fact]
    public async Task HandleUpdateAsync_RejectsTooShortAudioWithoutDownloading()
    {
        using Harness harness = Harness.Create();
        Message telegramMessage = CreateMessage();
        telegramMessage.Voice = new Voice
        {
            FileId = "voice-file",
            FileUniqueId = "voice-file-u",
            Duration = 0,
            MimeType = "audio/ogg",
        };
        Update update = new()
        {
            Id = 26,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        Assert.Empty(harness.Handler.Messages);
        Assert.Empty(harness.FileClient.DownloadedFileIds);
        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("too short", sent.Text);
        Assert.Contains("1 second", sent.Text);
    }

    [Fact]
    public async Task HandleUpdateAsync_RejectsTooLongAudioWithoutDownloading()
    {
        using Harness harness = Harness.Create(new TelegramBotOptions
        {
            AllowedUserIds = [1234],
            MaxAudioDurationSeconds = 5,
        });
        Message telegramMessage = CreateMessage();
        telegramMessage.Audio = new Audio
        {
            FileId = "voice-file",
            FileUniqueId = "voice-file-u",
            Duration = 6,
            MimeType = "audio/ogg",
        };
        Update update = new()
        {
            Id = 27,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        Assert.Empty(harness.Handler.Messages);
        Assert.Empty(harness.FileClient.DownloadedFileIds);
        SentTelegramMessage sent = Assert.Single(harness.Sender.Sent);
        Assert.Contains("too long", sent.Text);
        Assert.Contains("5 seconds", sent.Text);
    }

    [Fact]
    public async Task HandleUpdateAsync_DownloadsVoiceAsTemporaryAudioAndDeletesItAfterForwarding()
    {
        using Harness harness = Harness.Create();
        Message telegramMessage = CreateMessage();
        telegramMessage.Voice = new Voice
        {
            FileId = "voice-file",
            FileUniqueId = "voice-file-u",
            Duration = 2,
            MimeType = "audio/ogg",
        };
        Update update = new()
        {
            Id = 15,
            Message = telegramMessage,
        };

        await harness.Service.HandleUpdateAsync(harness.FileClient, update, harness.Sender, CancellationToken.None);

        TelegramInboundMessage message = Assert.Single(harness.Handler.Messages);
        Assert.Null(message.Text);
        Assert.NotNull(message.AudioFilePath);
        Assert.True(harness.Handler.AudioPathExistedDuringHandle);
        Assert.False(File.Exists(message.AudioFilePath));
    }

    private static Message CreateMessage(
        string? text = null,
        string? caption = null,
        long userId = 1234,
        long chatId = 5555,
        ChatType chatType = ChatType.Private,
        int? messageThreadId = null,
        int messageId = 7,
        string? mediaGroupId = null)
        => new()
        {
            Id = messageId,
            From = CreateUser(userId),
            Chat = new Chat
            {
                Id = chatId,
                Type = chatType,
                FirstName = chatType is ChatType.Private ? "Operator" : null,
                Title = chatType is ChatType.Private ? null : "Codex group",
            },
            Text = text,
            Caption = caption,
            MessageThreadId = messageThreadId,
            MediaGroupId = mediaGroupId,
        };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
    }

    private static User CreateUser(long userId)
        => new()
        {
            Id = userId,
            IsBot = false,
            FirstName = "Operator",
        };

    private sealed class Harness : IDisposable
    {
        private Harness(
            TemporaryDirectory temp,
            CapturingTelegramUpdateHandler handler,
            TestTelegramBotMessageSender sender,
            FakeTelegramUpdateFileClient fileClient,
            TelegramBotStateStore stateStore,
            TelegramMessageContextStore messageContextStore,
            TelegramDebugTraceStore traceStore,
            TelegramCodexBotHostedService service)
        {
            Temp = temp;
            Handler = handler;
            Sender = sender;
            FileClient = fileClient;
            StateStore = stateStore;
            MessageContextStore = messageContextStore;
            TraceStore = traceStore;
            Service = service;
        }

        public TemporaryDirectory Temp { get; }

        public CapturingTelegramUpdateHandler Handler { get; }

        public TestTelegramBotMessageSender Sender { get; }

        public FakeTelegramUpdateFileClient FileClient { get; }

        public TelegramBotStateStore StateStore { get; }

        public TelegramMessageContextStore MessageContextStore { get; }

        public TelegramDebugTraceStore TraceStore { get; }

        public TelegramCodexBotHostedService Service { get; }

        public static Harness Create(TelegramBotOptions? options = null)
            => Create(options, null);

        public static Harness Create(
            TelegramBotOptions? options,
            TelegramInputOptions? inputOptions)
        {
            TemporaryDirectory temp = TemporaryDirectory.Create();
            CapturingTelegramUpdateHandler handler = new();
            TestTelegramBotMessageSender sender = new();
            FakeTelegramUpdateFileClient fileClient = new();
            TelegramMessageContextStore messageContextStore = new();
            IOptions<CodexTelegramOptions> codexOptions = Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
            {
                Workspace = new CodexWorkspaceOptions
                {
                    DataRoot = temp.Path,
                },
            });
            TelegramBotStateStore stateStore = new(codexOptions);
            TelegramDebugTraceStore traceStore = new(
                codexOptions,
                Microsoft.Extensions.Options.Options.Create(new TelegramDebugTraceOptions()));
            options ??= new TelegramBotOptions
            {
                AllowedUserIds = [1234],
                AllowedChatIds = [-1005555],
            };
            TelegramCodexBotHostedService service = new(
                handler,
                sender,
                stateStore,
                messageContextStore,
                traceStore,
                Microsoft.Extensions.Options.Options.Create(options),
                Microsoft.Extensions.Options.Options.Create(inputOptions ?? new TelegramInputOptions()),
                NullLogger<TelegramCodexBotHostedService>.Instance);

            return new Harness(temp, handler, sender, fileClient, stateStore, messageContextStore, traceStore, service);
        }

        public void Dispose()
        {
            foreach (TelegramInboundMessage message in Handler.Messages)
            {
                foreach (TelegramAttachmentDescriptor attachment in message.Attachments ?? [])
                {
                    TryDelete(attachment.FilePath);
                }

                TryDelete(message.AudioFilePath);
            }

            Temp.Dispose();
        }

        private static void TryDelete(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    private sealed class CapturingTelegramUpdateHandler : ITelegramCodexBotUpdateHandler
    {
        public List<TelegramInboundMessage> Messages { get; } = [];

        public List<TelegramInboundCallback> Callbacks { get; } = [];

        public bool AudioPathExistedDuringHandle { get; private set; }

        public Task HandleMessageAsync(
            TelegramInboundMessage message,
            ITelegramBotMessageSender sender,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(message.AudioFilePath))
            {
                AudioPathExistedDuringHandle = File.Exists(message.AudioFilePath);
            }

            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task HandleCallbackAsync(
            TelegramInboundCallback callback,
            ITelegramBotMessageSender sender,
            CancellationToken cancellationToken)
        {
            Callbacks.Add(callback);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTelegramUpdateFileClient : ITelegramUpdateFileClient
    {
        public string? ThrowOnGetFileId { get; set; }

        public List<string> RequestedFileIds { get; } = [];

        public List<string> DownloadedFileIds { get; } = [];

        public Task<TGFile> GetFileAsync(string fileId, CancellationToken cancellationToken)
        {
            RequestedFileIds.Add(fileId);

            if (string.Equals(fileId, ThrowOnGetFileId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"download failed for {fileId}");
            }

            return Task.FromResult(new TGFile
            {
                FileId = fileId,
                FileUniqueId = fileId + "-u",
                FilePath = $"telegram/{fileId}{ResolveExtension(fileId)}",
            });
        }

        public async Task DownloadFileAsync(TGFile file, Stream destination, CancellationToken cancellationToken)
        {
            DownloadedFileIds.Add(file.FileId);
            byte[] data = [0x43, 0x54];
            await destination.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }

        private static string ResolveExtension(string fileId)
            => fileId switch
            {
                "doc-file" => ".pdf",
                "image-doc" => ".png",
                "voice-file" => ".ogg",
                "video-note-file" => ".mp4",
                "sticker-file" => ".webp",
                _ => ".jpg",
            };
    }

    private sealed class TestTelegramBotMessageSender : ITelegramBotMessageSender
    {
        public List<SentTelegramMessage> Sent { get; } = [];

        public Exception? SendException { get; set; }

        public Task SendTextMessageAsync(
            TelegramConversationScope conversation,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
        {
            Sent.Add(new SentTelegramMessage(conversation, text));
            if (SendException is not null)
            {
                throw SendException;
            }

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

        public List<TelegramMessageAcknowledgement> Acknowledgements { get; } = [];

        public Task AcknowledgeMessageAsync(TelegramMessageAcknowledgement acknowledgement, CancellationToken cancellationToken)
        {
            Acknowledgements.Add(acknowledgement);
            return Task.CompletedTask;
        }

        public Task SendTypingActionAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ReactToMessageAsync(TelegramMessageReaction reaction, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed record SentTelegramMessage(TelegramConversationScope Conversation, string Text);
}
