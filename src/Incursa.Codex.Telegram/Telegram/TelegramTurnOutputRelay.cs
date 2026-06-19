using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Publishes Codex turn events to every Telegram conversation following the thread.
/// </summary>
internal interface ITelegramTurnOutputRelay
{
    /// <summary>
    /// Publishes the initial editable status card after Codex accepts a turn but before stream events arrive.
    /// </summary>
    /// <param name="threadId">Codex thread ID.</param>
    /// <param name="turnId">Codex turn ID.</param>
    /// <param name="cancellationToken">Cancellation token for request aborts.</param>
    /// <returns>A task that completes when the initial status card has been queued, sent, or ignored.</returns>
    Task PublishTurnAcceptedAsync(string threadId, string turnId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Publishes one Codex timeline entry to Telegram followers.
    /// </summary>
    /// <param name="entry">Timeline entry to publish.</param>
    /// <param name="cancellationToken">Cancellation token for request aborts.</param>
    /// <returns>A task that completes when the entry has been queued or ignored.</returns>
    Task PublishTurnEventAsync(CodexTimelineEntryVm entry, CancellationToken cancellationToken);

    /// <summary>
    /// Reposts the current editable live card as a fresh message for a specific Telegram conversation.
    /// If the conversation does not yet have a live card, a fresh one is created and shown.
    /// </summary>
    /// <param name="threadId">Codex thread ID.</param>
    /// <param name="conversation">Telegram conversation that should receive the refreshed card.</param>
    /// <param name="activity">Short activity line to place on the live card.</param>
    /// <param name="cancellationToken">Cancellation token for request aborts.</param>
    /// <returns><see langword="true" /> when a live card was refreshed or newly created; otherwise <see langword="false" />.</returns>
    Task<bool> RepostLiveCardAsync(
        string threadId,
        TelegramConversationScope conversation,
        string activity,
        CancellationToken cancellationToken)
        => Task.FromResult(false);
}

/// <summary>
/// Converts Codex timeline entries into rate-limited Telegram outbound messages.
/// </summary>
internal sealed class TelegramTurnOutputRelay : ITelegramTurnOutputRelay
{
    private const int LiveCardRepostDelayMilliseconds = 500;
    private const string AgentMessageDeltaType = "item.agentMessage.delta";
    private const string TurnStartedType = "turn.started";
    private const string TurnCompletedType = "turn.completed";
    private const string TurnFailedType = "turn.failed";
    private const string TurnInterruptedType = "turn.interrupted";
    private const string TurnFinalResponseType = "turn.finalResponse";
    private const string TurnCompletionMarker = "~~ turn complete ~~";
    private const string LegacyTurnFinishedMarker = "~~ fin ~~";
    private const string TurnCompletedMarkerType = "turn.completed.marker";
    private const int InternalProgressMaxCharacters = 2000;
    private const long MaxTelegramPhotoBytes = 10L * 1024L * 1024L;
    private const long MaxTelegramDocumentBytes = 50L * 1024L * 1024L;
    private const string UnknownTurnId = "unknown";

    private readonly ConcurrentDictionary<string, AgentMessageProgressBuffer> _agentMessageBuffersByThreadId = new(StringComparer.Ordinal);
    private readonly IOutboundTelegramQueue _outboundQueue;
    private readonly ITelegramThreadFollowRegistry _followRegistry;
    private readonly ITelegramTurnReactionRegistry _reactionRegistry;
    private readonly ITelegramBotMessageSender _messageSender;
    private readonly TelegramOutboundOptions _options;
    private readonly TelegramOutputOptions _outputOptions;
    private readonly ITelegramOutputModeState _outputModeState;
    private readonly ICodexSessionEventLog _eventLog;
    private readonly ITelegramDebugTraceStore _traceStore;
    private readonly ITelegramBotStateStore? _stateStore;
    private readonly ILogger<TelegramTurnOutputRelay> _logger;
    private readonly ConcurrentDictionary<TelegramLiveTurnCardKey, TelegramLiveTurnCardState> _liveCards = new();
    private readonly ConcurrentDictionary<TelegramLiveTurnCardKey, TelegramCompactPulseState> _compactPulses = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TelegramTurnOutputRelay"/> class.
    /// </summary>
    /// <param name="outboundQueue">Outbound Telegram queue.</param>
    /// <param name="followRegistry">Registry of Telegram conversations following Codex threads.</param>
    /// <param name="reactionRegistry">Registry that maps Codex turns back to their source Telegram messages for reactions.</param>
    /// <param name="messageSender">Telegram sender used for best-effort message reactions.</param>
    /// <param name="options">Outbound delivery options.</param>
    /// <param name="outputOptions">Turn-output presentation and history options.</param>
    /// <param name="outputModeState">Runtime output presentation mode state.</param>
    /// <param name="logger">Logger for enqueue failures.</param>
    /// <param name="eventLog">Session event projection updated when assistant text is actually queued to Telegram.</param>
    /// <param name="traceStore">Optional trace store for turn and delivery diagnostics.</param>
    /// <param name="stateStore">Optional persisted Telegram state used to recover output followers after restart or race windows.</param>
    public TelegramTurnOutputRelay(
        IOutboundTelegramQueue outboundQueue,
        ITelegramThreadFollowRegistry followRegistry,
        ITelegramTurnReactionRegistry reactionRegistry,
        ITelegramBotMessageSender messageSender,
        IOptions<TelegramOutboundOptions> options,
        IOptions<TelegramOutputOptions> outputOptions,
        ITelegramOutputModeState outputModeState,
        ILogger<TelegramTurnOutputRelay> logger,
        ICodexSessionEventLog? eventLog = null,
        ITelegramDebugTraceStore? traceStore = null,
        ITelegramBotStateStore? stateStore = null)
    {
        _outboundQueue = outboundQueue;
        _followRegistry = followRegistry;
        _reactionRegistry = reactionRegistry;
        _messageSender = messageSender;
        _options = options.Value;
        _outputOptions = outputOptions.Value;
        _outputModeState = outputModeState;
        _eventLog = eventLog ?? NullCodexSessionEventLog.Instance;
        _traceStore = traceStore ?? NullTelegramDebugTraceStore.Instance;
        _stateStore = stateStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishTurnAcceptedAsync(string threadId, string turnId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        TelegramOutputPresentationMode presentationMode = _outputModeState.CurrentMode;
        if (presentationMode is TelegramOutputPresentationMode.Verbose or TelegramOutputPresentationMode.Compact)
        {
            return;
        }

        CodexTimelineEntryVm entry = new(
            TurnStartedType,
            "Turn started",
            turnId,
            "Waiting for Codex events.",
            "info",
            DateTimeOffset.UtcNow,
            threadId,
            turnId,
            new Dictionary<string, string?>
            {
                ["status"] = "accepted",
                ["source"] = "coordinator",
            },
            true);

        await PublishLiveCardAsync(
            entry,
            CodexOutboundMessageKind.Progress,
            "Waiting for Codex events.",
            isTerminal: false,
            force: true,
            repost: false,
            presentationMode,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PublishTurnEventAsync(CodexTimelineEntryVm entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.ThreadId))
        {
            return;
        }

        TelegramOutputPresentationMode presentationMode = _outputModeState.CurrentMode;
        if (IsAgentMessageDelta(entry))
        {
            string? updateText = AppendAgentMessageDelta(
                entry.ThreadId,
                entry.Body,
                _options.AgentMessageUpdateMinChars,
                _options.AgentMessageUpdateMaxChars);
            await RecordCodexEventTraceAsync(entry, CodexOutboundMessageKind.Update, isTerminal: false, entry.Body, cancellationToken).ConfigureAwait(false);
            await PublishLiveCardAsync(
                entry,
                CodexOutboundMessageKind.Update,
                updateText ?? entry.Body,
                isTerminal: false,
                force: false,
                repost: false,
                presentationMode,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(updateText))
            {
                if (ShouldPublishDurableText(entry, CodexOutboundMessageKind.Update, presentationMode))
                {
                    await PublishTextAsync(entry.ThreadId, entry.TurnId, entry.Type, updateText, CodexOutboundMessageKind.Update, OutboundPriority.High, presentationMode, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await PublishCompactPulseAsync(entry, CodexOutboundMessageKind.Update, updateText, cancellationToken).ConfigureAwait(false);
                    await RecordSuppressedOutputAsync(entry, CodexOutboundMessageKind.Update, updateText, presentationMode, cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        AgentMessageFlush? bufferedAgentMessage = null;
        if (IsTerminalTurnEvent(entry) || IsFinalResponse(entry))
        {
            if (_agentMessageBuffersByThreadId.TryRemove(entry.ThreadId, out AgentMessageProgressBuffer? buffer))
            {
                // Terminal events are the only point where we know whether already-streamed assistant
                // text duplicates the final response or whether there is unpublished tail text to flush.
                bufferedAgentMessage = buffer.Flush();
            }
        }

        if (await TryPublishExplicitMediaAsync(entry, presentationMode, cancellationToken).ConfigureAwait(false))
        {
            await RecordCodexEventTraceAsync(entry, CodexOutboundMessageKind.Update, IsTerminalTurnEvent(entry), entry.Body, cancellationToken).ConfigureAwait(false);
            await PublishLiveCardAsync(
                entry,
                CodexOutboundMessageKind.Update,
                entry.Body ?? entry.Title,
                IsTerminalTurnEvent(entry),
                force: true,
                repost: true,
                presentationMode,
                cancellationToken).ConfigureAwait(false);
            if (presentationMode == TelegramOutputPresentationMode.Compact)
            {
                await PublishCompactPulseAsync(entry, CodexOutboundMessageKind.Update, entry.Body ?? entry.Title, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        CodexOutboundMessageKind kind = Classify(entry);
        bool isTerminal = IsTerminalTurnEvent(entry);
        string? text = entry.IsInternal && kind == CodexOutboundMessageKind.Progress
            ? FormatInternalProgressEntry(entry)
            : FormatEntry(entry, bufferedAgentMessage);
        bool hasText = !string.IsNullOrWhiteSpace(text);
        bool shouldPublishDurableText = hasText && ShouldPublishDurableText(entry, kind, presentationMode);
        bool publishDurableTextBeforeCard = shouldPublishDurableText && IsHighPriorityDurableEvent(entry, kind);
        await RecordCodexEventTraceAsync(entry, kind, isTerminal, text, cancellationToken).ConfigureAwait(false);
        if (publishDurableTextBeforeCard)
        {
            await PublishTextAsync(entry.ThreadId, entry.TurnId, entry.Type, text!, kind, ResolvePriority(entry, kind), presentationMode, cancellationToken).ConfigureAwait(false);
        }

        bool publishLegacyMarkerBeforeCard = isTerminal
            && string.Equals(entry.Type, TurnCompletedType, StringComparison.OrdinalIgnoreCase);
        if (publishLegacyMarkerBeforeCard)
        {
            await PublishTextAsync(
                entry.ThreadId,
                entry.TurnId,
                TurnCompletedMarkerType,
                LegacyTurnFinishedMarker,
                CodexOutboundMessageKind.Completion,
                OutboundPriority.High,
                presentationMode,
                cancellationToken).ConfigureAwait(false);
        }

        await PublishLiveCardAsync(
            entry,
            kind,
            text,
            isTerminal,
            force: isTerminal || IsHighPriorityDurableEvent(entry, kind),
            repost: publishDurableTextBeforeCard || publishLegacyMarkerBeforeCard,
            presentationMode,
            cancellationToken).ConfigureAwait(false);

        if (entry.IsInternal && kind is not (CodexOutboundMessageKind.Error or CodexOutboundMessageKind.System or CodexOutboundMessageKind.Progress))
        {
            return;
        }

        if (hasText && !publishDurableTextBeforeCard)
        {
            if (shouldPublishDurableText)
            {
                await PublishTextAsync(entry.ThreadId, entry.TurnId, entry.Type, text!, kind, ResolvePriority(entry, kind), presentationMode, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await PublishCompactPulseAsync(entry, kind, entry.Body ?? text, cancellationToken).ConfigureAwait(false);
                await RecordSuppressedOutputAsync(entry, kind, text!, presentationMode, cancellationToken).ConfigureAwait(false);
            }
        }

        if (isTerminal)
        {
            ClearCompactPulseState(entry.ThreadId, entry.TurnId);
            await ReactToTerminalTurnAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> RepostLiveCardAsync(
        string threadId,
        TelegramConversationScope conversation,
        string activity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        TelegramOutputPresentationMode presentationMode = _outputModeState.CurrentMode;
        if (presentationMode is TelegramOutputPresentationMode.Verbose or TelegramOutputPresentationMode.Compact)
        {
            return false;
        }

        TelegramLiveTurnCardKey key = new(threadId, conversation);
        TelegramLiveTurnCardState state = _liveCards.GetOrAdd(
            key,
            _ => new TelegramLiveTurnCardState(threadId, UnknownTurnId, conversation));

        TelegramOutboundQueueStatus outboundStatus = await GetOutboundStatusAsync(cancellationToken).ConfigureAwait(false);
        TelegramLiveTurnCardSnapshot snapshot = state.RecordActivity(activity);
        return await PublishLiveCardSnapshotAsync(
            state,
            snapshot,
            outboundStatus,
            presentationMode,
            repost: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryPublishExplicitMediaAsync(
        CodexTimelineEntryVm entry,
        TelegramOutputPresentationMode presentationMode,
        CancellationToken cancellationToken)
    {
        if (!TryGetMetadata(entry, "explicitMediaKind", out _))
        {
            return false;
        }

        if (!TryResolveExplicitMediaFile(entry, out string path, out string? contentType))
        {
            return false;
        }

        string fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        FileInfo? info = File.Exists(path) ? new FileInfo(path) : null;
        if (info is not null && info.Length > MaxTelegramDocumentBytes)
        {
            await PublishTextAsync(
                entry.ThreadId!,
                entry.TurnId,
                entry.Type,
                $"Codex produced {fileName}, but it is too large for Telegram file delivery.",
                CodexOutboundMessageKind.System,
                OutboundPriority.High,
                presentationMode,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        TelegramOutboundFileKind kind = IsTelegramPhotoCandidate(path, info)
            ? TelegramOutboundFileKind.Photo
            : TelegramOutboundFileKind.Document;
        await PublishFileAsync(
            entry.ThreadId!,
            entry.TurnId,
            entry.Type,
            new OutboundTelegramFile
            {
                Kind = kind,
                Path = path,
                FileName = fileName,
                Caption = $"Codex artifact: {fileName}",
                ContentType = contentType ?? ResolveContentType(path),
            },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task PublishLiveCardAsync(
        CodexTimelineEntryVm entry,
        CodexOutboundMessageKind kind,
        string? text,
        bool isTerminal,
        bool force,
        bool repost,
        TelegramOutputPresentationMode presentationMode,
        CancellationToken cancellationToken)
    {
        if (presentationMode is TelegramOutputPresentationMode.Verbose or TelegramOutputPresentationMode.Compact
            || string.IsNullOrWhiteSpace(entry.ThreadId))
        {
            return;
        }

        IReadOnlyCollection<TelegramConversationScope> targets = await ResolveTargetsAsync(entry.ThreadId, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return;
        }

        TelegramOutboundQueueStatus outboundStatus = await GetOutboundStatusAsync(cancellationToken).ConfigureAwait(false);

        foreach (TelegramConversationScope target in targets)
        {
            TelegramLiveTurnCardKey key = new(entry.ThreadId, target);
            TelegramLiveTurnCardState state = _liveCards.GetOrAdd(key, _ => new TelegramLiveTurnCardState(entry.ThreadId, ResolveLiveTurnId(entry), target));
            TelegramLiveTurnCardSnapshot snapshot = state.Record(entry, kind, text, isTerminal);
            bool forceEdit = force || snapshot.TurnChanged || IsRetryNotice(entry);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!forceEdit && snapshot.LastEditUtc is { } lastEdit && now - lastEdit < TimeSpan.FromSeconds(_outputOptions.LiveCardMinEditIntervalSeconds))
            {
                continue;
            }

            await PublishLiveCardSnapshotAsync(state, snapshot, outboundStatus, presentationMode, repost, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TelegramOutboundQueueStatus> GetOutboundStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _outboundQueue.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new TelegramOutboundQueueStatus(0, 0, 0, 0, null, null, null, []);
        }
    }

    private static bool HasPendingOutboundForConversation(TelegramOutboundQueueStatus status, TelegramConversationScope conversation)
        => status.Destinations.Any(destination =>
            destination.ChatId == conversation.ChatId
            && destination.MessageThreadId == conversation.MessageThreadId
            && (destination.PendingMessageCount > 0 || destination.PendingChunkCount > 0));

    private async Task<bool> PublishLiveCardSnapshotAsync(
        TelegramLiveTurnCardState state,
        TelegramLiveTurnCardSnapshot snapshot,
        TelegramOutboundQueueStatus outboundStatus,
        TelegramOutputPresentationMode presentationMode,
        bool repost,
        CancellationToken cancellationToken)
    {
        TelegramOutboundDestinationStatus? destination = outboundStatus.Destinations.FirstOrDefault(item =>
            item.ChatId == snapshot.Conversation.ChatId
            && item.MessageThreadId == snapshot.Conversation.MessageThreadId);
        if (repost && HasPendingOutboundForConversation(outboundStatus, snapshot.Conversation))
        {
            // Give the queued durable message one flush window to land before we refresh the live card.
            await Task.Delay(TimeSpan.FromMilliseconds(LiveCardRepostDelayMilliseconds), cancellationToken).ConfigureAwait(false);
            outboundStatus = await GetOutboundStatusAsync(cancellationToken).ConfigureAwait(false);
            destination = outboundStatus.Destinations.FirstOrDefault(item =>
                item.ChatId == snapshot.Conversation.ChatId
                && item.MessageThreadId == snapshot.Conversation.MessageThreadId);
        }

        string cardText = BuildLiveCardText(snapshot, destination);
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>> buttons = BuildLiveCardButtons(snapshot);
        TelegramDebugMessageContext debugContext = new(
            "turn-live-card",
            snapshot.ThreadId,
            ResolveTraceTurnId(snapshot.TurnId),
            snapshot.TurnId,
            presentationMode.ToString(),
            TraceId: _traceStore.TryGetTraceIdForTurn(snapshot.ThreadId, ResolveTraceTurnId(snapshot.TurnId)));

        try
        {
            int? previousMessageId = snapshot.MessageId;
            int? messageId;
            if (previousMessageId.HasValue)
            {
                bool edited = await _messageSender.TryEditTextMessageAsync(
                    snapshot.Conversation,
                    previousMessageId.Value,
                    cardText,
                    buttons,
                    cancellationToken,
                    debugContext).ConfigureAwait(false);
                if (!edited)
                {
                    if (!repost)
                    {
                        state.MarkEdited(previousMessageId, DateTimeOffset.UtcNow);
                        await RecordLiveCardTraceAsync(snapshot, "telegram.live_card.edit_failed", previousMessageId, cancellationToken).ConfigureAwait(false);
                        return false;
                    }

                    messageId = await _messageSender.SendTextMessageAndGetIdAsync(
                        snapshot.Conversation,
                        cardText,
                        buttons,
                        cancellationToken,
                        debugContext).ConfigureAwait(false);
                }
                else
                {
                    messageId = previousMessageId;
                }
            }
            else
            {
                messageId = await _messageSender.SendTextMessageAndGetIdAsync(snapshot.Conversation, cardText, buttons, cancellationToken, debugContext).ConfigureAwait(false);
            }

            if (messageId.HasValue)
            {
                state.MarkEdited(messageId.Value, DateTimeOffset.UtcNow);
                if (previousMessageId.HasValue && previousMessageId.Value != messageId.Value)
                {
                    await RecordLiveCardTraceAsync(snapshot, "telegram.live_card.reposted", messageId.Value, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                state.MarkEdited(previousMessageId, DateTimeOffset.UtcNow);
            }

            await RecordLiveCardTraceAsync(snapshot, "telegram.live_card.updated", messageId ?? previousMessageId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to update Telegram live turn card for thread {ThreadId} turn {TurnId}.", snapshot.ThreadId, snapshot.TurnId);
            await RecordLiveCardTraceAsync(snapshot, "telegram.live_card.failed", snapshot.MessageId, cancellationToken, exception.Message).ConfigureAwait(false);
            return false;
        }
    }

    private Task RecordLiveCardTraceAsync(
        TelegramLiveTurnCardSnapshot snapshot,
        string kind,
        int? messageId,
        CancellationToken cancellationToken,
        string? error = null)
    {
        string? traceId = _traceStore.TryGetTraceIdForTurn(snapshot.ThreadId, ResolveTraceTurnId(snapshot.TurnId));
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return Task.CompletedTask;
        }

        return _traceStore.RecordAsync(
            new TelegramDebugTraceEvent(
                traceId,
                DateTimeOffset.UtcNow,
                kind,
                SessionId: snapshot.ThreadId,
                TurnId: snapshot.TurnId,
                ChatId: snapshot.Conversation.ChatId,
                MessageThreadId: snapshot.Conversation.MessageThreadId,
                Direction: "telegram",
                Status: snapshot.StateText,
                Metadata: new Dictionary<string, string>
                {
                    ["messageId"] = messageId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    ["updatesCaptured"] = snapshot.UpdateCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["progressSuppressed"] = snapshot.ProgressCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["finalResponseCaptured"] = snapshot.FinalResponseCaptured.ToString(),
                },
                Error: error,
                Source: "TelegramLiveCard"),
            cancellationToken);
    }

    private static string BuildLiveCardText(
        TelegramLiveTurnCardSnapshot snapshot,
        TelegramOutboundDestinationStatus? destination)
    {
        bool draining = destination is not null && (destination.PendingMessageCount > 0 || destination.PendingChunkCount > 0);
        string stateText = snapshot.TerminalEventSeen
            ? snapshot.Failed
                ? "Codex failed"
                : snapshot.Interrupted
                    ? snapshot.FinalResponseCaptured
                        ? draining ? "Codex interrupted; sending captured output" : "Codex interrupted; captured output sent"
                        : draining ? "Codex interrupted; output still pending" : "Codex interrupted"
                : draining ? "Codex finished; sending final output" : "Codex finished"
            : snapshot.FinalResponseCaptured ? "Final response captured" : "Codex is working";

        StringBuilder builder = new();
        builder.AppendLine($"--- live card: {ResolveLiveCardBoundaryStatus(snapshot, draining)} ---");
        builder.AppendLine(stateText);
        builder.AppendLine(
            $"Updates {snapshot.UpdateCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} | Progress {snapshot.ProgressCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(snapshot.Latest))
        {
            builder.AppendLine("Latest:");
            builder.AppendLine(snapshot.Latest);
        }
        builder.AppendLine("--- /live card ---");
        return builder.ToString().TrimEnd();
    }

    private static string ResolveLiveCardBoundaryStatus(TelegramLiveTurnCardSnapshot snapshot, bool draining)
    {
        if (snapshot.Failed)
        {
            return "failed";
        }

        if (snapshot.Interrupted)
        {
            return draining ? "interrupted sending" : "interrupted";
        }

        if (snapshot.TerminalEventSeen)
        {
            return draining ? "finished sending" : "finished";
        }

        return snapshot.FinalResponseCaptured ? "final captured" : "working";
    }

    private static IReadOnlyList<IReadOnlyList<TelegramReplyButton>> BuildLiveCardButtons(TelegramLiveTurnCardSnapshot snapshot)
        =>
        [
            [
                new TelegramReplyButton("Show Updates", $"turn:updates|{snapshot.ThreadId}"),
                new TelegramReplyButton("Show Full Turn", $"turn:full|{snapshot.ThreadId}"),
            ],
            [
                new TelegramReplyButton("Trace", $"trace:{snapshot.ThreadId}"),
                new TelegramReplyButton("Refresh", $"status:{snapshot.ThreadId}"),
            ],
        ];

    private async Task PublishFileAsync(
        string threadId,
        string? turnId,
        string eventType,
        OutboundTelegramFile file,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<TelegramConversationScope> targets = await ResolveTargetsAsync(threadId, cancellationToken).ConfigureAwait(false);
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
                        MessageId = $"{threadId}:{eventType}:file:{Guid.NewGuid():n}",
                        ChatId = target.ChatId,
                        MessageThreadId = target.MessageThreadId,
                        SessionId = threadId,
                        TurnId = string.IsNullOrWhiteSpace(turnId) ? null : turnId,
                        Kind = CodexOutboundMessageKind.Update,
                        Text = file.Caption ?? file.FileName ?? "Codex artifact",
                        File = file,
                        CreatedUtc = DateTimeOffset.UtcNow,
                        Priority = OutboundPriority.High,
                        TraceId = _traceStore.TryGetTraceIdForTurn(threadId, turnId),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to enqueue Codex media event {EventType} for thread {ThreadId} to Telegram destination {Destination}.", eventType, threadId, target);
            }
        }
    }

    private async Task ReactToTerminalTurnAsync(CodexTimelineEntryVm entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.ThreadId) || string.IsNullOrWhiteSpace(entry.TurnId))
        {
            return;
        }

        TelegramTurnReactionTarget? target = _reactionRegistry.TryTake(entry.ThreadId, entry.TurnId);
        if (target is null)
        {
            return;
        }

        TelegramMessageReactionKind reactionKind = string.Equals(entry.Type, TurnFailedType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Severity, "danger", StringComparison.OrdinalIgnoreCase)
                ? TelegramMessageReactionKind.Failed
                : TelegramMessageReactionKind.Completed;
        await _messageSender.ReactToMessageAsync(
            new TelegramMessageReaction(target.Conversation, target.MessageId, reactionKind),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishTextAsync(
        string threadId,
        string? turnId,
        string eventType,
        string text,
        CodexOutboundMessageKind kind,
        OutboundPriority priority,
        TelegramOutputPresentationMode presentationMode,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<TelegramConversationScope> targets = await ResolveTargetsAsync(threadId, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return;
        }

        bool enqueued = false;
        DateTimeOffset createdUtc = DateTimeOffset.UtcNow;
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
                        TurnId = string.IsNullOrWhiteSpace(turnId) ? null : turnId,
                        Kind = kind,
                        Text = FormatSpecialMessageForTelegram(eventType, kind, text, presentationMode),
                        CreatedUtc = createdUtc,
                        Priority = priority,
                        TraceId = _traceStore.TryGetTraceIdForTurn(threadId, turnId),
                    },
                    cancellationToken).ConfigureAwait(false);
                enqueued = true;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to enqueue Codex turn event {EventType} for thread {ThreadId} to Telegram destination {Destination}.", eventType, threadId, target);
            }
        }

        if (enqueued && IsVisibleAssistantOutput(eventType, kind))
        {
            _eventLog.RecordVisibleAssistantOutput(threadId, turnId, createdUtc, text);
        }
    }

    private async Task PublishCompactPulseAsync(
        CodexTimelineEntryVm entry,
        CodexOutboundMessageKind kind,
        string? text,
        CancellationToken cancellationToken)
    {
        if (_outputModeState.CurrentMode != TelegramOutputPresentationMode.Compact
            || string.IsNullOrWhiteSpace(entry.ThreadId)
            || IsTerminalTurnEvent(entry)
            || IsHighPriorityDurableEvent(entry, kind)
            || kind is not (CodexOutboundMessageKind.Progress or CodexOutboundMessageKind.Update))
        {
            return;
        }

        IReadOnlyCollection<TelegramConversationScope> targets = await ResolveTargetsAsync(entry.ThreadId, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan pulseInterval = TimeSpan.FromSeconds(_outputOptions.CompactPulseIntervalSeconds);
        string activity = FormatCompactActivity(text);
        foreach (TelegramConversationScope target in targets)
        {
            TelegramLiveTurnCardKey key = new(entry.ThreadId, target);
            TelegramCompactPulseState state = _compactPulses.GetOrAdd(key, _ => new TelegramCompactPulseState(entry.TurnId, now));
            if (!state.TryMarkPulse(entry.TurnId, now, pulseInterval, out TimeSpan activeFor))
            {
                continue;
            }

            int queuedPromptCount = await CountQueuedPromptsAsync(target, cancellationToken).ConfigureAwait(false);
            await EnqueueCompactPulseAsync(entry, target, activity, activeFor, queuedPromptCount, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnqueueCompactPulseAsync(
        CodexTimelineEntryVm entry,
        TelegramConversationScope target,
        string activity,
        TimeSpan activeFor,
        int queuedPromptCount,
        DateTimeOffset createdUtc,
        CancellationToken cancellationToken)
    {
        string threadId = entry.ThreadId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        StringBuilder builder = new();
        builder.AppendLine($"Still working: {activity}");
        if (activeFor >= TimeSpan.FromMinutes(1))
        {
            builder.AppendLine($"Active: {FormatDuration(activeFor)}");
        }

        if (queuedPromptCount > 0)
        {
            builder.AppendLine($"Queue: {queuedPromptCount.ToString(CultureInfo.InvariantCulture)} pending");
        }

        try
        {
            await _outboundQueue.EnqueueAsync(
                new OutboundTelegramMessage
                {
                    MessageId = $"{threadId}:compact-pulse:{Guid.NewGuid():n}",
                    ChatId = target.ChatId,
                    MessageThreadId = target.MessageThreadId,
                    SessionId = threadId,
                    TurnId = string.IsNullOrWhiteSpace(entry.TurnId) ? null : entry.TurnId,
                    Kind = CodexOutboundMessageKind.Progress,
                    Text = builder.ToString().TrimEnd(),
                    CreatedUtc = createdUtc,
                    Priority = OutboundPriority.Low,
                    TraceId = _traceStore.TryGetTraceIdForTurn(threadId, entry.TurnId),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to enqueue compact Codex progress pulse for thread {ThreadId} to Telegram destination {Destination}.", entry.ThreadId, target);
        }
    }

    private async Task<int> CountQueuedPromptsAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
    {
        if (_stateStore is null)
        {
            return 0;
        }

        IReadOnlyList<TelegramQueuedPrompt> queued = await _stateStore.ListQueuedPromptsAsync(null, conversation, cancellationToken).ConfigureAwait(false);
        return queued.Count;
    }

    private void ClearCompactPulseState(string? threadId, string? turnId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        foreach (TelegramLiveTurnCardKey key in _compactPulses.Keys.Where(key => string.Equals(key.ThreadId, threadId, StringComparison.Ordinal)).ToArray())
        {
            if (_compactPulses.TryGetValue(key, out TelegramCompactPulseState? state)
                && (string.IsNullOrWhiteSpace(turnId) || state.MatchesTurn(turnId)))
            {
                _compactPulses.TryRemove(key, out _);
            }
        }
    }

    private async Task<IReadOnlyCollection<TelegramConversationScope>> ResolveTargetsAsync(
        string threadId,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<TelegramConversationScope> targets = _followRegistry.GetTargets(threadId);
        if (targets.Count > 0 || _stateStore is null)
        {
            return targets;
        }

        try
        {
            IReadOnlyCollection<TelegramConversationState> states = await _stateStore.ListConversationStatesAsync(cancellationToken).ConfigureAwait(false);
            TelegramConversationScope[] restoredTargets = states
                .Where(state => string.Equals(state.ActiveSessionId, threadId, StringComparison.OrdinalIgnoreCase))
                .Select(state => state.Scope)
                .Distinct()
                .ToArray();

            foreach (TelegramConversationScope restoredTarget in restoredTargets)
            {
                _followRegistry.FollowThread(restoredTarget, threadId);
            }

            if (restoredTargets.Length > 0)
            {
                _logger.LogDebug("Restored {TargetCount} Telegram output follower(s) for thread {ThreadId} from persisted state.", restoredTargets.Length, threadId);
                return restoredTargets;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to restore Telegram output followers for thread {ThreadId} from persisted state.", threadId);
        }

        return targets;
    }

    private static string ResolveLiveTurnId(CodexTimelineEntryVm entry)
        => string.IsNullOrWhiteSpace(entry.TurnId) ? UnknownTurnId : entry.TurnId;

    private static string? ResolveTraceTurnId(string? turnId)
        => string.IsNullOrWhiteSpace(turnId) || string.Equals(turnId, UnknownTurnId, StringComparison.Ordinal) ? null : turnId;

    private static string? FormatEntry(CodexTimelineEntryVm entry, string? bufferedAgentMessage)
        => FormatEntry(entry, bufferedAgentMessage is null ? null : new AgentMessageFlush(bufferedAgentMessage, null, bufferedAgentMessage, false));

    private Task RecordCodexEventTraceAsync(
        CodexTimelineEntryVm entry,
        CodexOutboundMessageKind kind,
        bool isTerminal,
        string? text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.ThreadId) || string.IsNullOrWhiteSpace(entry.TurnId))
        {
            return Task.CompletedTask;
        }

        string? traceId = _traceStore.TryGetTraceIdForTurn(entry.ThreadId, entry.TurnId);
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return Task.CompletedTask;
        }

        return _traceStore.RecordAsync(
            new TelegramDebugTraceEvent(
                traceId,
                entry.Timestamp == default ? DateTimeOffset.UtcNow : entry.Timestamp,
                isTerminal ? "codex.terminal" : "codex.event",
                SessionId: entry.ThreadId,
                TurnId: entry.TurnId,
                Direction: "codex",
                Status: isTerminal ? entry.Type : null,
                TextLength: text?.Length ?? 0,
                Metadata: new Dictionary<string, string>
                {
                    ["eventType"] = entry.Type,
                    ["severity"] = entry.Severity,
                    ["outboundKind"] = kind.ToString(),
                    ["title"] = entry.Title,
                    ["bodyLength"] = (entry.Body?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["visibleAssistantOutput"] = IsVisibleAssistantOutput(entry.Type, kind).ToString(),
                },
                TextBody: entry.Body ?? text,
                Source: "CodexNormalizedEvent"),
            cancellationToken);
    }

    private static string? FormatEntry(CodexTimelineEntryVm entry, AgentMessageFlush? bufferedAgentMessage)
    {
        if (IsInterruptedTurnEvent(entry))
        {
            return null;
        }

        if (string.Equals(entry.Type, TurnFinalResponseType, StringComparison.OrdinalIgnoreCase))
        {
            return RemoveTurnFinishedMarker(ResolveFinalResponseText(entry, bufferedAgentMessage));
        }

        if (string.Equals(entry.Type, TurnCompletedType, StringComparison.OrdinalIgnoreCase))
        {
            string? finalResponse = RemoveTurnFinishedMarker(ResolveFinalResponseText(entry, bufferedAgentMessage));
            if (!string.IsNullOrWhiteSpace(finalResponse))
            {
                return finalResponse;
            }

            return null;
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

        if (string.Equals(entry.Type, "turn.retry", StringComparison.OrdinalIgnoreCase))
        {
            return entry.Subtitle ?? entry.Body ?? "Retrying.";
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
            || string.Equals(entry.Type, TurnFailedType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Type, TurnInterruptedType, StringComparison.OrdinalIgnoreCase);

    private static bool IsInterruptedTurnEvent(CodexTimelineEntryVm entry)
        => string.Equals(entry.Type, TurnInterruptedType, StringComparison.OrdinalIgnoreCase);

    private static bool IsFinalResponse(CodexTimelineEntryVm entry)
        => string.Equals(entry.Type, TurnFinalResponseType, StringComparison.OrdinalIgnoreCase);

    private static bool IsItemProgressEntry(CodexTimelineEntryVm entry)
        => entry.Type.StartsWith("item.", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveFinalResponseText(CodexTimelineEntryVm entry, AgentMessageFlush? bufferedAgentMessage)
    {
        string? entryBody = RemoveTurnFinishedMarker(entry.Body);
        if (!string.IsNullOrWhiteSpace(entryBody))
        {
            if (bufferedAgentMessage?.PublishedAny == true
                && !string.IsNullOrWhiteSpace(bufferedAgentMessage.PublishedText))
            {
                string normalizedEntryBody = NormalizeText(entryBody);
                string normalizedPublishedText = NormalizeText(bufferedAgentMessage.PublishedText);
                if (normalizedEntryBody.StartsWith(normalizedPublishedText, StringComparison.Ordinal))
                {
                    string remainder = normalizedEntryBody[normalizedPublishedText.Length..].TrimStart();
                    if (string.IsNullOrWhiteSpace(remainder))
                    {
                        return bufferedAgentMessage.UnpublishedText;
                    }

                    return remainder;
                }
            }

            return entryBody;
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
        if (normalized.EndsWith(TurnCompletionMarker, StringComparison.Ordinal))
        {
            normalized = normalized[..^TurnCompletionMarker.Length].TrimEnd();
        }

        if (normalized.EndsWith(LegacyTurnFinishedMarker, StringComparison.Ordinal))
        {
            normalized = normalized[..^LegacyTurnFinishedMarker.Length].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string FormatSpecialMessageForTelegram(
        string eventType,
        CodexOutboundMessageKind kind,
        string text,
        TelegramOutputPresentationMode presentationMode)
    {
        if (presentationMode == TelegramOutputPresentationMode.Verbose)
        {
            return text;
        }

        string? boundary = ResolveSpecialMessageBoundary(eventType, kind, text);
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return text;
        }

        string normalized = text.Trim();
        return $"--- {boundary} ---{Environment.NewLine}{normalized}{Environment.NewLine}--- /{boundary} ---";
    }

    private static string? ResolveSpecialMessageBoundary(string eventType, CodexOutboundMessageKind kind, string text)
    {
        if (kind == CodexOutboundMessageKind.Completion
            && !string.Equals(eventType, TurnCompletedMarkerType, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(text.Trim(), LegacyTurnFinishedMarker, StringComparison.Ordinal))
        {
            return "final";
        }

        if (kind == CodexOutboundMessageKind.Error)
        {
            return "codex error";
        }

        if (kind == CodexOutboundMessageKind.System)
        {
            return "codex status";
        }

        return null;
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

        if (string.Equals(entry.Type, TurnInterruptedType, StringComparison.OrdinalIgnoreCase))
        {
            return CodexOutboundMessageKind.System;
        }

        if (string.Equals(entry.Type, TurnCompletedType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Type, TurnFinalResponseType, StringComparison.OrdinalIgnoreCase))
        {
            return CodexOutboundMessageKind.Completion;
        }

        if (entry.IsInternal)
        {
            return CodexOutboundMessageKind.Progress;
        }

        return CodexOutboundMessageKind.Update;
    }

    private static OutboundPriority ResolvePriority(CodexTimelineEntryVm entry, CodexOutboundMessageKind kind)
    {
        if (IsApprovalNeeded(entry))
        {
            return OutboundPriority.Critical;
        }

        return ResolvePriority(kind);
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

    private static bool ShouldPublishDurableText(
        CodexTimelineEntryVm entry,
        CodexOutboundMessageKind kind,
        TelegramOutputPresentationMode presentationMode)
    {
        if (presentationMode == TelegramOutputPresentationMode.Verbose)
        {
            return true;
        }

        return IsHighPriorityDurableEvent(entry, kind);
    }

    private static bool IsHighPriorityDurableEvent(CodexTimelineEntryVm entry, CodexOutboundMessageKind kind)
        => kind is CodexOutboundMessageKind.Completion or CodexOutboundMessageKind.Error or CodexOutboundMessageKind.System
            || IsApprovalNeeded(entry)
            || string.Equals(entry.Type, "turn.closeout.warning", StringComparison.OrdinalIgnoreCase);

    private static bool IsApprovalNeeded(CodexTimelineEntryVm entry)
        => ContainsAny(entry.Type, "approval", "plan.question", "plan_input", "input.request")
            || ContainsAny(entry.Title, "approval", "question", "waiting for input")
            || ContainsAny(entry.Subtitle, "approval", "question", "waiting for input");

    private static bool ContainsAny(string? value, params string[] needles)
        => !string.IsNullOrWhiteSpace(value)
            && needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private async Task RecordSuppressedOutputAsync(
        CodexTimelineEntryVm entry,
        CodexOutboundMessageKind kind,
        string text,
        TelegramOutputPresentationMode presentationMode,
        CancellationToken cancellationToken)
    {
        string? traceId = _traceStore.TryGetTraceIdForTurn(entry.ThreadId!, entry.TurnId);
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return;
        }

        await _traceStore.RecordAsync(
            new TelegramDebugTraceEvent(
                traceId,
                DateTimeOffset.UtcNow,
                "telegram.output.suppressed",
                SessionId: entry.ThreadId,
                TurnId: entry.TurnId,
                Direction: "telegram",
                Status: presentationMode.ToString(),
                TextLength: text.Length,
                Metadata: new Dictionary<string, string>
                {
                    ["eventType"] = entry.Type,
                    ["outboundKind"] = kind.ToString(),
                    ["presentationMode"] = presentationMode.ToString(),
                },
                Source: "TelegramOutputProjection"),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsVisibleAssistantOutput(string eventType, CodexOutboundMessageKind kind)
        => string.Equals(eventType, AgentMessageDeltaType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventType, "turn.assistant.recovered", StringComparison.OrdinalIgnoreCase)
            || string.Equals(eventType, TurnFinalResponseType, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(eventType, TurnCompletedType, StringComparison.OrdinalIgnoreCase)
                && kind == CodexOutboundMessageKind.Completion);

    private static string ShortId(string value)
        => value.Length <= 8 ? value : value[..8];

    private static string? ResolveVisibleSummary(CodexTimelineEntryVm entry, CodexOutboundMessageKind kind, string? text)
    {
        if (IsInterruptedTurnEvent(entry))
        {
            return null;
        }

        string? candidate = !string.IsNullOrWhiteSpace(text)
            ? text
            : !string.IsNullOrWhiteSpace(entry.Body) ? entry.Body : entry.Subtitle ?? entry.Title;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        string normalized = NormalizeText(candidate).Replace("\n", " ", StringComparison.Ordinal);
        if (kind == CodexOutboundMessageKind.Progress && normalized.StartsWith("Progress:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["Progress:".Length..].Trim();
        }

        return normalized.Length <= 180 ? normalized : normalized[..177] + "...";
    }

    private static string? ResolveActivitySummary(CodexTimelineEntryVm entry, CodexOutboundMessageKind kind, string? text)
    {
        if (IsRetryNotice(entry))
        {
            string details = entry.Subtitle ?? entry.Body ?? "Retrying.";
            if (ContainsAny(entry.Title, "visible output"))
            {
                return $"No visible output yet. {details}";
            }

            if (ContainsAny(entry.Title, "capacity"))
            {
                return $"Selected model is at capacity. {details}";
            }

            return details;
        }

        string? progress = kind == CodexOutboundMessageKind.Progress
            ? FormatInternalProgressEntry(entry)
            : null;
        if (!string.IsNullOrWhiteSpace(progress))
        {
            return progress;
        }

        if (string.Equals(entry.Type, "thread.tokenUsage.updated", StringComparison.OrdinalIgnoreCase))
        {
            return "Usage updated";
        }

        if (ContainsAny(entry.Type, "reasoning")
            || ContainsAny(entry.Title, "reasoning")
            || ContainsAny(entry.Subtitle, "reasoning")
            || ContainsAny(entry.Body, "reasoning"))
        {
            return "Thinking";
        }

        if (ContainsAny(entry.Type, "collabAgentToolCall")
            || ContainsAny(entry.Title, "collab")
            || ContainsAny(entry.Subtitle, "collab")
            || ContainsAny(entry.Body, "collabAgentToolCall"))
        {
            return "Tool call";
        }

        if (string.Equals(entry.Severity, "warning", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(entry.Type, "warning")
            || ContainsAny(entry.Title, "warning")
            || ContainsAny(entry.Subtitle, "warning")
            || ContainsAny(entry.Body, "warning"))
        {
            return "Warning";
        }

        return null;
    }

    private static bool TryGetMetadata(CodexTimelineEntryVm entry, string key, out string? value)
        => entry.Metadata.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value);

    private static string? GetMetadata(CodexTimelineEntryVm entry, string key)
        => entry.Metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static bool TryResolveExplicitMediaFile(CodexTimelineEntryVm entry, out string path, out string? contentType)
    {
        if (TryResolveExplicitMediaPath(entry, out path, out contentType))
        {
            return true;
        }

        return TryMaterializeExplicitMediaData(entry, out path, out contentType);
    }

    private static bool TryResolveExplicitMediaPath(CodexTimelineEntryVm entry, out string path, out string? contentType)
    {
        string? candidate = GetMetadata(entry, "path") ?? GetMetadata(entry, "result");
        contentType = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            path = string.Empty;
            return false;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            if (!uri.IsFile)
            {
                path = string.Empty;
                return false;
            }

            candidate = uri.LocalPath;
        }
        else if (!Path.IsPathRooted(candidate))
        {
            path = string.Empty;
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            path = string.Empty;
            return false;
        }

        if (!IsSupportedExplicitMediaExtension(fullPath))
        {
            path = string.Empty;
            return false;
        }

        path = fullPath;
        contentType = NormalizeSupportedContentType(GetMetadata(entry, "contentType")) ?? ResolveContentType(fullPath);
        return true;
    }

    private static bool TryMaterializeExplicitMediaData(CodexTimelineEntryVm entry, out string path, out string? contentType)
    {
        path = string.Empty;
        contentType = null;
        string? candidate = GetMetadata(entry, "result");
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        string encoded = candidate.Trim();
        string? declaredContentType = NormalizeSupportedContentType(GetMetadata(entry, "contentType"));
        const string dataPrefix = "data:";
        int commaIndex = encoded.IndexOf(',');
        if (encoded.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase) && commaIndex > dataPrefix.Length)
        {
            string header = encoded[dataPrefix.Length..commaIndex];
            if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string mediaType = header.Split(';', 2)[0];
            declaredContentType = NormalizeSupportedContentType(mediaType);
            encoded = encoded[(commaIndex + 1)..].Trim();
        }

        if (encoded.Length < 128)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(RemoveBase64Whitespace(encoded));
        }
        catch (FormatException)
        {
            return false;
        }

        contentType = ResolveImageContentType(bytes) ?? declaredContentType;
        if (contentType is null)
        {
            return false;
        }

        string extension = ExtensionForContentType(contentType);
        string fileName = ResolveMaterializedFileName(entry, extension);
        string directory = Path.Combine(Path.GetTempPath(), "codex-telegram", "outbound-artifacts");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, bytes);
        return true;
    }

    private static bool IsTelegramPhotoCandidate(string path, FileInfo? info)
    {
        if (info is not null && info.Length > MaxTelegramPhotoBytes)
        {
            return false;
        }

        return Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp";
    }

    private static bool IsSupportedExplicitMediaExtension(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".gif" or ".jpg" or ".jpeg" or ".png" or ".webp";

    private static string ResolveContentType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };

    private static string? NormalizeSupportedContentType(string? contentType)
        => string.IsNullOrWhiteSpace(contentType)
            ? null
            : contentType.Trim().ToLowerInvariant() switch
            {
                "image/gif" => "image/gif",
                "image/jpeg" or "image/jpg" => "image/jpeg",
                "image/png" => "image/png",
                "image/webp" => "image/webp",
                _ => null,
            };

    private static string? ResolveImageContentType(byte[] bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4e
            && bytes[3] == 0x47
            && bytes[4] == 0x0d
            && bytes[5] == 0x0a
            && bytes[6] == 0x1a
            && bytes[7] == 0x0a)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 3 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return "image/gif";
        }

        if (bytes.Length >= 12
            && bytes[0] == 0x52
            && bytes[1] == 0x49
            && bytes[2] == 0x46
            && bytes[3] == 0x46
            && bytes[8] == 0x57
            && bytes[9] == 0x45
            && bytes[10] == 0x42
            && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        return null;
    }

    private static string ExtensionForContentType(string contentType)
        => contentType switch
        {
            "image/gif" => ".gif",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".bin",
        };

    private static string ResolveMaterializedFileName(CodexTimelineEntryVm entry, string extension)
    {
        string rawId = GetMetadata(entry, "id")
            ?? entry.TurnId
            ?? Guid.NewGuid().ToString("n");
        string safeId = SanitizeFileName(rawId);
        return $"codex-image-{safeId}{extension}";
    }

    private static string SanitizeFileName(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                builder.Append(ch);
            }
        }

        return builder.Length == 0 ? Guid.NewGuid().ToString("n") : builder.ToString();
    }

    private static string RemoveBase64Whitespace(string value)
    {
        if (!value.Any(char.IsWhiteSpace))
        {
            return value;
        }

        StringBuilder builder = new(value.Length);
        foreach (char ch in value)
        {
            if (!char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

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

    private static string NormalizeText(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static string FormatCompactActivity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "waiting for Codex";
        }

        string normalized = NormalizeText(text);
        normalized = string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "waiting for Codex";
        }

        const int maxActivityCharacters = 90;
        return normalized.Length <= maxActivityCharacters
            ? normalized
            : normalized[..(maxActivityCharacters - 1)].TrimEnd() + "...";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return "<1m";
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes)).ToString(CultureInfo.InvariantCulture)}m";
        }

        int hours = (int)duration.TotalHours;
        int minutes = duration.Minutes;
        return minutes == 0
            ? $"{hours.ToString(CultureInfo.InvariantCulture)}h"
            : $"{hours.ToString(CultureInfo.InvariantCulture)}h {minutes.ToString(CultureInfo.InvariantCulture)}m";
    }

    private sealed record TelegramLiveTurnCardKey(
        string ThreadId,
        TelegramConversationScope Conversation);

    private sealed class TelegramCompactPulseState
    {
        private readonly object _gate = new();
        private string? _turnId;
        private DateTimeOffset _startedUtc;
        private DateTimeOffset? _lastPulseUtc;

        public TelegramCompactPulseState(string? turnId, DateTimeOffset startedUtc)
        {
            _turnId = NormalizeTurnId(turnId);
            _startedUtc = startedUtc;
        }

        public bool MatchesTurn(string turnId)
        {
            lock (_gate)
            {
                return string.Equals(_turnId, NormalizeTurnId(turnId), StringComparison.Ordinal);
            }
        }

        public bool TryMarkPulse(string? turnId, DateTimeOffset now, TimeSpan interval, out TimeSpan activeFor)
        {
            lock (_gate)
            {
                string? normalizedTurnId = NormalizeTurnId(turnId);
                if (!string.Equals(_turnId, normalizedTurnId, StringComparison.Ordinal))
                {
                    _turnId = normalizedTurnId;
                    _startedUtc = now;
                    _lastPulseUtc = null;
                }

                activeFor = now - _startedUtc;
                if (_lastPulseUtc is { } lastPulse && now - lastPulse < interval)
                {
                    return false;
                }

                _lastPulseUtc = now;
                return true;
            }
        }

        private static string? NormalizeTurnId(string? turnId)
            => string.IsNullOrWhiteSpace(turnId) ? null : turnId.Trim();
    }

    private sealed record TelegramLiveTurnCardSnapshot(
        string ThreadId,
        string TurnId,
        TelegramConversationScope Conversation,
        int? MessageId,
        DateTimeOffset? LastEditUtc,
        string StateText,
        string? Latest,
        string? Activity,
        bool TurnChanged,
        int UpdateCount,
        int ProgressCount,
        int ArtifactCount,
        bool FinalResponseCaptured,
        bool TerminalEventSeen,
        bool Interrupted,
        bool Failed);

    private sealed class TelegramLiveTurnCardState
    {
        private readonly object _gate = new();
        private string TurnId { get; set; }
        private string? _latest;
        private string? _activity;
        private int? _messageId;
        private DateTimeOffset? _lastEditUtc;
        private int _updateCount;
        private int _progressCount;
        private int _artifactCount;
        private bool _finalResponseCaptured;
        private bool _terminalEventSeen;
        private bool _interrupted;
        private bool _failed;

        public TelegramLiveTurnCardState(string threadId, string turnId, TelegramConversationScope conversation)
        {
            ThreadId = threadId;
            TurnId = turnId;
            Conversation = conversation;
        }

        private string ThreadId { get; }

        private TelegramConversationScope Conversation { get; }

        public TelegramLiveTurnCardSnapshot Record(
            CodexTimelineEntryVm entry,
            CodexOutboundMessageKind kind,
            string? text,
            bool terminal)
        {
            lock (_gate)
            {
                string entryTurnId = ResolveLiveTurnId(entry);
                bool turnChanged = !string.Equals(TurnId, entryTurnId, StringComparison.Ordinal);
                if (turnChanged)
                {
                    TurnId = entryTurnId;
                    _latest = null;
                    _activity = null;
                    _updateCount = 0;
                    _progressCount = 0;
                    _artifactCount = 0;
                    _finalResponseCaptured = false;
                    _terminalEventSeen = false;
                    _interrupted = false;
                    _failed = false;
                }

                if (kind == CodexOutboundMessageKind.Progress || entry.IsInternal || IsRetryNotice(entry))
                {
                    _progressCount++;
                }
                else if (kind == CodexOutboundMessageKind.Update)
                {
                    _updateCount++;
                }

                if (entry.Metadata.ContainsKey("explicitMediaKind"))
                {
                    _artifactCount++;
                }

                if (IsFinalResponse(entry) || (string.Equals(entry.Type, TurnCompletedType, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(text)))
                {
                    _finalResponseCaptured = true;
                }

                if (terminal)
                {
                    _terminalEventSeen = true;
                    _activity = null;
                }

                if (string.Equals(entry.Type, TurnInterruptedType, StringComparison.OrdinalIgnoreCase))
                {
                    _interrupted = true;
                }

                if (string.Equals(entry.Type, TurnFailedType, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Severity, "danger", StringComparison.OrdinalIgnoreCase))
                {
                    _failed = true;
                }

                if (entry.IsInternal || IsRetryNotice(entry))
                {
                    _activity = ResolveActivitySummary(entry, kind, text) ?? _activity;
                }
                else
                {
                    _latest = ResolveVisibleSummary(entry, kind, text) ?? _latest;
                }

                return Snapshot(turnChanged);
            }
        }

        public TelegramLiveTurnCardSnapshot RecordActivity(string activity)
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(activity))
                {
                    _activity = activity.Trim();
                }

                _progressCount++;
                return Snapshot(turnChanged: false);
            }
        }

        public void MarkEdited(int? messageId, DateTimeOffset editedUtc)
        {
            lock (_gate)
            {
                _messageId = messageId;
                _lastEditUtc = editedUtc;
            }
        }

        private TelegramLiveTurnCardSnapshot Snapshot(bool turnChanged)
            => new(
                ThreadId,
                TurnId,
                Conversation,
                _messageId,
                _lastEditUtc,
                _terminalEventSeen ? _failed ? "failed" : _interrupted ? "interrupted" : "completed" : "working",
                _latest,
                _activity,
                turnChanged,
                _updateCount,
                _progressCount,
                _artifactCount,
                _finalResponseCaptured,
                _terminalEventSeen,
                _interrupted,
                _failed);
    }

    private static bool IsRetryNotice(CodexTimelineEntryVm entry)
        => string.Equals(entry.Type, "turn.retry", StringComparison.OrdinalIgnoreCase);

    private sealed record AgentMessageFlush(string FullText, string? PublishedText, string? UnpublishedText, bool PublishedAny);

    private sealed class AgentMessageProgressBuffer
    {
        private readonly object _gate = new();
        private string _text = string.Empty;
        private string _publishedText = string.Empty;
        private int _publishedLength;

        public string? Append(string delta, int minChars, int maxChars)
        {
            lock (_gate)
            {
                _text += NormalizeDeltaForAppend(_text, delta);

                int boundary = FindPublishBoundary(_text, _publishedLength, minChars, maxChars);
                if (boundary <= _publishedLength)
                {
                    return null;
                }

                string segment = _text[_publishedLength..boundary];
                _publishedLength = boundary;
                string? published = FormatLiveAgentProgress(segment);
                if (!string.IsNullOrWhiteSpace(published))
                {
                    _publishedText += published;
                }

                return published;
            }
        }

        private static string NormalizeDeltaForAppend(string existingText, string delta)
        {
            if (!ShouldInsertSentenceSeparator(existingText, delta))
            {
                return delta;
            }

            return " " + delta;
        }

        private static bool ShouldInsertSentenceSeparator(string existingText, string delta)
        {
            if (string.IsNullOrEmpty(existingText) || string.IsNullOrEmpty(delta))
            {
                return false;
            }

            if (char.IsWhiteSpace(existingText[^1]) || char.IsWhiteSpace(delta[0]))
            {
                return false;
            }

            if (existingText[^1] is not ('.' or '!' or '?'))
            {
                return false;
            }

            return StartsLikeSentence(delta);
        }

        private static bool StartsLikeSentence(string value)
        {
            int index = 0;
            while (index < value.Length && IsLeadingPunctuation(value[index]))
            {
                index++;
            }

            if (index >= value.Length)
            {
                return false;
            }

            if (!char.IsUpper(value[index]))
            {
                return false;
            }

            if (index + 1 >= value.Length)
            {
                return true;
            }

            char next = value[index + 1];
            return char.IsLower(next)
                || next is '\''
                || next is '\u2018'
                || next is '\u2019'
                || next is '\u201c'
                || next is '\u201d';
        }

        private static bool IsLeadingPunctuation(char ch)
            => ch is '\''
                or '"'
                or '\u2018'
                or '\u2019'
                or '\u201c'
                or '\u201d'
                or '('
                or '['
                or '{';

        public AgentMessageFlush Flush()
        {
            lock (_gate)
            {
                string unpublished = _publishedLength >= _text.Length
                    ? string.Empty
                    : _text[_publishedLength..];
                return new AgentMessageFlush(_text, _publishedText, CleanAgentProgress(unpublished), _publishedLength > 0);
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
                if (boundary < minimumBoundary)
                {
                    continue;
                }

                if (!requireFollowingWhitespace)
                {
                    return boundary;
                }

                if (boundary < text.Length)
                {
                    if (char.IsWhiteSpace(text[boundary]))
                    {
                        return boundary;
                    }

                    continue;
                }

                char punctuation = text[boundary - 1];
                char preceding = boundary >= 2 ? text[boundary - 2] : '\0';
                if (char.IsWhiteSpace(preceding))
                {
                    continue;
                }

                if (punctuation == '.' && char.IsDigit(preceding))
                {
                    continue;
                }

                return boundary;
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
