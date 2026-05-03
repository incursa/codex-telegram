namespace Incursa.Codex.Telegram.Telegram;

public sealed record TelegramAttachmentDescriptor(
    string FilePath,
    string FileName,
    string? ContentType,
    bool IsImage);
