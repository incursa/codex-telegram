using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Logging;

namespace Incursa.Codex.Telegram.Telegram;

internal interface ITelegramQueuedPromptProcessor
{
    Task<bool> ProcessNextAsync(CancellationToken cancellationToken);
}

internal sealed class TelegramQueuedPromptProcessor : ITelegramQueuedPromptProcessor
{
    private readonly ITelegramBotStateStore _stateStore;
    private readonly ICodexSessionManager _sessionManager;
    private readonly ICodexTurnExecutionCoordinator _turnCoordinator;
    private readonly ITelegramThreadFollowRegistry _followRegistry;
    private readonly ITelegramTypingIndicatorRegistry _typingIndicatorRegistry;
    private readonly IOutboundTelegramQueue _outboundQueue;
    private readonly ITelegramBotMessageSender _sender;
    private readonly ITelegramDebugTraceStore _traceStore;
    private readonly ILogger<TelegramQueuedPromptProcessor> _logger;

    public TelegramQueuedPromptProcessor(
        ITelegramBotStateStore stateStore,
        ICodexSessionManager sessionManager,
        ICodexTurnExecutionCoordinator turnCoordinator,
        ITelegramThreadFollowRegistry followRegistry,
        ITelegramTypingIndicatorRegistry typingIndicatorRegistry,
        IOutboundTelegramQueue outboundQueue,
        ITelegramBotMessageSender sender,
        ITelegramDebugTraceStore traceStore,
        ILogger<TelegramQueuedPromptProcessor> logger)
    {
        _stateStore = stateStore;
        _sessionManager = sessionManager;
        _turnCoordinator = turnCoordinator;
        _followRegistry = followRegistry;
        _typingIndicatorRegistry = typingIndicatorRegistry;
        _outboundQueue = outboundQueue;
        _sender = sender;
        _traceStore = traceStore;
        _logger = logger;
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        TelegramOutboundQueueStatus outboundStatus = await _outboundQueue.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        TelegramQueuedPrompt? prompt = await _stateStore.DequeueNextQueuedPromptAsync(
            _turnCoordinator.GetActiveThreadIds(),
            GetPendingOutboundConversations(outboundStatus),
            cancellationToken).ConfigureAwait(false);
        if (prompt is null)
        {
            return false;
        }

        CodexSessionSummary? session = await _sessionManager.GetSessionAsync(prompt.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            await RecordQueuedPromptFailureAsync(prompt, "session_missing", "The target session is no longer available.", cancellationToken).ConfigureAwait(false);
            TryDeleteAttachments(prompt.Attachments);
            await _sender.SendTextMessageAsync(
                prompt.ConversationScope,
                $"Queued message for {prompt.SessionName} was skipped because the session is no longer available.",
                null,
                cancellationToken,
                CreateDebugContext("queued-worker", prompt.SessionId, kind: "skipped")).ConfigureAwait(false);
            return true;
        }

        string? selectedSessionId = await _stateStore.GetActiveSessionIdAsync(prompt.ConversationScope, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(selectedSessionId, prompt.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            await RecordQueuedPromptFailureAsync(prompt, "session_changed", "The conversation now points at another session.", cancellationToken).ConfigureAwait(false);
            TryDeleteAttachments(prompt.Attachments);
            await _sender.SendTextMessageAsync(
                prompt.ConversationScope,
                $"Queued message for {prompt.SessionName} was skipped because this conversation now points at another session.",
                null,
                cancellationToken,
                CreateDebugContext("queued-worker", prompt.SessionId, kind: "skipped")).ConfigureAwait(false);
            return true;
        }

        try
        {
            if (_turnCoordinator.HasActiveTurnForThread(prompt.SessionId)
                || IsLive(session)
                || HasPendingOutboundForConversation(outboundStatus, prompt.ConversationScope))
            {
                await _stateStore.EnqueueQueuedPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
                return false;
            }

            string? missingAttachment = FindMissingAttachment(prompt.Attachments);
            if (!string.IsNullOrWhiteSpace(missingAttachment))
            {
                string error = $"Queued attachment is no longer available: {missingAttachment}";
                await RecordQueuedPromptFailureAsync(prompt, "attachment_missing", error, cancellationToken).ConfigureAwait(false);
                TryDeleteAttachments(prompt.Attachments);
                await _sender.SendTextMessageAsync(
                    prompt.ConversationScope,
                    $"Queued message for {session.Name} failed because an attachment is no longer available. Send the attachment again and retry.",
                    null,
                    cancellationToken,
                    CreateDebugContext("queued-worker", prompt.SessionId, kind: "failed")).ConfigureAwait(false);
                return true;
            }

            _followRegistry.FollowThread(prompt.ConversationScope, prompt.SessionId);
            using IDisposable typingRegistration = _typingIndicatorRegistry.Track(prompt.ConversationScope);
            await _sender.SendTextMessageAsync(
                prompt.ConversationScope,
                prompt.PlanMode
                    ? $"Starting queued Plan mode request for {session.Name}. Live updates will stream here."
                    : $"Starting queued message for {session.Name}. Live updates will stream here.",
                null,
                cancellationToken,
                CreateDebugContext("queued-worker", prompt.SessionId, kind: "start", traceId: prompt.TraceId)).ConfigureAwait(false);
            await _sender.SendTypingActionAsync(prompt.ConversationScope, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<CodexInputItem>? attachmentInput = prompt.Attachments is { Count: > 0 }
                ? TelegramAttachmentInputBuilder.BuildInputItems(prompt.Text, prompt.Attachments)
                : null;
            string traceId = prompt.TraceId ?? _traceStore.CreateTraceId();
            string traceKind = prompt.PlanMode ? "codex.plan.start" : "codex.send.start";
            await _traceStore.RecordAsync(
                new TelegramDebugTraceEvent(
                    traceId,
                    DateTimeOffset.UtcNow,
                    traceKind,
                    SessionId: prompt.SessionId,
                    ChatId: prompt.ChatId,
                    MessageThreadId: prompt.MessageThreadId,
                    UserId: prompt.UserId,
                    Direction: "codex",
                    Status: "queued",
                    TextLength: prompt.Text.Length,
                    AttachmentCount: prompt.Attachments?.Count ?? 0,
                    InputItemCount: attachmentInput?.Count ?? 1,
                    OutboundQueueItemId: prompt.Id,
                    Metadata: BuildQueuedPromptTraceMetadata(prompt.Attachments),
                    TextBody: prompt.Text),
                cancellationToken).ConfigureAwait(false);
            CodexThreadExecutionVm execution = prompt.Attachments is { Count: > 0 }
                ? await _sessionManager.SendAsync(
                    prompt.SessionId,
                    attachmentInput!,
                    cancellationToken).ConfigureAwait(false)
                : prompt.PlanMode
                    ? await _sessionManager.SendPlanAsync(prompt.SessionId, prompt.Text, cancellationToken).ConfigureAwait(false)
                : await _sessionManager.SendAsync(prompt.SessionId, prompt.Text, cancellationToken).ConfigureAwait(false);
            _followRegistry.FollowThread(prompt.ConversationScope, execution.ThreadId);
            await _traceStore.BindTurnAsync(
                traceId,
                execution.ThreadId,
                execution.TurnId,
                cancellationToken).ConfigureAwait(false);
            // Codex may read local image paths after SendAsync returns.
            return true;
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("already active", StringComparison.OrdinalIgnoreCase))
        {
            await _stateStore.EnqueueQueuedPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Queued prompt {PromptId} for session {SessionId} was requeued because another turn started first.", prompt.Id, prompt.SessionId);
            await _sender.SendTextMessageAsync(
                prompt.ConversationScope,
                $"Queued message for {session.Name} is still queued because another Codex turn started first.",
                null,
                cancellationToken,
                CreateDebugContext("queued-worker", prompt.SessionId, kind: "requeued")).ConfigureAwait(false);
            return false;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Queued prompt {PromptId} for session {SessionId} failed to start.", prompt.Id, prompt.SessionId);
            await RecordQueuedPromptFailureAsync(prompt, "send_failed", exception.Message, cancellationToken).ConfigureAwait(false);
            TryDeleteAttachments(prompt.Attachments);
            await _sender.SendTextMessageAsync(
                prompt.ConversationScope,
                $"Queued message for {session.Name} failed to start: {exception.Message}",
                null,
                cancellationToken,
                CreateDebugContext("queued-worker", prompt.SessionId, kind: "failed")).ConfigureAwait(false);
            return true;
        }
    }

    private TelegramDebugMessageContext CreateDebugContext(string source, string sessionId, string? kind = null, string? traceId = null)
        => new(source, sessionId, null, _turnCoordinator.GetActiveTurnId(sessionId), kind, TraceId: traceId);

    private Task RecordQueuedPromptFailureAsync(
        TelegramQueuedPrompt prompt,
        string status,
        string error,
        CancellationToken cancellationToken)
        => _traceStore.RecordAsync(
            new TelegramDebugTraceEvent(
                prompt.TraceId ?? _traceStore.CreateTraceId(),
                DateTimeOffset.UtcNow,
                "telegram.input.failed",
                SessionId: prompt.SessionId,
                ChatId: prompt.ChatId,
                MessageThreadId: prompt.MessageThreadId,
                UserId: prompt.UserId,
                Direction: "queued",
                Status: status,
                TextLength: prompt.Text.Length,
                AttachmentCount: prompt.Attachments?.Count ?? 0,
                OutboundQueueItemId: prompt.Id,
                Error: error,
                TextBody: prompt.Text),
            cancellationToken);

    private static IReadOnlyDictionary<string, string>? BuildQueuedPromptTraceMetadata(
        IReadOnlyList<TelegramAttachmentDescriptor>? attachments)
    {
        if (attachments is not { Count: > 0 })
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["attachmentTypes"] = string.Join(
                ",",
                attachments.Select(attachment => attachment.IsImage ? "image" : attachment.ContentType ?? "file")),
            ["attachmentNames"] = string.Join(",", attachments.Select(attachment => attachment.FileName)),
            ["attachmentPaths"] = string.Join(",", attachments.Select(attachment => attachment.FilePath)),
        };
    }

    private static string? FindMissingAttachment(IReadOnlyList<TelegramAttachmentDescriptor>? attachments)
    {
        foreach (TelegramAttachmentDescriptor attachment in attachments ?? [])
        {
            if (!File.Exists(attachment.FilePath))
            {
                return attachment.FilePath;
            }
        }

        return null;
    }

    private static void TryDeleteAttachments(IReadOnlyList<TelegramAttachmentDescriptor>? attachments)
    {
        foreach (TelegramAttachmentDescriptor attachment in attachments ?? [])
        {
            try
            {
                File.Delete(attachment.FilePath);
            }
            catch
            {
            }
        }
    }

    private static IReadOnlyCollection<TelegramConversationScope> GetPendingOutboundConversations(TelegramOutboundQueueStatus status)
        => status.Destinations
            .Where(HasPendingOutbound)
            .Select(destination => new TelegramConversationScope(destination.ChatId, destination.MessageThreadId))
            .Distinct()
            .ToArray();

    private static bool HasPendingOutboundForConversation(TelegramOutboundQueueStatus status, TelegramConversationScope conversation)
        => status.Destinations.Any(destination =>
            destination.ChatId == conversation.ChatId
            && destination.MessageThreadId == conversation.MessageThreadId
            && HasPendingOutbound(destination));

    private static bool HasPendingOutbound(TelegramOutboundDestinationStatus destination)
        => destination.PendingMessageCount > 0 || destination.PendingChunkCount > 0;

    private static bool IsLive(CodexSessionSummary session)
        => session.Status is CodexSessionStatus.Running or CodexSessionStatus.Starting;
}
