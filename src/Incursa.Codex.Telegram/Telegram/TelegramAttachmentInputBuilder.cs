using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Telegram;

internal static class TelegramAttachmentInputBuilder
{
    public static IReadOnlyList<CodexInputItem> BuildInputItems(
        string? text,
        IReadOnlyList<TelegramAttachmentDescriptor>? attachments)
    {
        List<CodexInputItem> items = new();

        if (!string.IsNullOrWhiteSpace(text))
        {
            items.Add(new CodexTextInput { Text = text.Trim() });
        }

        foreach (TelegramAttachmentDescriptor attachment in attachments ?? [])
        {
            if (attachment.IsImage)
            {
                items.Add(new CodexLocalImageInput { Path = attachment.FilePath });
                continue;
            }

            items.Add(new CodexMentionInput
            {
                Name = string.IsNullOrWhiteSpace(attachment.FileName)
                    ? Path.GetFileName(attachment.FilePath)
                    : attachment.FileName,
                Path = attachment.FilePath,
            });
        }

        return items;
    }
}
