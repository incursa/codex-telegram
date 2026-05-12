using System.Collections.Concurrent;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Classifies outbound file delivery for Telegram-native sends.
/// </summary>
internal enum TelegramOutboundFileKind
{
    Photo,
    Document,
}

/// <summary>
/// Telegram file payload that Codex explicitly asked the bot to show.
/// </summary>
internal sealed record OutboundTelegramFile
{
    public required TelegramOutboundFileKind Kind { get; init; }

    public required string Path { get; init; }

    public string? FileName { get; init; }

    public string? Caption { get; init; }

    public string? ContentType { get; init; }
}

/// <summary>
/// Telegram chat action aligned with the next outbound send.
/// </summary>
internal enum TelegramChatActivity
{
    Typing,
    UploadPhoto,
    UploadDocument,
}

/// <summary>
/// Simple message reaction states used for low-noise Telegram status.
/// </summary>
internal enum TelegramMessageReactionKind
{
    Accepted,
    Completed,
    Failed,
}

/// <summary>
/// Message reaction request for a Telegram source message.
/// </summary>
internal sealed record TelegramMessageReaction(
    TelegramConversationScope Conversation,
    int MessageId,
    TelegramMessageReactionKind Kind);

internal sealed record TelegramTurnReactionTarget(
    TelegramConversationScope Conversation,
    int MessageId);

internal interface ITelegramTurnReactionRegistry
{
    void Register(string threadId, string turnId, TelegramConversationScope conversation, int messageId);

    TelegramTurnReactionTarget? TryTake(string threadId, string turnId);
}

internal sealed class TelegramTurnReactionRegistry : ITelegramTurnReactionRegistry
{
    private readonly ConcurrentDictionary<TurnKey, TelegramTurnReactionTarget> _targets = new();

    public void Register(string threadId, string turnId, TelegramConversationScope conversation, int messageId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId) || messageId <= 0)
        {
            return;
        }

        _targets[new TurnKey(threadId, turnId)] = new TelegramTurnReactionTarget(conversation, messageId);
    }

    public TelegramTurnReactionTarget? TryTake(string threadId, string turnId)
        => _targets.TryRemove(new TurnKey(threadId, turnId), out TelegramTurnReactionTarget? target)
            ? target
            : null;

    private readonly record struct TurnKey(string ThreadId, string TurnId);
}
