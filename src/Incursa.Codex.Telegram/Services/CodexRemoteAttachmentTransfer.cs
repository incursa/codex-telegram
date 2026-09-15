namespace Incursa.Codex.Telegram.Services;

internal sealed record CodexRemoteAttachmentPayload(
    string FileName,
    string? ContentType,
    bool IsImage,
    string ContentBase64);

internal static class CodexRemoteAttachmentTransfer
{
    public const int MaximumAttachmentCount = 8;
    public const long MaximumAttachmentBytes = 16L * 1024 * 1024;
    public const long MaximumTotalBytes = 32L * 1024 * 1024;
    public const int MaximumEncodedPayloadBytes = 48 * 1024 * 1024;

    public static bool IsValidMetadata(CodexRemoteAttachmentPayload? attachment)
        => attachment is not null
            && !string.IsNullOrWhiteSpace(attachment.FileName)
            && attachment.FileName.Length <= 240
            && !attachment.FileName.Any(char.IsControl)
            && !attachment.FileName.Contains('/')
            && !attachment.FileName.Contains('\\')
            && (attachment.ContentType is null || attachment.ContentType.Length <= 160 && !attachment.ContentType.Any(char.IsControl))
            && !string.IsNullOrWhiteSpace(attachment.ContentBase64)
            && attachment.ContentBase64.Length <= MaximumEncodedPayloadBytes;

    public static bool TryDecode(
        IReadOnlyList<CodexRemoteAttachmentPayload>? attachments,
        out IReadOnlyList<(CodexRemoteAttachmentPayload Attachment, byte[] Content)> decoded)
    {
        decoded = [];
        if (attachments is null or { Count: 0 })
        {
            return true;
        }

        if (attachments.Count > MaximumAttachmentCount)
        {
            return false;
        }

        List<(CodexRemoteAttachmentPayload Attachment, byte[] Content)> values = new(attachments.Count);
        long totalBytes = 0;
        try
        {
            foreach (CodexRemoteAttachmentPayload attachment in attachments)
            {
                if (!IsValidMetadata(attachment))
                {
                    return false;
                }

                byte[] content = Convert.FromBase64String(attachment.ContentBase64);
                if (content.LongLength > MaximumAttachmentBytes || totalBytes > MaximumTotalBytes - content.LongLength)
                {
                    return false;
                }

                totalBytes += content.LongLength;
                values.Add((attachment, content));
            }
        }
        catch (FormatException)
        {
            return false;
        }

        decoded = values;
        return true;
    }
}
