using System.Collections.Concurrent;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Classifies a queued Telegram message so filtering and compaction can make safe tradeoffs.
/// </summary>
internal enum CodexOutboundMessageKind
{
    /// <summary>
    /// Internal progress such as tool activity or command execution status.
    /// </summary>
    Progress,

    /// <summary>
    /// User-visible incremental Codex output.
    /// </summary>
    Update,

    /// <summary>
    /// User-visible failure output.
    /// </summary>
    Error,

    /// <summary>
    /// Terminal turn output.
    /// </summary>
    Completion,

    /// <summary>
    /// Bot-generated system notices, including local compaction notices.
    /// </summary>
    System,
}

/// <summary>
/// Delivery priority used when multiple Telegram destinations are ready at the same time.
/// </summary>
internal enum OutboundPriority
{
    /// <summary>
    /// Defer behind normal user-visible updates.
    /// </summary>
    Low = 0,

    /// <summary>
    /// Standard priority for ordinary live output.
    /// </summary>
    Normal = 10,

    /// <summary>
    /// Sends without waiting for the batching window.
    /// </summary>
    High = 20,

    /// <summary>
    /// Highest priority, used for errors and other urgent operator-facing notices.
    /// </summary>
    Critical = 30,
}

/// <summary>
/// Telegram message waiting for outbound rate-limited delivery.
/// </summary>
internal sealed record OutboundTelegramMessage
{
    /// <summary>
    /// Gets the unique queue item identifier used for diagnostics.
    /// </summary>
    public required string MessageId { get; init; }

    /// <summary>
    /// Gets the Telegram chat ID that should receive the message.
    /// </summary>
    public required long ChatId { get; init; }

    /// <summary>
    /// Gets the Telegram forum topic thread ID, when the destination is a topic.
    /// </summary>
    public int? MessageThreadId { get; init; }

    /// <summary>
    /// Gets the Codex session ID associated with the message.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the message kind used for filtering and compaction.
    /// </summary>
    public required CodexOutboundMessageKind Kind { get; init; }

    /// <summary>
    /// Gets the text to deliver after batching and chunking.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the UTC time when the source event was created.
    /// </summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// Gets the priority used by destination selection.
    /// </summary>
    public OutboundPriority Priority { get; init; } = OutboundPriority.Normal;
}

/// <summary>
/// Identifies one Telegram delivery destination.
/// </summary>
/// <param name="ChatId">Telegram chat ID.</param>
/// <param name="MessageThreadId">Telegram forum topic thread ID, when present.</param>
internal readonly record struct TelegramDestinationKey(long ChatId, int? MessageThreadId)
{
    /// <summary>
    /// Converts the queue key to the shared Telegram conversation value object.
    /// </summary>
    /// <returns>A conversation scope with the same chat and topic.</returns>
    public TelegramConversationScope ToConversationScope()
        => new(ChatId, MessageThreadId);
}

/// <summary>
/// Identifies the per-chat send budget shared by all topics in one Telegram chat.
/// </summary>
/// <param name="ChatId">Telegram chat ID.</param>
internal readonly record struct TelegramSendBudgetKey(long ChatId);

/// <summary>
/// Snapshot of all pending outbound Telegram work.
/// </summary>
/// <param name="PendingDestinationCount">Number of chat/topic destinations with pending work.</param>
/// <param name="PendingMessageCount">Number of unprepared messages still buffered.</param>
/// <param name="PendingChunkCount">Number of prepared Telegram chunks still waiting to send.</param>
/// <param name="PendingCharacterCount">Approximate pending text character count.</param>
/// <param name="OldestWaitingDestination">Oldest destination waiting for delivery.</param>
/// <param name="OldestFirstPendingUtc">Creation time of the oldest pending message.</param>
/// <param name="GlobalBackoffUntilUtc">Global retry backoff end, when active.</param>
/// <param name="Destinations">Per-destination queue details.</param>
internal sealed record TelegramOutboundQueueStatus(
    int PendingDestinationCount,
    int PendingMessageCount,
    int PendingChunkCount,
    int PendingCharacterCount,
    TelegramDestinationKey? OldestWaitingDestination,
    DateTimeOffset? OldestFirstPendingUtc,
    DateTimeOffset? GlobalBackoffUntilUtc,
    IReadOnlyList<TelegramOutboundDestinationStatus> Destinations);

/// <summary>
/// Snapshot of pending outbound Telegram work for one chat/topic destination.
/// </summary>
/// <param name="ChatId">Telegram chat ID.</param>
/// <param name="MessageThreadId">Telegram forum topic thread ID, when present.</param>
/// <param name="SessionId">Most recent Codex session ID associated with the destination.</param>
/// <param name="PendingMessageCount">Number of unprepared buffered messages.</param>
/// <param name="PendingChunkCount">Number of prepared Telegram chunks.</param>
/// <param name="PendingCharacterCount">Approximate pending text character count.</param>
/// <param name="FirstPendingUtc">Creation time of the first pending message.</param>
/// <param name="LastEnqueuedUtc">UTC time when this destination last received a queue item.</param>
/// <param name="ChatBackoffUntilUtc">Per-chat retry backoff end, when active.</param>
/// <param name="LastSentUtc">UTC time when this destination last sent a Telegram message.</param>
internal sealed record TelegramOutboundDestinationStatus(
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

/// <summary>
/// Queue abstraction for Codex output that must be rate-limited before Telegram delivery.
/// </summary>
internal interface IOutboundTelegramQueue
{
    /// <summary>
    /// Adds a message to the outbound Telegram queue.
    /// </summary>
    /// <param name="message">Message to enqueue.</param>
    /// <param name="cancellationToken">Cancellation token for request aborts.</param>
    /// <returns>A completed value task after the message is accepted or discarded by configuration.</returns>
    ValueTask EnqueueAsync(OutboundTelegramMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a point-in-time queue status snapshot.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for request aborts.</param>
    /// <returns>Current outbound queue status.</returns>
    Task<TelegramOutboundQueueStatus> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Sends a prepared Telegram text chunk to the Telegram API.
/// </summary>
internal interface IOutboundTelegramMessageSender
{
    /// <summary>
    /// Sends text to one Telegram conversation.
    /// </summary>
    /// <param name="conversation">Telegram destination.</param>
    /// <param name="text">Prepared text chunk.</param>
    /// <param name="cancellationToken">Cancellation token for request aborts.</param>
    /// <returns>A task that completes after the Telegram API call finishes.</returns>
    Task SendTextMessageAsync(TelegramConversationScope conversation, string text, CancellationToken cancellationToken);
}

/// <summary>
/// Exception raised when Telegram reports that outbound sends are being rate limited.
/// </summary>
internal sealed class TelegramOutboundRateLimitException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TelegramOutboundRateLimitException"/> class.
    /// </summary>
    /// <param name="message">Diagnostic exception message.</param>
    /// <param name="retryAfter">Telegram retry-after duration, when supplied by the API.</param>
    /// <param name="innerException">Original Telegram exception.</param>
    public TelegramOutboundRateLimitException(string message, TimeSpan? retryAfter, Exception? innerException = null)
        : base(message, innerException)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets Telegram's requested retry delay, when provided.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Background scheduler that batches, chunks, and rate-limits live Codex output for Telegram.
/// </summary>
internal sealed class OutboundTelegramScheduler : BackgroundService, IOutboundTelegramQueue
{
    private const int TelegramGroupChatIdUpperBound = -1;
    private const int GlobalSendBudgetWindowSeconds = 1;
    private const int DefaultRateLimitBackoffSeconds = 5;
    private const int SchedulerFailureDelaySeconds = 1;
    private const string TurnFinishedMarker = "~~ fin ~~";
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

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundTelegramScheduler"/> class.
    /// </summary>
    /// <param name="sender">Sender used for prepared Telegram chunks.</param>
    /// <param name="chunker">Text chunker used to stay below Telegram message limits.</param>
    /// <param name="timeProvider">Clock used for deterministic rate-limit tests.</param>
    /// <param name="options">Live outbound scheduler options.</param>
    /// <param name="logger">Logger for send failures and compaction.</param>
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <summary>
    /// Attempts to send the next ready Telegram chunk.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for shutdown.</param>
    /// <returns><see langword="true"/> when a chunk was sent; otherwise <see langword="false"/>.</returns>
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
            ApplyBackoff(pending.Value.Destination.ChatId, GetChatInterval(pending.Value.Destination.ChatId, options), global: false);
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

    /// <inheritdoc />
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

                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Max(TelegramOutboundLimits.MinFlushIntervalMilliseconds, _options.FlushIntervalMilliseconds)),
                    _timeProvider,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Telegram outbound scheduler failed while processing pending messages.");
                await Task.Delay(TimeSpan.FromSeconds(SchedulerFailureDelaySeconds), _timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    // Never-sent destinations go first so one busy topic cannot starve a newly followed topic.
    private DestinationBuffer? SelectNextBuffer(DateTimeOffset now, TelegramOutboundOptions options)
        => _buffers.Values
            .Where(buffer => buffer.HasPending)
            .Where(buffer => IsBatchReady(buffer, now, options))
            .Where(buffer => IsChatAllowed(buffer.Destination.ChatId, now, options))
            .OrderBy(buffer => GetLastSentUtc(buffer) is null ? 0 : 1)
            .ThenBy(buffer => GetLastSentUtc(buffer) ?? DateTimeOffset.MinValue)
            .ThenBy(buffer => buffer.FirstPendingUtc)
            .ThenByDescending(buffer => buffer.HighestPriority)
            .ThenBy(buffer => buffer.LastEnqueuedUtc)
            .ThenBy(buffer => buffer.Destination.ChatId)
            .ThenBy(buffer => buffer.Destination.MessageThreadId ?? 0)
            .FirstOrDefault();

    private DateTimeOffset? GetLastSentUtc(DestinationBuffer buffer)
        => GetBudget(buffer.Destination.ChatId).LastSentUtc ?? buffer.LastSentUtc;

    private bool IsBatchReady(DestinationBuffer buffer, DateTimeOffset now, TelegramOutboundOptions options)
    {
        if (buffer.HasPreparedChunks || buffer.HighestPriority >= OutboundPriority.High)
        {
            // Prepared chunks must drain in order, and urgent updates should not wait for batching.
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
        DateTimeOffset until = _timeProvider.GetUtcNow() + (retryAfter ?? TimeSpan.FromSeconds(DefaultRateLimitBackoffSeconds));
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
        DateTimeOffset cutoff = now - TimeSpan.FromSeconds(GlobalSendBudgetWindowSeconds);
        while (_globalSendTimestamps.Count > 0 && _globalSendTimestamps.Peek() <= cutoff)
        {
            _globalSendTimestamps.Dequeue();
        }
    }

    private static TimeSpan GetChatInterval(long chatId, TelegramOutboundOptions options)
        => chatId <= TelegramGroupChatIdUpperBound
            ? TimeSpan.FromSeconds(options.GroupMinimumSendIntervalSeconds)
            : TimeSpan.FromSeconds(options.PrivateMinimumSendIntervalSeconds);

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset right)
        => left is null || right > left.Value ? right : left;

    private readonly record struct PendingSend(TelegramDestinationKey Destination, string Text);

    private sealed class BudgetState
    {
        /// <summary>
        /// Gets or sets the last successful send time for a Telegram chat.
        /// </summary>
        public DateTimeOffset? LastSentUtc { get; set; }

        /// <summary>
        /// Gets or sets the active retry backoff boundary for a Telegram chat.
        /// </summary>
        public DateTimeOffset? BackoffUntilUtc { get; set; }
    }

    /// <summary>
    /// Holds unprepared messages and prepared chunks for a single Telegram destination.
    /// </summary>
    private sealed class DestinationBuffer
    {
        private readonly List<PendingOutboundItem> _messages = [];
        private readonly Queue<string> _chunks = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="DestinationBuffer"/> class.
        /// </summary>
        /// <param name="destination">Telegram destination represented by this buffer.</param>
        public DestinationBuffer(TelegramDestinationKey destination)
        {
            Destination = destination;
        }

        /// <summary>
        /// Gets the Telegram destination represented by this buffer.
        /// </summary>
        public TelegramDestinationKey Destination { get; }

        /// <summary>
        /// Gets the most recent non-empty Codex session ID associated with buffered messages.
        /// </summary>
        public string? SessionId { get; private set; }

        /// <summary>
        /// Gets the source creation time for the first pending message.
        /// </summary>
        public DateTimeOffset? FirstPendingUtc { get; private set; }

        /// <summary>
        /// Gets the last time a message was added to this buffer.
        /// </summary>
        public DateTimeOffset? LastEnqueuedUtc { get; private set; }

        /// <summary>
        /// Gets the last successful send time for this destination.
        /// </summary>
        public DateTimeOffset? LastSentUtc { get; private set; }

        /// <summary>
        /// Gets the number of buffered messages that have not yet been formatted into chunks.
        /// </summary>
        public int PendingMessageCount => _messages.Count;

        /// <summary>
        /// Gets the number of formatted chunks waiting to be sent.
        /// </summary>
        public int PendingChunkCount => _chunks.Count;

        /// <summary>
        /// Gets the approximate pending text character count.
        /// </summary>
        public int PendingCharacterCount => _messages.Sum(message => message.Text.Length) + _chunks.Sum(chunk => chunk.Length);

        /// <summary>
        /// Gets a value indicating whether this buffer already has prepared Telegram chunks.
        /// </summary>
        public bool HasPreparedChunks => _chunks.Count > 0;

        /// <summary>
        /// Gets a value indicating whether this buffer has any work left to send.
        /// </summary>
        public bool HasPending => _messages.Count > 0 || _chunks.Count > 0;

        /// <summary>
        /// Gets the highest priority among unprepared messages.
        /// </summary>
        public OutboundPriority HighestPriority
            => _messages.Count == 0 ? OutboundPriority.Normal : _messages.Max(message => message.Priority);

        /// <summary>
        /// Adds one outbound message to this destination buffer.
        /// </summary>
        /// <param name="message">Message to buffer.</param>
        /// <param name="now">Current scheduler time.</param>
        public void Enqueue(OutboundTelegramMessage message, DateTimeOffset now)
        {
            _messages.Add(new PendingOutboundItem(message.MessageId, message.SessionId, message.Kind, message.Text, message.CreatedUtc, message.Priority));
            FirstPendingUtc ??= message.CreatedUtc == default ? now : message.CreatedUtc;
            LastEnqueuedUtc = now;
            SessionId = string.IsNullOrWhiteSpace(message.SessionId) ? SessionId : message.SessionId;
        }

        /// <summary>
        /// Gets the next prepared chunk, preparing chunks from buffered messages if necessary.
        /// </summary>
        /// <param name="chunker">Telegram text chunker.</param>
        /// <param name="maxMessageChars">Maximum text length for each Telegram send.</param>
        /// <returns>The next chunk to send, or <see langword="null"/> when nothing can be prepared.</returns>
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

        /// <summary>
        /// Marks the current prepared chunk as sent.
        /// </summary>
        /// <param name="sentAt">UTC time of the successful send.</param>
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

        /// <summary>
        /// Removes lower-value buffered messages until the destination is inside local memory limits.
        /// </summary>
        /// <param name="maxChars">Maximum pending character count.</param>
        /// <param name="maxMessages">Maximum pending message count.</param>
        /// <returns>Number of messages removed and summarized.</returns>
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

                // Prefer dropping progress and ordinary updates; high-priority errors/completions are the
                // most important evidence when a Telegram destination is overloaded.
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
            int standaloneIndex = _messages.FindIndex(IsStandaloneMessage);
            if (standaloneIndex == 0)
            {
                PendingOutboundItem standalone = _messages[0];
                _messages.RemoveAt(0);
                return FormatBatchItem(standalone.Text);
            }

            int count = standaloneIndex > 0 ? standaloneIndex : _messages.Count;
            List<PendingOutboundItem> messages = _messages.GetRange(0, count);
            _messages.RemoveRange(0, count);
            return FormatBatch(messages);
        }

        private static string FormatBatch(IReadOnlyList<PendingOutboundItem> messages)
        {
            if (messages.Count == 1)
            {
                return messages[0].Text;
            }

            List<string> lines = [];
            for (int index = 0; index < messages.Count; index++)
            {
                if (index > 0)
                {
                    lines.Add(string.Empty);
                }

                lines.Add(FormatBatchItem(messages[index].Text));
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string FormatBatchItem(string value)
            => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

        private static bool IsStandaloneMessage(PendingOutboundItem message)
            => string.Equals(FormatBatchItem(message.Text), TurnFinishedMarker, StringComparison.Ordinal);
    }

    /// <summary>
    /// Immutable outbound item stored inside a destination buffer.
    /// </summary>
    /// <param name="MessageId">Queue item identifier.</param>
    /// <param name="SessionId">Associated Codex session ID.</param>
    /// <param name="Kind">Message kind for compaction.</param>
    /// <param name="Text">Text to include in a batch.</param>
    /// <param name="CreatedUtc">Source creation time.</param>
    /// <param name="Priority">Delivery priority.</param>
    private sealed record PendingOutboundItem(
        string MessageId,
        string? SessionId,
        CodexOutboundMessageKind Kind,
        string Text,
        DateTimeOffset CreatedUtc,
        OutboundPriority Priority);
}
