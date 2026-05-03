using System.Collections.Concurrent;

namespace Incursa.Codex.Telegram.Telegram;

public interface ITelegramThreadFollowRegistry
{
    void FollowThread(TelegramConversationScope conversation, string threadId);

    IReadOnlyCollection<TelegramConversationScope> GetTargets(string threadId);
}

internal sealed class TelegramThreadFollowRegistry : ITelegramThreadFollowRegistry
{
    private readonly ConcurrentDictionary<TelegramConversationScope, string> _threadByConversation = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<TelegramConversationScope, byte>> _conversationsByThreadId = new(StringComparer.OrdinalIgnoreCase);

    public void FollowThread(TelegramConversationScope conversation, string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        if (_threadByConversation.TryGetValue(conversation, out string? previousThreadId)
            && !string.Equals(previousThreadId, threadId, StringComparison.OrdinalIgnoreCase)
            && _conversationsByThreadId.TryGetValue(previousThreadId, out ConcurrentDictionary<TelegramConversationScope, byte>? previousConversations))
        {
            previousConversations.TryRemove(conversation, out _);
        }

        _threadByConversation[conversation] = threadId;
        ConcurrentDictionary<TelegramConversationScope, byte> conversations = _conversationsByThreadId.GetOrAdd(threadId, _ => new ConcurrentDictionary<TelegramConversationScope, byte>());
        conversations[conversation] = 0;
    }

    public IReadOnlyCollection<TelegramConversationScope> GetTargets(string threadId)
    {
        if (!_conversationsByThreadId.TryGetValue(threadId, out ConcurrentDictionary<TelegramConversationScope, byte>? conversations))
        {
            return Array.Empty<TelegramConversationScope>();
        }

        return conversations.Keys.ToArray();
    }
}
