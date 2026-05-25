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
    /// Sends without waiting for the normal-priority send window.
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
    /// Gets the Codex turn ID associated with the message, when the source event supplied one.
    /// </summary>
    public string? TurnId { get; init; }

    /// <summary>
    /// Gets the trace correlation ID associated with this outbound message, when known.
    /// </summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// Gets the message kind used for filtering and compaction.
    /// </summary>
    public required CodexOutboundMessageKind Kind { get; init; }

    /// <summary>
    /// Gets the text to deliver after queueing and chunking.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the Telegram-native file payload to send as a standalone item, when present.
    /// </summary>
    public OutboundTelegramFile? File { get; init; }

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
/// Prepared Telegram delivery emitted by the outbound observable stream.
/// </summary>
internal sealed class OutboundTelegramDelivery
{
    private readonly TaskCompletionSource<OutboundTelegramDeliveryResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancellation = new();

    internal OutboundTelegramDelivery(
        TelegramDestinationKey destination,
        string? text,
        OutboundTelegramFile? file,
        TelegramDebugMessageContext? debugContext,
        string? traceId,
        string? sessionId,
        string? turnId,
        TelegramOutboundOptions options)
    {
        Destination = destination;
        Text = text;
        File = file;
        DebugContext = debugContext;
        TraceId = traceId;
        SessionId = sessionId;
        TurnId = turnId;
        Options = options;
    }

    /// <summary>
    /// Gets the Telegram conversation that should receive this delivery.
    /// </summary>
    public TelegramConversationScope Conversation => Destination.ToConversationScope();

    /// <summary>
    /// Gets the text payload to send, when this delivery is a text chunk.
    /// </summary>
    public string? Text { get; }

    /// <summary>
    /// Gets the Telegram-native file payload to send, when this delivery is a file item.
    /// </summary>
    public OutboundTelegramFile? File { get; }

    /// <summary>
    /// Gets optional diagnostic context for Telegram debug preambles.
    /// </summary>
    public TelegramDebugMessageContext? DebugContext { get; }

    /// <summary>
    /// Gets a token that is cancelled when the scheduler times out this delivery.
    /// </summary>
    public CancellationToken CancellationToken => _cancellation.Token;

    internal TelegramDestinationKey Destination { get; }

    internal string? TraceId { get; }

    internal string? SessionId { get; }

    internal string? TurnId { get; }

    internal TelegramOutboundOptions Options { get; }

    internal Task<OutboundTelegramDeliveryResult> Completion => _completion.Task;

    /// <summary>
    /// Reports that the subscriber successfully delivered the Telegram message.
    /// </summary>
    public void Complete()
        => _completion.TrySetResult(OutboundTelegramDeliveryResult.Completed);

    /// <summary>
    /// Reports that the subscriber could not deliver the Telegram message.
    /// </summary>
    /// <param name="exception">Delivery failure.</param>
    public void Fail(Exception exception)
        => _completion.TrySetResult(OutboundTelegramDeliveryResult.Failed(exception));

    internal void SetResult(OutboundTelegramDeliveryResult result)
        => _completion.TrySetResult(result);

    internal Task CancelAsync()
        => _cancellation.CancelAsync();
}

/// <summary>
/// Result reported by a subscriber after handling an observable outbound delivery.
/// </summary>
internal sealed record OutboundTelegramDeliveryResult(bool Succeeded, Exception? Exception)
{
    /// <summary>
    /// Successful delivery result.
    /// </summary>
    public static OutboundTelegramDeliveryResult Completed { get; } = new(true, null);

    /// <summary>
    /// Creates a failed delivery result.
    /// </summary>
    /// <param name="exception">Delivery failure.</param>
    /// <returns>Failed delivery result.</returns>
    public static OutboundTelegramDeliveryResult Failed(Exception exception)
        => new(false, exception);
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
/// Observable stream of ready-to-send Telegram deliveries.
/// </summary>
internal interface IOutboundTelegramDeliveryStream
{
    /// <summary>
    /// Observes prepared Telegram deliveries after queueing, chunking, batching, and rate limits are applied.
    /// </summary>
    /// <returns>Observable delivery stream.</returns>
    IObservable<OutboundTelegramDelivery> ObserveDeliveries();
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
    /// <param name="debugContext">Diagnostic source context for optional Telegram debug preambles.</param>
    /// <returns>A task that completes after the Telegram API call finishes.</returns>
    Task SendTextMessageAsync(
        TelegramConversationScope conversation,
        string text,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext = null);

    /// <summary>
    /// Sends a Telegram-native file payload to one conversation.
    /// </summary>
    /// <param name="conversation">Telegram destination.</param>
    /// <param name="file">Prepared file payload.</param>
    /// <param name="cancellationToken">Cancellation token for request aborts.</param>
    /// <param name="debugContext">Diagnostic source context for optional Telegram debug preambles.</param>
    /// <returns>A task that completes after the Telegram API call finishes.</returns>
    Task SendFileMessageAsync(
        TelegramConversationScope conversation,
        OutboundTelegramFile file,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext = null);
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
/// Exception raised when one Telegram outbound send exceeds the configured scheduler timeout.
/// </summary>
internal sealed class TelegramOutboundSendTimeoutException : TimeoutException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TelegramOutboundSendTimeoutException"/> class.
    /// </summary>
    /// <param name="timeout">Configured send timeout.</param>
    public TelegramOutboundSendTimeoutException(TimeSpan timeout)
        : base($"Telegram outbound send did not complete within {timeout}.")
    {
        Timeout = timeout;
    }

    /// <summary>
    /// Gets the timeout that was exceeded.
    /// </summary>
    public TimeSpan Timeout { get; }
}

/// <summary>
/// Background scheduler that chunks and rate-limits live Codex output for Telegram.
/// </summary>
internal sealed class OutboundTelegramScheduler : BackgroundService, IOutboundTelegramQueue, IOutboundTelegramDeliveryStream
{
    private const int TelegramGroupChatIdUpperBound = -1;
    private const int GlobalSendBudgetWindowSeconds = 1;
    private const int DefaultRateLimitBackoffSeconds = 5;
    private const int SchedulerFailureDelaySeconds = 1;
    private readonly ConcurrentDictionary<TelegramDestinationKey, DestinationBuffer> _buffers = new();
    private readonly ConcurrentDictionary<TelegramSendBudgetKey, BudgetState> _chatBudgets = new();
    private readonly Queue<DateTimeOffset> _globalSendTimestamps = new();
    private readonly object _gate = new();
    private readonly IOutboundTelegramMessageSender _sender;
    private readonly TelegramMessageChunker _chunker;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboundTelegramScheduler> _logger;
    private readonly ITelegramDebugTraceStore _traceStore;
    private TelegramOutboundOptions _options;
    private TaskCompletionSource<bool> _workAvailableSignal = CreateWorkAvailableSignal();
    private DateTimeOffset? _globalBackoffUntilUtc;
    private int _activeDeliveryStreamSubscriptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundTelegramScheduler"/> class.
    /// </summary>
    /// <param name="sender">Sender used for prepared Telegram chunks.</param>
    /// <param name="chunker">Text chunker used to stay below Telegram message limits.</param>
    /// <param name="timeProvider">Clock used for deterministic rate-limit tests.</param>
    /// <param name="options">Live outbound scheduler options.</param>
    /// <param name="logger">Logger for send failures and compaction.</param>
    /// <param name="traceStore">Optional trace store for delivery diagnostics.</param>
    public OutboundTelegramScheduler(
        IOutboundTelegramMessageSender sender,
        TelegramMessageChunker chunker,
        TimeProvider timeProvider,
        IOptionsMonitor<TelegramOutboundOptions> options,
        ILogger<OutboundTelegramScheduler> logger,
        ITelegramDebugTraceStore? traceStore = null)
    {
        _sender = sender;
        _chunker = chunker;
        _timeProvider = timeProvider;
        _logger = logger;
        _traceStore = traceStore ?? NullTelegramDebugTraceStore.Instance;
        _options = options.CurrentValue;
        options.OnChange(updated => _options = updated);
    }

    /// <inheritdoc />
    public async ValueTask EnqueueAsync(OutboundTelegramMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TelegramOutboundOptions options = _options;
        if (!options.Enabled || (string.IsNullOrWhiteSpace(message.Text) && message.File is null))
        {
            return;
        }

        if (message.Kind == CodexOutboundMessageKind.Progress && !options.IncludeProgressMessages)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        OutboundTelegramMessage normalized = message with
        {
            CreatedUtc = message.CreatedUtc == default ? now : message.CreatedUtc,
            Text = message.Text.Trim(),
            File = NormalizeFile(message.File),
            TraceId = string.IsNullOrWhiteSpace(message.TraceId)
                ? _traceStore.TryGetTraceIdForTurn(message.SessionId, message.TurnId)
                : message.TraceId,
        };

        TelegramDestinationKey destination = new(normalized.ChatId, normalized.MessageThreadId);
        int chunkCount = normalized.File is not null
            ? 1
            : _chunker.Split(normalized.Text, options.MaxMessageChars).Count;
        lock (_gate)
        {
            DestinationBuffer buffer = _buffers.GetOrAdd(destination, _ => new DestinationBuffer(destination));
            buffer.Enqueue(normalized, now);
            CompactIfNeeded(buffer, options);
            SignalWorkAvailableLocked();
        }

        await _traceStore.RecordAsync(
            new TelegramDebugTraceEvent(
                normalized.TraceId ?? string.Empty,
                now,
                "telegram.outbound.enqueue",
                SessionId: normalized.SessionId,
                TurnId: normalized.TurnId,
                ChatId: normalized.ChatId,
                MessageThreadId: normalized.MessageThreadId,
                Direction: "outbound",
                TextLength: normalized.Text.Length,
                OutboundQueueItemId: normalized.MessageId,
                ChunkCount: chunkCount,
                Metadata: new Dictionary<string, string>
                {
                    ["kind"] = normalized.Kind.ToString(),
                },
                TextBody: normalized.Text,
                Source: "TelegramOutbound"),
            cancellationToken).ConfigureAwait(false);

        return;
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

    /// <inheritdoc />
    public IObservable<OutboundTelegramDelivery> ObserveDeliveries()
        => new OutboundTelegramDeliveryObservable(this);

    /// <summary>
    /// Attempts to send the next ready Telegram chunk.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for shutdown.</param>
    /// <returns><see langword="true"/> when a chunk was sent; otherwise <see langword="false"/>.</returns>
    internal async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        OutboundTelegramDelivery? delivery = TryTakeReadyDelivery(cancellationToken);
        if (delivery is null)
        {
            return false;
        }

        OutboundTelegramDeliveryResult result = await SendDeliveryAsync(delivery, cancellationToken).ConfigureAwait(false);
        return await CompleteDeliveryAsync(delivery, result, cancellationToken).ConfigureAwait(false);
    }

    private Task RecordOutboundTraceAsync(
        OutboundTelegramDelivery pending,
        string kind,
        string? error,
        CancellationToken cancellationToken)
        => _traceStore.RecordAsync(
            new TelegramDebugTraceEvent(
                pending.TraceId ?? string.Empty,
                _timeProvider.GetUtcNow(),
                kind,
                SessionId: pending.SessionId,
                TurnId: pending.TurnId,
                ChatId: pending.Destination.ChatId,
                MessageThreadId: pending.Destination.MessageThreadId,
                Direction: "outbound",
                ChunkLength: pending.Text?.Length ?? pending.File?.Caption?.Length,
                Error: error,
                Metadata: BuildPendingSendTraceMetadata(pending),
                TextBody: pending.Text ?? pending.File?.Caption,
                Source: "TelegramOutbound"),
            cancellationToken);

    private static IReadOnlyDictionary<string, string>? BuildPendingSendTraceMetadata(OutboundTelegramDelivery pending)
    {
        if (pending.File is null)
        {
            return null;
        }

        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["fileKind"] = pending.File.Kind.ToString(),
            ["filePath"] = pending.File.Path,
        };
        if (!string.IsNullOrWhiteSpace(pending.File.FileName))
        {
            metadata["fileName"] = pending.File.FileName;
        }

        if (!string.IsNullOrWhiteSpace(pending.File.ContentType))
        {
            metadata["contentType"] = pending.File.ContentType;
        }

        return metadata;
    }

    private OutboundTelegramDelivery? TryTakeReadyDelivery(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TelegramOutboundOptions options = _options;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (!options.Enabled || IsGlobalBlocked(now, options))
            {
                return null;
            }

            DestinationBuffer? buffer = SelectNextBuffer(now, options);
            if (buffer is null)
            {
                return null;
            }

            PreparedOutboundChunk? chunk = buffer.PeekOrPrepareChunk(_chunker, options.MaxMessageChars);
            if (chunk is null || !chunk.HasPayload)
            {
                _buffers.TryRemove(buffer.Destination, out _);
                return null;
            }

            return new OutboundTelegramDelivery(
                buffer.Destination,
                chunk.Text,
                chunk.File,
                chunk.DebugContext,
                chunk.TraceId,
                chunk.SessionId,
                chunk.TurnId,
                options);
        }
    }

    private async Task<OutboundTelegramDeliveryResult> SendDeliveryAsync(
        OutboundTelegramDelivery delivery,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendWithTimeoutAsync(delivery, delivery.Options, cancellationToken).ConfigureAwait(false);
            return OutboundTelegramDeliveryResult.Completed;
        }
        catch (Exception exception)
        {
            return OutboundTelegramDeliveryResult.Failed(exception);
        }
    }

    private async Task<bool> CompleteDeliveryAsync(
        OutboundTelegramDelivery delivery,
        OutboundTelegramDeliveryResult result,
        CancellationToken cancellationToken)
    {
        if (!result.Succeeded)
        {
            Exception exception = result.Exception ?? new InvalidOperationException("Telegram outbound delivery failed without an exception.");
            if (exception is TelegramOutboundRateLimitException rateLimitException)
            {
                ApplyBackoff(delivery.Destination.ChatId, rateLimitException.RetryAfter, global: false);
                await RecordOutboundTraceAsync(delivery, "telegram.outbound.rate_limited", rateLimitException.Message, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    rateLimitException,
                    "Telegram outbound send was rate limited for chat {ChatId}; retry after {RetryAfter}.",
                    delivery.Destination.ChatId,
                    rateLimitException.RetryAfter);
                return false;
            }

            if (exception is TelegramOutboundSendTimeoutException timeoutException)
            {
                ApplyBackoff(delivery.Destination.ChatId, Max(GetChatInterval(delivery.Destination.ChatId, delivery.Options), timeoutException.Timeout), global: false);
                await RecordOutboundTraceAsync(delivery, "telegram.outbound.timeout", timeoutException.Message, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    timeoutException,
                    "Telegram outbound send timed out for chat {ChatId} topic {MessageThreadId}; message remains queued and other destinations can continue.",
                    delivery.Destination.ChatId,
                    delivery.Destination.MessageThreadId);
                return false;
            }

            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw exception;
            }

            ApplyBackoff(delivery.Destination.ChatId, GetChatInterval(delivery.Destination.ChatId, delivery.Options), global: false);
            await RecordOutboundTraceAsync(delivery, "telegram.outbound.failed", exception.Message, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                exception,
                "Telegram outbound send failed for chat {ChatId} topic {MessageThreadId}; message remains queued.",
                delivery.Destination.ChatId,
                delivery.Destination.MessageThreadId);
            return false;
        }

        lock (_gate)
        {
            DateTimeOffset sentAt = _timeProvider.GetUtcNow();
            DestinationBuffer? buffer = _buffers.TryGetValue(delivery.Destination, out DestinationBuffer? current) ? current : null;
            buffer?.CompleteCurrentChunk(sentAt);
            if (buffer is not null && !buffer.HasPending)
            {
                _buffers.TryRemove(buffer.Destination, out _);
            }

            BudgetState budget = GetBudget(delivery.Destination.ChatId);
            budget.LastSentUtc = sentAt;
            _globalSendTimestamps.Enqueue(sentAt);
            TrimGlobalSendTimestamps(sentAt);
        }

        await RecordOutboundTraceAsync(delivery, "telegram.outbound.sent", null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task SendWithTimeoutAsync(OutboundTelegramDelivery pending, TelegramOutboundOptions options, CancellationToken cancellationToken)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(1, options.SendTimeoutSeconds));
        using CancellationTokenSource sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task sendTask = pending.File is not null
            ? _sender.SendFileMessageAsync(pending.Conversation, pending.File, sendCancellation.Token, pending.DebugContext)
            : _sender.SendTextMessageAsync(pending.Conversation, pending.Text ?? string.Empty, sendCancellation.Token, pending.DebugContext);
        Task timeoutTask = Task.Delay(timeout, _timeProvider, cancellationToken);

        Task completed = await Task.WhenAny(sendTask, timeoutTask).ConfigureAwait(false);
        if (ReferenceEquals(completed, sendTask))
        {
            await sendTask.ConfigureAwait(false);
            return;
        }

        await timeoutTask.ConfigureAwait(false);
        await sendCancellation.CancelAsync().ConfigureAwait(false);
        ObserveTimedOutSend(sendTask, pending);
        throw new TelegramOutboundSendTimeoutException(timeout);
    }

    private void ObserveTimedOutSend(Task sendTask, OutboundTelegramDelivery pending)
    {
        _ = sendTask.ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                {
                    _logger.LogDebug(
                        task.Exception,
                        "Timed-out Telegram outbound send later faulted for chat {ChatId} topic {MessageThreadId}.",
                        pending.Destination.ChatId,
                        pending.Destination.MessageThreadId);
                    return;
                }

                if (task.IsCanceled)
                {
                    _logger.LogDebug(
                        "Timed-out Telegram outbound send was cancelled for chat {ChatId} topic {MessageThreadId}.",
                        pending.Destination.ChatId,
                        pending.Destination.MessageThreadId);
                    return;
                }

                _logger.LogWarning(
                    "Timed-out Telegram outbound send later completed for chat {ChatId} topic {MessageThreadId}; the queued chunk may be retried because Telegram acceptance could not be confirmed before the timeout.",
                    pending.Destination.ChatId,
                    pending.Destination.MessageThreadId);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using OutboundTelegramDeliverySender sender = new(this, stoppingToken);
                using IDisposable subscription = ObserveDeliveries().Subscribe(sender);
                await sender.Completion.WaitAsync(stoppingToken).ConfigureAwait(false);
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

    private async Task RunDeliveryStreamAsync(IObserver<OutboundTelegramDelivery> observer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                OutboundTelegramDelivery? delivery = TryTakeReadyDelivery(cancellationToken);
                if (delivery is null)
                {
                    await WaitForWorkOrDelayAsync(GetFlushDelay(), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                observer.OnNext(delivery);
                OutboundTelegramDeliveryResult result = await WaitForDeliveryCompletionAsync(delivery, cancellationToken).ConfigureAwait(false);
                await CompleteDeliveryAsync(delivery, result, cancellationToken).ConfigureAwait(false);
            }

            observer.OnCompleted();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            observer.OnCompleted();
        }
        catch (Exception exception)
        {
            observer.OnError(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _activeDeliveryStreamSubscriptions, 0);
        }
    }

    private TimeSpan GetFlushDelay()
        => TimeSpan.FromMilliseconds(Math.Max(TelegramOutboundLimits.MinFlushIntervalMilliseconds, _options.FlushIntervalMilliseconds));

    private async Task<OutboundTelegramDeliveryResult> WaitForDeliveryCompletionAsync(
        OutboundTelegramDelivery delivery,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(1, delivery.Options.SendTimeoutSeconds));
        Task timeoutTask = Task.Delay(timeout, _timeProvider, cancellationToken);
        Task<OutboundTelegramDeliveryResult> completionTask = delivery.Completion;
        Task completed = await Task.WhenAny(completionTask, timeoutTask).ConfigureAwait(false);
        if (ReferenceEquals(completed, completionTask))
        {
            return await completionTask.ConfigureAwait(false);
        }

        await timeoutTask.ConfigureAwait(false);
        await delivery.CancelAsync().ConfigureAwait(false);
        ObserveTimedOutDelivery(delivery);
        return OutboundTelegramDeliveryResult.Failed(new TelegramOutboundSendTimeoutException(timeout));
    }

    private void ObserveTimedOutDelivery(OutboundTelegramDelivery delivery)
    {
        _ = delivery.Completion.ContinueWith(
            task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger.LogDebug(
                        task.Exception,
                        "Timed-out Telegram outbound observable delivery later faulted for chat {ChatId} topic {MessageThreadId}.",
                        delivery.Destination.ChatId,
                        delivery.Destination.MessageThreadId);
                    return;
                }

                if (!task.Result.Succeeded)
                {
                    _logger.LogDebug(
                        task.Result.Exception,
                        "Timed-out Telegram outbound observable delivery later reported failure for chat {ChatId} topic {MessageThreadId}.",
                        delivery.Destination.ChatId,
                        delivery.Destination.MessageThreadId);
                    return;
                }

                _logger.LogWarning(
                    "Timed-out Telegram outbound observable delivery later completed for chat {ChatId} topic {MessageThreadId}; the queued chunk may be retried because Telegram acceptance could not be confirmed before the timeout.",
                    delivery.Destination.ChatId,
                    delivery.Destination.MessageThreadId);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
            // Prepared chunks must drain in order, and urgent updates should not wait for the normal send window.
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
            RecordTraceWithoutBlocking(
                new TelegramDebugTraceEvent(
                    buffer.TraceId ?? string.Empty,
                    _timeProvider.GetUtcNow(),
                    "telegram.outbound.compacted",
                    SessionId: buffer.SessionId,
                    TurnId: buffer.TurnId,
                    ChatId: buffer.Destination.ChatId,
                    MessageThreadId: buffer.Destination.MessageThreadId,
                    Direction: "outbound",
                    CompactedCount: compacted));
            _logger.LogInformation(
                "Compacted {CompactedCount} Telegram outbound messages for chat {ChatId} topic {MessageThreadId}.",
                compacted,
                buffer.Destination.ChatId,
                buffer.Destination.MessageThreadId);
        }
    }

    private void RecordTraceWithoutBlocking(TelegramDebugTraceEvent evt)
    {
        Task traceTask;
        try
        {
            traceTask = _traceStore.RecordAsync(evt, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to start Telegram trace write for {TraceKind}.", evt.Kind);
            return;
        }

        if (traceTask.IsCompletedSuccessfully)
        {
            return;
        }

        _ = ObserveTraceWriteAsync(traceTask, evt.Kind);
    }

    private async Task ObserveTraceWriteAsync(Task traceTask, string kind)
    {
        try
        {
            await traceTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to write Telegram trace event {TraceKind}.", kind);
        }
    }

    private BudgetState GetBudget(long chatId)
        => _chatBudgets.GetOrAdd(new TelegramSendBudgetKey(chatId), _ => new BudgetState());

    private void SignalWorkAvailableLocked()
    {
        _workAvailableSignal.TrySetResult(true);
    }

    private async Task WaitForWorkOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Task wakeTask;
        lock (_gate)
        {
            wakeTask = _workAvailableSignal.Task;
        }

        using CancellationTokenSource delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task delayTask = Task.Delay(delay, _timeProvider, delayCancellation.Token);
        Task completed = await Task.WhenAny(wakeTask, delayTask).ConfigureAwait(false);
        if (ReferenceEquals(completed, delayTask))
        {
            await delayTask.ConfigureAwait(false);
            return;
        }

        await wakeTask.ConfigureAwait(false);
        delayCancellation.Cancel();
        try
        {
            await delayTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        lock (_gate)
        {
            if (ReferenceEquals(_workAvailableSignal.Task, wakeTask))
            {
                _workAvailableSignal = CreateWorkAvailableSignal();
            }
        }
    }

    private static TaskCompletionSource<bool> CreateWorkAvailableSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private static OutboundTelegramFile? NormalizeFile(OutboundTelegramFile? file)
        => file is null
            ? null
            : file with
            {
                Path = file.Path.Trim(),
                FileName = string.IsNullOrWhiteSpace(file.FileName) ? null : Path.GetFileName(file.FileName.Trim()),
                Caption = string.IsNullOrWhiteSpace(file.Caption) ? null : file.Caption.Trim(),
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType.Trim(),
            };

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset right)
        => left is null || right > left.Value ? right : left;

    private static TimeSpan Max(TimeSpan left, TimeSpan right)
        => left >= right ? left : right;

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
        private readonly Queue<PreparedOutboundChunk> _chunks = new();

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

        public string? TurnId { get; private set; }

        public string? TraceId { get; private set; }

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
        public int PendingCharacterCount
            => _messages.Sum(message => message.Text.Length + (message.File?.Caption?.Length ?? 0))
                + _chunks.Sum(chunk => (chunk.Text?.Length ?? 0) + (chunk.File?.Caption?.Length ?? 0));

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
            _messages.Add(new PendingOutboundItem(
                message.MessageId,
                message.SessionId,
                message.TurnId,
                message.TraceId,
                message.Kind,
                message.Text,
                message.File,
                message.CreatedUtc,
                message.Priority));
            FirstPendingUtc ??= message.CreatedUtc == default ? now : message.CreatedUtc;
            LastEnqueuedUtc = now;
            SessionId = string.IsNullOrWhiteSpace(message.SessionId) ? SessionId : message.SessionId;
            TurnId = string.IsNullOrWhiteSpace(message.TurnId) ? TurnId : message.TurnId;
            TraceId = string.IsNullOrWhiteSpace(message.TraceId) ? TraceId : message.TraceId;
        }

        /// <summary>
        /// Gets the next prepared chunk, preparing chunks from buffered messages if necessary.
        /// </summary>
        /// <param name="chunker">Telegram text chunker.</param>
        /// <param name="maxMessageChars">Maximum text length for each Telegram send.</param>
        /// <returns>The next chunk to send, or <see langword="null"/> when nothing can be prepared.</returns>
        public PreparedOutboundChunk? PeekOrPrepareChunk(TelegramMessageChunker chunker, int maxMessageChars)
        {
            if (_chunks.Count == 0 && _messages.Count > 0)
            {
                PreparedOutboundSend prepared = FormatNextSend();
                if (prepared.File is not null)
                {
                    _chunks.Enqueue(new PreparedOutboundChunk(prepared.Text, prepared.File, prepared.DebugContext, prepared.TraceId, prepared.SessionId, prepared.TurnId));
                }
                else
                {
                    foreach (string chunk in chunker.Split(prepared.Text ?? string.Empty, maxMessageChars))
                    {
                        _chunks.Enqueue(new PreparedOutboundChunk(chunk, null, prepared.DebugContext, prepared.TraceId, prepared.SessionId, prepared.TurnId));
                    }
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
            List<PendingOutboundItem> compactedItems = [];
            int compacted = 0;
            while ((_messages.Count > maxMessages || PendingCharacterCount > maxChars) && _messages.Count > 1)
            {
                int index = _messages.FindIndex(message => message.Kind == CodexOutboundMessageKind.Progress);
                if (index < 0)
                {
                    index = _messages.FindIndex(message => message.Priority < OutboundPriority.High && message.File is null);
                }

                if (index < 0 || _messages.Count <= 1)
                {
                    break;
                }

                // Prefer compacting progress and ordinary updates; high-priority errors/completions are the
                // most important evidence when a Telegram destination is overloaded.
                compactedItems.Add(_messages[index]);
                _messages.RemoveAt(index);
                compacted++;
            }

            if (compacted > 0)
            {
                string compactedText = BuildCompactedText(compactedItems, compacted);
                _messages.Insert(0, new PendingOutboundItem(
                    "compacted-" + Guid.NewGuid().ToString("n"),
                    SessionId,
                    ResolveSingleValue(compactedItems.Select(message => message.TurnId)),
                    ResolveSingleValue(compactedItems.Select(message => message.TraceId)),
                    CodexOutboundMessageKind.System,
                    compactedText,
                    null,
                    FirstPendingUtc ?? DateTimeOffset.UtcNow,
                    OutboundPriority.High));
            }

            return compacted;
        }

        private static string BuildCompactedText(IReadOnlyList<PendingOutboundItem> compactedItems, int compactedCount)
        {
            string compactedBody = FormatBatch(compactedItems);
            return string.Join(
                Environment.NewLine + Environment.NewLine,
                $"... {compactedCount} older outbound updates compacted to protect local memory.",
                compactedBody);
        }

        private PreparedOutboundSend FormatNextSend()
        {
            PendingOutboundItem message = _messages[0];
            _messages.RemoveAt(0);
            if (message.File is not null)
            {
                return new PreparedOutboundSend(
                    string.IsNullOrWhiteSpace(message.Text) ? message.File.Caption : message.Text,
                    message.File,
                    CreateDebugContext([message]),
                    message.TraceId,
                    message.SessionId,
                    message.TurnId);
            }

            return new PreparedOutboundSend(
                FormatBatchItem(message.Text),
                null,
                CreateDebugContext([message]),
                message.TraceId,
                message.SessionId,
                message.TurnId);
        }

        private static TelegramDebugMessageContext CreateDebugContext(IReadOnlyList<PendingOutboundItem> messages)
        {
            string? sessionId = ResolveSingleValue(messages.Select(message => message.SessionId));
            string? turnId = ResolveSingleValue(messages.Select(message => message.TurnId));
            string? kind = ResolveSingleValue(messages.Select(message => message.Kind.ToString()));
            string? traceId = ResolveSingleValue(messages.Select(message => message.TraceId));
            string? messageId = messages.Count == 1 ? messages[0].MessageId : null;
            return new TelegramDebugMessageContext(
                "outbound",
                sessionId,
                turnId,
                ActiveTurnId: null,
                kind,
                messageId,
                messages.Count,
                traceId);
        }

        private static string? ResolveSingleValue(IEnumerable<string?> values)
        {
            string? result = null;
            foreach (string? value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (result is null)
                {
                    result = value;
                    continue;
                }

                if (!string.Equals(result, value, StringComparison.Ordinal))
                {
                    return "mixed";
                }
            }

            return result;
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

    }

    /// <summary>
    /// Immutable outbound item stored inside a destination buffer.
    /// </summary>
    /// <param name="MessageId">Queue item identifier.</param>
    /// <param name="SessionId">Associated Codex session ID.</param>
    /// <param name="TurnId">Associated Codex turn ID.</param>
    /// <param name="TraceId">Associated trace correlation ID.</param>
    /// <param name="Kind">Message kind for compaction.</param>
    /// <param name="Text">Text to include in a batch.</param>
    /// <param name="File">Standalone Telegram file payload, when present.</param>
    /// <param name="CreatedUtc">Source creation time.</param>
    /// <param name="Priority">Delivery priority.</param>
    private sealed record PendingOutboundItem(
        string MessageId,
        string? SessionId,
        string? TurnId,
        string? TraceId,
        CodexOutboundMessageKind Kind,
        string Text,
        OutboundTelegramFile? File,
        DateTimeOffset CreatedUtc,
        OutboundPriority Priority);

    private sealed record PreparedOutboundSend(
        string? Text,
        OutboundTelegramFile? File,
        TelegramDebugMessageContext DebugContext,
        string? TraceId,
        string? SessionId,
        string? TurnId);

    private sealed record PreparedOutboundChunk(
        string? Text,
        OutboundTelegramFile? File,
        TelegramDebugMessageContext DebugContext,
        string? TraceId,
        string? SessionId,
        string? TurnId)
    {
        public bool HasPayload => File is not null || !string.IsNullOrWhiteSpace(Text);
    }

    private sealed class OutboundTelegramDeliveryObservable : IObservable<OutboundTelegramDelivery>
    {
        private readonly OutboundTelegramScheduler _scheduler;

        public OutboundTelegramDeliveryObservable(OutboundTelegramScheduler scheduler)
        {
            _scheduler = scheduler;
        }

        public IDisposable Subscribe(IObserver<OutboundTelegramDelivery> observer)
        {
            if (Interlocked.CompareExchange(ref _scheduler._activeDeliveryStreamSubscriptions, 1, 0) != 0)
            {
                observer.OnError(new InvalidOperationException("The outbound Telegram delivery stream already has an active subscriber."));
                return NoopDisposable.Instance;
            }

            CancellationTokenSource cancellation = new();
            Task streamTask = Task.Run(() => _scheduler.RunDeliveryStreamAsync(observer, cancellation.Token), CancellationToken.None);
            return new DeliveryStreamSubscription(cancellation, streamTask);
        }
    }

    private sealed class DeliveryStreamSubscription : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private int _disposed;

        public DeliveryStreamSubscription(CancellationTokenSource cancellation, Task streamTask)
        {
            _cancellation = cancellation;
            _ = streamTask.ContinueWith(
                _ => _cancellation.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cancellation.Cancel();
            }
        }
    }

    private sealed class OutboundTelegramDeliverySender : IObserver<OutboundTelegramDelivery>, IDisposable
    {
        private readonly OutboundTelegramScheduler _scheduler;
        private readonly CancellationToken _stoppingToken;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public OutboundTelegramDeliverySender(OutboundTelegramScheduler scheduler, CancellationToken stoppingToken)
        {
            _scheduler = scheduler;
            _stoppingToken = stoppingToken;
        }

        public Task Completion => _completion.Task;

        public void OnNext(OutboundTelegramDelivery value)
        {
            if (_disposed != 0)
            {
                value.Fail(new OperationCanceledException(_stoppingToken));
                return;
            }

            _ = SendAndReportAsync(value);
        }

        public void OnError(Exception error)
            => _completion.TrySetException(error);

        public void OnCompleted()
            => _completion.TrySetResult();

        public void Dispose()
            => Interlocked.Exchange(ref _disposed, 1);

        private async Task SendAndReportAsync(OutboundTelegramDelivery delivery)
        {
            OutboundTelegramDeliveryResult result = await _scheduler.SendDeliveryAsync(delivery, _stoppingToken).ConfigureAwait(false);
            delivery.SetResult(result);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
