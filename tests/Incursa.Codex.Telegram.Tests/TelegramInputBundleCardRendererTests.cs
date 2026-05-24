using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramInputBundleCardRendererTests
{
    [Fact]
    public void Render_WhenActiveTurn_ReturnsDecisionButtonsWithoutTraceButton()
    {
        TelegramInputBundleCardRenderer renderer = CreateRenderer(previewCharacters: 80);
        TelegramInputBundle bundle = new()
        {
            Id = "bundle-1",
            UserId = 42,
            Conversation = new TelegramConversationScope(1234, 55),
            Intent = TelegramInputBundleIntent.SteerCurrentTurn,
            Status = TelegramInputBundleStatus.Capturing,
            TextParts =
            [
                new TelegramInputTextPart(new string('x', 90), "text", 10, DateTimeOffset.Parse("2026-05-23T10:00:00Z")),
            ],
            Attachments =
            [
                new TelegramAttachmentDescriptor(@"C:\temp\image.png", "image.png", "image/png", IsImage: true),
            ],
            SourceMessageIds = [10],
            CreatedAt = DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
            ExpiresAt = DateTimeOffset.Parse("2026-05-23T10:30:00Z"),
            TraceId = "trace-123456789",
        };

        TelegramInputBundleCard card = renderer.Render(
            bundle,
            new TelegramInputBundleCardContext(
                HasSteerableTurn: true,
                ShouldQueueForLater: false,
                AllowAttachmentSteering: true));

        Assert.Contains("Input ready", card.Text);
        Assert.Contains("Action: Steer current turn", card.Text);
        Assert.Contains("Auto: Queue next after 25s idle", card.Text);
        Assert.Contains("Text: 1 part", card.Text);
        Assert.Contains("Attachments: 1 file (image)", card.Text);
        Assert.DoesNotContain("Sources:", card.Text);
        Assert.Contains(string.Concat(new string('x', 80), "..."), card.Text);
        Assert.DoesNotContain("Trace:", card.Text);

        Assert.Equal(
            ["Steer current turn", "Queue next", "Add more", "Clear", "Cancel"],
            card.Buttons.SelectMany(row => row.Select(button => button.Text)).ToArray());
        Assert.Equal(
            [
                TelegramInputBundleCardBehavior.CallbackData(bundle, "bsteer"),
                TelegramInputBundleCardBehavior.CallbackData(bundle, "bqueue"),
                TelegramInputBundleCardBehavior.CallbackData(bundle, "badd"),
                TelegramInputBundleCardBehavior.CallbackData(bundle, "bclear"),
                TelegramInputBundleCardBehavior.CallbackData(bundle, "bcancel"),
            ],
            card.Buttons.SelectMany(row => row.Select(button => button.CallbackData)).ToArray());
    }

    [Fact]
    public void Render_WhenIdle_ReturnsSendButtonWithoutQueueSteerOrTrace()
    {
        TelegramInputBundleCardRenderer renderer = CreateRenderer(previewCharacters: 80);
        TelegramInputBundle bundle = new()
        {
            Id = "bundle-1",
            UserId = 42,
            Conversation = new TelegramConversationScope(1234, 55),
            Intent = TelegramInputBundleIntent.SendNow,
            Status = TelegramInputBundleStatus.Capturing,
            TextParts =
            [
                new TelegramInputTextPart("ready to send", "text", 10, DateTimeOffset.Parse("2026-05-23T10:00:00Z")),
            ],
            CreatedAt = DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
            TraceId = "trace-123456789",
        };

        TelegramInputBundleCard card = renderer.Render(
            bundle,
            new TelegramInputBundleCardContext(
                HasSteerableTurn: false,
                ShouldQueueForLater: false,
                AllowAttachmentSteering: true));

        Assert.Contains("Input ready", card.Text);
        Assert.Contains("Action: Send now", card.Text);
        Assert.Contains("Auto: Send now after 25s idle", card.Text);
        Assert.DoesNotContain("Trace:", card.Text);
        Assert.Equal(
            ["Send now", "Add more", "Clear", "Cancel"],
            card.Buttons.SelectMany(row => row.Select(button => button.Text)).ToArray());
        Assert.Equal(
            [
                TelegramInputBundleCardBehavior.CallbackData(bundle, "bsend"),
                TelegramInputBundleCardBehavior.CallbackData(bundle, "badd"),
                TelegramInputBundleCardBehavior.CallbackData(bundle, "bclear"),
                TelegramInputBundleCardBehavior.CallbackData(bundle, "bcancel"),
            ],
            card.Buttons.SelectMany(row => row.Select(button => button.CallbackData)).ToArray());
    }

    [Fact]
    public void Render_WhenAttachmentSteeringDisabled_QueuesFullBundleAndOffersTextOnlySteer()
    {
        TelegramInputBundleCardRenderer renderer = CreateRenderer(previewCharacters: 80);
        TelegramInputBundle bundle = new()
        {
            Id = "bundle-1",
            UserId = 42,
            Conversation = new TelegramConversationScope(1234, 55),
            Intent = TelegramInputBundleIntent.SteerCurrentTurn,
            Status = TelegramInputBundleStatus.Capturing,
            TextParts =
            [
                new TelegramInputTextPart("transcript", "voice transcript", 10, DateTimeOffset.Parse("2026-05-23T10:00:00Z")),
            ],
            Attachments =
            [
                new TelegramAttachmentDescriptor(@"C:\temp\image.png", "image.png", "image/png", IsImage: true),
            ],
            CreatedAt = DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
        };

        TelegramInputBundleCard card = renderer.Render(
            bundle,
            new TelegramInputBundleCardContext(
                HasSteerableTurn: true,
                ShouldQueueForLater: false,
                AllowAttachmentSteering: false));

        Assert.Contains("Action: Queue next", card.Text);
        Assert.Contains("Attachment steering is not supported", card.Text);
        Assert.Equal(
            ["Queue next", "Text-only steer", "Add more", "Clear", "Cancel"],
            card.Buttons.SelectMany(row => row.Select(button => button.Text)).ToArray());
    }

    [Fact]
    public void Render_WhenTelegramIsDrainingWithoutActiveTurn_QueuesInsteadOfSteering()
    {
        TelegramInputBundleCardRenderer renderer = CreateRenderer(previewCharacters: 80);
        TelegramInputBundle bundle = new()
        {
            Id = "bundle-1",
            UserId = 42,
            Conversation = new TelegramConversationScope(1234, 55),
            Intent = TelegramInputBundleIntent.QueueNext,
            Status = TelegramInputBundleStatus.Capturing,
            CreatedAt = DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
        };

        TelegramInputBundleCard card = renderer.Render(
            bundle,
            new TelegramInputBundleCardContext(
                HasSteerableTurn: false,
                ShouldQueueForLater: true,
                AllowAttachmentSteering: true));

        Assert.Contains("Action: Queue next", card.Text);
        Assert.Equal(
            ["Queue next", "Add more", "Clear", "Cancel"],
            card.Buttons.SelectMany(row => row.Select(button => button.Text)).ToArray());
    }

    private static TelegramInputBundleCardRenderer CreateRenderer(int previewCharacters)
        => new(Microsoft.Extensions.Options.Options.Create(new TelegramInputOptions
        {
            PreviewCharacters = previewCharacters,
        }));
}
