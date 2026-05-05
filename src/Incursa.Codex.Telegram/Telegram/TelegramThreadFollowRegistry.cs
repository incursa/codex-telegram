using System.Collections.Concurrent;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Tracks which Telegram conversations should receive live output for Codex threads.
/// </summary>
internal interface ITelegramThreadFollowRegistry
{
    /// <summary>
    /// Makes a Telegram conversation follow a Codex thread.
    /// </summary>
    /// <param name="conversation">Telegram conversation that should receive output.</param>
    /// <param name="threadId">Codex thread ID to follow.</param>
    void FollowThread(TelegramConversationScope conversation, string threadId);

    /// <summary>
    /// Gets Telegram conversations following a Codex thread.
    /// </summary>
    /// <param name="threadId">Codex thread ID.</param>
    /// <returns>Telegram conversations currently following the thread.</returns>
    IReadOnlyCollection<TelegramConversationScope> GetTargets(string threadId);
}

/// <summary>
/// In-memory implementation of Telegram conversation follow state.
/// </summary>
internal sealed class TelegramThreadFollowRegistry : ITelegramThreadFollowRegistry
{
    private readonly ConcurrentDictionary<TelegramConversationScope, string> _threadByConversation = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<TelegramConversationScope, byte>> _conversationsByThreadId = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
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
            // A Telegram conversation follows exactly one thread at a time for live output; otherwise
            // an old session could keep leaking updates into a conversation after /use or /new.
            previousConversations.TryRemove(conversation, out _);
        }

        _threadByConversation[conversation] = threadId;
        ConcurrentDictionary<TelegramConversationScope, byte> conversations = _conversationsByThreadId.GetOrAdd(threadId, _ => new ConcurrentDictionary<TelegramConversationScope, byte>());
        conversations[conversation] = 0;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<TelegramConversationScope> GetTargets(string threadId)
    {
        if (!_conversationsByThreadId.TryGetValue(threadId, out ConcurrentDictionary<TelegramConversationScope, byte>? conversations))
        {
            return Array.Empty<TelegramConversationScope>();
        }

        return conversations.Keys.ToArray();
    }
}
