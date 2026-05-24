using System.Globalization;
using System.Text;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

internal interface ITelegramInputBundleCardRenderer
{
    TelegramInputBundleCard Render(TelegramInputBundle bundle, TelegramInputBundleCardContext context);
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

    public TelegramInputBundleCard Render(TelegramInputBundle bundle, TelegramInputBundleCardContext context)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(context);
        TelegramInputBundleCardBehavior behavior = TelegramInputBundleCardBehavior.Resolve(bundle, context);
        return new TelegramInputBundleCard(BuildText(bundle, behavior), BuildButtons(bundle, behavior));
    }

    private string BuildText(TelegramInputBundle bundle, TelegramInputBundleCardBehavior behavior)
    {
        StringBuilder builder = new();
        builder.AppendLine(bundle.Status is TelegramInputBundleStatus.Capturing
            ? bundle.HasContent ? "Input ready" : "Add input"
            : behavior.StatusText);
        if (!string.IsNullOrWhiteSpace(bundle.SessionId))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Session: {bundle.SessionName} ({ShortId(bundle.SessionId)})");
        }

        builder.AppendLine(CultureInfo.InvariantCulture, $"Action: {behavior.PrimaryActionText}");
        int autoDispatchAfterSeconds = _options.Value.AutoDispatchAfterSeconds;
        if (bundle.Status is TelegramInputBundleStatus.Capturing && bundle.HasContent && autoDispatchAfterSeconds > 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Auto: {behavior.AutoDispatchActionText} after {autoDispatchAfterSeconds.ToString(CultureInfo.InvariantCulture)}s idle");
        }

        if (bundle.TextParts.Count > 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Text: {FormatCount(bundle.TextParts.Count, "part")}, {bundle.CombinedText.Length.ToString(CultureInfo.InvariantCulture)} chars");
        }

        if (bundle.Attachments.Count > 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Attachments: {FormatAttachmentSummary(bundle.Attachments)}");
        }

        string combinedText = bundle.CombinedText;
        if (!string.IsNullOrWhiteSpace(combinedText))
        {
            builder.AppendLine();
            builder.AppendLine("Preview:");
            builder.AppendLine(TrimPreview(combinedText, GetPreviewCharacters()));
        }

        if (!string.IsNullOrWhiteSpace(behavior.AdvisoryText))
        {
            builder.AppendLine();
            builder.AppendLine(behavior.AdvisoryText);
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<IReadOnlyList<TelegramReplyButton>> BuildButtons(
        TelegramInputBundle bundle,
        TelegramInputBundleCardBehavior behavior)
        => behavior.ActionRows
            .Select(row => row
                .Select(action => new TelegramReplyButton(
                    action.Label,
                    TelegramInputBundleCardBehavior.CallbackData(bundle, action.CallbackPrefix)))
                .ToArray())
            .ToArray();

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

    private static string FormatCount(int count, string singular)
        => count == 1
            ? $"1 {singular}"
            : $"{count.ToString(CultureInfo.InvariantCulture)} {singular}s";

    private static string FormatAttachmentSummary(IReadOnlyCollection<TelegramAttachmentDescriptor> attachments)
    {
        string count = FormatCount(attachments.Count, "file");
        string types = string.Join(
            ", ",
            attachments
                .Select(GetAttachmentType)
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(type => type, StringComparer.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(types) ? count : $"{count} ({types})";
    }

    private static string GetAttachmentType(TelegramAttachmentDescriptor attachment)
    {
        if (attachment.IsImage)
        {
            return "image";
        }

        if (string.IsNullOrWhiteSpace(attachment.ContentType))
        {
            return "file";
        }

        int slashIndex = attachment.ContentType.IndexOf('/', StringComparison.Ordinal);
        return slashIndex > 0 ? attachment.ContentType[..slashIndex] : attachment.ContentType;
    }
}
