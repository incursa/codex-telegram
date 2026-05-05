namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Local descriptor for a Telegram file downloaded for Codex input.
/// </summary>
/// <param name="FilePath">Local downloaded file path.</param>
/// <param name="FileName">User-facing file name.</param>
/// <param name="ContentType">Media type, when Telegram supplied one.</param>
/// <param name="IsImage">Whether the file should be treated as an image input.</param>
internal sealed record TelegramAttachmentDescriptor(
    string FilePath,
    string FileName,
    string? ContentType,
    bool IsImage);
