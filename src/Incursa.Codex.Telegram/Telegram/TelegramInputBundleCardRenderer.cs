using System.Globalization;
using System.Text;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

internal interface ITelegramInputBundleCardRenderer
{
    TelegramInputBundleCard Render(TelegramInputBundle bundle);
}

internal sealed record TelegramInputBundleCard(
    string Text,
    IReadOnlyList<IReadOnlyList<TelegramReplyButton>> Buttons);

internal sealed class TelegramInputBundleCardRenderer : ITelegramInputBundleCardRenderer
{
    private readonly IOptions<TelegramInputOptions> _options;

    public TelegramInputBundleCardRenderer(IOptions<TelegramInputOptions> options)
    {
        _options = options;
    }

    public TelegramInputBundleCard Render(TelegramInputBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return new TelegramInputBundleCard(BuildText(bundle), BuildButtons(bundle));
    }

    private string BuildText(TelegramInputBundle bundle)
    {
        StringBuilder builder = new();
        builder.AppendLine("Input bundle");
        if (!string.IsNullOrWhiteSpace(bundle.SessionId))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Target: {bundle.SessionName} ({ShortId(bundle.SessionId)})");
        }

        builder.AppendLine(CultureInfo.InvariantCulture, $"Status: {FormatStatus(bundle)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Intent: {FormatIntent(bundle.Intent)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Text parts: {bundle.TextParts.Count}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Attachments: {bundle.Attachments.Count}");

        if (bundle.SourceMessageIds.Count > 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Sources: {string.Join(", ", bundle.SourceMessageIds)}");
        }

        if (bundle.ExpiresAt is not null)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Expires: {bundle.ExpiresAt:yyyy-MM-dd HH:mm:ss 'UTC'}");
        }

        string combinedText = bundle.CombinedText;
        if (!string.IsNullOrWhiteSpace(combinedText))
        {
            builder.AppendLine();
            builder.AppendLine(TrimPreview(combinedText, GetPreviewCharacters()));
        }

        if (!string.IsNullOrWhiteSpace(bundle.TraceId))
        {
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"Trace: {ShortId(bundle.TraceId)}");
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<IReadOnlyList<TelegramReplyButton>> BuildButtons(TelegramInputBundle bundle)
        =>
        [
            [
                new TelegramReplyButton("Send now", $"bsend:{bundle.Id}"),
                new TelegramReplyButton("Queue next", $"bqueue:{bundle.Id}"),
            ],
            [new TelegramReplyButton("Steer current turn", $"bsteer:{bundle.Id}")],
            [
                new TelegramReplyButton("Add more", $"badd:{bundle.Id}"),
                new TelegramReplyButton("Clear", $"bclear:{bundle.Id}"),
                new TelegramReplyButton("Cancel", $"bcancel:{bundle.Id}"),
            ],
            [new TelegramReplyButton("Trace", $"btrace:{bundle.Id}")],
        ];

    private static string FormatStatus(TelegramInputBundle bundle)
        => bundle.Status is TelegramInputBundleStatus.Capturing && bundle.HasContent
            ? "Ready"
            : bundle.Status switch
            {
                TelegramInputBundleStatus.Capturing => "Capturing",
                TelegramInputBundleStatus.Submitted => "Submitted",
                TelegramInputBundleStatus.Queued => "Queued",
                TelegramInputBundleStatus.Steered => "Steered",
                TelegramInputBundleStatus.Sent => "Sent",
                TelegramInputBundleStatus.Cancelled => "Cancelled",
                TelegramInputBundleStatus.Expired => "Expired",
                _ => bundle.Status.ToString(),
            };

    private static string FormatIntent(TelegramInputBundleIntent intent)
        => intent switch
        {
            TelegramInputBundleIntent.SendNow => "Send now",
            TelegramInputBundleIntent.QueueNext => "Queue next",
            TelegramInputBundleIntent.SteerCurrentTurn => "Steer current turn",
            _ => intent.ToString(),
        };

    private int GetPreviewCharacters()
        => Math.Clamp(
            _options.Value.PreviewCharacters,
            TelegramInputLimits.MinPreviewCharacters,
            TelegramInputLimits.MaxPreviewCharacters);

    private static string TrimPreview(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maxLength).TrimEnd(), "...");
    }

    private static string ShortId(string value)
        => value.Length <= 8 ? value : value[..8];
}
