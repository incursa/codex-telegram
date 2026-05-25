namespace Incursa.Codex.Telegram.Telegram;

internal enum TelegramMessageAuthor
{
    User,
    Bot,
}

internal sealed record TelegramMessageAcknowledgement(
    TelegramConversationScope Conversation,
    int MessageId,
    string? BusinessConnectionId);

internal sealed record TelegramMessageContextRecord(
    TelegramConversationScope Conversation,
    int MessageId,
    TelegramMessageAuthor Author,
    string Text,
    DateTimeOffset CreatedUtc);

internal sealed record TelegramReplyContext(
    int MessageId,
    TelegramMessageAuthor Author,
    string Text,
    IReadOnlyList<TelegramMessageContextRecord> PriorMessages);

internal interface ITelegramMessageContextStore
{
    ValueTask RecordAsync(TelegramMessageContextRecord message, CancellationToken cancellationToken);

    Task<TelegramReplyContext?> ResolveReplyContextAsync(
        TelegramConversationScope conversation,
        int messageId,
        TelegramMessageAuthor author,
        string? fallbackText,
        CancellationToken cancellationToken);
}

internal sealed class TelegramMessageContextStore : ITelegramMessageContextStore
{
    private const int MaxMessagesPerConversation = 80;
    private const int MaxStoredTextLength = 2_000;

    private readonly object _gate = new();
    private readonly Dictionary<TelegramConversationScope, List<TelegramMessageContextRecord>> _messagesByConversation = [];

    public ValueTask RecordAsync(TelegramMessageContextRecord message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string text = Normalize(message.Text, MaxStoredTextLength);
        if (string.IsNullOrWhiteSpace(text))
        {
            return ValueTask.CompletedTask;
        }

        TelegramMessageContextRecord normalized = message with { Text = text };
        lock (_gate)
        {
            List<TelegramMessageContextRecord> messages = GetMessages(message.Conversation);
            int existingIndex = messages.FindIndex(candidate => candidate.MessageId == message.MessageId);
            if (existingIndex >= 0)
            {
                messages[existingIndex] = normalized;
            }
            else
            {
                messages.Add(normalized);
            }

            Trim(messages);
        }

        return ValueTask.CompletedTask;
    }

    public Task<TelegramReplyContext?> ResolveReplyContextAsync(
        TelegramConversationScope conversation,
        int messageId,
        TelegramMessageAuthor author,
        string? fallbackText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_messagesByConversation.TryGetValue(conversation, out List<TelegramMessageContextRecord>? messages))
            {
                return Task.FromResult(CreateFallbackContext(messageId, author, fallbackText));
            }

            int matchIndex = messages.FindIndex(message => message.MessageId == messageId);
            TelegramMessageContextRecord? match = matchIndex >= 0 ? messages[matchIndex] : null;
            string? text = match?.Text ?? Normalize(fallbackText, MaxStoredTextLength);
            if (string.IsNullOrWhiteSpace(text))
            {
                return Task.FromResult<TelegramReplyContext?>(null);
            }

            TelegramMessageAuthor resolvedAuthor = match?.Author ?? author;
            if (resolvedAuthor == TelegramMessageAuthor.Bot && TelegramReplyContextTextClassifier.ShouldSuppressBotReplyContext(text))
            {
                return Task.FromResult<TelegramReplyContext?>(null);
            }

            TelegramMessageContextRecord[] priorMessages = ResolvePriorMessages(messages, matchIndex, messageId);

            return Task.FromResult<TelegramReplyContext?>(new TelegramReplyContext(
                messageId,
                resolvedAuthor,
                text,
                priorMessages));
        }
    }

    private static TelegramReplyContext? CreateFallbackContext(
        int messageId,
        TelegramMessageAuthor author,
        string? fallbackText)
    {
        string text = Normalize(fallbackText, MaxStoredTextLength);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (author == TelegramMessageAuthor.Bot && TelegramReplyContextTextClassifier.ShouldSuppressBotReplyContext(text))
        {
            return null;
        }

        return new TelegramReplyContext(messageId, author, text, []);
    }

    private List<TelegramMessageContextRecord> GetMessages(TelegramConversationScope conversation)
    {
        if (!_messagesByConversation.TryGetValue(conversation, out List<TelegramMessageContextRecord>? messages))
        {
            messages = [];
            _messagesByConversation[conversation] = messages;
        }

        return messages;
    }

    private static TelegramMessageContextRecord[] ResolvePriorMessages(
        List<TelegramMessageContextRecord> messages,
        int matchIndex,
        int messageId)
    {
        IEnumerable<TelegramMessageContextRecord> prior = matchIndex >= 0
            ? messages.Take(matchIndex)
            : messages.Where(message => message.MessageId < messageId);

        return prior
            .Where(message => !ShouldSuppressBotReplyContext(message))
            .TakeLast(2)
            .ToArray();
    }

    private static bool ShouldSuppressBotReplyContext(TelegramMessageContextRecord message)
        => message.Author == TelegramMessageAuthor.Bot
            && TelegramReplyContextTextClassifier.ShouldSuppressBotReplyContext(message.Text);

    private static void Trim(List<TelegramMessageContextRecord> messages)
    {
        if (messages.Count <= MaxMessagesPerConversation)
        {
            return;
        }

        messages.RemoveRange(0, messages.Count - MaxMessagesPerConversation);
    }

    private static string Normalize(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }
}

internal sealed class NullTelegramMessageContextStore : ITelegramMessageContextStore
{
    public static NullTelegramMessageContextStore Instance { get; } = new();

    private NullTelegramMessageContextStore()
    {
    }

    public ValueTask RecordAsync(TelegramMessageContextRecord message, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public Task<TelegramReplyContext?> ResolveReplyContextAsync(
        TelegramConversationScope conversation,
        int messageId,
        TelegramMessageAuthor author,
        string? fallbackText,
        CancellationToken cancellationToken)
        => Task.FromResult<TelegramReplyContext?>(null);
}

internal static class TelegramReplyContextTextClassifier
{
    public static bool ShouldSuppressBotReplyContext(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string normalized = text.Trim().ReplaceLineEndings("\n");

        return normalized.StartsWith("--- live card:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("--- codex error", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("--- codex status", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Codex failed", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Codex interrupted", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Codex is working", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Codex finished", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Queued for ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Queued Plan mode request", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Queued for next turn", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Session status card", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("Input bundle", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\nSession:", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("\nMode:", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("\nLatest:", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\nState:", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("\nQueue count:", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\nTelegram delivery:", StringComparison.OrdinalIgnoreCase);
    }
}
