using System.Collections.Concurrent;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

public enum CodexOutboundMessageKind
{
    Progress,
    Update,
    Error,
    Completion,
    System,
}

public enum OutboundPriority
{
    Low = 0,
    Normal = 10,
    High = 20,
    Critical = 30,
}

public sealed record OutboundTelegramMessage
{
    public required string MessageId { get; init; }

    public required long ChatId { get; init; }

    public int? MessageThreadId { get; init; }

    public required string SessionId { get; init; }

    public required CodexOutboundMessageKind Kind { get; init; }

    public required string Text { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public OutboundPriority Priority { get; init; } = OutboundPriority.Normal;
}

public readonly record struct TelegramDestinationKey(long ChatId, int? MessageThreadId)
{
    public TelegramConversationScope ToConversationScope()
        => new(ChatId, MessageThreadId);
}

public readonly record struct TelegramSendBudgetKey(long ChatId);

public sealed record TelegramOutboundQueueStatus(
    int PendingDestinationCount,
    int PendingMessageCount,
    int PendingChunkCount,
    int PendingCharacterCount,
    TelegramDestinationKey? OldestWaitingDestination,
    DateTimeOffset? OldestFirstPendingUtc,
    DateTimeOffset? GlobalBackoffUntilUtc,
    IReadOnlyList<TelegramOutboundDestinationStatus> Destinations);

public sealed record TelegramOutboundDestinationStatus(
    long ChatId,
    int? MessageThreadId,
    string? SessionId,
    int PendingMessageCount,
    int PendingChunkCount,
    int PendingCharacterCount,
    DateTimeOffset? FirstPendingUtc,
    DateTimeOffset? LastEnqueuedUtc,
    DateTimeOffset? ChatBackoffUntilUtc,
    DateTimeOffset? LastSentUtc);

public interface IOutboundTelegramQueue
{
    ValueTask EnqueueAsync(OutboundTelegramMessage message, CancellationToken cancellationToken);

    Task<TelegramOutboundQueueStatus> GetStatusAsync(CancellationToken cancellationToken);
}

internal interface IOutboundTelegramMessageSender
{
    Task SendTextMessageAsync(TelegramConversationScope conversation, string text, CancellationToken cancellationToken);
}

internal sealed class TelegramOutboundRateLimitException : Exception
{
    public TelegramOutboundRateLimitException(string message, TimeSpan? retryAfter, Exception? innerException = null)
        : base(message, innerException)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}

internal sealed class OutboundTelegramScheduler : BackgroundService, IOutboundTelegramQueue
{
    private readonly ConcurrentDictionary<TelegramDestinationKey, DestinationBuffer> _buffers = new();
    private readonly ConcurrentDictionary<TelegramSendBudgetKey, BudgetState> _chatBudgets = new();
    private readonly Queue<DateTimeOffset> _globalSendTimestamps = new();
    private readonly object _gate = new();
    private readonly IOutboundTelegramMessageSender _sender;
    private readonly TelegramMessageChunker _chunker;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboundTelegramScheduler> _logger;
    private TelegramOutboundOptions _options;
    private DateTimeOffset? _globalBackoffUntilUtc;

    public OutboundTelegramScheduler(
        IOutboundTelegramMessageSender sender,
        TelegramMessageChunker chunker,
        TimeProvider timeProvider,
        IOptionsMonitor<TelegramOutboundOptions> options,
        ILogger<OutboundTelegramScheduler> logger)
    {
        _sender = sender;
        _chunker = chunker;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options.CurrentValue;
        options.OnChange(updated => _options = updated);
    }

    public ValueTask EnqueueAsync(OutboundTelegramMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TelegramOutboundOptions options = _options;
        if (!options.Enabled || string.IsNullOrWhiteSpace(message.Text))
        {
            return ValueTask.CompletedTask;
        }

        if (message.Kind == CodexOutboundMessageKind.Progress && !options.IncludeProgressMessages)
        {
            return ValueTask.CompletedTask;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        OutboundTelegramMessage normalized = message with
        {
            CreatedUtc = message.CreatedUtc == default ? now : message.CreatedUtc,
            Text = message.Text.Trim(),
        };

        TelegramDestinationKey destination = new(normalized.ChatId, normalized.MessageThreadId);
        lock (_gate)
        {
            DestinationBuffer buffer = _buffers.GetOrAdd(destination, _ => new DestinationBuffer(destination));
            buffer.Enqueue(normalized, now);
            CompactIfNeeded(buffer, options);
        }

        return ValueTask.CompletedTask;
    }

    public Task<TelegramOutboundQueueStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            IReadOnlyList<DestinationBuffer> pending = _buffers.Values
                .Where(buffer => buffer.HasPending)
                .OrderBy(buffer => buffer.FirstPendingUtc)
                .ThenBy(buffer => buffer.Destination.ChatId)
                .ThenBy(buffer => buffer.Destination.MessageThreadId ?? 0)
                .ToArray();

            List<TelegramOutboundDestinationStatus> destinations = new(pending.Count);
            foreach (DestinationBuffer buffer in pending)
            {
                BudgetState budget = GetBudget(buffer.Destination.ChatId);
                destinations.Add(new TelegramOutboundDestinationStatus(
                    buffer.Destination.ChatId,
                    buffer.Destination.MessageThreadId,
                    buffer.SessionId,
                    buffer.PendingMessageCount,
                    buffer.PendingChunkCount,
                    buffer.PendingCharacterCount,
                    buffer.FirstPendingUtc,
                    buffer.LastEnqueuedUtc,
                    budget.BackoffUntilUtc > now ? budget.BackoffUntilUtc : null,
                    buffer.LastSentUtc));
            }

            DestinationBuffer? oldest = pending.FirstOrDefault();
            return Task.FromResult(new TelegramOutboundQueueStatus(
                pending.Count,
                pending.Sum(buffer => buffer.PendingMessageCount),
                pending.Sum(buffer => buffer.PendingChunkCount),
                pending.Sum(buffer => buffer.PendingCharacterCount),
                oldest?.Destination,
                oldest?.FirstPendingUtc,
                _globalBackoffUntilUtc > now ? _globalBackoffUntilUtc : null,
                destinations));
        }
    }

    internal async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        PendingSend? pending = null;
        TelegramOutboundOptions options = _options;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (!options.Enabled || IsGlobalBlocked(now, options))
            {
                return false;
            }

            DestinationBuffer? buffer = SelectNextBuffer(now, options);
            if (buffer is null)
            {
                return false;
            }

            string? chunk = buffer.PeekOrPrepareChunk(_chunker, options.MaxMessageChars);
            if (string.IsNullOrWhiteSpace(chunk))
            {
                _buffers.TryRemove(buffer.Destination, out _);
                return false;
            }

            pending = new PendingSend(buffer.Destination, chunk);
        }

        try
        {
            await _sender.SendTextMessageAsync(pending.Value.Destination.ToConversationScope(), pending.Value.Text, cancellationToken).ConfigureAwait(false);
        }
        catch (TelegramOutboundRateLimitException exception)
        {
            ApplyBackoff(pending.Value.Destination.ChatId, exception.RetryAfter, global: false);
            _logger.LogWarning(
                exception,
                "Telegram outbound send was rate limited for chat {ChatId}; retry after {RetryAfter}.",
                pending.Value.Destination.ChatId,
                exception.RetryAfter);
            return false;
        }
        catch (Exception exception)
        {
            ApplyBackoff(pending.Value.Destination.ChatId, TimeSpan.FromSeconds(options.GroupMinimumSendIntervalSeconds), global: false);
            _logger.LogWarning(
                exception,
                "Telegram outbound send failed for chat {ChatId} topic {MessageThreadId}; message remains queued.",
                pending.Value.Destination.ChatId,
                pending.Value.Destination.MessageThreadId);
            return false;
        }

        lock (_gate)
        {
            DateTimeOffset sentAt = _timeProvider.GetUtcNow();
            DestinationBuffer? buffer = _buffers.TryGetValue(pending.Value.Destination, out DestinationBuffer? current) ? current : null;
            buffer?.CompleteCurrentChunk(sentAt);
            if (buffer is not null && !buffer.HasPending)
            {
                _buffers.TryRemove(buffer.Destination, out _);
            }

            BudgetState budget = GetBudget(pending.Value.Destination.ChatId);
            budget.LastSentUtc = sentAt;
            _globalSendTimestamps.Enqueue(sentAt);
            TrimGlobalSendTimestamps(sentAt);
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool processed;
                do
                {
                    processed = await ProcessNextAsync(stoppingToken).ConfigureAwait(false);
                }
                while (processed && !stoppingToken.IsCancellationRequested);

                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50, _options.FlushIntervalMilliseconds)), _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Telegram outbound scheduler failed while processing pending messages.");
                await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private DestinationBuffer? SelectNextBuffer(DateTimeOffset now, TelegramOutboundOptions options)
        => _buffers.Values
            .Where(buffer => buffer.HasPending)
            .Where(buffer => IsBatchReady(buffer, now, options))
            .Where(buffer => IsChatAllowed(buffer.Destination.ChatId, now, options))
            .OrderBy(buffer => buffer.LastSentUtc is null ? 0 : 1)
            .ThenBy(buffer => buffer.LastSentUtc ?? DateTimeOffset.MinValue)
            .ThenBy(buffer => buffer.FirstPendingUtc)
            .ThenByDescending(buffer => buffer.HighestPriority)
            .ThenBy(buffer => buffer.LastEnqueuedUtc)
            .ThenBy(buffer => buffer.Destination.ChatId)
            .ThenBy(buffer => buffer.Destination.MessageThreadId ?? 0)
            .FirstOrDefault();

    private bool IsBatchReady(DestinationBuffer buffer, DateTimeOffset now, TelegramOutboundOptions options)
    {
        if (buffer.HasPreparedChunks || buffer.HighestPriority >= OutboundPriority.High)
        {
            return true;
        }

        return buffer.FirstPendingUtc is null
            || now - buffer.FirstPendingUtc.Value >= TimeSpan.FromSeconds(options.BatchWindowSeconds);
    }

    private bool IsChatAllowed(long chatId, DateTimeOffset now, TelegramOutboundOptions options)
    {
        BudgetState budget = GetBudget(chatId);
        if (budget.BackoffUntilUtc > now)
        {
            return false;
        }

        if (budget.LastSentUtc is null)
        {
            return true;
        }

        TimeSpan interval = GetChatInterval(chatId, options);
        return now - budget.LastSentUtc.Value >= interval;
    }

    private bool IsGlobalBlocked(DateTimeOffset now, TelegramOutboundOptions options)
    {
        if (_globalBackoffUntilUtc > now)
        {
            return true;
        }

        TrimGlobalSendTimestamps(now);
        return _globalSendTimestamps.Count >= options.GlobalMaxMessagesPerSecond;
    }

    private void ApplyBackoff(long chatId, TimeSpan? retryAfter, bool global)
    {
        DateTimeOffset until = _timeProvider.GetUtcNow() + (retryAfter ?? TimeSpan.FromSeconds(5));
        lock (_gate)
        {
            if (global)
            {
                _globalBackoffUntilUtc = Max(_globalBackoffUntilUtc, until);
                return;
            }

            BudgetState budget = GetBudget(chatId);
            budget.BackoffUntilUtc = Max(budget.BackoffUntilUtc, until);
        }
    }

    private void CompactIfNeeded(DestinationBuffer buffer, TelegramOutboundOptions options)
    {
        if (buffer.PendingCharacterCount <= options.MaxBufferedCharsPerDestination
            && buffer.PendingMessageCount <= options.MaxBufferedMessagesPerDestination)
        {
            return;
        }

        int compacted = buffer.Compact(options.MaxBufferedCharsPerDestination, options.MaxBufferedMessagesPerDestination);
        if (compacted > 0)
        {
            _logger.LogInformation(
                "Compacted {CompactedCount} Telegram outbound messages for chat {ChatId} topic {MessageThreadId}.",
                compacted,
                buffer.Destination.ChatId,
                buffer.Destination.MessageThreadId);
        }
    }

    private BudgetState GetBudget(long chatId)
        => _chatBudgets.GetOrAdd(new TelegramSendBudgetKey(chatId), _ => new BudgetState());

    private void TrimGlobalSendTimestamps(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - TimeSpan.FromSeconds(1);
        while (_globalSendTimestamps.Count > 0 && _globalSendTimestamps.Peek() <= cutoff)
        {
            _globalSendTimestamps.Dequeue();
        }
    }

    private static TimeSpan GetChatInterval(long chatId, TelegramOutboundOptions options)
        => chatId < 0
            ? TimeSpan.FromSeconds(options.GroupMinimumSendIntervalSeconds)
            : TimeSpan.FromSeconds(options.PrivateMinimumSendIntervalSeconds);

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset right)
        => left is null || right > left.Value ? right : left;

    private readonly record struct PendingSend(TelegramDestinationKey Destination, string Text);

    private sealed class BudgetState
    {
        public DateTimeOffset? LastSentUtc { get; set; }

        public DateTimeOffset? BackoffUntilUtc { get; set; }
    }

    private sealed class DestinationBuffer
    {
        private readonly List<PendingOutboundItem> _messages = [];
        private readonly Queue<string> _chunks = new();

        public DestinationBuffer(TelegramDestinationKey destination)
        {
            Destination = destination;
        }

        public TelegramDestinationKey Destination { get; }

        public string? SessionId { get; private set; }

        public DateTimeOffset? FirstPendingUtc { get; private set; }

        public DateTimeOffset? LastEnqueuedUtc { get; private set; }

        public DateTimeOffset? LastSentUtc { get; private set; }

        public int PendingMessageCount => _messages.Count;

        public int PendingChunkCount => _chunks.Count;

        public int PendingCharacterCount => _messages.Sum(message => message.Text.Length) + _chunks.Sum(chunk => chunk.Length);

        public bool HasPreparedChunks => _chunks.Count > 0;

        public bool HasPending => _messages.Count > 0 || _chunks.Count > 0;

        public OutboundPriority HighestPriority
            => _messages.Count == 0 ? OutboundPriority.Normal : _messages.Max(message => message.Priority);

        public void Enqueue(OutboundTelegramMessage message, DateTimeOffset now)
        {
            _messages.Add(new PendingOutboundItem(message.MessageId, message.SessionId, message.Kind, message.Text, message.CreatedUtc, message.Priority));
            FirstPendingUtc ??= message.CreatedUtc == default ? now : message.CreatedUtc;
            LastEnqueuedUtc = now;
            SessionId = string.IsNullOrWhiteSpace(message.SessionId) ? SessionId : message.SessionId;
        }

        public string? PeekOrPrepareChunk(TelegramMessageChunker chunker, int maxMessageChars)
        {
            if (_chunks.Count == 0 && _messages.Count > 0)
            {
                string text = FormatNextSend();
                foreach (string chunk in chunker.Split(text, maxMessageChars))
                {
                    _chunks.Enqueue(chunk);
                }
            }

            return _chunks.Count == 0 ? null : _chunks.Peek();
        }

        public void CompleteCurrentChunk(DateTimeOffset sentAt)
        {
            LastSentUtc = sentAt;
            if (_chunks.Count > 0)
            {
                _chunks.Dequeue();
            }

            if (!HasPending)
            {
                FirstPendingUtc = null;
                LastEnqueuedUtc = null;
            }
        }

        public int Compact(int maxChars, int maxMessages)
        {
            int compacted = 0;
            while ((_messages.Count > maxMessages || PendingCharacterCount > maxChars) && _messages.Count > 1)
            {
                int index = _messages.FindIndex(message => message.Kind == CodexOutboundMessageKind.Progress);
                if (index < 0)
                {
                    index = _messages.FindIndex(message => message.Priority < OutboundPriority.High);
                }

                if (index < 0 || _messages.Count <= 1)
                {
                    break;
                }

                _messages.RemoveAt(index);
                compacted++;
            }

            if (compacted > 0)
            {
                _messages.Insert(0, new PendingOutboundItem(
                    "compacted-" + Guid.NewGuid().ToString("n"),
                    SessionId,
                    CodexOutboundMessageKind.System,
                    $"... {compacted} older outbound updates compacted to protect local memory.",
                    FirstPendingUtc ?? DateTimeOffset.UtcNow,
                    OutboundPriority.Normal));
            }

            return compacted;
        }

        private string FormatNextSend()
        {
            List<PendingOutboundItem> messages = [.. _messages];
            _messages.Clear();
            return FormatBatch(messages);
        }

        private static string FormatBatch(IReadOnlyList<PendingOutboundItem> messages)
        {
            if (messages.Count == 1)
            {
                return messages[0].Text;
            }

            string? sessionId = messages.FirstOrDefault(message => !string.IsNullOrWhiteSpace(message.SessionId))?.SessionId;
            string header = string.IsNullOrWhiteSpace(sessionId) ? "[Codex]" : $"[{Shorten(sessionId!)}]";
            List<string> lines =
            [
                header,
                $"{messages.Count} updates",
                string.Empty,
            ];

            for (int index = 0; index < messages.Count; index++)
            {
                if (index > 0)
                {
                    lines.Add(string.Empty);
                    lines.Add("---");
                    lines.Add(string.Empty);
                }

                lines.Add(NormalizeForBatchItem(messages[index].Text));
            }

            lines.Add(string.Empty);
            lines.Add("Use /tail 100 for more detail.");
            return string.Join(Environment.NewLine, lines);
        }

        private static string Shorten(string value)
            => value.Length <= 16 ? value : value[..8];

        private static string NormalizeForBatchItem(string value)
        {
            string line = value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? value.Trim();
            return line.Length <= 240 ? line : line[..237] + "...";
        }
    }

    private sealed record PendingOutboundItem(
        string MessageId,
        string? SessionId,
        CodexOutboundMessageKind Kind,
        string Text,
        DateTimeOffset CreatedUtc,
        OutboundPriority Priority);
}
