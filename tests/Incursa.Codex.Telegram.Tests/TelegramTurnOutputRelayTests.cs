using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramTurnOutputRelayTests
{
    [Fact]
    public async Task PublishTurnEventAsync_IgnoresEventsWithoutThreadId()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramTurnOutputRelay relay = CreateRelay(queue, new TelegramThreadFollowRegistry());

        await relay.PublishTurnEventAsync(CreateEntry(threadId: " "), CancellationToken.None);

        Assert.Empty(queue.Messages);
    }

    [Fact]
    public async Task PublishTurnEventAsync_DoesNothingWhenThreadIsNotFollowed()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramTurnOutputRelay relay = CreateRelay(queue, new TelegramThreadFollowRegistry());

        await relay.PublishTurnEventAsync(CreateEntry(body: "visible update"), CancellationToken.None);

        Assert.Empty(queue.Messages);
    }

    [Fact]
    public async Task PublishTurnEventAsync_PublishesVisibleUpdateToEveryFollower()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = new();
        followRegistry.FollowThread(new TelegramConversationScope(111, 10), "thread-1");
        followRegistry.FollowThread(new TelegramConversationScope(222, null), "thread-1");
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(
                type: "item.tool_output",
                title: "Tool output",
                subtitle: "dotnet test",
                body: "Tests passed."),
            CancellationToken.None);

        Assert.Equal(2, queue.Messages.Count);
        Assert.All(queue.Messages, message =>
        {
            Assert.Equal("thread-1", message.SessionId);
            Assert.Equal(CodexOutboundMessageKind.Update, message.Kind);
            Assert.Equal(OutboundPriority.Normal, message.Priority);
            Assert.Contains("Tool output", message.Text);
            Assert.Contains("dotnet test", message.Text);
            Assert.Contains("Tests passed.", message.Text);
        });
        Assert.Contains(queue.Messages, message => message.ChatId == 111 && message.MessageThreadId == 10);
        Assert.Contains(queue.Messages, message => message.ChatId == 222 && message.MessageThreadId is null);
    }

    [Fact]
    public async Task PublishTurnEventAsync_ExplicitImageMediaQueuesTelegramFilePayload()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string filePath = Path.Combine(temp.Path, "browser-shot.png");
        await File.WriteAllBytesAsync(filePath, [0x89, 0x50, 0x4e, 0x47], CancellationToken.None);
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(
                type: "item.image_view",
                title: "Image",
                body: null,
                metadata: new Dictionary<string, string?>
                {
                    ["explicitMediaKind"] = "image-view",
                    ["path"] = filePath,
                }),
            CancellationToken.None);

        OutboundTelegramMessage message = Assert.Single(queue.Messages);
        Assert.Equal(CodexOutboundMessageKind.Update, message.Kind);
        Assert.Equal(OutboundPriority.High, message.Priority);
        Assert.Equal("Codex artifact: browser-shot.png", message.Text);
        Assert.NotNull(message.File);
        Assert.Equal(TelegramOutboundFileKind.Photo, message.File.Kind);
        Assert.Equal(filePath, message.File.Path);
        Assert.Equal("browser-shot.png", message.File.FileName);
        Assert.Equal("image/png", message.File.ContentType);
    }

    [Fact]
    public async Task PublishTurnEventAsync_ExplicitGifMediaQueuesDocumentPayload()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string filePath = Path.Combine(temp.Path, "animation.gif");
        await File.WriteAllBytesAsync(filePath, [0x47, 0x49, 0x46], CancellationToken.None);
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(
                type: "item.image_generation",
                title: "Generated image",
                body: null,
                metadata: new Dictionary<string, string?>
                {
                    ["explicitMediaKind"] = "image-generation",
                    ["result"] = filePath,
                }),
            CancellationToken.None);

        OutboundTelegramMessage message = Assert.Single(queue.Messages);
        Assert.NotNull(message.File);
        Assert.Equal(TelegramOutboundFileKind.Document, message.File.Kind);
        Assert.Equal("image/gif", message.File.ContentType);
    }

    [Fact]
    public async Task PublishTurnEventAsync_ExplicitBase64ImageMediaMaterializesTelegramFilePayload()
    {
        byte[] imageBytes = new byte[160];
        byte[] pngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        pngSignature.CopyTo(imageBytes, 0);
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(
                type: "item.image_generation",
                title: "Generated image",
                body: null,
                metadata: new Dictionary<string, string?>
                {
                    ["explicitMediaKind"] = "image-generation",
                    ["id"] = "ig-test",
                    ["result"] = Convert.ToBase64String(imageBytes),
                }),
            CancellationToken.None);

        OutboundTelegramMessage message = Assert.Single(queue.Messages);
        Assert.NotNull(message.File);
        Assert.Equal(TelegramOutboundFileKind.Photo, message.File.Kind);
        Assert.Equal("codex-image-ig-test.png", message.File.FileName);
        Assert.Equal("image/png", message.File.ContentType);
        Assert.True(File.Exists(message.File.Path));
        Assert.Equal(imageBytes, await File.ReadAllBytesAsync(message.File.Path, CancellationToken.None));

        File.Delete(message.File.Path);
    }

    [Theory]
    [InlineData("turn.completed", "info", 1)]
    [InlineData("turn.failed", "danger", 2)]
    public async Task PublishTurnEventAsync_TerminalEventReactsToRegisteredSourceMessage(
        string eventType,
        string severity,
        int expectedReaction)
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnReactionRegistry reactionRegistry = new();
        TestTelegramBotMessageSender sender = new();
        reactionRegistry.Register("thread-1", "turn-1", new TelegramConversationScope(1234, 55), 42);
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry, reactionRegistry: reactionRegistry, messageSender: sender);

        await relay.PublishTurnEventAsync(
            CreateEntry(
                type: eventType,
                title: eventType,
                body: null,
                severity: severity),
            CancellationToken.None);

        TelegramMessageReaction reaction = Assert.Single(sender.Reactions);
        Assert.Equal(new TelegramConversationScope(1234, 55), reaction.Conversation);
        Assert.Equal(42, reaction.MessageId);
        Assert.Equal((TelegramMessageReactionKind)expectedReaction, reaction.Kind);
    }

    [Fact]
    public async Task PublishTurnEventAsync_ContinuesAfterOneDestinationFailsToEnqueue()
    {
        FakeOutboundTelegramQueue queue = new();
        queue.Exceptions.Enqueue(new InvalidOperationException("queue unavailable"));
        TelegramThreadFollowRegistry followRegistry = new();
        followRegistry.FollowThread(new TelegramConversationScope(111, null), "thread-1");
        followRegistry.FollowThread(new TelegramConversationScope(222, null), "thread-1");
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(CreateEntry(body: "still deliver to another chat"), CancellationToken.None);

        Assert.Single(queue.Messages);
        Assert.True(queue.Messages.Single().ChatId is 111 or 222);
    }

    [Fact]
    public async Task PublishTurnEventAsync_FormatsInternalCommandProgressWhenProgressRelayIsEnabled()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(
                type: "item.command",
                title: "Internal command",
                isInternal: true,
                metadata: new Dictionary<string, string?>
                {
                    ["command"] = "dotnet test tests/Incursa.Codex.Telegram.Tests",
                    ["status"] = "completed",
                }),
            CancellationToken.None);

        OutboundTelegramMessage message = Assert.Single(queue.Messages);
        Assert.Equal(CodexOutboundMessageKind.Progress, message.Kind);
        Assert.Equal(OutboundPriority.Low, message.Priority);
        Assert.Equal("Command finished: dotnet test tests/Incursa.Codex.Telegram.Tests", message.Text);
    }

    [Theory]
    [InlineData("turn.started", null, null, null, null, "Turn started.")]
    [InlineData("item.command", "dotnet build", "status", "running", null, "Running command: dotnet build")]
    [InlineData("item.command", "dotnet test", "status", "failed", null, "Command failed: dotnet test")]
    [InlineData("item.command", "dotnet pack", "exitCode", "2", null, "Command failed (2): dotnet pack")]
    [InlineData("item.tool", "shell", "status", "running", null, "Using tool: shell [running]")]
    [InlineData("item.file_change", "3", "status", "completed", null, "File changes: 3 [completed]")]
    [InlineData("item.web_search", "telegram bot api", null, null, null, "Web search: telegram bot api")]
    [InlineData("item.progress", null, null, null, "Installing packages", "Progress: Installing packages")]
    public async Task PublishTurnEventAsync_FormatsInternalProgressVariants(
        string type,
        string? primaryValue,
        string? secondaryKey,
        string? secondaryValue,
        string? subtitle,
        string expectedText)
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        Dictionary<string, string?> metadata = [];
        switch (type)
        {
            case "item.command":
                metadata["command"] = primaryValue;
                break;
            case "item.tool":
                metadata["tool"] = primaryValue;
                break;
            case "item.file_change":
                metadata["changeCount"] = primaryValue;
                break;
            case "item.web_search":
                metadata["query"] = primaryValue;
                break;
        }

        if (secondaryKey is not null)
        {
            metadata[secondaryKey] = secondaryValue;
        }

        await relay.PublishTurnEventAsync(
            CreateEntry(type: type, title: "Internal progress", subtitle: subtitle, isInternal: true, metadata: metadata),
            CancellationToken.None);

        Assert.Equal(expectedText, Assert.Single(queue.Messages).Text);
    }

    [Fact]
    public async Task PublishTurnEventAsync_FormatsInternalMcpToolProgress()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(
                type: "item.mcp_tool",
                title: "MCP tool",
                isInternal: true,
                metadata: new Dictionary<string, string?>
                {
                    ["server"] = "github",
                    ["tool"] = "search_issues",
                    ["status"] = "completed",
                }),
            CancellationToken.None);

        Assert.Equal("Using MCP tool: github/search_issues [completed]", Assert.Single(queue.Messages).Text);
    }

    [Fact]
    public async Task PublishTurnEventAsync_OmitsWhitespaceStatusSuffixFromInternalToolProgress()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(
                type: "item.tool",
                title: "Tool",
                isInternal: true,
                metadata: new Dictionary<string, string?>
                {
                    ["tool"] = "shell",
                    ["status"] = " ",
                }),
            CancellationToken.None);

        Assert.Equal("Using tool: shell", Assert.Single(queue.Messages).Text);
    }

    [Fact]
    public async Task PublishTurnEventAsync_TruncatesLongInternalCommandProgress()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        string command = new('x', 2100);

        await relay.PublishTurnEventAsync(
            CreateEntry(
                type: "item.command",
                title: "Command",
                isInternal: true,
                metadata: new Dictionary<string, string?>
                {
                    ["command"] = command,
                    ["status"] = "running",
                }),
            CancellationToken.None);

        string text = Assert.Single(queue.Messages).Text;
        Assert.StartsWith("Running command: ", text, StringComparison.Ordinal);
        Assert.EndsWith("...", text, StringComparison.Ordinal);
        Assert.True(text.Length < command.Length);
    }

    [Fact]
    public async Task PublishTurnEventAsync_FormatsDangerousInternalEventAsError()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(type: "item.internal_error", title: "Internal error", body: "tool crashed", severity: "danger", isInternal: true),
            CancellationToken.None);

        OutboundTelegramMessage message = Assert.Single(queue.Messages);
        Assert.Equal(CodexOutboundMessageKind.Error, message.Kind);
        Assert.Equal(OutboundPriority.Critical, message.Priority);
        Assert.Equal("Internal error" + Environment.NewLine + "tool crashed", message.Text);
    }

    [Fact]
    public async Task PublishTurnEventAsync_SuppressesInternalReasoningNoise()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(
                type: "item.reasoning",
                title: "Reasoning",
                subtitle: "Reasoning (high)",
                isInternal: true),
            CancellationToken.None);

        Assert.Empty(queue.Messages);
    }

    [Fact]
    public async Task PublishTurnEventAsync_SuppressesInternalContextCompactionNoise()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(
                type: "item.compaction",
                title: "Context compaction",
                subtitle: "Context compaction",
                isInternal: true),
            CancellationToken.None);

        Assert.Empty(queue.Messages);
    }

    [Fact]
    public async Task PublishTurnEventAsync_PublishesAgentDeltasAndOnlyUnpublishedCompletionText()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry, new TelegramOutboundOptions
        {
            AgentMessageUpdateMinChars = 5,
            AgentMessageUpdateMaxChars = 12,
        });

        await relay.PublishTurnEventAsync(
            CreateEntry(type: "item.agentMessage.delta", title: "Agent", body: "hello world.\nremaining"),
            CancellationToken.None);
        await relay.PublishTurnEventAsync(
            CreateEntry(type: "turn.completed", title: "Turn completed", body: "hello world.\nremaining", severity: "success"),
            CancellationToken.None);

        Assert.Collection(
            queue.Messages,
            message =>
            {
                Assert.Equal(CodexOutboundMessageKind.Update, message.Kind);
                Assert.Equal(OutboundPriority.High, message.Priority);
                Assert.Equal("hello world.", message.Text);
            },
            message =>
            {
                Assert.Equal(CodexOutboundMessageKind.Completion, message.Kind);
                Assert.Equal(OutboundPriority.High, message.Priority);
                Assert.Equal("remaining", message.Text);
            },
            message =>
            {
                Assert.Equal(CodexOutboundMessageKind.Completion, message.Kind);
                Assert.Equal(OutboundPriority.High, message.Priority);
                Assert.Equal("~~ fin ~~", message.Text);
            });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task PublishTurnEventAsync_IgnoresEmptyAgentMessageDelta(string? delta)
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry, new TelegramOutboundOptions
        {
            AgentMessageUpdateMinChars = 1,
            AgentMessageUpdateMaxChars = 80,
        });

        await relay.PublishTurnEventAsync(
            CreateEntry(type: "item.agentMessage.delta", title: "Agent", body: delta),
            CancellationToken.None);

        Assert.Empty(queue.Messages);
    }

    [Fact]
    public async Task PublishTurnEventAsync_SuppressesBareFinishedMarkerWhenAllAgentTextWasAlreadyPublished()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry, new TelegramOutboundOptions
        {
            AgentMessageUpdateMinChars = 5,
            AgentMessageUpdateMaxChars = 20,
        });

        await relay.PublishTurnEventAsync(
            CreateEntry(type: "item.agentMessage.delta", title: "Agent", body: "all done.\n"),
            CancellationToken.None);
        await relay.PublishTurnEventAsync(
            CreateEntry(type: "turn.completed", title: "Turn completed", body: "all done.", severity: "success"),
            CancellationToken.None);

        Assert.Equal("all done.", Assert.Single(queue.Messages).Text);
    }

    [Fact]
    public async Task PublishTurnEventAsync_FlushesUnpublishedAgentTextWhenCompletionHasNoBody()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry, new TelegramOutboundOptions
        {
            AgentMessageUpdateMinChars = 20,
            AgentMessageUpdateMaxChars = 50,
        });

        await relay.PublishTurnEventAsync(
            CreateEntry(type: "item.agentMessage.delta", title: "Agent", body: "short final"),
            CancellationToken.None);
        await relay.PublishTurnEventAsync(
            CreateEntry(type: "turn.completed", title: "Turn completed", body: null, severity: "success"),
            CancellationToken.None);

        Assert.Collection(
            queue.Messages,
            message => Assert.Equal("short final", message.Text),
            message => Assert.Equal("~~ fin ~~", message.Text));
    }

    [Fact]
    public async Task PublishTurnEventAsync_StripsEmbeddedFinishedMarkerAndPublishesMarkerSeparately()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(type: "turn.completed", title: "Turn completed", body: "Finished already" + Environment.NewLine + Environment.NewLine + "~~ fin ~~", severity: "success"),
            CancellationToken.None);

        Assert.Collection(
            queue.Messages,
            message => Assert.Equal("Finished already", message.Text),
            message => Assert.Equal("~~ fin ~~", message.Text));
    }

    [Fact]
    public async Task PublishTurnEventAsync_PublishesBareFinishedMarkerForEmptyCompletion()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(type: "turn.completed", title: "Turn completed", body: null, severity: "success"),
            CancellationToken.None);

        Assert.Equal("~~ fin ~~", Assert.Single(queue.Messages).Text);
    }

    [Fact]
    public async Task PublishTurnEventAsync_CleansMarkdownFencesFromAgentProgress()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry, new TelegramOutboundOptions
        {
            AgentMessageUpdateMinChars = 1,
            AgentMessageUpdateMaxChars = 80,
        });

        await relay.PublishTurnEventAsync(
            CreateEntry(type: "item.agentMessage.delta", title: "Agent", body: "```\n---\nactual progress\n```\n"),
            CancellationToken.None);

        Assert.Equal("actual progress", Assert.Single(queue.Messages).Text);
    }

    [Fact]
    public async Task PublishTurnEventAsync_DropsAgentProgressWhenOnlyFenceNoiseWasBuffered()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry, new TelegramOutboundOptions
        {
            AgentMessageUpdateMinChars = 1,
            AgentMessageUpdateMaxChars = 80,
        });

        await relay.PublishTurnEventAsync(
            CreateEntry(type: "item.agentMessage.delta", title: "Agent", body: "```\n---\n```\n"),
            CancellationToken.None);

        Assert.Empty(queue.Messages);
    }

    [Fact]
    public async Task PublishTurnEventAsync_PublishesWhitespaceBoundedAgentProgressAtMaxLength()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry, new TelegramOutboundOptions
        {
            AgentMessageUpdateMinChars = 5,
            AgentMessageUpdateMaxChars = 12,
        });

        await relay.PublishTurnEventAsync(
            CreateEntry(type: "item.agentMessage.delta", title: "Agent", body: "alpha beta gamma"),
            CancellationToken.None);

        Assert.Equal("alpha beta", Assert.Single(queue.Messages).Text);
    }

    [Fact]
    public async Task PublishTurnEventAsync_DoesNotSplitAgentProgressAtTrailingVersionPeriod()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry, new TelegramOutboundOptions
        {
            AgentMessageUpdateMinChars = 5,
            AgentMessageUpdateMaxChars = 80,
        });

        await relay.PublishTurnEventAsync(
            CreateEntry(type: "item.agentMessage.delta", title: "Agent", body: "Published package 1.0."),
            CancellationToken.None);
        await relay.PublishTurnEventAsync(
            CreateEntry(type: "item.agentMessage.delta", title: "Agent", body: "13 for release. Next step"),
            CancellationToken.None);

        Assert.Equal("Published package 1.0.13 for release.", Assert.Single(queue.Messages).Text);
    }

    [Fact]
    public async Task PublishTurnEventAsync_DoesNotSplitAgentProgressAtTrailingDotNetPeriod()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry, new TelegramOutboundOptions
        {
            AgentMessageUpdateMinChars = 5,
            AgentMessageUpdateMaxChars = 80,
        });

        await relay.PublishTurnEventAsync(
            CreateEntry(type: "item.agentMessage.delta", title: "Agent", body: "Use ."),
            CancellationToken.None);
        await relay.PublishTurnEventAsync(
            CreateEntry(type: "item.agentMessage.delta", title: "Agent", body: "NET 10. It works"),
            CancellationToken.None);

        Assert.Equal("Use .NET 10.", Assert.Single(queue.Messages).Text);
    }

    [Fact]
    public async Task PublishTurnEventAsync_PublishesFinishedMarkerSeparatelyForFailures()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(type: "turn.failed", title: "Turn failed", body: "Codex crashed.", severity: "danger"),
            CancellationToken.None);

        Assert.Collection(
            queue.Messages,
            message =>
            {
                Assert.Equal(CodexOutboundMessageKind.Error, message.Kind);
                Assert.Equal(OutboundPriority.Critical, message.Priority);
                Assert.Contains("Turn failed", message.Text);
                Assert.Contains("Codex crashed.", message.Text);
                Assert.DoesNotContain("~~ fin ~~", message.Text);
            },
            message =>
            {
                Assert.Equal(CodexOutboundMessageKind.Completion, message.Kind);
                Assert.Equal(OutboundPriority.High, message.Priority);
                Assert.Equal("~~ fin ~~", message.Text);
            });
    }

    [Fact]
    public async Task PublishTurnEventAsync_UsesMetadataWhenBodyIsEmpty()
    {
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);

        await relay.PublishTurnEventAsync(
            CreateEntry(
                title: "Event",
                body: null,
                metadata: new Dictionary<string, string?>
                {
                    ["path"] = "README.md",
                    ["empty"] = "",
                }),
            CancellationToken.None);

        Assert.Contains("path: README.md", Assert.Single(queue.Messages).Text);
        Assert.DoesNotContain("empty", queue.Messages.Single().Text);
    }

    private static TelegramTurnOutputRelay CreateRelay(
        FakeOutboundTelegramQueue queue,
        TelegramThreadFollowRegistry followRegistry,
        TelegramOutboundOptions? options = null,
        ITelegramTurnReactionRegistry? reactionRegistry = null,
        TestTelegramBotMessageSender? messageSender = null)
        => new(
            queue,
            followRegistry,
            reactionRegistry ?? new TelegramTurnReactionRegistry(),
            messageSender ?? new TestTelegramBotMessageSender(),
            Microsoft.Extensions.Options.Options.Create(options ?? new TelegramOutboundOptions()),
            NullLogger<TelegramTurnOutputRelay>.Instance);

    private static TelegramThreadFollowRegistry FollowThread()
    {
        TelegramThreadFollowRegistry followRegistry = new();
        followRegistry.FollowThread(new TelegramConversationScope(1234, 55), "thread-1");
        return followRegistry;
    }

    private static CodexTimelineEntryVm CreateEntry(
        string type = "item.message",
        string title = "Agent message",
        string? subtitle = null,
        string? body = "Hello from Codex.",
        string severity = "info",
        string? threadId = "thread-1",
        string? turnId = "turn-1",
        IReadOnlyDictionary<string, string?>? metadata = null,
        bool isInternal = false)
        => new(
            type,
            title,
            subtitle,
            body,
            severity,
            DateTimeOffset.Parse("2026-05-04T00:00:00Z"),
            threadId,
            turnId,
            metadata ?? new Dictionary<string, string?>(),
            isInternal);

    private sealed class FakeOutboundTelegramQueue : IOutboundTelegramQueue
    {
        public List<OutboundTelegramMessage> Messages { get; } = [];

        public Queue<Exception> Exceptions { get; } = [];

        public ValueTask EnqueueAsync(OutboundTelegramMessage message, CancellationToken cancellationToken)
        {
            if (Exceptions.TryDequeue(out Exception? exception))
            {
                throw exception;
            }

            Messages.Add(message);
            return ValueTask.CompletedTask;
        }

        public Task<TelegramOutboundQueueStatus> GetStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(new TelegramOutboundQueueStatus(0, 0, 0, 0, null, null, null, []));
    }

    private sealed class TestTelegramBotMessageSender : ITelegramBotMessageSender
    {
        public List<TelegramMessageReaction> Reactions { get; } = [];

        public Task SendTextMessageAsync(
            TelegramConversationScope conversation,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
            => Task.CompletedTask;

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
        {
            Reactions.Add(reaction);
            return Task.CompletedTask;
        }
    }
}
