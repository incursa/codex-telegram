using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Logging;

namespace Incursa.Codex.Telegram.Telegram;

public interface ITelegramQueuedPromptProcessor
{
    Task<bool> ProcessNextAsync(CancellationToken cancellationToken);
}

internal sealed class TelegramQueuedPromptProcessor : ITelegramQueuedPromptProcessor
{
    private readonly ITelegramBotStateStore _stateStore;
    private readonly ICodexSessionManager _sessionManager;
    private readonly ICodexTurnExecutionCoordinator _turnCoordinator;
    private readonly ITelegramThreadFollowRegistry _followRegistry;
    private readonly IOutboundTelegramQueue _outboundQueue;
    private readonly ITelegramBotMessageSender _sender;
    private readonly ILogger<TelegramQueuedPromptProcessor> _logger;

    public TelegramQueuedPromptProcessor(
        ITelegramBotStateStore stateStore,
        ICodexSessionManager sessionManager,
        ICodexTurnExecutionCoordinator turnCoordinator,
        ITelegramThreadFollowRegistry followRegistry,
        IOutboundTelegramQueue outboundQueue,
        ITelegramBotMessageSender sender,
        ILogger<TelegramQueuedPromptProcessor> logger)
    {
        _stateStore = stateStore;
        _sessionManager = sessionManager;
        _turnCoordinator = turnCoordinator;
        _followRegistry = followRegistry;
        _outboundQueue = outboundQueue;
        _sender = sender;
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
            TryDeleteAttachments(prompt.Attachments);
            await _sender.SendTextMessageAsync(
                prompt.ConversationScope,
                $"Queued message for {prompt.SessionName} was skipped because the session is no longer available.",
                null,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        try
        {
            if (_turnCoordinator.HasActiveTurnForThread(prompt.SessionId)
                || HasPendingOutboundForConversation(outboundStatus, prompt.ConversationScope))
            {
                await _stateStore.EnqueueQueuedPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
                return false;
            }

            _followRegistry.FollowThread(prompt.ConversationScope, prompt.SessionId);
            CodexThreadExecutionVm execution = prompt.Attachments is { Count: > 0 }
                ? await _sessionManager.SendAsync(
                    prompt.SessionId,
                    TelegramAttachmentInputBuilder.BuildInputItems(prompt.Text, prompt.Attachments),
                    cancellationToken).ConfigureAwait(false)
                : await _sessionManager.SendAsync(prompt.SessionId, prompt.Text, cancellationToken).ConfigureAwait(false);
            _followRegistry.FollowThread(prompt.ConversationScope, execution.ThreadId);
            TryDeleteAttachments(prompt.Attachments);
            return true;
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("already active", StringComparison.OrdinalIgnoreCase))
        {
            await _stateStore.EnqueueQueuedPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Queued prompt {PromptId} for session {SessionId} was requeued because another turn started first.", prompt.Id, prompt.SessionId);
            return false;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Queued prompt {PromptId} for session {SessionId} failed to start.", prompt.Id, prompt.SessionId);
            TryDeleteAttachments(prompt.Attachments);
            await _sender.SendTextMessageAsync(
                prompt.ConversationScope,
                $"Queued message for {session.Name} failed to start: {exception.Message}",
                null,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
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
}
