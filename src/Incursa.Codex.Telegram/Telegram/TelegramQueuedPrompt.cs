namespace Incursa.Codex.Telegram.Telegram;

public sealed record TelegramQueuedPrompt(
    string Id,
    long UserId,
    long ChatId,
    string SessionId,
    string SessionName,
    string Text,
    DateTimeOffset EnqueuedAt,
    int? MessageThreadId = null,
    IReadOnlyList<TelegramAttachmentDescriptor>? Attachments = null)
{
    public TelegramConversationScope ConversationScope => new(ChatId, MessageThreadId);
}
