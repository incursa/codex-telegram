using System.Collections.Concurrent;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Publishes Codex turn events to every Telegram conversation following the thread.
/// </summary>
internal interface ITelegramTurnOutputRelay
{
    /// <summary>
    /// Publishes one Codex timeline entry to Telegram followers.
    /// </summary>
    /// <param name="entry">Timeline entry to publish.</param>
    /// <param name="cancellationToken">Cancellation token for request aborts.</param>
    /// <returns>A task that completes when the entry has been queued or ignored.</returns>
    Task PublishTurnEventAsync(CodexTimelineEntryVm entry, CancellationToken cancellationToken);
}

/// <summary>
/// Converts Codex timeline entries into rate-limited Telegram outbound messages.
/// </summary>
internal sealed class TelegramTurnOutputRelay : ITelegramTurnOutputRelay
{
    private const string AgentMessageDeltaType = "item.agentMessage.delta";
    private const string TurnStartedType = "turn.started";
    private const string TurnCompletedType = "turn.completed";
    private const string TurnFailedType = "turn.failed";
    private const string TurnFinishedMarker = "~~ fin ~~";
    private const int InternalProgressMaxCharacters = 2000;

    private readonly ConcurrentDictionary<string, AgentMessageProgressBuffer> _agentMessageBuffersByThreadId = new(StringComparer.Ordinal);
    private readonly IOutboundTelegramQueue _outboundQueue;
    private readonly ITelegramThreadFollowRegistry _followRegistry;
    private readonly TelegramOutboundOptions _options;
    private readonly ILogger<TelegramTurnOutputRelay> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelegramTurnOutputRelay"/> class.
    /// </summary>
    /// <param name="outboundQueue">Outbound Telegram queue.</param>
    /// <param name="followRegistry">Registry of Telegram conversations following Codex threads.</param>
    /// <param name="options">Outbound delivery options.</param>
    /// <param name="logger">Logger for enqueue failures.</param>
    public TelegramTurnOutputRelay(
        IOutboundTelegramQueue outboundQueue,
        ITelegramThreadFollowRegistry followRegistry,
        IOptions<TelegramOutboundOptions> options,
        ILogger<TelegramTurnOutputRelay> logger)
    {
        _outboundQueue = outboundQueue;
        _followRegistry = followRegistry;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishTurnEventAsync(CodexTimelineEntryVm entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.ThreadId))
        {
            return;
        }

        if (IsAgentMessageDelta(entry))
        {
            string? updateText = AppendAgentMessageDelta(
                entry.ThreadId,
                entry.Body,
                _options.AgentMessageUpdateMinChars,
                _options.AgentMessageUpdateMaxChars);
            if (!string.IsNullOrWhiteSpace(updateText))
            {
                await PublishTextAsync(entry.ThreadId, entry.Type, updateText, CodexOutboundMessageKind.Update, OutboundPriority.High, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        AgentMessageFlush? bufferedAgentMessage = null;
        if (IsTerminalTurnEvent(entry))
        {
            if (_agentMessageBuffersByThreadId.TryRemove(entry.ThreadId, out AgentMessageProgressBuffer? buffer))
            {
                // Terminal events are the only point where we know whether already-streamed assistant
                // text duplicates the final response or whether there is unpublished tail text to flush.
                bufferedAgentMessage = buffer.Flush();
            }
        }

        CodexOutboundMessageKind kind = Classify(entry);
        if (entry.IsInternal && kind is not (CodexOutboundMessageKind.Error or CodexOutboundMessageKind.System or CodexOutboundMessageKind.Progress))
        {
            return;
        }

        bool isTerminal = IsTerminalTurnEvent(entry);
        string? text = entry.IsInternal && kind == CodexOutboundMessageKind.Progress
            ? FormatInternalProgressEntry(entry)
            : FormatEntry(entry, bufferedAgentMessage);

        if (!string.IsNullOrWhiteSpace(text))
        {
            await PublishTextAsync(entry.ThreadId, entry.Type, text, kind, ResolvePriority(kind), cancellationToken).ConfigureAwait(false);
        }

        if (isTerminal)
        {
            await PublishTextAsync(entry.ThreadId, entry.Type + ".finished", TurnFinishedMarker, CodexOutboundMessageKind.Completion, OutboundPriority.High, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishTextAsync(
        string threadId,
        string eventType,
        string text,
        CodexOutboundMessageKind kind,
        OutboundPriority priority,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<TelegramConversationScope> targets = _followRegistry.GetTargets(threadId);
        if (targets.Count == 0)
        {
            return;
        }

        foreach (TelegramConversationScope target in targets)
        {
            try
            {
                await _outboundQueue.EnqueueAsync(
                    new OutboundTelegramMessage
                    {
                        MessageId = $"{threadId}:{eventType}:{Guid.NewGuid():n}",
                        ChatId = target.ChatId,
                        MessageThreadId = target.MessageThreadId,
                        SessionId = threadId,
                        Kind = kind,
                        Text = text,
                        CreatedUtc = DateTimeOffset.UtcNow,
                        Priority = priority,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to enqueue Codex turn event {EventType} for thread {ThreadId} to Telegram destination {Destination}.", eventType, threadId, target);
            }
        }
    }

    private static string? FormatEntry(CodexTimelineEntryVm entry, string? bufferedAgentMessage)
        => FormatEntry(entry, bufferedAgentMessage is null ? null : new AgentMessageFlush(bufferedAgentMessage, bufferedAgentMessage, false));

    private static string? FormatEntry(CodexTimelineEntryVm entry, AgentMessageFlush? bufferedAgentMessage)
    {
        if (string.Equals(entry.Type, TurnCompletedType, StringComparison.OrdinalIgnoreCase))
        {
            return RemoveTurnFinishedMarker(ResolveFinalResponseText(entry, bufferedAgentMessage));
        }

        List<string> lines = [entry.Title];

        if (!string.IsNullOrWhiteSpace(entry.Subtitle))
        {
            lines.Add(entry.Subtitle);
        }

        if (!string.IsNullOrWhiteSpace(entry.Body))
        {
            lines.Add(entry.Body);
        }

        if (lines.Count == 1 && entry.Metadata.Count > 0)
        {
            lines.Add(string.Join(
                Environment.NewLine,
                entry.Metadata
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                    .Select(pair => $"{pair.Key}: {pair.Value}")));
        }

        string text = string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
        return IsTerminalTurnEvent(entry) ? RemoveTurnFinishedMarker(text) : text;
    }

    private static string? FormatInternalProgressEntry(CodexTimelineEntryVm entry)
    {
        if (string.Equals(entry.Type, TurnStartedType, StringComparison.OrdinalIgnoreCase))
        {
            return "Turn started.";
        }

        if (TryGetMetadata(entry, "command", out string? command))
        {
            string status = GetMetadata(entry, "status") ?? string.Empty;
            string? exitCode = GetMetadata(entry, "exitCode");
            if (!string.IsNullOrWhiteSpace(exitCode) && !string.Equals(exitCode, "0", StringComparison.OrdinalIgnoreCase))
            {
                return $"Command failed ({exitCode}): {Truncate(command!)}";
            }

            if (status.Contains("completed", StringComparison.OrdinalIgnoreCase)
                || status.Contains("succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return $"Command finished: {Truncate(command!)}";
            }

            if (status.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || status.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return $"Command failed: {Truncate(command!)}";
            }

            return $"Running command: {Truncate(command!)}";
        }

        if (TryGetMetadata(entry, "server", out string? server) && TryGetMetadata(entry, "tool", out string? mcpTool))
        {
            return $"Using MCP tool: {server}/{mcpTool}{FormatStatusSuffix(entry)}";
        }

        if (TryGetMetadata(entry, "tool", out string? tool))
        {
            return $"Using tool: {tool}{FormatStatusSuffix(entry)}";
        }

        if (TryGetMetadata(entry, "changeCount", out string? changeCount))
        {
            return $"File changes: {changeCount}{FormatStatusSuffix(entry)}";
        }

        if (TryGetMetadata(entry, "query", out string? query))
        {
            return $"Web search: {Truncate(query!)}";
        }

        if (IsItemProgressEntry(entry) && !string.IsNullOrWhiteSpace(entry.Subtitle))
        {
            string? summary = CleanInternalProgressSummary(entry.Subtitle);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                return $"Progress: {summary}";
            }
        }

        if (string.Equals(entry.Severity, "danger", StringComparison.OrdinalIgnoreCase))
        {
            return FormatEntry(entry, (AgentMessageFlush?)null);
        }

        return null;
    }

    private static bool IsAgentMessageDelta(CodexTimelineEntryVm entry)
        => string.Equals(entry.Type, AgentMessageDeltaType, StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalTurnEvent(CodexTimelineEntryVm entry)
        => string.Equals(entry.Type, TurnCompletedType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Type, TurnFailedType, StringComparison.OrdinalIgnoreCase);

    private static bool IsItemProgressEntry(CodexTimelineEntryVm entry)
        => entry.Type.StartsWith("item.", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveFinalResponseText(CodexTimelineEntryVm entry, AgentMessageFlush? bufferedAgentMessage)
    {
        if (!string.IsNullOrWhiteSpace(entry.Body))
        {
            if (bufferedAgentMessage?.PublishedAny == true && TextEquals(entry.Body, bufferedAgentMessage.FullText))
            {
                // Avoid repeating a full final response that was already streamed through live deltas.
                return !string.IsNullOrWhiteSpace(bufferedAgentMessage.UnpublishedText)
                    ? bufferedAgentMessage.UnpublishedText
                    : null;
            }

            return entry.Body;
        }

        if (!string.IsNullOrWhiteSpace(bufferedAgentMessage?.UnpublishedText))
        {
            return bufferedAgentMessage.UnpublishedText;
        }

        return null;
    }

    private static string? RemoveTurnFinishedMarker(string? text)
    {
        string normalized = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
        if (normalized.EndsWith(TurnFinishedMarker, StringComparison.Ordinal))
        {
            normalized = normalized[..^TurnFinishedMarker.Length].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private string? AppendAgentMessageDelta(string threadId, string? delta, int minChars, int maxChars)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return null;
        }

        AgentMessageProgressBuffer buffer = _agentMessageBuffersByThreadId.GetOrAdd(threadId, _ => new AgentMessageProgressBuffer());
        return buffer.Append(delta, minChars, maxChars);
    }

    private static CodexOutboundMessageKind Classify(CodexTimelineEntryVm entry)
    {
        if (string.Equals(entry.Type, TurnFailedType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Severity, "danger", StringComparison.OrdinalIgnoreCase))
        {
            return CodexOutboundMessageKind.Error;
        }

        if (string.Equals(entry.Type, TurnCompletedType, StringComparison.OrdinalIgnoreCase))
        {
            return CodexOutboundMessageKind.Completion;
        }

        if (entry.IsInternal)
        {
            return CodexOutboundMessageKind.Progress;
        }

        return CodexOutboundMessageKind.Update;
    }

    private static OutboundPriority ResolvePriority(CodexOutboundMessageKind kind)
        => kind switch
        {
            CodexOutboundMessageKind.Error => OutboundPriority.Critical,
            CodexOutboundMessageKind.Completion => OutboundPriority.High,
            CodexOutboundMessageKind.System => OutboundPriority.High,
            CodexOutboundMessageKind.Update => OutboundPriority.Normal,
            _ => OutboundPriority.Low,
        };

    private static bool TryGetMetadata(CodexTimelineEntryVm entry, string key, out string? value)
        => entry.Metadata.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value);

    private static string? GetMetadata(CodexTimelineEntryVm entry, string key)
        => entry.Metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string FormatStatusSuffix(CodexTimelineEntryVm entry)
        => GetMetadata(entry, "status") is { } status ? $" [{status}]" : string.Empty;

    private static string Truncate(string value)
        => value.Length <= InternalProgressMaxCharacters ? value : value[..(InternalProgressMaxCharacters - 1)] + "...";

    private static string? CleanInternalProgressSummary(string value)
    {
        string summary = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(summary)
            || summary.StartsWith("Reasoning (", StringComparison.OrdinalIgnoreCase)
            || summary.Equals("Context compaction", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Truncate(summary);
    }

    private static bool TextEquals(string left, string right)
        => string.Equals(NormalizeText(left), NormalizeText(right), StringComparison.Ordinal);

    private static string NormalizeText(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private sealed record AgentMessageFlush(string FullText, string? UnpublishedText, bool PublishedAny);

    private sealed class AgentMessageProgressBuffer
    {
        private readonly object _gate = new();
        private string _text = string.Empty;
        private int _publishedLength;

        public string? Append(string delta, int minChars, int maxChars)
        {
            lock (_gate)
            {
                _text += delta;

                int boundary = FindPublishBoundary(_text, _publishedLength, minChars, maxChars);
                if (boundary <= _publishedLength)
                {
                    return null;
                }

                string segment = _text[_publishedLength..boundary];
                _publishedLength = boundary;
                return FormatLiveAgentProgress(segment);
            }
        }

        public AgentMessageFlush Flush()
        {
            lock (_gate)
            {
                string unpublished = _publishedLength >= _text.Length
                    ? string.Empty
                    : _text[_publishedLength..];
                return new AgentMessageFlush(_text, CleanAgentProgress(unpublished), _publishedLength > 0);
            }
        }

        private static int FindPublishBoundary(string text, int startIndex, int minChars, int maxChars)
        {
            int safeMax = Math.Max(1, maxChars);
            int safeMin = Math.Clamp(minChars, 1, safeMax);
            int available = text.Length - startIndex;
            if (available < safeMin)
            {
                return -1;
            }

            int limit = Math.Min(text.Length, startIndex + safeMax);
            int minimumBoundary = startIndex + safeMin;

            // Prefer human-readable boundaries so live Telegram updates do not chop sentences
            // unless the assistant has already produced a very long uninterrupted segment.
            int boundary = FindPreferredBoundary(text, startIndex, minimumBoundary, limit, ch => ch == '\n', requireFollowingWhitespace: false);
            if (boundary > startIndex)
            {
                return boundary;
            }

            boundary = FindPreferredBoundary(text, startIndex, minimumBoundary, limit, ch => ch is '.' or '!' or '?', requireFollowingWhitespace: true);
            if (boundary > startIndex)
            {
                return boundary;
            }

            if (available < safeMax)
            {
                return -1;
            }

            boundary = FindWhitespaceBoundary(text, startIndex, minimumBoundary, limit);
            return boundary > startIndex ? boundary : limit;
        }

        private static int FindPreferredBoundary(string text, int startIndex, int minimumBoundary, int limit, Func<char, bool> isBoundary, bool requireFollowingWhitespace)
        {
            for (int index = limit - 1; index >= startIndex; index--)
            {
                if (!isBoundary(text[index]))
                {
                    continue;
                }

                int boundary = index + 1;
                if (boundary >= minimumBoundary
                    && (!requireFollowingWhitespace || (boundary < text.Length && char.IsWhiteSpace(text[boundary]))))
                {
                    return boundary;
                }
            }

            return -1;
        }

        private static int FindWhitespaceBoundary(string text, int startIndex, int minimumBoundary, int limit)
        {
            for (int index = limit - 1; index >= minimumBoundary; index--)
            {
                if (char.IsWhiteSpace(text[index]))
                {
                    return index + 1;
                }
            }

            return -1;
        }

        private static string? FormatLiveAgentProgress(string text)
        {
            string? cleaned = CleanAgentProgress(text);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return null;
            }

            return cleaned;
        }

        private static string? CleanAgentProgress(string text)
        {
            string[] lines = NormalizeText(text)
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !string.Equals(line, "---", StringComparison.Ordinal))
                .Where(line => !line.StartsWith("```", StringComparison.Ordinal))
                .ToArray();

            if (lines.Length == 0)
            {
                return null;
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
