using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramInputBundleCardRendererTests
{
    [Fact]
    public void Render_ReturnsSummaryTextAndExpectedButtonRows()
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
        };

        TelegramInputBundleCard card = renderer.Render(bundle);

        Assert.Contains("Input bundle", card.Text);
        Assert.Contains("Status: Ready", card.Text);
        Assert.Contains("Intent: Steer current turn", card.Text);
        Assert.Contains("Text parts: 1", card.Text);
        Assert.Contains("Attachments: 1", card.Text);
        Assert.Contains("Sources: 10", card.Text);
        Assert.Contains(string.Concat(new string('x', 80), "..."), card.Text);

        Assert.Equal(
            ["Send now", "Queue next", "Steer current turn", "Add more", "Clear", "Cancel", "Trace"],
            card.Buttons.SelectMany(row => row.Select(button => button.Text)).ToArray());
        Assert.Equal(
            ["bsend:bundle-1", "bqueue:bundle-1", "bsteer:bundle-1", "badd:bundle-1", "bclear:bundle-1", "bcancel:bundle-1", "btrace:bundle-1"],
            card.Buttons.SelectMany(row => row.Select(button => button.CallbackData)).ToArray());
    }

    private static TelegramInputBundleCardRenderer CreateRenderer(int previewCharacters)
        => new(Microsoft.Extensions.Options.Options.Create(new TelegramInputOptions
        {
            PreviewCharacters = previewCharacters,
        }));
}
