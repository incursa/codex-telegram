namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// User prompt waiting for a Codex session to become available.
/// </summary>
/// <param name="Id">Queue item identifier.</param>
/// <param name="UserId">Telegram user ID that submitted the prompt.</param>
/// <param name="ChatId">Telegram chat ID where the prompt was submitted.</param>
/// <param name="SessionId">Target Codex session ID.</param>
/// <param name="SessionName">Display name for the target Codex session.</param>
/// <param name="Text">Prompt text.</param>
/// <param name="EnqueuedAt">UTC time when the prompt was queued.</param>
/// <param name="MessageThreadId">Telegram forum topic thread ID, when the prompt came from a topic.</param>
/// <param name="Attachments">Telegram attachments to forward with the prompt.</param>
/// <param name="PlanMode">Whether the queued prompt should start as a plan-mode turn.</param>
/// <param name="TraceId">Diagnostic trace correlation ID associated with the queued prompt.</param>
internal sealed record TelegramQueuedPrompt(
    string Id,
    long UserId,
    long ChatId,
    string SessionId,
    string SessionName,
    string Text,
    DateTimeOffset EnqueuedAt,
    int? MessageThreadId = null,
    IReadOnlyList<TelegramAttachmentDescriptor>? Attachments = null,
    bool PlanMode = false,
    string? TraceId = null)
{
    /// <summary>
    /// Gets the Telegram conversation that owns this queued prompt.
    /// </summary>
    public TelegramConversationScope ConversationScope => new(ChatId, MessageThreadId);
}
