using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

internal interface ITelegramBotMessageSender
{
    Task SendTextMessageAsync(
        TelegramConversationScope conversation,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext = null);

    Task EditTextMessageAsync(
        TelegramConversationScope conversation,
        int messageId,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken,
        TelegramDebugMessageContext? debugContext = null);

    Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken);

    Task AcknowledgeMessageAsync(TelegramMessageAcknowledgement acknowledgement, CancellationToken cancellationToken);

    Task ReactToMessageAsync(TelegramMessageReaction reaction, CancellationToken cancellationToken);

    Task SendTypingActionAsync(TelegramConversationScope conversation, CancellationToken cancellationToken);
}

internal interface ITelegramCodexBotUpdateHandler
{
    Task HandleMessageAsync(
        TelegramInboundMessage message,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken);

    Task HandleCallbackAsync(
        TelegramInboundCallback callback,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken);
}

internal sealed record TelegramInboundMessage(
    long UserId,
    long ChatId,
    string ChatType,
    string? Text,
    int? MessageThreadId = null,
    string? AudioFilePath = null,
    IReadOnlyList<TelegramAttachmentDescriptor>? Attachments = null,
    int? SourceMessageId = null,
    TelegramReplyContext? ReplyContext = null)
{
    public TelegramConversationScope ConversationScope => new(ChatId, MessageThreadId);
}

internal sealed record TelegramInboundCallback(
    string Id,
    long UserId,
    long ChatId,
    string ChatType,
    string? Data,
    int? MessageThreadId = null,
    int? SourceMessageId = null)
{
    public TelegramConversationScope ConversationScope => new(ChatId, MessageThreadId);
}

internal sealed record TelegramReplyButton(string Text, string CallbackData);

internal sealed class TelegramCodexBotCommandHandler : ITelegramCodexBotUpdateHandler
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private const int DefaultTailLineCount = 40;
    private const int DefaultSessionListLimit = 8;
    private const int QueuedPromptPreviewLength = 160;
    private const int ReplyContextPreviewLength = 1_200;
    private static readonly TimeSpan TelegramSendStartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InlineUsageSummaryTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StatusUsageSummaryTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan InlineUsageSummaryCacheDuration = TimeSpan.FromSeconds(30);

    private readonly TelegramCommandParser _parser;
    private readonly TelegramMessageChunker _chunker;
    private readonly ICodexSessionManager _sessionManager;
    private readonly ICodexAccountUsageService _accountUsageService;
    private readonly ICodexProjectCatalogStore _projectCatalogStore;
    private readonly CodexWorkspaceBrowser _workspaceBrowser;
    private readonly ITelegramBotStateStore _stateStore;
    private readonly ICodexTurnExecutionCoordinator _turnCoordinator;
    private readonly ITelegramThreadFollowRegistry _followRegistry;
    private readonly ITelegramTypingIndicatorRegistry _typingIndicatorRegistry;
    private readonly ITelegramTurnReactionRegistry _turnReactionRegistry;
    private readonly ITelegramDebugPreambleMode _debugPreambleMode;
    private readonly ITelegramForumTopicService _topicService;
    private readonly IAudioTranscriptionService _audioTranscriptionService;
    private readonly IOutboundTelegramQueue _outboundQueue;
    private readonly TelegramBotOptions _options;
    private readonly ILogger<TelegramCodexBotCommandHandler> _logger;
    private readonly SemaphoreSlim _usageSummaryLock = new(1, 1);
    private bool _hasCachedUsageSummary;
    private string? _cachedUsageSummary;
    private DateTimeOffset _cachedUsageSummaryExpiresAtUtc;

    public TelegramCodexBotCommandHandler(
        TelegramCommandParser parser,
        TelegramMessageChunker chunker,
        ICodexSessionManager sessionManager,
        ICodexAccountUsageService accountUsageService,
        ICodexProjectCatalogStore projectCatalogStore,
        CodexWorkspaceBrowser workspaceBrowser,
        ITelegramBotStateStore stateStore,
        ICodexTurnExecutionCoordinator turnCoordinator,
        ITelegramThreadFollowRegistry followRegistry,
        ITelegramTypingIndicatorRegistry typingIndicatorRegistry,
        ITelegramTurnReactionRegistry turnReactionRegistry,
        ITelegramDebugPreambleMode debugPreambleMode,
        ITelegramForumTopicService topicService,
        IAudioTranscriptionService audioTranscriptionService,
        IOutboundTelegramQueue outboundQueue,
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramCodexBotCommandHandler> logger)
    {
        _parser = parser;
        _chunker = chunker;
        _sessionManager = sessionManager;
        _accountUsageService = accountUsageService;
        _projectCatalogStore = projectCatalogStore;
        _workspaceBrowser = workspaceBrowser;
        _stateStore = stateStore;
        _turnCoordinator = turnCoordinator;
        _followRegistry = followRegistry;
        _typingIndicatorRegistry = typingIndicatorRegistry;
        _turnReactionRegistry = turnReactionRegistry;
        _debugPreambleMode = debugPreambleMode;
        _topicService = topicService;
        _audioTranscriptionService = audioTranscriptionService;
        _outboundQueue = outboundQueue;
        _options = options.Value;
        _logger = logger;
    }

    public async Task HandleMessageAsync(
        TelegramInboundMessage message,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        ParsedTelegramCommand command = _parser.Parse(message.Text);
        bool isAuthorized = await IsAuthorizedAsync(message, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Parsed Telegram message for chat {ChatId} topic {MessageThreadId}; command: {IsCommand}; command name: {CommandName}; text length: {TextLength}; has audio path: {HasAudioPath}; attachments: {AttachmentCount}.",
            message.ChatId,
            message.MessageThreadId,
            command.IsCommand,
            string.IsNullOrWhiteSpace(command.Name) ? "<none>" : command.Name,
            command.Text.Length,
            !string.IsNullOrWhiteSpace(message.AudioFilePath),
            message.Attachments?.Count ?? 0);

        if (!isAuthorized && !IsWhoAmI(command) && !CanRunChatTrustSetup(message, command))
        {
            if (IsAllowedUser(message.UserId) && !IsPrivateChat(message) && command.IsCommand)
            {
                await ReplyAsync(
                    sender,
                    message,
                    "This chat is not trusted yet. Send /trust here from an allowlisted admin account, or continue in a private chat.",
                    null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            _logger.LogWarning("Ignoring unauthorized Telegram user {UserId}.", message.UserId);
            return;
        }

        if (!command.IsCommand && !CanRoutePlainText(message))
        {
            _logger.LogInformation(
                "Ignoring non-command Telegram message in chat {ChatId} of type {ChatType} without a topic thread.",
                message.ChatId,
                message.ChatType);
            await ReplyAsync(sender, message, BuildNotRoutedMessage(message), null, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(message.AudioFilePath))
            {
                await HandleAudioMessageAsync(message, sender, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!command.IsCommand)
            {
                _logger.LogDebug(
                    "Routing plain Telegram message for chat {ChatId} topic {MessageThreadId}; text length {TextLength}; attachments {AttachmentCount}.",
                    message.ChatId,
                    message.MessageThreadId,
                    command.Text.Length,
                    message.Attachments?.Count ?? 0);
                await SendToActiveSessionAsync(message, command.Text, sender, cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (command.Name)
            {
                case "help":
                    await ReplyAsync(sender, message, BuildHelpText(), null, cancellationToken).ConfigureAwait(false);
                    break;
                case "whoami":
                    await ReplyAsync(
                        sender,
                        message,
                        $"Telegram user ID: {message.UserId}{Environment.NewLine}Chat ID: {message.ChatId}{Environment.NewLine}Topic thread ID: {(message.MessageThreadId?.ToString(CultureInfo.InvariantCulture) ?? "(none)")}",
                        null,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "version":
                    await ReplyAsync(sender, message, BuildVersionText(), null, cancellationToken).ConfigureAwait(false);
                    break;
                case "trust":
                    await HandleTrustAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "projects":
                    await HandleProjectsAsync(message, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "project":
                    await HandleProjectAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "topic":
                    await HandleTopicAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "topics":
                case "threads":
                    await HandleTopicListAsync(message, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "sessions":
                    await HandleSessionsAsync(message, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "new":
                    await HandleNewAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "use":
                    await HandleUseAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "send":
                    await HandleSendCommandAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "steer":
                    await HandleSteerAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "queue":
                case "queued":
                    await HandleQueueAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "model":
                    await HandleModelAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "thinking":
                    await HandleThinkingAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "goal":
                    await HandleGoalAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "tail":
                    await HandleTailAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "status":
                    await HandleStatusAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "usage":
                    await HandleUsageAsync(message, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "doctor":
                case "diag":
                case "diagnostics":
                    await HandleDoctorAsync(message, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "debug":
                    await HandleDebugAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "outbound":
                    await HandleOutboundAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "stop":
                    await HandleStopAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "restart":
                    await HandleRestartAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "kill":
                    await HandleKillAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "rename":
                    await HandleRenameAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "forget":
                    await HandleForgetAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await ReplyAsync(sender, message, "Unknown command. Send /help for the supported commands.", null, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            string action = command.IsCommand
                ? $"command {command.Name}"
                : !string.IsNullOrWhiteSpace(message.AudioFilePath)
                    ? "audio message"
                    : message.Attachments is { Count: > 0 } ? "attachment message" : "message";
            _logger.LogError(exception, "Telegram {Action} failed for user {UserId}.", action, message.UserId);
            await ReactToSourceMessageAsync(sender, message, TelegramMessageReactionKind.Failed, cancellationToken).ConfigureAwait(false);
            await ReplyAsync(sender, message, $"{FormatActionFailurePrefix(command, message)} failed: {exception.Message}", null, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task HandleCallbackAsync(
        TelegramInboundCallback callback,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(callback, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Ignoring unauthorized Telegram callback user {UserId}.", callback.UserId);
            await sender.AnswerCallbackQueryAsync(callback.Id, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        string[] parts = SplitCallbackData(callback.Data);
        if (parts.Length != 2)
        {
            await sender.AnswerCallbackQueryAsync(callback.Id, "Unsupported action.", cancellationToken).ConfigureAwait(false);
            return;
        }

        TelegramInboundMessage callbackMessage = ToMessage(callback);
        try
        {
            switch (parts[0])
            {
                case "nav":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Opening menu.", cancellationToken).ConfigureAwait(false);
                    await HandleNavigationAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "use":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Selected.", cancellationToken).ConfigureAwait(false);
                    await HandleUseAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "project":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Selected project.", cancellationToken).ConfigureAwait(false);
                    await HandleProjectSelectAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "status":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Status.", cancellationToken).ConfigureAwait(false);
                    await HandleStatusAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "model":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Model settings.", cancellationToken).ConfigureAwait(false);
                    await HandleModelMenuAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "modelset":
                case "ms":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Updated model.", cancellationToken).ConfigureAwait(false);
                    await HandleModelSelectionAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "thinking":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Thinking settings.", cancellationToken).ConfigureAwait(false);
                    await HandleThinkingMenuAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "thinkingset":
                case "ts":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Updated thinking.", cancellationToken).ConfigureAwait(false);
                    await HandleThinkingSelectionAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "back":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Back.", cancellationToken).ConfigureAwait(false);
                    await HandleStatusAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "tail":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Tail.", cancellationToken).ConfigureAwait(false);
                    await HandleTailAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "qnow":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Sending queued item.", cancellationToken).ConfigureAwait(false);
                    await HandleQueueSendNowCallbackAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "qedit":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Edit instructions sent.", cancellationToken).ConfigureAwait(false);
                    await HandleQueueEditCallbackAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "qdel":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Deleting queued item.", cancellationToken).ConfigureAwait(false);
                    await HandleQueueDeleteCallbackAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "stop":
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Stopping.", cancellationToken).ConfigureAwait(false);
                    await HandleStopAsync(callbackMessage, parts[1], sender, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await sender.AnswerCallbackQueryAsync(callback.Id, "Unsupported action.", cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Telegram callback {CallbackData} failed for user {UserId}.", callback.Data, callback.UserId);
            await sender.AnswerCallbackQueryAsync(callback.Id, "Action failed.", cancellationToken).ConfigureAwait(false);
            await ReplyAsync(sender, callbackMessage, $"Action failed: {exception.Message}", null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleProjectsAsync(TelegramInboundMessage message, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectChoice> projects = await ListProjectChoicesAsync(cancellationToken).ConfigureAwait(false);
        string? activeProject = await _stateStore.GetActiveProjectWorkingDirectoryAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, FormatProjects(projects, activeProject), BuildProjectButtons(projects), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleNavigationAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        string target = arguments.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            await ReplyAsync(sender, message, "Usage: tap one of the navigation buttons.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (target)
        {
            case "projects":
                await HandleProjectsAsync(message, sender, cancellationToken).ConfigureAwait(false);
                return;
            case "sessions":
                await HandleSessionsAsync(message, sender, cancellationToken).ConfigureAwait(false);
                return;
            case "topics":
                await HandleTopicListAsync(message, sender, cancellationToken).ConfigureAwait(false);
                return;
            case "current":
                await HandleTopicCurrentAsync(message, sender, cancellationToken).ConfigureAwait(false);
                return;
            case "new":
                await HandleTopicNewFromMenuAsync(message, sender, cancellationToken).ConfigureAwait(false);
                return;
            case "help":
                await ReplyAsync(sender, message, BuildHelpText(), null, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await ReplyAsync(sender, message, "Unsupported navigation action.", null, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task HandleTrustAsync(
        TelegramInboundMessage message,
        string arguments,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedUser(message.UserId))
        {
            _logger.LogWarning("Ignoring chat trust request from unauthorized Telegram user {UserId}.", message.UserId);
            return;
        }

        if (IsPrivateChat(message))
        {
            await ReplyAsync(sender, message, "Private chats already trust allowlisted users. No chat trust entry is needed.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        string action = arguments.Trim();
        if (action.Equals("remove", StringComparison.OrdinalIgnoreCase)
            || action.Equals("revoke", StringComparison.OrdinalIgnoreCase)
            || action.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            bool removed = await _stateStore.RemoveTrustedChatAsync(message.ChatId, cancellationToken).ConfigureAwait(false);
            bool configured = IsConfiguredAllowedSharedChat(message.ChatId);
            string removalStatus = removed
                ? "Removed Telegram-granted trust for this chat."
                : "This chat did not have Telegram-granted trust.";
            string configurationStatus = configured
                ? " It is still allowed by TelegramBot:AllowedChatIds."
                : string.Empty;

            await ReplyAsync(sender, message, removalStatus + configurationStatus, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(action) && !action.Equals("chat", StringComparison.OrdinalIgnoreCase) && !action.Equals("here", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(sender, message, "Usage: /trust, /trust chat, or /trust remove", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        bool alreadyConfigured = IsConfiguredAllowedSharedChat(message.ChatId);
        bool alreadyTrusted = await _stateStore.IsChatTrustedAsync(message.ChatId, cancellationToken).ConfigureAwait(false);
        if (!alreadyConfigured && !alreadyTrusted)
        {
            await _stateStore.TrustChatAsync(message.ChatId, cancellationToken).ConfigureAwait(false);
        }

        await ReplyAsync(sender, message, FormatTrustResult(message, alreadyConfigured, alreadyTrusted), null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleProjectAsync(
        TelegramInboundMessage message,
        string arguments,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        string[] parts = SplitArguments(arguments, 2);
        if (parts.Length == 0)
        {
            await HandleProjectsAsync(message, sender, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (parts[0].Equals("add", StringComparison.OrdinalIgnoreCase))
        {
            await HandleProjectAddAsync(message, parts.Length > 1 ? parts[1] : string.Empty, sender, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (parts[0].Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            await HandleProjectCurrentAsync(message, sender, cancellationToken).ConfigureAwait(false);
            return;
        }

        await HandleProjectSelectAsync(message, arguments, sender, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleProjectAddAsync(
        TelegramInboundMessage message,
        string workingDirectory,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            await ReplyAsync(sender, message, "Usage: /project add <absolute directory path>", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        CodexWorkspaceValidationVm validation = _workspaceBrowser.ValidateWorkingDirectory(workingDirectory);
        if (!validation.IsValid || string.IsNullOrWhiteSpace(validation.NormalizedPath))
        {
            await ReplyAsync(sender, message, $"Project path rejected: {validation.Message}", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        CodexProjectCatalogRecord project = await _projectCatalogStore.AddAsync(validation.NormalizedPath, cancellationToken).ConfigureAwait(false);
        ProjectChoice choice = ToProjectChoice(project);
        await _stateStore.SetActiveProjectWorkingDirectoryAsync(message.ConversationScope, choice.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, BuildSelectedProjectReply("Added and selected", choice), null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleProjectSelectAsync(
        TelegramInboundMessage message,
        string arguments,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectChoice> projects = await ListProjectChoicesAsync(cancellationToken).ConfigureAwait(false);
        ResolvedProject resolved = ResolveProject(projects, arguments);
        if (resolved.Project is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _stateStore.SetActiveProjectWorkingDirectoryAsync(message.ConversationScope, resolved.Project.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, BuildSelectedProjectReply("Selected", resolved.Project), null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleProjectCurrentAsync(TelegramInboundMessage message, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        ResolvedProject resolved = await ResolveActiveProjectAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        if (resolved.Project is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReplyAsync(sender, message, FormatProjectStatus(resolved.Project), null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleTopicAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        string[] parts = SplitArguments(arguments, 2);
        if (parts.Length == 0)
        {
            await HandleTopicListAsync(message, sender, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (parts[0].Equals("list", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("ls", StringComparison.OrdinalIgnoreCase))
        {
            await HandleTopicListAsync(message, sender, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (parts[0].Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            await HandleTopicNewAsync(message, parts.Length > 1 ? parts[1] : string.Empty, sender, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (parts[0].Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            await HandleTopicCurrentAsync(message, sender, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (parts[0].Equals("attach", StringComparison.OrdinalIgnoreCase))
        {
            await HandleTopicAttachAsync(message, parts.Length > 1 ? parts[1] : string.Empty, sender, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReplyAsync(sender, message, "Usage: /topic list, /topic current, /topic attach [sessionId], or /topic new <name> [| <absolute directory path>]", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleTopicNewAsync(
        TelegramInboundMessage message,
        string arguments,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        TopicCreationRequest request = ParseTopicCreationRequest(arguments);
        if (!request.IsValid)
        {
            await ReplyAsync(sender, message, "Usage: /topic new <name> [| <absolute directory path>]", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? workingDirectory = request.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            CodexWorkspaceValidationVm validation = _workspaceBrowser.ValidateWorkingDirectory(workingDirectory);
            if (!validation.IsValid || string.IsNullOrWhiteSpace(validation.NormalizedPath))
            {
                await ReplyAsync(sender, message, $"Project path rejected: {validation.Message}", null, cancellationToken).ConfigureAwait(false);
                return;
            }

            workingDirectory = validation.NormalizedPath;
        }
        else
        {
            ResolvedProject resolvedProject = await ResolveActiveProjectAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
            if (resolvedProject.Project is null)
            {
                await ReplyAsync(sender, message, resolvedProject.Message, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            workingDirectory = resolvedProject.Project.WorkingDirectory;
        }

        await CreateTopicAndSessionAsync(
            message,
            request.Name,
            workingDirectory,
            sender,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleTopicCurrentAsync(TelegramInboundMessage message, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        ResolvedSession session = await ResolveActiveSessionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        if (session.Session is null)
        {
            await ReplyAsync(sender, message, session.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        ResolvedProject resolvedProject = await ResolveActiveProjectAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        CodexSessionModelSettings settings = await _sessionManager.GetModelSettingsAsync(session.Session.Id, cancellationToken).ConfigureAwait(false);
        string? usageSummary = await TryBuildAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
        StringBuilder builder = new();
        builder.AppendLine($"Topic thread ID: {message.MessageThreadId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
        builder.AppendLine(FormatStatus(session.Session, settings, usageSummary));
        if (resolvedProject.Project is not null)
        {
            builder.AppendLine();
            builder.AppendLine(FormatProjectStatus(resolvedProject.Project));
        }

        await ReplyAsync(sender, message, builder.ToString().TrimEnd(), BuildSessionButtons([session.Session!], includeUse: false), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleTopicAttachAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        if (!IsForumTopicChat(message) || message.MessageThreadId is null)
        {
            await ReplyAsync(sender, message, "Run /topic attach inside the Telegram forum topic you want to bind to a Codex session.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        ResolvedSession resolved = string.IsNullOrWhiteSpace(arguments)
            ? await ResolveAttachDefaultSessionAsync(message, cancellationToken).ConfigureAwait(false)
            : await ResolveSessionAsync(message.ConversationScope, arguments, cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _stateStore.SetActiveSessionIdAsync(message.ConversationScope, resolved.Session.Id, cancellationToken).ConfigureAwait(false);
        _followRegistry.FollowThread(message.ConversationScope, resolved.Session.Id);
        if (!string.IsNullOrWhiteSpace(resolved.Session.WorkingDirectory))
        {
            await _stateStore.SetActiveProjectWorkingDirectoryAsync(message.ConversationScope, resolved.Session.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        }

        CodexSessionModelSettings settings = await _sessionManager.GetModelSettingsAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
        string? usageSummary = await TryBuildAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
        await ReplyAsync(
            sender,
            message,
            BuildSelectedSessionReply("Attached this topic to", resolved.Session, settings, CodexTextFormatting.ResolveProjectName(resolved.Session.WorkingDirectory), usageSummary),
            BuildSessionButtons([resolved.Session], includeUse: false),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleTopicListAsync(TelegramInboundMessage message, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<TelegramConversationState> states = await _stateStore.ListConversationStatesForChatAsync(message.ChatId, cancellationToken).ConfigureAwait(false);
        IReadOnlyCollection<CodexSessionSummary> sessions = await _sessionManager.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, CodexSessionSummary> sessionsById = sessions.ToDictionary(session => session.Id, StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<CodexSessionSummary> buttonSessions = states
            .Select(state => state.ActiveSessionId is not null && sessionsById.TryGetValue(state.ActiveSessionId, out CodexSessionSummary? session) ? session : null)
            .Where(session => session is not null)
            .Select(session => session!)
            .ToArray();

        await ReplyAsync(
            sender,
            message,
            FormatTopicList(states.ToArray(), sessionsById, message.ConversationScope),
            BuildSessionButtons(buttonSessions, includeUse: false),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSessionsAsync(TelegramInboundMessage message, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        string? activeSessionId = await _stateStore.GetActiveSessionIdAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        IReadOnlyCollection<string> trackedSessionIds = await _stateStore.GetTrackedSessionIdsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyCollection<CodexSessionSummary> sessions = await _sessionManager.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        SessionListRequest request = ParseSessionListRequest(_parser.Parse(message.Text).Arguments);
        SessionListView view = BuildSessionListView(sessions, activeSessionId, trackedSessionIds, request);
        await ReplyAsync(sender, message, FormatSessions(view), BuildSessionButtons(view.Sessions), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleTopicNewFromMenuAsync(TelegramInboundMessage message, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        ResolvedProject resolvedProject = await ResolveActiveProjectAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        if (resolvedProject.Project is null)
        {
            await ReplyAsync(sender, message, resolvedProject.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        string topicName = BuildDefaultTopicName(resolvedProject.Project);
        await CreateTopicAndSessionAsync(message, topicName, resolvedProject.Project.WorkingDirectory, sender, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleNewAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        ResolvedProject resolvedProject = await ResolveActiveProjectAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        if (resolvedProject.Project is null)
        {
            await ReplyAsync(sender, message, resolvedProject.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        string sessionName = string.IsNullOrWhiteSpace(arguments)
            ? BuildDefaultSessionName(resolvedProject.Project)
            : arguments.Trim();
        CodexSessionSummary session = await CreateAndSelectSessionAsync(
            message.ConversationScope,
            sessionName,
            resolvedProject.Project.WorkingDirectory,
            cancellationToken).ConfigureAwait(false);
        CodexSessionModelSettings settings = await _sessionManager.GetModelSettingsAsync(session.Id, cancellationToken).ConfigureAwait(false);
        string? usageSummary = await TryBuildAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, BuildSelectedSessionReply("Created and selected", session, settings, usageSummary: usageSummary), BuildSessionButtons([session], includeUse: false), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleUseAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        ResolvedSession resolved = await ResolveSessionAsync(message.ConversationScope, arguments, cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _stateStore.SetActiveSessionIdAsync(message.ConversationScope, resolved.Session.Id, cancellationToken).ConfigureAwait(false);
        _followRegistry.FollowThread(message.ConversationScope, resolved.Session.Id);
        CodexSessionModelSettings settings = await _sessionManager.GetModelSettingsAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
        string? usageSummary = await TryBuildAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, BuildSelectedSessionReply("Selected", resolved.Session, settings, usageSummary: usageSummary), BuildSessionButtons([resolved.Session], includeUse: false), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSendCommandAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            await ReplyAsync(sender, message, "Usage: /send <text>", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendToActiveSessionAsync(message, arguments, sender, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendToActiveSessionAsync(TelegramInboundMessage message, string? text, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        bool hasAttachments = message.Attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(text) && !hasAttachments)
        {
            await ReplyAsync(sender, message, "Usage: /send <text>", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        bool retainAttachments = false;
        try
        {
            CodexSessionSummary session = await ResolveOrCreateChatSessionAsync(message, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug(
                "Resolved Telegram chat {ChatId} topic {MessageThreadId} to session {SessionId} ({SessionName}) for message dispatch.",
                message.ChatId,
                message.MessageThreadId,
                session.Id,
                session.Name);
            ModelControlRequest modelControl = string.IsNullOrWhiteSpace(text) ? ModelControlRequest.Empty : TryExtractModelControl(text);
            if (modelControl.HasControl)
            {
                CodexSessionModelSettings settings = await _sessionManager.UpdateModelSettingsAsync(
                    session.Id,
                    modelControl.Model,
                    modelControl.ReasoningEffort,
                    cancellationToken).ConfigureAwait(false);
                string? usageSummary = await TryBuildAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
                await ReplyAsync(sender, message, "Updated model settings:" + Environment.NewLine + FormatModelSettings(settings, usageSummary), null, cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(modelControl.RemainingText) && !hasAttachments)
                {
                    return;
                }

                text = modelControl.RemainingText;
            }

            retainAttachments = await SendOrQueueAsync(message, session, BuildCodexInputText(message, text), sender, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Attachments are local paths; keep them once they are queued or handed to Codex.
            if (!retainAttachments)
            {
                TryDeleteAttachments(message.Attachments);
            }
        }
    }

    private async Task HandleAudioMessageAsync(TelegramInboundMessage message, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.AudioFilePath))
        {
            await ReplyAsync(sender, message, "No audio file was attached.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        string transcript;
        try
        {
            transcript = await _audioTranscriptionService.TranscribeAsync(message.AudioFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Audio transcription failed for Telegram user {UserId} in chat {ChatId}.", message.UserId, message.ChatId);
            await ReplyAsync(sender, message, $"Audio transcription failed: {exception.Message}", null, cancellationToken).ConfigureAwait(false);
            return;
        }
        finally
        {
            TryDeleteAudioFile(message.AudioFilePath);
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            await ReplyAsync(sender, message, "I couldn't transcribe that audio.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReplyAsync(sender, message, $"Here's what I transcribed:{Environment.NewLine}{Environment.NewLine}{transcript}", null, cancellationToken).ConfigureAwait(false);
        await SendToActiveSessionAsync(message with { Text = transcript, AudioFilePath = null, Attachments = null }, transcript, sender, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SendOrQueueAsync(
        TelegramInboundMessage message,
        CodexSessionSummary session,
        string? text,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken,
        bool allowUnreadableThreadRecovery = true)
    {
        string trimmed = text?.Trim() ?? string.Empty;
        await _stateStore.TrackSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);

        if (_turnCoordinator.HasActiveTurnForThread(session.Id))
        {
            _logger.LogDebug(
                "Telegram message for chat {ChatId} topic {MessageThreadId} is being queued because session {SessionId} already has an active turn.",
                message.ChatId,
                message.MessageThreadId,
                session.Id);
            await QueuePromptAsync(message, session, trimmed, sender, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (IsLive(session.Status))
        {
            _logger.LogDebug(
                "Telegram message for chat {ChatId} topic {MessageThreadId} is being queued because session {SessionId} is reported as {SessionStatus}.",
                message.ChatId,
                message.MessageThreadId,
                session.Id,
                session.Status);
            await QueuePromptAsync(message, session, trimmed, sender, cancellationToken).ConfigureAwait(false);
            return true;
        }

        TelegramOutboundQueueStatus outboundStatus = await _outboundQueue.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (HasPendingOutboundForConversation(outboundStatus, message.ConversationScope))
        {
            _logger.LogDebug(
                "Telegram message for chat {ChatId} topic {MessageThreadId} is being queued because prior output for that conversation is still pending.",
                message.ChatId,
                message.MessageThreadId);
            await QueuePromptAsync(message, session, trimmed, sender, cancellationToken).ConfigureAwait(false);
            return true;
        }

        _followRegistry.FollowThread(message.ConversationScope, session.Id);
        IDisposable? typingRegistration = _typingIndicatorRegistry.Track(message.ConversationScope);
        try
        {
            await sender.SendTypingActionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug(
                "Sending Telegram message from chat {ChatId} topic {MessageThreadId} to session {SessionId}; text length {TextLength}; attachments {AttachmentCount}.",
                message.ChatId,
                message.MessageThreadId,
                session.Id,
                trimmed.Length,
                message.Attachments?.Count ?? 0);
            Task<CodexThreadExecutionVm> sendTask = StartSessionSendAsync(message, session, trimmed, cancellationToken);
            Task timeoutTask = Task.Delay(TelegramSendStartTimeout, cancellationToken);
            Task completedTask = await Task.WhenAny(sendTask, timeoutTask).ConfigureAwait(false);
            if (!ReferenceEquals(completedTask, sendTask))
            {
                _ = ObserveSlowTelegramSendAsync(sendTask, message, session, trimmed, sender, typingRegistration);
                typingRegistration = null;
                await ReplyAsync(
                    sender,
                    message.ConversationScope,
                    $"Starting turn for {session.Name}. This is taking longer than usual, but live updates will stream here when Codex begins.",
                    BuildSessionButtons([session], includeUse: false),
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            CodexThreadExecutionVm execution = await sendTask.ConfigureAwait(false);
            _followRegistry.FollowThread(message.ConversationScope, execution.ThreadId);
            RegisterTurnReactionTarget(message, execution);
            _logger.LogDebug(
                "Telegram message from chat {ChatId} topic {MessageThreadId} started turn {TurnId} on session {SessionId}.",
                message.ChatId,
                message.MessageThreadId,
                execution.TurnId,
                execution.ThreadId);
            return message.Attachments is { Count: > 0 };
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("already active", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                exception,
                "Telegram message for chat {ChatId} topic {MessageThreadId} raced with an active turn for session {SessionId}; queueing.",
                message.ChatId,
                message.MessageThreadId,
                session.Id);
            await QueuePromptAsync(message, session, trimmed, sender, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (allowUnreadableThreadRecovery && IsUnreadableCodexThreadException(exception))
        {
            return await RecoverUnreadableSessionAndRetryAsync(
                message,
                session,
                trimmed,
                exception,
                sender,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            typingRegistration?.Dispose();
        }
    }

    private async Task<bool> RecoverUnreadableSessionAndRetryAsync(
        TelegramInboundMessage message,
        CodexSessionSummary staleSession,
        string text,
        Exception exception,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            exception,
            "Selected Codex session {SessionId} for chat {ChatId} topic {MessageThreadId} could not be resumed because its local thread store is unreadable. Clearing the conversation selection and creating a replacement session.",
            staleSession.Id,
            message.ChatId,
            message.MessageThreadId);

        await _stateStore.ClearActiveSessionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        string? workingDirectory = await ResolvePreferredWorkingDirectoryAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        CodexSessionSummary replacement = await _sessionManager.CreateSessionAsync(
            new CreateCodexSessionRequest(BuildDefaultSessionNameForWorkingDirectory(workingDirectory), workingDirectory),
            cancellationToken).ConfigureAwait(false);
        await _stateStore.SetActiveSessionIdAsync(message.ConversationScope, replacement.Id, cancellationToken).ConfigureAwait(false);
        _followRegistry.FollowThread(message.ConversationScope, replacement.Id);

        await ReplyAsync(
            sender,
            message.ConversationScope,
            $"The selected Codex session {GetShortSessionId(staleSession.Id)} could not be resumed because its local transcript was empty or unreadable. I started a fresh session and will send this message there.",
            BuildSessionButtons([replacement], includeUse: false),
            cancellationToken).ConfigureAwait(false);

        return await SendOrQueueAsync(
            message,
            replacement,
            text,
            sender,
            cancellationToken,
            allowUnreadableThreadRecovery: false).ConfigureAwait(false);
    }

    private Task<CodexThreadExecutionVm> StartSessionSendAsync(
        TelegramInboundMessage message,
        CodexSessionSummary session,
        string text,
        CancellationToken cancellationToken)
        => message.Attachments is { Count: > 0 }
            ? _sessionManager.SendAsync(
                session.Id,
                TelegramAttachmentInputBuilder.BuildInputItems(text, message.Attachments),
                cancellationToken)
            : _sessionManager.SendAsync(session.Id, text, cancellationToken);

    private void RegisterTurnReactionTarget(TelegramInboundMessage message, CodexThreadExecutionVm execution)
    {
        if (!message.SourceMessageId.HasValue)
        {
            return;
        }

        _turnReactionRegistry.Register(
            execution.ThreadId,
            execution.TurnId,
            message.ConversationScope,
            message.SourceMessageId.Value);
    }

    private async Task ObserveSlowTelegramSendAsync(
        Task<CodexThreadExecutionVm> sendTask,
        TelegramInboundMessage message,
        CodexSessionSummary session,
        string text,
        ITelegramBotMessageSender sender,
        IDisposable typingRegistration)
    {
        try
        {
            CodexThreadExecutionVm execution = await sendTask.ConfigureAwait(false);
            _followRegistry.FollowThread(message.ConversationScope, execution.ThreadId);
            RegisterTurnReactionTarget(message, execution);
            _logger.LogDebug(
                "Slow Telegram message from chat {ChatId} topic {MessageThreadId} eventually started turn {TurnId} on session {SessionId}.",
                message.ChatId,
                message.MessageThreadId,
                execution.TurnId,
                execution.ThreadId);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("already active", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                exception,
                "Slow Telegram message for chat {ChatId} topic {MessageThreadId} eventually raced with an active turn for session {SessionId}; queueing.",
                message.ChatId,
                message.MessageThreadId,
                session.Id);
            await QueuePromptAsync(message, session, text, sender, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Slow Telegram message for chat {ChatId} topic {MessageThreadId} failed to start session {SessionId}.",
                message.ChatId,
                message.MessageThreadId,
                session.Id);
            TryDeleteAttachments(message.Attachments);
            await ReactToSourceMessageAsync(sender, message, TelegramMessageReactionKind.Failed, CancellationToken.None).ConfigureAwait(false);
            await ReplyAsync(
                sender,
                message.ConversationScope,
                $"Message for {session.Name} failed to start: {exception.Message}",
                BuildSessionButtons([session], includeUse: false),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            typingRegistration.Dispose();
        }
    }

    private async Task QueuePromptAsync(
        TelegramInboundMessage message,
        CodexSessionSummary session,
        string text,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        await _stateStore.TrackSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
        await _stateStore.EnqueueQueuedPromptAsync(
            new TelegramQueuedPrompt(
                Guid.NewGuid().ToString("n"),
                message.UserId,
                message.ChatId,
                session.Id,
                session.Name,
                text,
                DateTimeOffset.UtcNow,
                message.MessageThreadId,
                message.Attachments?.ToArray()),
            cancellationToken).ConfigureAwait(false);

        _followRegistry.FollowThread(message.ConversationScope, session.Id);
        await ReplyAsync(sender, message, $"Queued for {session.Name}. I'll send it when the active Codex turn or pending Telegram output finishes.", BuildSessionButtons([session], includeUse: false), cancellationToken).ConfigureAwait(false);
    }

    private static bool HasPendingOutboundForConversation(TelegramOutboundQueueStatus status, TelegramConversationScope conversation)
        => status.Destinations.Any(destination =>
            destination.ChatId == conversation.ChatId
            && destination.MessageThreadId == conversation.MessageThreadId
            && (destination.PendingMessageCount > 0 || destination.PendingChunkCount > 0));

    private async Task HandleTailAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        int lineCount = DefaultTailLineCount;
        string sessionArgument = string.Empty;
        string[] parts = SplitArguments(arguments, 2);
        if (parts.Length == 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCount))
        {
            lineCount = parsedCount;
        }
        else if (parts.Length > 0)
        {
            sessionArgument = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedCount))
            {
                lineCount = parsedCount;
            }
        }

        ResolvedSession resolved = string.IsNullOrWhiteSpace(sessionArgument)
            ? await ResolveActiveSessionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false)
            : await ResolveSessionAsync(message.ConversationScope, sessionArgument, cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        _followRegistry.FollowThread(message.ConversationScope, resolved.Session.Id);
        string tail = await _sessionManager.TailAsync(resolved.Session.Id, lineCount, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, tail, BuildSessionButtons([resolved.Session]), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSteerAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            await ReplyAsync(sender, message, "Usage: /steer <text>", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        ResolvedSession resolved = await ResolveActiveSessionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            string input = BuildCodexInputText(message, arguments);
            await _sessionManager.SteerAsync(resolved.Session.Id, input, cancellationToken).ConfigureAwait(false);
            _followRegistry.FollowThread(message.ConversationScope, resolved.Session.Id);
            await ReplyAsync(sender, message, $"Steered {resolved.Session.Name}.", BuildSessionButtons([resolved.Session], includeUse: false), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            await ReplyAsync(sender, message, exception.Message, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildCodexInputText(TelegramInboundMessage message, string? text)
    {
        string userText = text?.Trim() ?? string.Empty;
        if (message.ReplyContext is null)
        {
            return userText;
        }

        StringBuilder builder = new();
        builder.AppendLine("Telegram reply context:");
        builder.AppendLine("The operator replied to a previous Telegram message. Use this context to interpret the operator reply, but treat only the operator reply as the new instruction.");

        if (message.ReplyContext.PriorMessages.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Nearby earlier Telegram messages from this chat:");
            foreach (TelegramMessageContextRecord prior in message.ReplyContext.PriorMessages)
            {
                builder.Append("- ");
                builder.Append(FormatTelegramMessageAuthor(prior.Author));
                builder.Append(" message ");
                builder.Append(prior.MessageId.ToString(CultureInfo.InvariantCulture));
                builder.Append(": ");
                builder.AppendLine(TruncateForReplyContext(prior.Text).ReplaceLineEndings(" "));
            }
        }

        builder.AppendLine();
        builder.Append("Replied-to ");
        builder.Append(FormatTelegramMessageAuthor(message.ReplyContext.Author));
        builder.Append(" message ");
        builder.Append(message.ReplyContext.MessageId.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine(":");
        builder.AppendLine(IndentReplyContext(message.ReplyContext.Text));

        builder.AppendLine();
        builder.AppendLine("Operator reply:");
        builder.AppendLine(string.IsNullOrWhiteSpace(userText) ? "(no text; see attached Telegram content)" : userText);
        return builder.ToString().TrimEnd();
    }

    private static string FormatTelegramMessageAuthor(TelegramMessageAuthor author)
        => author is TelegramMessageAuthor.Bot ? "Codex" : "user";

    private static string IndentReplyContext(string text)
    {
        string truncated = TruncateForReplyContext(text);
        return string.Join(
            Environment.NewLine,
            truncated.ReplaceLineEndings("\n").Split('\n').Select(line => "> " + line));
    }

    private static string TruncateForReplyContext(string text)
        => text.Length <= ReplyContextPreviewLength
            ? text
            : text[..ReplyContextPreviewLength] + "...";

    private async Task HandleModelAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        ResolvedSession resolved = await ResolveActiveSessionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(arguments))
        {
            CodexSessionModelSettings current = await _sessionManager.GetModelSettingsAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
            string? usageSummary = await TryBuildAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
            await ReplyAsync(sender, message, "Model settings:" + Environment.NewLine + FormatModelSettings(current, usageSummary), BuildModelSelectionButtons(current), cancellationToken, includeNavigationButtons: false).ConfigureAwait(false);
            return;
        }

        ModelControlRequest modelControl = ParseModelControlArguments(arguments);
        if (!modelControl.HasControl)
        {
            await ReplyAsync(sender, message, "Usage: /model [model] [thinking <minimal|low|medium|high|xhigh>]", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            CodexSessionModelSettings settings = await _sessionManager.UpdateModelSettingsAsync(
                resolved.Session.Id,
                modelControl.Model,
                modelControl.ReasoningEffort,
                cancellationToken).ConfigureAwait(false);
            string? usageSummary = await TryBuildAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
            await ReplyAsync(sender, message, "Updated model settings:" + Environment.NewLine + FormatModelSettings(settings, usageSummary), BuildSessionButtons([resolved.Session], includeUse: false), cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            await ReplyAsync(sender, message, exception.Message, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleThinkingAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        ResolvedSession resolved = await ResolveActiveSessionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(arguments))
        {
            CodexSessionModelSettings current = await _sessionManager.GetModelSettingsAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
            string? usageSummary = await TryBuildAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
            await ReplyAsync(sender, message, "Thinking settings:" + Environment.NewLine + FormatModelSettings(current, usageSummary), BuildThinkingSelectionButtons(current), cancellationToken, includeNavigationButtons: false).ConfigureAwait(false);
            return;
        }

        await HandleModelAsync(message, "thinking " + arguments, sender, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleGoalAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        ResolvedSession resolved = await ResolveActiveSessionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        GoalCommandRequest request = ParseGoalCommand(arguments);
        if (request.Action is GoalCommandAction.Invalid)
        {
            await ReplyAsync(sender, message, "Usage: /goal [objective|set <objective>|clear|pause|resume|complete]", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            if (request.Action is GoalCommandAction.Clear)
            {
                bool cleared = await _sessionManager.ClearGoalAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
                await ReplyAsync(sender, message, cleared ? "Cleared the session goal." : "No session goal was set.", null, cancellationToken).ConfigureAwait(false);
                return;
            }

            CodexThreadGoalVm? goal = request.Action switch
            {
                GoalCommandAction.Show => await _sessionManager.GetGoalAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false),
                GoalCommandAction.Set => await _sessionManager.SetGoalAsync(resolved.Session.Id, request.Objective, request.TokenBudget, cancellationToken).ConfigureAwait(false),
                GoalCommandAction.Pause => await _sessionManager.SetGoalStatusAsync(resolved.Session.Id, CodexThreadGoalStatus.Paused, cancellationToken).ConfigureAwait(false),
                GoalCommandAction.Resume => await _sessionManager.SetGoalStatusAsync(resolved.Session.Id, CodexThreadGoalStatus.Active, cancellationToken).ConfigureAwait(false),
                GoalCommandAction.Complete => await _sessionManager.SetGoalStatusAsync(resolved.Session.Id, CodexThreadGoalStatus.Complete, cancellationToken).ConfigureAwait(false),
                _ => null,
            };

            string prefix = request.Action switch
            {
                GoalCommandAction.Set => "Updated session goal:",
                GoalCommandAction.Pause => "Paused session goal:",
                GoalCommandAction.Resume => "Resumed session goal:",
                GoalCommandAction.Complete => "Completed session goal:",
                _ => "Session goal:",
            };

            await ReplyAsync(sender, message, goal is null ? "No session goal is set." : $"{prefix}{Environment.NewLine}{FormatGoal(goal)}", null, cancellationToken).ConfigureAwait(false);
        }
        catch (CodexCapabilityNotSupportedException)
        {
            await ReplyAsync(sender, message, "Goals are unavailable: use the app-server backend with an up-to-date Codex CLI.", null, cancellationToken).ConfigureAwait(false);
        }
        catch (CodexMethodNotFoundException)
        {
            await ReplyAsync(sender, message, "Goals are unavailable: the installed Codex app-server does not expose thread goal methods. Update Codex and try /goal again.", null, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            await ReplyAsync(sender, message, exception.Message, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleModelMenuAsync(
        TelegramInboundMessage message,
        string arguments,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        ResolvedSession resolved = string.IsNullOrWhiteSpace(arguments)
            ? await ResolveActiveSessionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false)
            : await ResolveSessionAsync(message.ConversationScope, arguments, cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await EditCallbackProgressAsync(sender, message, "Loading model settings...", cancellationToken).ConfigureAwait(false);
        CodexSessionModelSettings settings = await _sessionManager.GetModelSettingsAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
        string? usageSummary = await TryBuildAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, "Model settings:" + Environment.NewLine + FormatModelSettings(settings, usageSummary), BuildModelSelectionButtons(settings), cancellationToken, includeNavigationButtons: false).ConfigureAwait(false);
    }

    private async Task HandleModelSelectionAsync(
        TelegramInboundMessage message,
        string arguments,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        string[] parts = arguments.Split('|', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            await ReplyAsync(sender, message, "Usage: Model buttons must include a session and a model.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        ResolvedSession resolved = await ResolveSessionAsync(message.ConversationScope, parts[0], cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await EditCallbackProgressAsync(sender, message, "Updating model settings...", cancellationToken).ConfigureAwait(false);
        string model = parts[1];
        if (int.TryParse(model, NumberStyles.Integer, CultureInfo.InvariantCulture, out int modelIndex))
        {
            CodexSessionModelSettings current = await _sessionManager.GetModelSettingsAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
            if (modelIndex < 0 || modelIndex >= current.AvailableModels.Count)
            {
                await ReplyAsync(sender, message, "That model button is stale. Use /model to refresh the model list.", null, cancellationToken).ConfigureAwait(false);
                return;
            }

            model = current.AvailableModels[modelIndex].Id;
        }

        CodexSessionModelSettings settings = await _sessionManager.UpdateModelSettingsAsync(resolved.Session.Id, model, null, cancellationToken).ConfigureAwait(false);
        string? usageSummary = await TryBuildAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, "Model settings:" + Environment.NewLine + FormatModelSettings(settings, usageSummary), BuildModelSelectionButtons(settings), cancellationToken, includeNavigationButtons: false).ConfigureAwait(false);
    }

    private async Task HandleThinkingMenuAsync(
        TelegramInboundMessage message,
        string arguments,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        ResolvedSession resolved = string.IsNullOrWhiteSpace(arguments)
            ? await ResolveActiveSessionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false)
            : await ResolveSessionAsync(message.ConversationScope, arguments, cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await EditCallbackProgressAsync(sender, message, "Loading thinking settings...", cancellationToken).ConfigureAwait(false);
        CodexSessionModelSettings settings = await _sessionManager.GetModelSettingsAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
        string? usageSummary = await TryBuildAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, "Thinking settings:" + Environment.NewLine + FormatModelSettings(settings, usageSummary), BuildThinkingSelectionButtons(settings), cancellationToken, includeNavigationButtons: false).ConfigureAwait(false);
    }

    private async Task HandleThinkingSelectionAsync(
        TelegramInboundMessage message,
        string arguments,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        string[] parts = arguments.Split('|', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            await ReplyAsync(sender, message, "Usage: Thinking buttons must include a session and an effort.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        ResolvedSession resolved = await ResolveSessionAsync(message.ConversationScope, parts[0], cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await EditCallbackProgressAsync(sender, message, "Updating thinking settings...", cancellationToken).ConfigureAwait(false);
        CodexSessionModelSettings settings = await _sessionManager.UpdateModelSettingsAsync(resolved.Session.Id, null, parts[1], cancellationToken).ConfigureAwait(false);
        string? usageSummary = await TryBuildAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, "Thinking settings:" + Environment.NewLine + FormatModelSettings(settings, usageSummary), BuildThinkingSelectionButtons(settings), cancellationToken, includeNavigationButtons: false).ConfigureAwait(false);
    }

    private async Task HandleStatusAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        ResolvedSession resolved = string.IsNullOrWhiteSpace(arguments)
            ? await ResolveActiveSessionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false)
            : await ResolveSessionAsync(message.ConversationScope, arguments, cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        CodexSessionModelSettings settings = await _sessionManager.GetModelSettingsAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
        string? usageSummary = await TryBuildStatusAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, FormatStatus(resolved.Session, settings, usageSummary), BuildSessionButtons([resolved.Session]), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleUsageAsync(TelegramInboundMessage message, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        try
        {
            CodexAccountUsageVm usage = await _accountUsageService.GetUsageAsync(cancellationToken).ConfigureAwait(false);
            await ReplyAsync(sender, message, FormatAccountUsage(usage), null, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception)
        {
            _logger.LogWarning(exception, "Codex usage lookup failed because the Codex executable was not found.");
            await ReplyAsync(
                sender,
                message,
                "Codex usage unavailable: the Codex executable was not found. Set Codex:CodexPathOverride or make sure `codex` is on PATH, then try /usage again.",
                null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (CodexCapabilityNotSupportedException exception)
        {
            _logger.LogWarning(exception, "Codex usage lookup failed because the configured backend does not support account rate limits.");
            await ReplyAsync(
                sender,
                message,
                "Codex usage unavailable: the configured Codex backend does not support account rate limits. Use the app-server backend and an up-to-date Codex CLI, then try /usage again.",
                null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (CodexMethodNotFoundException exception)
        {
            _logger.LogWarning(exception, "Codex usage lookup failed because the installed Codex app-server does not expose account rate limits.");
            await ReplyAsync(
                sender,
                message,
                "Codex usage unavailable: the installed Codex app-server does not expose account rate limits. Update Codex and try /usage again.",
                null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleQueueAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        string[] parts = SplitArguments(arguments, 3);
        if (parts.Length == 0
            || parts[0].Equals("list", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("ls", StringComparison.OrdinalIgnoreCase))
        {
            bool includeAll = parts.Length > 1 && IsAllQueueSelector(parts[1]);
            await ReplyWithQueueListAsync(message, sender, includeAll, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (IsAllQueueSelector(parts[0]))
        {
            await ReplyWithQueueListAsync(message, sender, includeAll: true, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (parts[0].Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            await HandleQueueEditCommandAsync(message, parts, sender, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (parts[0].Equals("delete", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("del", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("remove", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("rm", StringComparison.OrdinalIgnoreCase))
        {
            await HandleQueueDeleteCommandAsync(message, parts, sender, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (parts[0].Equals("send", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("now", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("steer", StringComparison.OrdinalIgnoreCase))
        {
            await HandleQueueSendNowCommandAsync(message, parts, sender, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReplyAsync(sender, message, BuildQueueUsage(), null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleQueueEditCommandAsync(
        TelegramInboundMessage message,
        string[] parts,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2]))
        {
            await ReplyAsync(sender, message, "Usage: /queue edit <id> <new text>", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        ResolvedQueuedPrompt resolved = await ResolveOwnedQueuedPromptAsync(message, parts[1], cancellationToken).ConfigureAwait(false);
        if (resolved.Prompt is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        TelegramQueuedPrompt? updated = await _stateStore.TryUpdateQueuedPromptTextAsync(
            resolved.Prompt.Id,
            message.UserId,
            parts[2].Trim(),
            cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            await ReplyAsync(sender, message, "That queued item was already sent or removed. Use /queue to refresh.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReplyWithQueueListAsync(
            message,
            sender,
            includeAll: false,
            $"Updated queued item {GetShortQueuedPromptId(updated.Id)}.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleQueueDeleteCommandAsync(
        TelegramInboundMessage message,
        string[] parts,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            await ReplyAsync(sender, message, "Usage: /queue delete <id>", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        ResolvedQueuedPrompt resolved = await ResolveOwnedQueuedPromptAsync(message, parts[1], cancellationToken).ConfigureAwait(false);
        if (resolved.Prompt is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await DeleteQueuedPromptAsync(message, resolved.Prompt.Id, sender, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleQueueSendNowCommandAsync(
        TelegramInboundMessage message,
        string[] parts,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            await ReplyAsync(sender, message, "Usage: /queue send <id>", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        ResolvedQueuedPrompt resolved = await ResolveOwnedQueuedPromptAsync(message, parts[1], cancellationToken).ConfigureAwait(false);
        if (resolved.Prompt is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendQueuedPromptNowAsync(message, resolved.Prompt.Id, sender, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleQueueDeleteCallbackAsync(
        TelegramInboundMessage message,
        string promptId,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
        => await DeleteQueuedPromptAsync(message, promptId, sender, cancellationToken).ConfigureAwait(false);

    private async Task HandleQueueSendNowCallbackAsync(
        TelegramInboundMessage message,
        string promptId,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
        => await SendQueuedPromptNowAsync(message, promptId, sender, cancellationToken).ConfigureAwait(false);

    private async Task HandleQueueEditCallbackAsync(
        TelegramInboundMessage message,
        string promptId,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        TelegramQueuedPrompt? prompt = await _stateStore.TryGetQueuedPromptAsync(promptId, cancellationToken).ConfigureAwait(false);
        if (prompt is null || prompt.UserId != message.UserId)
        {
            await ReplyAsync(sender, message, "That queued item was already sent or removed. Use /queue to refresh.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        string shortId = GetShortQueuedPromptId(prompt.Id);
        await ReplyAsync(
            sender,
            message.ConversationScope,
            $"To edit queued item {shortId}, send:{Environment.NewLine}/queue edit {shortId} <new text>",
            null,
            cancellationToken,
            includeNavigationButtons: false,
            editMessageId: null).ConfigureAwait(false);
    }

    private async Task DeleteQueuedPromptAsync(
        TelegramInboundMessage message,
        string promptId,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        TelegramQueuedPrompt? removed = await _stateStore.TryRemoveQueuedPromptAsync(promptId, message.UserId, cancellationToken).ConfigureAwait(false);
        if (removed is null)
        {
            await ReplyAsync(sender, message, "That queued item was already sent or removed. Use /queue to refresh.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        TryDeleteAttachments(removed.Attachments);
        await ReplyWithQueueListAsync(
            message,
            sender,
            includeAll: false,
            $"Deleted queued item {GetShortQueuedPromptId(removed.Id)}.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendQueuedPromptNowAsync(
        TelegramInboundMessage message,
        string promptId,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        TelegramQueuedPrompt? removed = await _stateStore.TryRemoveQueuedPromptAsync(promptId, message.UserId, cancellationToken).ConfigureAwait(false);
        if (removed is null)
        {
            await ReplyAsync(sender, message, "That queued item was already sent or removed. Use /queue to refresh.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        CodexSessionSummary? session = await _sessionManager.GetSessionAsync(removed.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            TryDeleteAttachments(removed.Attachments);
            await ReplyWithQueueListAsync(
                message,
                sender,
                includeAll: false,
                $"Removed queued item {GetShortQueuedPromptId(removed.Id)} because its session is no longer available.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            if (removed.Attachments is { Count: > 0 })
            {
                await _sessionManager.SteerAsync(
                    removed.SessionId,
                    TelegramAttachmentInputBuilder.BuildInputItems(removed.Text, removed.Attachments),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _sessionManager.SteerAsync(removed.SessionId, removed.Text, cancellationToken).ConfigureAwait(false);
            }

            _followRegistry.FollowThread(removed.ConversationScope, removed.SessionId);
            await ReplyWithQueueListAsync(
                message,
                sender,
                includeAll: false,
                $"Sent queued item {GetShortQueuedPromptId(removed.Id)} to the active turn for {session.Name}.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _stateStore.EnqueueQueuedPromptAsync(removed, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(exception, "Queued prompt {PromptId} could not be sent now; it remains queued.", removed.Id);
            await ReplyWithQueueListAsync(
                message,
                sender,
                includeAll: false,
                $"Could not send queued item {GetShortQueuedPromptId(removed.Id)} now: {exception.Message} It is still queued.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReplyWithQueueListAsync(
        TelegramInboundMessage message,
        ITelegramBotMessageSender sender,
        bool includeAll,
        string? prefix,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TelegramQueuedPrompt> prompts = await _stateStore.ListQueuedPromptsAsync(
            message.UserId,
            includeAll ? null : message.ConversationScope,
            cancellationToken).ConfigureAwait(false);
        string text = FormatQueuedPrompts(prompts, includeAll, message.ConversationScope, prefix);
        await ReplyAsync(sender, message, text, BuildQueuedPromptButtons(prompts), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedQueuedPrompt> ResolveOwnedQueuedPromptAsync(
        TelegramInboundMessage message,
        string selector,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return new ResolvedQueuedPrompt(null, "Usage: /queue <edit|delete|send> <id>");
        }

        IReadOnlyList<TelegramQueuedPrompt> prompts = await _stateStore.ListQueuedPromptsAsync(
            message.UserId,
            null,
            cancellationToken).ConfigureAwait(false);
        if (prompts.Count == 0)
        {
            return new ResolvedQueuedPrompt(null, "You do not have any queued prompts.");
        }

        string trimmed = selector.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            IReadOnlyList<TelegramQueuedPrompt> conversationPrompts = prompts
                .Where(prompt => prompt.ConversationScope == message.ConversationScope)
                .OrderBy(prompt => prompt.EnqueuedAt)
                .ThenBy(prompt => prompt.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (index >= 1 && index <= conversationPrompts.Count)
            {
                return new ResolvedQueuedPrompt(conversationPrompts[index - 1], string.Empty);
            }
        }

        List<TelegramQueuedPrompt> matches = prompts
            .Where(prompt => prompt.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                || prompt.Id.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => new ResolvedQueuedPrompt(null, $"Queued item '{trimmed}' was not found. Use /queue to list queued prompts."),
            1 => new ResolvedQueuedPrompt(matches[0], string.Empty),
            _ => new ResolvedQueuedPrompt(null, $"Queued item id '{trimmed}' is ambiguous. Use more characters from /queue."),
        };
    }

    private async Task HandleOutboundAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        string command = SplitArguments(arguments, 2).FirstOrDefault() ?? "status";
        if (!command.Equals("status", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(arguments))
        {
            await ReplyAsync(sender, message, "Only /outbound status is implemented in this slice.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        TelegramOutboundQueueStatus status = await _outboundQueue.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, FormatOutboundStatus(status, message.ConversationScope.ChatId), null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleDebugAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        string command = SplitArguments(arguments, 2).FirstOrDefault() ?? "status";
        if (command.Equals("on", StringComparison.OrdinalIgnoreCase)
            || command.Equals("enable", StringComparison.OrdinalIgnoreCase)
            || command.Equals("enabled", StringComparison.OrdinalIgnoreCase))
        {
            _debugPreambleMode.SetRuntimeOverride(true);
            await ReplyAsync(sender, message, FormatDebugModeStatus("Debug preambles enabled."), null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command.Equals("off", StringComparison.OrdinalIgnoreCase)
            || command.Equals("disable", StringComparison.OrdinalIgnoreCase)
            || command.Equals("disabled", StringComparison.OrdinalIgnoreCase))
        {
            _debugPreambleMode.SetRuntimeOverride(false);
            await ReplyAsync(sender, message, FormatDebugModeStatus("Debug preambles disabled."), null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command.Equals("reset", StringComparison.OrdinalIgnoreCase)
            || command.Equals("config", StringComparison.OrdinalIgnoreCase)
            || command.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            _debugPreambleMode.ClearRuntimeOverride();
            await ReplyAsync(sender, message, FormatDebugModeStatus("Debug preambles reset to configuration."), null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command.Equals("status", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(arguments))
        {
            await ReplyAsync(sender, message, FormatDebugModeStatus("Debug preamble status."), null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReplyAsync(sender, message, "Usage: /debug [status|on|off|reset]", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleDoctorAsync(TelegramInboundMessage message, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        builder.AppendLine($"Codex Telegram doctor {GetApplicationVersion()}");
        builder.AppendLine();
        builder.AppendLine(await FormatDoctorConversationAsync(message, cancellationToken).ConfigureAwait(false));
        builder.AppendLine();
        await AppendDoctorProjectAndSessionAsync(builder, message, cancellationToken).ConfigureAwait(false);
        builder.AppendLine();
        AppendDoctorWorkspace(builder);
        builder.AppendLine();
        await AppendDoctorOutboundAsync(builder, message, cancellationToken).ConfigureAwait(false);
        builder.AppendLine();
        builder.AppendLine(await BuildDoctorNextActionAsync(message, cancellationToken).ConfigureAwait(false));

        await ReplyAsync(sender, message, builder.ToString().TrimEnd(), null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleStopAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        ResolvedSession resolved = string.IsNullOrWhiteSpace(arguments)
            ? await ResolveActiveSessionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false)
            : await ResolveSessionAsync(message.ConversationScope, arguments, cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _sessionManager.StopAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, $"Stopped {resolved.Session.Name}. Pending queued messages for that session were cleared.", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleRestartAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        await ReplyAsync(
            sender,
            message,
            "Restart is managed outside this standalone process. Stop the terminal process and start Incursa.Codex.Telegram again, or run it under a service manager that restarts on exit.",
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleKillAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        string[] parts = SplitArguments(arguments, 2);
        if (parts.Length != 2 || !string.Equals(parts[1], "confirm", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(sender, message, "Usage: /kill <sessionId> confirm", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        ResolvedSession resolved = await ResolveSessionAsync(message.ConversationScope, parts[0], cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _sessionManager.KillAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, $"Killed {resolved.Session.Name}. Pending queued messages for that session were cleared.", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleRenameAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        string[] parts = SplitArguments(arguments, 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            await ReplyAsync(sender, message, "Usage: /rename <sessionId> <new name>", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        ResolvedSession resolved = await ResolveSessionAsync(message.ConversationScope, parts[0], cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _sessionManager.RenameAsync(resolved.Session.Id, parts[1], cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, $"Renamed to {parts[1].Trim()}.", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleForgetAsync(TelegramInboundMessage message, string arguments, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        ResolvedSession resolved = await ResolveSessionAsync(message.ConversationScope, arguments, cancellationToken).ConfigureAwait(false);
        if (resolved.Session is null)
        {
            await ReplyAsync(sender, message, resolved.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _sessionManager.ForgetAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, $"Forgot {resolved.Session.Name}. Transcript logs were not deleted.", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedSession> ResolveActiveSessionAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
    {
        string? activeSessionId = await _stateStore.GetActiveSessionIdAsync(conversation, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(activeSessionId))
        {
            return new ResolvedSession(null, "No active session is selected in this conversation. Use /sessions, /new [name], /use <sessionId>, or just send a message to start a new session.");
        }

        CodexSessionSummary? session;
        try
        {
            session = await _sessionManager.GetSessionAsync(activeSessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnreadableCodexThreadException(exception))
        {
            _logger.LogWarning(
                exception,
                "Clearing selected Codex session {SessionId} because its local thread store is unreadable.",
                activeSessionId);
            await _stateStore.ClearActiveSessionAsync(conversation, cancellationToken).ConfigureAwait(false);
            return new ResolvedSession(null, "The selected session could not be read and was cleared. Use /sessions, /new [name], /use <sessionId>, or send a message to start a new session.");
        }

        if (session is null)
        {
            await _stateStore.ClearActiveSessionAsync(conversation, cancellationToken).ConfigureAwait(false);
            return new ResolvedSession(null, "The selected session is no longer known. Use /sessions, /new [name], /use <sessionId>, or send a message to start a new session.");
        }

        return new ResolvedSession(session, string.Empty);
    }

    private async Task<ResolvedSession> ResolveSessionAsync(TelegramConversationScope conversation, string arguments, CancellationToken cancellationToken)
    {
        string sessionId = SplitArguments(arguments, 2).FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return await ResolveActiveSessionAsync(conversation, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyCollection<CodexSessionSummary> sessions = await _sessionManager.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        List<CodexSessionSummary> matches = sessions
            .Where(session => session.Id.Equals(sessionId, StringComparison.OrdinalIgnoreCase)
                || session.Id.StartsWith(sessionId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => new ResolvedSession(null, $"Session '{sessionId}' was not found. Use /sessions to list known sessions."),
            1 => new ResolvedSession(matches[0], string.Empty),
            _ => new ResolvedSession(null, $"Session id '{sessionId}' is ambiguous. Use a longer id from /sessions."),
        };
    }

    private async Task<ResolvedSession> ResolveAttachDefaultSessionAsync(TelegramInboundMessage message, CancellationToken cancellationToken)
    {
        ResolvedSession currentTopicSession = await ResolveActiveSessionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        if (currentTopicSession.Session is not null)
        {
            return currentTopicSession;
        }

        TelegramConversationScope privateConversation = new(message.UserId, null);
        ResolvedSession privateSession = await ResolveActiveSessionAsync(privateConversation, cancellationToken).ConfigureAwait(false);
        if (privateSession.Session is not null)
        {
            return privateSession;
        }

        return new ResolvedSession(null, "No session is available to attach. Use /topic attach <sessionId>, /sessions, or select a session in your private chat first.");
    }

    private async Task<CodexSessionSummary> ResolveOrCreateChatSessionAsync(TelegramInboundMessage message, CancellationToken cancellationToken)
    {
        ResolvedSession resolved = await ResolveActiveSessionAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        if (resolved.Session is not null)
        {
            return resolved.Session;
        }

        string? workingDirectory = await ResolvePreferredWorkingDirectoryAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        CodexSessionSummary session = await _sessionManager.CreateSessionAsync(
            new CreateCodexSessionRequest(BuildDefaultSessionNameForWorkingDirectory(workingDirectory), workingDirectory),
            cancellationToken).ConfigureAwait(false);
        await _stateStore.SetActiveSessionIdAsync(message.ConversationScope, session.Id, cancellationToken).ConfigureAwait(false);
        _followRegistry.FollowThread(message.ConversationScope, session.Id);
        return session;
    }

    private async Task<CodexSessionSummary> CreateAndSelectSessionAsync(
        TelegramConversationScope conversation,
        string name,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        CodexSessionSummary session = await _sessionManager.CreateSessionAsync(
            new CreateCodexSessionRequest(name, workingDirectory),
            cancellationToken).ConfigureAwait(false);
        await _stateStore.SetActiveSessionIdAsync(conversation, session.Id, cancellationToken).ConfigureAwait(false);
        _followRegistry.FollowThread(conversation, session.Id);
        return session;
    }

    private async Task<ResolvedProject> ResolveActiveProjectAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
    {
        string? activeProject = await _stateStore.GetActiveProjectWorkingDirectoryAsync(conversation, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(activeProject))
        {
            IReadOnlyList<ProjectChoice> projects = await ListProjectChoicesAsync(cancellationToken).ConfigureAwait(false);
            string guidance = projects.Count == 0
                ? "Select a project before creating a session. Use /project add <absolute directory path>."
                : "Select a project before creating a session. Use /projects, then tap Use.";
            return new ResolvedProject(null, guidance);
        }

        IReadOnlyList<ProjectChoice> knownProjects = await ListProjectChoicesAsync(cancellationToken).ConfigureAwait(false);
        ProjectChoice? project = knownProjects.FirstOrDefault(candidate => PathComparer.Equals(candidate.WorkingDirectory, activeProject));
        if (project is null)
        {
            await _stateStore.ClearActiveProjectAsync(conversation, cancellationToken).ConfigureAwait(false);
            return new ResolvedProject(null, "The selected project is no longer known. Use /projects or /project add <absolute directory path>.");
        }

        return new ResolvedProject(project, string.Empty);
    }

    private async Task<string?> ResolvePreferredWorkingDirectoryAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
    {
        string? activeProject = await _stateStore.GetActiveProjectWorkingDirectoryAsync(conversation, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(activeProject))
        {
            return null;
        }

        IReadOnlyList<ProjectChoice> knownProjects = await ListProjectChoicesAsync(cancellationToken).ConfigureAwait(false);
        ProjectChoice? project = knownProjects.FirstOrDefault(candidate => PathComparer.Equals(candidate.WorkingDirectory, activeProject));
        if (project is null)
        {
            await _stateStore.ClearActiveProjectAsync(conversation, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return project.WorkingDirectory;
    }

    private async Task<IReadOnlyList<ProjectChoice>> ListProjectChoicesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CodexProjectCatalogRecord> projects = await _projectCatalogStore.ListAsync(cancellationToken).ConfigureAwait(false);
        return projects.Select(ToProjectChoice).ToArray();
    }

    private async Task ReplyAsync(
        ITelegramBotMessageSender sender,
        TelegramConversationScope conversation,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken)
        => await ReplyAsync(sender, conversation, text, buttons, cancellationToken, includeNavigationButtons: true, editMessageId: null).ConfigureAwait(false);

    private async Task ReplyAsync(
        ITelegramBotMessageSender sender,
        TelegramInboundMessage message,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken,
        bool includeNavigationButtons = true)
        => await ReplyAsync(sender, message.ConversationScope, text, buttons, cancellationToken, includeNavigationButtons, message.SourceMessageId).ConfigureAwait(false);

    private async Task ReplyAsync(
        ITelegramBotMessageSender sender,
        TelegramConversationScope conversation,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken,
        bool includeNavigationButtons,
        int? editMessageId)
    {
        TelegramDebugMessageContext? debugContext = await CreateReplyDebugContextAsync(conversation, cancellationToken).ConfigureAwait(false);
        int maxChunkLength = _debugPreambleMode.IsEnabled
            ? Math.Min(_options.MaxTelegramMessageLength, TelegramOutboundDefaults.MaxMessageChars)
            : _options.MaxTelegramMessageLength;
        IReadOnlyList<string> chunks = _chunker.Split(text, maxChunkLength);
        for (int index = 0; index < chunks.Count; index++)
        {
            bool isLastChunk = index == chunks.Count - 1;
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? chunkButtons = isLastChunk
                ? MergeButtons(buttons, includeNavigationButtons ? BuildNavigationButtons() : null)
                : null;

            if (index == 0 && editMessageId.HasValue && chunks.Count == 1)
            {
                await sender.EditTextMessageAsync(conversation, editMessageId.Value, chunks[index], chunkButtons, cancellationToken, debugContext).ConfigureAwait(false);
                continue;
            }

            await sender.SendTextMessageAsync(conversation, chunks[index], chunkButtons, cancellationToken, debugContext).ConfigureAwait(false);
        }
    }

    private static async Task ReactToSourceMessageAsync(
        ITelegramBotMessageSender sender,
        TelegramInboundMessage message,
        TelegramMessageReactionKind kind,
        CancellationToken cancellationToken)
    {
        if (!message.SourceMessageId.HasValue)
        {
            return;
        }

        await sender.ReactToMessageAsync(
            new TelegramMessageReaction(message.ConversationScope, message.SourceMessageId.Value, kind),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EditCallbackProgressAsync(
        ITelegramBotMessageSender sender,
        TelegramInboundMessage message,
        string text,
        CancellationToken cancellationToken)
    {
        if (!message.SourceMessageId.HasValue)
        {
            return;
        }

        TelegramDebugMessageContext? debugContext = await CreateReplyDebugContextAsync(message.ConversationScope, cancellationToken, "callback-edit").ConfigureAwait(false);
        await sender.EditTextMessageAsync(
            message.ConversationScope,
            message.SourceMessageId.Value,
            text,
            buttons: null,
            cancellationToken: cancellationToken,
            debugContext: debugContext).ConfigureAwait(false);
    }

    private async Task<TelegramDebugMessageContext?> CreateReplyDebugContextAsync(
        TelegramConversationScope conversation,
        CancellationToken cancellationToken,
        string source = "reply")
    {
        if (!_debugPreambleMode.IsEnabled)
        {
            return null;
        }

        string? sessionId = await _stateStore.GetActiveSessionIdAsync(conversation, cancellationToken).ConfigureAwait(false);
        return CreateDebugContext(source, sessionId);
    }

    private TelegramDebugMessageContext CreateDebugContext(
        string source,
        string? sessionId,
        string? turnId = null,
        string? kind = null,
        string? messageId = null,
        int? itemCount = null)
    {
        string? activeTurnId = string.IsNullOrWhiteSpace(sessionId)
            ? null
            : _turnCoordinator.GetActiveTurnId(sessionId);
        return new TelegramDebugMessageContext(source, sessionId, turnId, activeTurnId, kind, messageId, itemCount);
    }

    private async Task<bool> IsAuthorizedAsync(TelegramInboundCallback callback, CancellationToken cancellationToken)
        => TelegramAuthorization.IsAuthorized(
            callback.UserId,
            callback.ChatId,
            callback.ChatType,
            _options.AllowedUserIds,
            _options.AllowedChatIds,
            await _stateStore.GetTrustedChatIdsAsync(cancellationToken).ConfigureAwait(false));

    private async Task<bool> IsAuthorizedAsync(TelegramInboundMessage message, CancellationToken cancellationToken)
        => TelegramAuthorization.IsAuthorized(
            message.UserId,
            message.ChatId,
            message.ChatType,
            _options.AllowedUserIds,
            _options.AllowedChatIds,
            await _stateStore.GetTrustedChatIdsAsync(cancellationToken).ConfigureAwait(false));

    private static bool IsWhoAmI(ParsedTelegramCommand command)
        => command.IsCommand && string.Equals(command.Name, "whoami", StringComparison.OrdinalIgnoreCase);

    private bool CanRunChatTrustSetup(TelegramInboundMessage message, ParsedTelegramCommand command)
        => IsAllowedUser(message.UserId)
            && !IsPrivateChat(message)
            && (IsTrust(command) || IsDoctor(command));

    private static bool IsTrust(ParsedTelegramCommand command)
        => command.IsCommand && string.Equals(command.Name, "trust", StringComparison.OrdinalIgnoreCase);

    private static bool IsDoctor(ParsedTelegramCommand command)
        => command.IsCommand
            && (string.Equals(command.Name, "doctor", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command.Name, "diag", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command.Name, "diagnostics", StringComparison.OrdinalIgnoreCase));

    private bool IsAllowedUser(long userId)
        => _options.AllowedUserIds.Contains(userId);

    private bool IsConfiguredAllowedSharedChat(long chatId)
        => _options.AllowedChatIds.Contains(chatId);

    private static bool IsPrivateChat(TelegramInboundMessage message)
        => TelegramRoutingPolicy.IsPrivateChat(message.ChatType);

    private static bool IsForumTopicChat(TelegramInboundMessage message)
        => string.Equals(message.ChatType, "supergroup", StringComparison.OrdinalIgnoreCase);

    private static bool CanRoutePlainText(TelegramInboundMessage message)
        => TelegramRoutingPolicy.CanAutoRoute(message.ChatType, message.MessageThreadId);

    private static string BuildNotRoutedMessage(TelegramInboundMessage message)
        => TelegramRoutingPolicy.BuildNotRoutedMessage(message.ChatType);

    private static bool IsChatNotForumError(Exception exception)
        => exception.Message.Contains("chat is not a forum", StringComparison.OrdinalIgnoreCase);

    private static string BuildTopicCreationUnsupportedMessage(TelegramInboundMessage message)
        => IsPrivateChat(message)
            ? "This is a private chat, so /topic new cannot create a Telegram forum topic here. Use /new [name] to create a Codex session in this chat, or run /topic new inside a forum-enabled supergroup."
            : $"This is a {message.ChatType} chat, so /topic new only works in a forum-enabled supergroup. Use /new [name] to create a Codex session here.";

    private static string FormatActionFailurePrefix(ParsedTelegramCommand command, TelegramInboundMessage message)
        => command.IsCommand
            ? "Command"
            : !string.IsNullOrWhiteSpace(message.AudioFilePath)
                ? "Audio message"
                : message.Attachments is { Count: > 0 } ? "Attachment message" : "Message";

    private static void TryDeleteAudioFile(string? audioFilePath)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath))
        {
            return;
        }

        try
        {
            File.Delete(audioFilePath);
        }
        catch
        {
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

    private static string BuildHelpText()
        => string.Join(Environment.NewLine, [
            "Commands:",
            "Use the buttons below for quick navigation between sessions, projects, and help.",
            "/help - show this help",
            "/whoami - show Telegram user, chat, and topic thread IDs",
            "/version - show the running Codex Telegram app version",
            "/trust - trust the current group or forum chat for allowlisted users",
            "/projects - list known project directories",
            "/project add <path> - add and select a project",
            "/project <number|name|path> - select a project",
            "/topics - list Telegram topics/sessions in this chat",
            "/topic list - list Telegram topics/sessions in this chat",
            "/topic new <name> [| <absolute directory path>] - create a new Telegram forum topic and session in a forum-enabled supergroup",
            "/topic attach [sessionId] - bind the current Telegram forum topic to an existing Codex session",
            "/topic current - show the active topic/session in this conversation",
            "/sessions - show active and Telegram-managed sessions",
            "/sessions all [count] - show recent Codex history",
            "/new [name] - create and select a Codex session in the active project for this conversation",
            "/use <sessionId> - select the active session for this conversation",
            "/send <text> - send text to the active session",
            "/steer <text> - steer the active turn in the selected session",
            "/queue - view, edit, delete, or send queued prompts now",
            "/model [model] [thinking <effort>] - show or change the selected session model",
            "/thinking <minimal|low|medium|high|xhigh> - change the selected session thinking effort",
            "/goal [objective|set <objective>|clear|pause|resume|complete] - show or change the selected session goal",
            "/tail [count] - show recent output and keep following the session live",
            "/status [sessionId] - show session status",
            "/usage - show Codex account usage remaining and reset times",
            "/doctor - explain authorization, routing, active project/session, workspace roots, and queue state",
            "/debug [status|on|off|reset] - show or change diagnostic message preambles",
            "/outbound - show outbound Telegram queue status",
            "/stop [sessionId] - gracefully stop a session",
            "/restart confirm - explain how to restart this standalone process",
            "/kill <sessionId> confirm - hard-stop a session",
            "/rename <sessionId> <new name> - rename a session",
            "/forget <sessionId> - hide a stopped/exited session without deleting logs",
            "Plain text and audio in a private chat, trusted group, or topic stay on that conversation's session; if the conversation has none yet, the first message starts one and live output follows automatically.",
            "In forum topics, if plain text gets no response, Telegram bot privacy is likely hiding non-command messages; use /send <text> or disable privacy for this bot.",
            "Images, documents, and other attachments are forwarded to Codex; voice notes are transcribed with the configured OpenAI transcription model first.",
            "Replying to a Telegram message adds that message and nearby recent bot output as context for plain text, /send, /steer, and transcribed audio.",
            "Voice/text control phrase: Codex settings model gpt-5.4-mini thinking high: <prompt>"
        ]);

    private static string BuildVersionText()
        => string.Join(Environment.NewLine, [
            $"Incursa Codex Telegram {GetApplicationVersion()}",
            "If a documented command is unknown, the Telegram process is probably running an older binary than the repository or release you are reading."
        ]);

    private string FormatDebugModeStatus(string heading)
    {
        string overrideText = _debugPreambleMode.RuntimeOverrideEnabled switch
        {
            true => "on",
            false => "off",
            null => "(none)",
        };

        return string.Join(Environment.NewLine, [
            heading,
            $"Effective: {(_debugPreambleMode.IsEnabled ? "on" : "off")}",
            $"Runtime override: {overrideText}",
            $"Config default: {(_debugPreambleMode.ConfiguredDefaultEnabled ? "on" : "off")}",
            "When enabled, Telegram text messages are prefixed with source, chat/topic, session, turn, and active-turn metadata."
        ]);
    }

    private static string GetApplicationVersion()
        => typeof(TelegramCodexBotCommandHandler).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static string FormatProjects(IReadOnlyList<ProjectChoice> projects, string? activeProject)
    {
        if (projects.Count == 0)
        {
            return "No projects are configured. Use /project add <absolute directory path>.";
        }

        StringBuilder builder = new();
        builder.AppendLine("Projects:");
        for (int index = 0; index < projects.Count; index++)
        {
            ProjectChoice project = projects[index];
            string activeMarker = PathComparer.Equals(project.WorkingDirectory, activeProject) ? "*" : " ";
            builder.AppendLine($"{activeMarker} {index + 1}. {project.Name}");
            builder.AppendLine($"   {project.WorkingDirectory}");
        }

        builder.AppendLine("Use /project add <absolute path> to add another.");
        return builder.ToString().TrimEnd();
    }

    private static string FormatTrustResult(TelegramInboundMessage message, bool alreadyConfigured, bool alreadyTrusted)
    {
        string action = alreadyConfigured
            ? "This chat is already allowed by TelegramBot:AllowedChatIds."
            : alreadyTrusted
                ? "This chat is already trusted from Telegram."
                : "Trusted this chat for allowlisted users.";
        string topicLine = message.MessageThreadId.HasValue
            ? $"Topic thread ID: {message.MessageThreadId.Value.ToString(CultureInfo.InvariantCulture)}"
            : "Topic thread ID: (none)";

        return string.Join(Environment.NewLine, [
            action,
            $"Chat ID: {message.ChatId.ToString(CultureInfo.InvariantCulture)}",
            topicLine,
            "Trust applies to this Telegram chat; the chat root and each forum topic keep their own active project and session.",
            "Group-root messages can auto-route after trust, using the chat root as its own project/session scope.",
            "Use /trust remove here to remove Telegram-granted trust."
        ]);
    }

    private static string BuildSelectedProjectReply(string action, ProjectChoice project)
        => string.Join(Environment.NewLine, [
            $"{action} project {project.Name}.",
            project.WorkingDirectory,
            "Use /new [name] to start a Codex session there."
        ]);

    private static string FormatProjectStatus(ProjectChoice project)
        => string.Join(Environment.NewLine, [
            $"Project: {project.Name}",
            $"Path: {project.WorkingDirectory}",
            "New sessions will start in this directory."
        ]);

    private static SessionListView BuildSessionListView(
        IReadOnlyCollection<CodexSessionSummary> sessions,
        string? activeSessionId,
        IReadOnlyCollection<string> trackedSessionIds,
        SessionListRequest request)
    {
        HashSet<string> tracked = new(trackedSessionIds, StringComparer.OrdinalIgnoreCase);
        IOrderedEnumerable<CodexSessionSummary> ordered = sessions.OrderByDescending(session => session.LastActivityUtc);
        List<CodexSessionSummary> filtered = request.IncludeAll
            ? ordered.ToList()
            : ordered
                .Where(session => IsLive(session.Status)
                    || tracked.Contains(session.Id)
                    || string.Equals(session.Id, activeSessionId, StringComparison.OrdinalIgnoreCase))
                .ToList();

        return new SessionListView(
            filtered.Take(request.Limit).ToArray(),
            sessions.Count,
            filtered.Count,
            request.IncludeAll,
            request.Limit,
            activeSessionId);
    }

    private static string FormatSessions(SessionListView view)
    {
        if (view.Sessions.Count == 0)
        {
            return view.TotalSessionCount == 0
                ? "No Codex sessions are known yet. Use /new [name] to create one."
                : "No active or Telegram-managed sessions. Use /new [name], or /sessions all 10 to browse recent Codex history.";
        }

        StringBuilder builder = new();
        builder.AppendLine(view.IncludeAll ? "Recent Codex sessions:" : "Sessions:");
        for (int index = 0; index < view.Sessions.Count; index++)
        {
            CodexSessionSummary session = view.Sessions[index];
            string activeMarker = string.Equals(session.Id, view.ActiveSessionId, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            builder.AppendLine($"{activeMarker} {index + 1}. {session.Name} · {FormatStatusValue(session.Status)} · {FormatRelativeAge(session.LastActivityUtc)}");
        }

        if (view.FilteredSessionCount > view.Sessions.Count)
        {
            builder.AppendLine($"Showing {view.Sessions.Count} of {view.FilteredSessionCount}. Use /sessions all {Math.Min(view.FilteredSessionCount, view.Limit + DefaultSessionListLimit)} for more.");
        }

        if (!view.IncludeAll && view.TotalSessionCount > view.FilteredSessionCount)
        {
            builder.AppendLine("Older idle history is hidden. Use /sessions all 10.");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatTopicList(
        IReadOnlyList<TelegramConversationState> states,
        IReadOnlyDictionary<string, CodexSessionSummary> sessionsById,
        TelegramConversationScope currentScope)
    {
        if (states.Count == 0)
        {
            return "No Telegram topics or chat sessions are registered for this chat yet. Use /topic new <name> [| <absolute directory path>] or send a message in a topic.";
        }

        StringBuilder builder = new();
        builder.AppendLine("Telegram threads in this chat:");
        for (int index = 0; index < states.Count; index++)
        {
            TelegramConversationState state = states[index];
            string activeMarker = state.Scope.Equals(currentScope) ? "*" : " ";
            string scopeLabel = state.Scope.MessageThreadId is null
                ? "main chat"
                : $"topic {state.Scope.MessageThreadId.Value.ToString(CultureInfo.InvariantCulture)}";
            string sessionText = FormatTopicSessionSummary(state.ActiveSessionId, sessionsById);
            string projectText = string.IsNullOrWhiteSpace(state.ActiveProjectWorkingDirectory)
                ? string.Empty
                : $" · project {CodexTextFormatting.ResolveProjectName(state.ActiveProjectWorkingDirectory)}";
            string queueText = state.QueuedPromptCount > 0
                ? $" · queued {state.QueuedPromptCount.ToString(CultureInfo.InvariantCulture)}"
                : string.Empty;

            builder.AppendLine($"{activeMarker} {index + 1}. {scopeLabel} · {sessionText}{projectText}{queueText}");
        }

        builder.AppendLine("Open a Telegram topic and send a message there to keep work isolated.");
        return builder.ToString().TrimEnd();
    }

    private static string FormatTopicSessionSummary(
        string? sessionId,
        IReadOnlyDictionary<string, CodexSessionSummary> sessionsById)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return "<no session>";
        }

        if (!sessionsById.TryGetValue(sessionId, out CodexSessionSummary? session))
        {
            return $"session {GetShortSessionId(sessionId)} missing";
        }

        return $"{session.Name} · {FormatStatusValue(session.Status)} · {FormatRelativeAge(session.LastActivityUtc)}";
    }

    private static string FormatStatus(CodexSessionSummary session, CodexSessionModelSettings? settings = null, string? usageSummary = null)
    {
        StringBuilder builder = new();
        builder.AppendLine($"Session: {session.Name}");
        builder.AppendLine($"Status: {FormatStatusValue(session.Status)}");
        builder.AppendLine($"Working directory: {session.WorkingDirectory ?? "<default>"}");
        if (settings is not null)
        {
            builder.AppendLine($"Model: {FormatModelDisplay(settings)}");
            builder.AppendLine($"Thinking: {FormatValue(settings.ReasoningEffort)}");
        }

        if (!string.IsNullOrWhiteSpace(usageSummary))
        {
            builder.AppendLine(usageSummary);
        }

        builder.AppendLine($"Created: {FormatRelativeAge(session.CreatedUtc)}");
        builder.AppendLine($"Last activity: {FormatRelativeAge(session.LastActivityUtc)}");
        builder.AppendLine($"Use command: /use {GetShortSessionId(session.Id)}");
        if (session.ExitCode.HasValue)
        {
            builder.AppendLine($"Exit code: {session.ExitCode}");
        }

        if (!string.IsNullOrWhiteSpace(session.LastError))
        {
            builder.AppendLine($"Last error: {session.LastError}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatGoal(CodexThreadGoalVm goal)
    {
        StringBuilder builder = new();
        builder.AppendLine($"Objective: {goal.Objective}");
        builder.AppendLine($"Status: {FormatGoalStatus(goal.Status)}");

        if (goal.TokenBudget.HasValue)
        {
            builder.AppendLine($"Token budget: {goal.TokensUsed.ToString("N0", CultureInfo.InvariantCulture)}/{goal.TokenBudget.Value.ToString("N0", CultureInfo.InvariantCulture)}");
        }
        else if (goal.TokensUsed > 0)
        {
            builder.AppendLine($"Tokens used: {goal.TokensUsed.ToString("N0", CultureInfo.InvariantCulture)}");
        }

        if (goal.TimeUsedSeconds > 0)
        {
            builder.AppendLine($"Time used: {FormatDuration(TimeSpan.FromSeconds(goal.TimeUsedSeconds))}");
        }

        builder.AppendLine($"Updated: {FormatRelativeAge(goal.UpdatedAt)}");
        return builder.ToString().TrimEnd();
    }

    private static string FormatGoalStatus(CodexThreadGoalStatus status)
        => status switch
        {
            CodexThreadGoalStatus.Active => "active",
            CodexThreadGoalStatus.Paused => "paused",
            CodexThreadGoalStatus.BudgetLimited => "budget limited",
            CodexThreadGoalStatus.Complete => "complete",
            _ => status.ToString(),
        };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
    }

    private static string FormatAccountUsage(CodexAccountUsageVm usage)
    {
        if (usage.RateLimits.Count == 0)
        {
            return "Codex usage: no account usage windows were reported by Codex.";
        }

        CodexRateLimitSnapshotVm primaryBucket = ResolvePrimaryUsageBucket(usage)!;
        StringBuilder builder = new();
        builder.AppendLine("Codex usage");

        string bucketName = primaryBucket.LimitName ?? primaryBucket.LimitId ?? "default";
        if (!string.IsNullOrWhiteSpace(primaryBucket.PlanType))
        {
            builder.AppendLine($"Plan: {primaryBucket.PlanType}");
        }

        builder.AppendLine($"Bucket: {bucketName}");
        AppendUsageWindow(builder, "Primary window", primaryBucket.Primary, usage.RetrievedAtUtc);
        AppendUsageWindow(builder, "Secondary window", primaryBucket.Secondary, usage.RetrievedAtUtc);

        if (!string.IsNullOrWhiteSpace(primaryBucket.RateLimitReachedType))
        {
            builder.AppendLine($"Limit status: {primaryBucket.RateLimitReachedType}");
        }

        if (usage.RateLimits.Count > 1)
        {
            builder.AppendLine($"Other buckets reported: {(usage.RateLimits.Count - 1).ToString(CultureInfo.InvariantCulture)}");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds the best-effort compact account usage line for fast control replies.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the lookup.</param>
    /// <returns>Compact usage text, or <see langword="null" /> when usage is unavailable or too slow.</returns>
    private Task<string?> TryBuildAccountUsageSummaryAsync(CancellationToken cancellationToken)
        => TryBuildAccountUsageSummaryAsync(InlineUsageSummaryTimeout, useCachedMissingSummary: true, cancellationToken);

    /// <summary>
    /// Builds the compact account usage line for the explicit status command.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the lookup.</param>
    /// <returns>Compact usage text, or <see langword="null" /> when usage is unavailable.</returns>
    private Task<string?> TryBuildStatusAccountUsageSummaryAsync(CancellationToken cancellationToken)
        => TryBuildAccountUsageSummaryAsync(StatusUsageSummaryTimeout, useCachedMissingSummary: false, cancellationToken);

    /// <summary>
    /// Builds and caches the compact account usage line with caller-specific timeout behavior.
    /// </summary>
    /// <param name="timeoutDuration">Maximum time to wait for the Codex app-server.</param>
    /// <param name="useCachedMissingSummary">Whether a cached missing result is good enough for this caller.</param>
    /// <param name="cancellationToken">Cancellation token for the lookup.</param>
    /// <returns>Compact usage text, or <see langword="null" /> when usage is unavailable.</returns>
    private async Task<string?> TryBuildAccountUsageSummaryAsync(
        TimeSpan timeoutDuration,
        bool useCachedMissingSummary,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (CanUseCachedUsageSummary(now, useCachedMissingSummary))
        {
            return _cachedUsageSummary;
        }

        await _usageSummaryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (CanUseCachedUsageSummary(now, useCachedMissingSummary))
            {
                return _cachedUsageSummary;
            }

            _cachedUsageSummary = await FetchAccountUsageSummaryAsync(timeoutDuration, cancellationToken).ConfigureAwait(false);
            _cachedUsageSummaryExpiresAtUtc = now.Add(InlineUsageSummaryCacheDuration);
            _hasCachedUsageSummary = true;
            return _cachedUsageSummary;
        }
        finally
        {
            _usageSummaryLock.Release();
        }
    }

    /// <summary>
    /// Checks whether the cached usage summary is acceptable for the current caller.
    /// </summary>
    /// <param name="now">Current UTC time.</param>
    /// <param name="useCachedMissingSummary">Whether cached missing data should suppress another lookup.</param>
    /// <returns><see langword="true" /> when the cache can be reused.</returns>
    private bool CanUseCachedUsageSummary(DateTimeOffset now, bool useCachedMissingSummary)
        => _hasCachedUsageSummary
            && _cachedUsageSummaryExpiresAtUtc > now
            && (useCachedMissingSummary || !string.IsNullOrWhiteSpace(_cachedUsageSummary));

    /// <summary>
    /// Fetches and formats a compact usage line from Codex within a bounded timeout.
    /// </summary>
    /// <param name="timeoutDuration">Maximum time to wait for Codex.</param>
    /// <param name="cancellationToken">Cancellation token for the lookup.</param>
    /// <returns>Compact usage text, or <see langword="null" /> when usage cannot be shown inline.</returns>
    private async Task<string?> FetchAccountUsageSummaryAsync(TimeSpan timeoutDuration, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutDuration);

        try
        {
            CodexAccountUsageVm usage = await _accountUsageService.GetUsageAsync(timeout.Token).ConfigureAwait(false);
            return FormatAccountUsageSummary(usage);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(exception, "Codex usage summary lookup timed out before a control reply was sent.");
            return null;
        }
        catch (FileNotFoundException exception)
        {
            _logger.LogDebug(exception, "Skipping inline Codex usage summary because the Codex executable was not found.");
            return null;
        }
        catch (CodexCapabilityNotSupportedException exception)
        {
            _logger.LogDebug(exception, "Skipping inline Codex usage summary because the configured backend does not support account rate limits.");
            return null;
        }
        catch (CodexMethodNotFoundException exception)
        {
            _logger.LogDebug(exception, "Skipping inline Codex usage summary because the installed Codex app-server does not expose account rate limits.");
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Skipping inline Codex usage summary because usage lookup failed.");
            return null;
        }
    }

    private static string? FormatAccountUsageSummary(CodexAccountUsageVm usage)
    {
        CodexRateLimitSnapshotVm? primaryBucket = ResolvePrimaryUsageBucket(usage);
        if (primaryBucket is null)
        {
            return null;
        }

        List<string> windows = [];
        AddUsageWindowSummary(windows, "primary block", primaryBucket.Primary, usage.RetrievedAtUtc);
        AddUsageWindowSummary(windows, "secondary block", primaryBucket.Secondary, usage.RetrievedAtUtc);
        if (windows.Count == 0)
        {
            return null;
        }

        string prefix = string.IsNullOrWhiteSpace(primaryBucket.PlanType)
            ? "Rate limits"
            : $"Rate limits ({primaryBucket.PlanType})";
        return $"{prefix}: {string.Join("; ", windows)}";
    }

    private static CodexRateLimitSnapshotVm? ResolvePrimaryUsageBucket(CodexAccountUsageVm usage)
        => usage.RateLimits.FirstOrDefault(bucket => string.Equals(bucket.LimitId, "codex", StringComparison.OrdinalIgnoreCase))
            ?? usage.RateLimits.FirstOrDefault();

    private static void AddUsageWindowSummary(
        List<string> windows,
        string fallbackLabel,
        CodexRateLimitWindowVm? window,
        DateTimeOffset retrievedAtUtc)
    {
        if (window is null)
        {
            return;
        }

        string label = FormatWindowSummaryLabel(fallbackLabel, window.WindowDurationMinutes);
        int remainingPercent = Math.Clamp(100 - window.UsedPercent, 0, 100);
        StringBuilder builder = new();
        builder.Append($"{label}: {remainingPercent.ToString(CultureInfo.InvariantCulture)}%");
        if (window.ResetsAtUtc is { } resetsAtUtc)
        {
            builder.Append($", resets {FormatCompactResetTime(retrievedAtUtc, resetsAtUtc)}");
        }

        windows.Add(builder.ToString());
    }

    private static void AppendUsageWindow(StringBuilder builder, string fallbackLabel, CodexRateLimitWindowVm? window, DateTimeOffset retrievedAtUtc)
    {
        if (window is null)
        {
            builder.AppendLine($"{fallbackLabel}: not reported");
            return;
        }

        string label = FormatWindowLabel(fallbackLabel, window.WindowDurationMinutes);
        int remainingPercent = Math.Clamp(100 - window.UsedPercent, 0, 100);
        builder.Append($"{label}: {remainingPercent.ToString(CultureInfo.InvariantCulture)}% remaining ({window.UsedPercent.ToString(CultureInfo.InvariantCulture)}% used)");
        if (window.ResetsAtUtc is { } resetsAtUtc)
        {
            builder.Append($"; resets {FormatResetDistance(retrievedAtUtc, resetsAtUtc)} at {FormatResetTime(resetsAtUtc)}");
        }

        builder.AppendLine();
    }

    private static string FormatWindowLabel(string fallbackLabel, long? windowDurationMinutes)
    {
        if (!windowDurationMinutes.HasValue || windowDurationMinutes.Value <= 0)
        {
            return fallbackLabel;
        }

        return windowDurationMinutes.Value switch
        {
            300 => "5-hour window",
            10080 => "Weekly window",
            _ when windowDurationMinutes.Value % 1440 == 0 => $"{(windowDurationMinutes.Value / 1440).ToString(CultureInfo.InvariantCulture)}-day window",
            _ when windowDurationMinutes.Value % 60 == 0 => $"{(windowDurationMinutes.Value / 60).ToString(CultureInfo.InvariantCulture)}-hour window",
            _ => $"{windowDurationMinutes.Value.ToString(CultureInfo.InvariantCulture)}-minute window",
        };
    }

    /// <summary>
    /// Formats a short block label for compact status and menu usage summaries.
    /// </summary>
    /// <param name="fallbackLabel">Label to use when Codex does not report a duration.</param>
    /// <param name="windowDurationMinutes">Reported window duration in minutes.</param>
    /// <returns>Short block label.</returns>
    private static string FormatWindowSummaryLabel(string fallbackLabel, long? windowDurationMinutes)
    {
        if (!windowDurationMinutes.HasValue || windowDurationMinutes.Value <= 0)
        {
            return fallbackLabel;
        }

        return windowDurationMinutes.Value switch
        {
            300 => "5-hour block",
            10080 => "weekly block",
            _ when windowDurationMinutes.Value % 1440 == 0 => $"{(windowDurationMinutes.Value / 1440).ToString(CultureInfo.InvariantCulture)}-day block",
            _ when windowDurationMinutes.Value % 60 == 0 => $"{(windowDurationMinutes.Value / 60).ToString(CultureInfo.InvariantCulture)}-hour block",
            _ => $"{windowDurationMinutes.Value.ToString(CultureInfo.InvariantCulture)}-minute block",
        };
    }

    /// <summary>
    /// Formats a compact local reset timestamp for one-line usage summaries.
    /// </summary>
    /// <param name="retrievedAtUtc">UTC time when the usage data was retrieved.</param>
    /// <param name="resetsAtUtc">UTC reset time reported by Codex.</param>
    /// <returns>Local reset time using the shortest unambiguous format.</returns>
    private static string FormatCompactResetTime(DateTimeOffset retrievedAtUtc, DateTimeOffset resetsAtUtc)
    {
        DateTimeOffset retrievedAtLocal = retrievedAtUtc.ToLocalTime();
        DateTimeOffset resetsAtLocal = resetsAtUtc.ToLocalTime();
        string format = resetsAtLocal.Date == retrievedAtLocal.Date
            ? "h:mm tt"
            : resetsAtLocal.Year == retrievedAtLocal.Year
                ? "MMM d h:mm tt"
                : "yyyy-MM-dd h:mm tt";

        return resetsAtLocal.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string FormatResetDistance(DateTimeOffset retrievedAtUtc, DateTimeOffset resetsAtUtc)
    {
        TimeSpan remaining = resetsAtUtc.ToUniversalTime() - retrievedAtUtc.ToUniversalTime();
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }

        if (remaining >= TimeSpan.FromDays(1))
        {
            return $"in {(int)remaining.TotalDays}d {remaining.Hours}h";
        }

        if (remaining >= TimeSpan.FromHours(1))
        {
            return $"in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        }

        return $"in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)).ToString(CultureInfo.InvariantCulture)}m";
    }

    private static string FormatResetTime(DateTimeOffset resetsAtUtc)
        => resetsAtUtc.ToLocalTime().ToString("MMM d h:mm tt", CultureInfo.InvariantCulture);

    private static string BuildQueueUsage()
        => string.Join(Environment.NewLine, [
            "Usage:",
            "/queue - show queued prompts for this conversation",
            "/queue all - show your queued prompts across conversations",
            "/queue edit <id> <new text> - replace queued prompt text",
            "/queue delete <id> - delete a queued prompt",
            "/queue send <id> - remove a queued prompt and steer the active turn with it"
        ]);

    private static string FormatQueuedPrompts(
        IReadOnlyList<TelegramQueuedPrompt> prompts,
        bool includeAll,
        TelegramConversationScope currentConversation,
        string? prefix)
    {
        StringBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            builder.AppendLine(prefix);
            builder.AppendLine();
        }

        if (prompts.Count == 0)
        {
            builder.Append(includeAll
                ? "You do not have any queued prompts."
                : "No queued prompts for this conversation.");
            return builder.ToString();
        }

        builder.AppendLine(includeAll ? "Your queued prompts:" : "Queued prompts:");
        for (int index = 0; index < prompts.Count; index++)
        {
            TelegramQueuedPrompt prompt = prompts[index];
            string scopeText = includeAll
                ? $" · {FormatQueuedPromptScope(prompt.ConversationScope, currentConversation)}"
                : string.Empty;
            string attachmentText = prompt.Attachments is { Count: > 0 }
                ? $" · {prompt.Attachments.Count.ToString(CultureInfo.InvariantCulture)} attachment(s)"
                : string.Empty;

            builder.AppendLine($"{index + 1}. {prompt.SessionName} · id {GetShortQueuedPromptId(prompt.Id)} · queued {FormatRelativeAge(prompt.EnqueuedAt)}{scopeText}{attachmentText}");
            builder.AppendLine($"   {FormatQueuedPromptPreview(prompt)}");
        }

        builder.AppendLine();
        builder.AppendLine("Buttons can send now, edit, or delete each item. Send now steers the active turn; if no turn is active, the item stays queued.");
        builder.AppendLine("Text edit command: /queue edit <id> <new text>");
        return builder.ToString().TrimEnd();
    }

    private static string FormatQueuedPromptScope(TelegramConversationScope scope, TelegramConversationScope currentConversation)
    {
        if (scope == currentConversation)
        {
            return "current conversation";
        }

        return scope.MessageThreadId is null
            ? $"chat {scope.ChatId.ToString(CultureInfo.InvariantCulture)}"
            : $"chat {scope.ChatId.ToString(CultureInfo.InvariantCulture)} topic {scope.MessageThreadId.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string FormatQueuedPromptPreview(TelegramQueuedPrompt prompt)
    {
        string text = string.IsNullOrWhiteSpace(prompt.Text)
            ? "<attachment-only prompt>"
            : SingleLine(prompt.Text.Trim());

        return text.Length <= QueuedPromptPreviewLength
            ? text
            : text[..QueuedPromptPreviewLength].TrimEnd() + "...";
    }

    private static string SingleLine(string text)
    {
        StringBuilder builder = new(text.Length);
        bool lastWasWhiteSpace = false;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasWhiteSpace)
                {
                    builder.Append(' ');
                    lastWasWhiteSpace = true;
                }

                continue;
            }

            builder.Append(ch);
            lastWasWhiteSpace = false;
        }

        return builder.ToString();
    }

    private static IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? BuildQueuedPromptButtons(IReadOnlyList<TelegramQueuedPrompt> prompts)
    {
        if (prompts.Count == 0)
        {
            return null;
        }

        List<IReadOnlyList<TelegramReplyButton>> rows = [];
        for (int index = 0; index < prompts.Count; index++)
        {
            TelegramQueuedPrompt prompt = prompts[index];
            string suffix = prompts.Count == 1 ? string.Empty : $" {(index + 1).ToString(CultureInfo.InvariantCulture)}";
            rows.Add([
                new TelegramReplyButton($"Send now{suffix}", $"qnow:{prompt.Id}"),
                new TelegramReplyButton($"Edit{suffix}", $"qedit:{prompt.Id}"),
                new TelegramReplyButton($"Delete{suffix}", $"qdel:{prompt.Id}")
            ]);
        }

        return rows;
    }

    private static string GetShortQueuedPromptId(string promptId)
        => promptId.Length <= 8 ? promptId : promptId[..8];

    private static bool IsAllQueueSelector(string value)
        => value.Equals("all", StringComparison.OrdinalIgnoreCase)
            || value.Equals("mine", StringComparison.OrdinalIgnoreCase);

    private static string FormatOutboundStatus(TelegramOutboundQueueStatus status, long currentChatId)
    {
        StringBuilder builder = new();
        builder.AppendLine("Outbound Telegram queue:");
        builder.AppendLine($"Pending destinations: {status.PendingDestinationCount}");
        builder.AppendLine($"Pending messages: {status.PendingMessageCount}");
        builder.AppendLine($"Pending chunks: {status.PendingChunkCount}");
        builder.AppendLine($"Pending chars: {status.PendingCharacterCount}");

        if (status.OldestWaitingDestination is { } oldest)
        {
            builder.AppendLine($"Oldest waiting: chat {oldest.ChatId}, topic {oldest.MessageThreadId?.ToString(CultureInfo.InvariantCulture) ?? "<none>"} since {FormatNullableAge(status.OldestFirstPendingUtc)}");
        }

        if (status.GlobalBackoffUntilUtc is { } globalBackoff)
        {
            builder.AppendLine($"Global backoff: until {globalBackoff:u}");
        }

        IReadOnlyList<TelegramOutboundDestinationStatus> chatDestinations = status.Destinations
            .Where(destination => destination.ChatId == currentChatId)
            .Take(8)
            .ToArray();
        if (chatDestinations.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("This chat:");
            foreach (TelegramOutboundDestinationStatus destination in chatDestinations)
            {
                builder.AppendLine($"- topic {destination.MessageThreadId?.ToString(CultureInfo.InvariantCulture) ?? "<none>"}: {destination.PendingMessageCount} msg, {destination.PendingChunkCount} chunks, oldest {FormatNullableAge(destination.FirstPendingUtc)}");
                if (destination.ChatBackoffUntilUtc is { } chatBackoff)
                {
                    builder.AppendLine($"  backoff until {chatBackoff:u}");
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string> FormatDoctorConversationAsync(TelegramInboundMessage message, CancellationToken cancellationToken)
    {
        bool userAllowed = IsAllowedUser(message.UserId);
        bool chatNeedsAllowlist = !IsPrivateChat(message);
        bool configuredChatAllowed = IsConfiguredAllowedSharedChat(message.ChatId);
        bool telegramTrustedChat = chatNeedsAllowlist && await _stateStore.IsChatTrustedAsync(message.ChatId, cancellationToken).ConfigureAwait(false);
        bool chatAllowed = !chatNeedsAllowlist || configuredChatAllowed || telegramTrustedChat;
        bool authorized = await IsAuthorizedAsync(message, cancellationToken).ConfigureAwait(false);
        string routing = CanRoutePlainText(message)
            ? "Plain text, audio, and attachments can auto-route in this conversation."
            : "Plain text and attachments do not auto-route from this chat type. Use /send <text> or message me privately.";
        string chatAllowlistText = chatNeedsAllowlist
            ? chatAllowed ? configuredChatAllowed ? "allowed by config" : "trusted from Telegram" : "not allowed"
            : "not required for private chat";

        return string.Join(Environment.NewLine, [
            "Conversation:",
            $"- Chat: {message.ChatId.ToString(CultureInfo.InvariantCulture)} ({DescribeChat(message)})",
            $"- Topic thread: {message.MessageThreadId?.ToString(CultureInfo.InvariantCulture) ?? "<none>"}",
            $"- User allowlist: {(userAllowed ? "allowed" : "not allowed")}",
            $"- Chat allowlist: {chatAllowlistText}",
            $"- Effective access: {(authorized ? "allowed" : "blocked except setup commands")}",
            $"- Routing: {routing}"
        ]);
    }

    private async Task AppendDoctorProjectAndSessionAsync(StringBuilder builder, TelegramInboundMessage message, CancellationToken cancellationToken)
    {
        builder.AppendLine("Project and session:");

        try
        {
            IReadOnlyList<ProjectChoice> projects = await ListProjectChoicesAsync(cancellationToken).ConfigureAwait(false);
            string? activeProject = await _stateStore.GetActiveProjectWorkingDirectoryAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
            ProjectChoice? project = string.IsNullOrWhiteSpace(activeProject)
                ? null
                : projects.FirstOrDefault(candidate => PathComparer.Equals(candidate.WorkingDirectory, activeProject));

            builder.AppendLine($"- Known projects: {projects.Count.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine(project is null
                ? "- Active project: <none>"
                : $"- Active project: {project.Name} ({project.WorkingDirectory})");
        }
        catch (Exception exception)
        {
            builder.AppendLine($"- Project state: unavailable ({exception.Message})");
        }

        try
        {
            IReadOnlyCollection<CodexSessionSummary> sessions = await _sessionManager.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
            string? activeSessionId = await _stateStore.GetActiveSessionIdAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
            CodexSessionSummary? activeSession = string.IsNullOrWhiteSpace(activeSessionId)
                ? null
                : sessions.FirstOrDefault(session => string.Equals(session.Id, activeSessionId, StringComparison.OrdinalIgnoreCase));

            builder.AppendLine($"- Known sessions: {sessions.Count.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine(activeSession is null
                ? "- Active session: <none>"
                : $"- Active session: {activeSession.Name} ({FormatStatusValue(activeSession.Status)}, {FormatRelativeAge(activeSession.LastActivityUtc)})");
        }
        catch (Exception exception)
        {
            builder.AppendLine($"- Session state: unavailable ({exception.Message})");
        }
    }

    private void AppendDoctorWorkspace(StringBuilder builder)
    {
        builder.AppendLine("Workspace:");
        try
        {
            CodexWorkspaceOverviewVm overview = _workspaceBrowser.GetWorkspaceOverview();
            builder.AppendLine($"- Server platform: {overview.ServerPlatform}");
            builder.AppendLine($"- Process directory: {overview.CurrentWorkingDirectory}");
            builder.AppendLine($"- Default working directory: {overview.ConfiguredWorkingDirectory ?? "<none>"}");
            builder.AppendLine($"- Workspace roots: {overview.WorkspaceRoots.Count.ToString(CultureInfo.InvariantCulture)}");

            foreach (CodexWorkspaceRootVm root in overview.WorkspaceRoots.Take(4))
            {
                builder.AppendLine($"  - {root.Path} ({root.Status})");
            }

            if (overview.WorkspaceRoots.Count > 4)
            {
                builder.AppendLine($"  - ... {overview.WorkspaceRoots.Count - 4} more");
            }
        }
        catch (Exception exception)
        {
            builder.AppendLine($"- Workspace state: unavailable ({exception.Message})");
        }
    }

    private async Task AppendDoctorOutboundAsync(StringBuilder builder, TelegramInboundMessage message, CancellationToken cancellationToken)
    {
        builder.AppendLine("Outbound queue:");
        try
        {
            TelegramOutboundQueueStatus status = await _outboundQueue.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            builder.AppendLine($"- Pending messages: {status.PendingMessageCount.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"- Pending chunks: {status.PendingChunkCount.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"- Pending destinations: {status.PendingDestinationCount.ToString(CultureInfo.InvariantCulture)}");

            TelegramOutboundDestinationStatus? current = status.Destinations.FirstOrDefault(destination =>
                destination.ChatId == message.ChatId
                && destination.MessageThreadId == message.MessageThreadId);
            if (current is not null)
            {
                builder.AppendLine($"- This conversation: {current.PendingMessageCount.ToString(CultureInfo.InvariantCulture)} messages, {current.PendingChunkCount.ToString(CultureInfo.InvariantCulture)} chunks");
            }

            if (status.GlobalBackoffUntilUtc is { } backoff)
            {
                builder.AppendLine($"- Global backoff until: {backoff:u}");
            }
        }
        catch (Exception exception)
        {
            builder.AppendLine($"- Queue state: unavailable ({exception.Message})");
        }
    }

    private async Task<string> BuildDoctorNextActionAsync(TelegramInboundMessage message, CancellationToken cancellationToken)
    {
        bool userAllowed = IsAllowedUser(message.UserId);
        if (!userAllowed)
        {
            return "Next: add this Telegram user ID to TelegramBot:AllowedUserIds, then restart or relaunch if your configuration source does not reload.";
        }

        if (!IsPrivateChat(message)
            && !IsConfiguredAllowedSharedChat(message.ChatId)
            && !await _stateStore.IsChatTrustedAsync(message.ChatId, cancellationToken).ConfigureAwait(false))
        {
            return "Next: send /trust here to trust this chat for allowlisted users, or continue in a private chat.";
        }

        if (!CanRoutePlainText(message))
        {
            return "Next: use /send <text> or message me privately. This chat type is not automatically routed to Codex.";
        }

        try
        {
            string? activeProject = await _stateStore.GetActiveProjectWorkingDirectoryAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(activeProject))
            {
                return "Next: use /projects or /project add <absolute directory path>, then /new [name].";
            }

            string? activeSessionId = await _stateStore.GetActiveSessionIdAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(activeSessionId))
            {
                return "Next: use /new [name], or just send a message to start a session in the active project.";
            }
        }
        catch
        {
            return "Next: use /projects and /sessions to refresh local state; check the terminal logs if either command fails.";
        }

        return "Next: send a normal message to continue, or use /status, /tail, /usage, /model, /thinking, /goal, and /outbound if something seems off.";
    }

    private static string DescribeChat(TelegramInboundMessage message)
    {
        if (IsPrivateChat(message))
        {
            return "private chat";
        }

        if (message.MessageThreadId is not null)
        {
            return $"{message.ChatType} topic";
        }

        return $"{message.ChatType} root";
    }

    private static string FormatModelSettings(CodexSessionModelSettings settings, string? usageSummary = null)
    {
        StringBuilder builder = new();
        builder.AppendLine($"Session: {settings.SessionName}");
        builder.AppendLine($"Model: {FormatModelDisplay(settings)}");
        builder.AppendLine($"Thinking: {FormatValue(settings.ReasoningEffort)}");
        if (!string.IsNullOrWhiteSpace(usageSummary))
        {
            builder.AppendLine(usageSummary);
        }

        if (settings.AvailableReasoningEfforts.Count > 0)
        {
            builder.AppendLine($"Available thinking: {string.Join(", ", settings.AvailableReasoningEfforts)}");
        }

        if (settings.AvailableModels.Count > 0)
        {
            builder.AppendLine("Use /model <model> thinking <effort>. Examples:");
            foreach (CodexModelVm model in settings.AvailableModels.Take(8))
            {
                builder.AppendLine($"- /model {model.Id} thinking {model.DefaultReasoningEffort}");
            }
        }

        builder.AppendLine("Voice phrase: Codex settings model gpt-5.4-mini thinking high: <prompt>");
        return builder.ToString().TrimEnd();
    }

    private static string FormatModelDisplay(CodexSessionModelSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            CodexModelVm? model = settings.AvailableModels.FirstOrDefault(candidate => string.Equals(candidate.Id, settings.Model, StringComparison.OrdinalIgnoreCase));
            return model is null ? settings.Model : model.DisplayName;
        }

        CodexModelVm? defaultModel = settings.AvailableModels.FirstOrDefault(candidate => candidate.IsDefault) ?? settings.AvailableModels.FirstOrDefault();
        return defaultModel is null ? "<default>" : $"{defaultModel.DisplayName} (default)";
    }

    private static string FormatValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "<default>" : value;

    private static string FormatStatusValue(CodexSessionStatus status)
        => status switch
        {
            CodexSessionStatus.Exited => "idle",
            _ => status.ToString().ToLowerInvariant(),
        };

    private static string FormatRelativeAge(DateTimeOffset value)
    {
        TimeSpan age = DateTimeOffset.UtcNow - value.ToUniversalTime();
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours}h ago";
        }

        return $"{(int)age.TotalDays}d ago";
    }

    private static string FormatNullableAge(DateTimeOffset? value)
        => value is null ? "<none>" : FormatRelativeAge(value.Value);

    private static string BuildSelectedSessionReply(
        string action,
        CodexSessionSummary session,
        CodexSessionModelSettings settings,
        string? projectName = null,
        string? usageSummary = null)
    {
        List<string> lines =
        [
            $"{action} {session.Name}.",
            $"Project: {projectName ?? CodexTextFormatting.ResolveProjectName(session.WorkingDirectory)}",
            $"Model: {FormatModelDisplay(settings)}",
            $"Thinking: {FormatValue(settings.ReasoningEffort)}",
        ];

        if (!string.IsNullOrWhiteSpace(usageSummary))
        {
            lines.Add(usageSummary);
        }

        lines.Add("Send a message to continue, or use /tail, /status, /model, /thinking, /goal, or /usage when you need a control.");
        return string.Join(Environment.NewLine, lines);
    }

    internal static IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? BuildSessionButtons(
        IReadOnlyList<CodexSessionSummary> sessions,
        bool includeUse = true)
    {
        if (sessions.Count == 0 || !includeUse)
        {
            return null;
        }

        List<IReadOnlyList<TelegramReplyButton>> rows = [];
        for (int index = 0; index < sessions.Count; index++)
        {
            CodexSessionSummary session = sessions[index];
            string suffix = sessions.Count == 1 ? string.Empty : $" {(index + 1).ToString(CultureInfo.InvariantCulture)}";
            List<TelegramReplyButton> row = [];
            row.Add(new TelegramReplyButton($"Use{suffix}", $"use:{session.Id}"));

            rows.Add(row);
        }

        return rows;
    }

    internal static IReadOnlyList<IReadOnlyList<TelegramReplyButton>> BuildNavigationButtons()
        => [
            [
                new TelegramReplyButton("Sessions", "nav:sessions"),
                new TelegramReplyButton("Projects", "nav:projects"),
                new TelegramReplyButton("Help", "nav:help")
            ]
        ];

    private static string BuildDefaultTopicName(ProjectChoice project)
    {
        string projectName = string.IsNullOrWhiteSpace(project.Name)
            ? CodexTextFormatting.ResolveProjectName(project.WorkingDirectory)
            : project.Name.Trim();

        if (string.IsNullOrWhiteSpace(projectName))
        {
            projectName = "Topic";
        }

        string topicName = $"{projectName} lane {DateTimeOffset.UtcNow:yyyyMMdd HHmm}";
        return topicName.Length <= 120 ? topicName : topicName[..120].TrimEnd();
    }

    private static string BuildDefaultSessionName(ProjectChoice project)
    {
        string projectName = string.IsNullOrWhiteSpace(project.Name)
            ? CodexTextFormatting.ResolveProjectName(project.WorkingDirectory)
            : project.Name.Trim();

        return BuildDefaultSessionNameFromProjectName(projectName);
    }

    private static string BuildDefaultSessionNameForWorkingDirectory(string? workingDirectory)
        => string.IsNullOrWhiteSpace(workingDirectory)
            ? BuildDefaultSessionNameFromProjectName("Telegram")
            : BuildDefaultSessionNameFromProjectName(CodexTextFormatting.ResolveProjectName(workingDirectory));

    private static string BuildDefaultSessionNameFromProjectName(string? projectName)
    {
        string normalizedProjectName = string.IsNullOrWhiteSpace(projectName)
            ? "Telegram"
            : projectName.Trim();
        string sessionName = $"{normalizedProjectName} session {DateTimeOffset.UtcNow:yyyyMMdd HHmm}";
        return sessionName.Length <= 80 ? sessionName : sessionName[..80].TrimEnd();
    }

    private static IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? MergeButtons(
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? primary,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? secondary)
    {
        if (primary is null)
        {
            return secondary;
        }

        if (secondary is null)
        {
            return primary;
        }

        List<IReadOnlyList<TelegramReplyButton>> rows = [];
        rows.AddRange(primary);
        rows.AddRange(secondary);
        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? BuildModelSelectionButtons(CodexSessionModelSettings settings)
    {
        if (settings.AvailableModels.Count == 0)
        {
            return null;
        }

        List<IReadOnlyList<TelegramReplyButton>> rows = [];
        string sessionToken = GetShortSessionId(settings.SessionId);
        foreach (var item in settings.AvailableModels.Take(8).Select((model, index) => new { Model = model, Index = index }))
        {
            string label = item.Model.DisplayName;
            if (!string.IsNullOrWhiteSpace(settings.Model) && string.Equals(settings.Model, item.Model.Id, StringComparison.OrdinalIgnoreCase))
            {
                label = "[x] " + label;
            }

            rows.Add([new TelegramReplyButton(label, $"ms:{sessionToken}|{item.Index}")]);
        }

        rows.Add([new TelegramReplyButton("Back", $"back:{sessionToken}")]);
        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? BuildThinkingSelectionButtons(CodexSessionModelSettings settings)
    {
        if (settings.AvailableReasoningEfforts.Count == 0)
        {
            return null;
        }

        List<IReadOnlyList<TelegramReplyButton>> rows = [];
        string sessionToken = GetShortSessionId(settings.SessionId);
        foreach (CodexReasoningEffort effort in settings.AvailableReasoningEfforts.Distinct())
        {
            string label = effort.ToString();
            if (!string.IsNullOrWhiteSpace(settings.ReasoningEffort) && string.Equals(settings.ReasoningEffort, effort.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                label = "[x] " + label;
            }

            rows.Add([new TelegramReplyButton(label, $"ts:{sessionToken}|{effort}")]);
        }

        rows.Add([new TelegramReplyButton("Back", $"back:{sessionToken}")]);
        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? BuildProjectButtons(IReadOnlyList<ProjectChoice> projects)
    {
        if (projects.Count == 0)
        {
            return null;
        }

        List<IReadOnlyList<TelegramReplyButton>> rows = [];
        for (int index = 0; index < projects.Count; index++)
        {
            ProjectChoice project = projects[index];
            string suffix = projects.Count == 1 ? string.Empty : $" {(index + 1).ToString(CultureInfo.InvariantCulture)}";
            rows.Add([new TelegramReplyButton($"Use{suffix}", $"project:{project.Key}")]);
        }

        return rows;
    }

    private static ResolvedProject ResolveProject(IReadOnlyList<ProjectChoice> projects, string arguments)
    {
        string selector = SplitArguments(arguments, 2).FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selector))
        {
            return new ResolvedProject(null, "Usage: /project <number|name|path>");
        }

        if (projects.Count == 0)
        {
            return new ResolvedProject(null, "No projects are configured. Use /project add <absolute directory path>.");
        }

        if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            && index >= 1
            && index <= projects.Count)
        {
            return new ResolvedProject(projects[index - 1], string.Empty);
        }

        List<ProjectChoice> matches = projects
            .Where(project => project.Key.Equals(selector, StringComparison.OrdinalIgnoreCase)
                || project.WorkingDirectory.Equals(selector, PathComparison)
                || project.Name.Equals(selector, StringComparison.OrdinalIgnoreCase)
                || project.Name.StartsWith(selector, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => new ResolvedProject(null, $"Project '{selector}' was not found. Use /projects to list known projects."),
            1 => new ResolvedProject(matches[0], string.Empty),
            _ => new ResolvedProject(null, $"Project '{selector}' is ambiguous. Use the number from /projects or the full path."),
        };
    }

    private static ProjectChoice ToProjectChoice(CodexProjectCatalogRecord project)
        => new(
            GetProjectKey(project.WorkingDirectory),
            CodexTextFormatting.ResolveProjectName(project.WorkingDirectory),
            project.WorkingDirectory,
            project.AddedAt);

    private static string GetProjectKey(string workingDirectory)
    {
        string normalized = Path.GetFullPath(workingDirectory);
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static SessionListRequest ParseSessionListRequest(string arguments)
    {
        string[] parts = SplitArguments(arguments, 2);
        bool includeAll = parts.Length > 0
            && (parts[0].Equals("all", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("recent", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("history", StringComparison.OrdinalIgnoreCase));
        int limit = DefaultSessionListLimit;

        if (includeAll && parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit))
        {
            limit = parsedLimit;
        }
        else if (!includeAll && parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedLimit))
        {
            limit = parsedLimit;
        }

        return new SessionListRequest(includeAll, Math.Clamp(limit, 1, 20));
    }

    private static GoalCommandRequest ParseGoalCommand(string arguments)
    {
        string text = arguments.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new GoalCommandRequest(GoalCommandAction.Show, string.Empty, null);
        }

        string[] parts = SplitArguments(text, 2);
        string verb = parts[0];
        string remainder = parts.Length > 1 ? parts[1] : string.Empty;

        if (verb.Equals("show", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("current", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return new GoalCommandRequest(GoalCommandAction.Show, string.Empty, null);
        }

        if (verb.Equals("clear", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("remove", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            return new GoalCommandRequest(GoalCommandAction.Clear, string.Empty, null);
        }

        if (verb.Equals("pause", StringComparison.OrdinalIgnoreCase))
        {
            return new GoalCommandRequest(GoalCommandAction.Pause, string.Empty, null);
        }

        if (verb.Equals("resume", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            return new GoalCommandRequest(GoalCommandAction.Resume, string.Empty, null);
        }

        if (verb.Equals("complete", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("done", StringComparison.OrdinalIgnoreCase))
        {
            return new GoalCommandRequest(GoalCommandAction.Complete, string.Empty, null);
        }

        string objective = verb.Equals("set", StringComparison.OrdinalIgnoreCase)
            ? remainder.Trim()
            : text;
        if (string.IsNullOrWhiteSpace(objective))
        {
            return GoalCommandRequest.Invalid;
        }

        long? tokenBudget = ExtractTokenBudget(ref objective);
        return string.IsNullOrWhiteSpace(objective)
            ? GoalCommandRequest.Invalid
            : new GoalCommandRequest(GoalCommandAction.Set, objective.Trim(), tokenBudget);
    }

    private static long? ExtractTokenBudget(ref string objective)
    {
        foreach (string marker in new[] { " --budget ", " --tokens ", " budget ", " tokens " })
        {
            int markerIndex = objective.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }

            string value = objective[(markerIndex + marker.Length)..].Trim().Replace(",", string.Empty, StringComparison.Ordinal);
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedBudget) || parsedBudget <= 0)
            {
                return null;
            }

            objective = objective[..markerIndex].Trim();
            return parsedBudget;
        }

        return null;
    }

    private static string GetShortSessionId(string sessionId)
        => sessionId.Length <= 8 ? sessionId : sessionId[..8];

    private static bool IsUnreadableCodexThreadException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string message = current.Message;
            bool mentionsThreadRead = message.Contains("failed to read thread", StringComparison.OrdinalIgnoreCase)
                || message.Contains("thread-store internal error", StringComparison.OrdinalIgnoreCase);
            bool mentionsEmptyRollout = message.Contains("rollout", StringComparison.OrdinalIgnoreCase)
                && message.Contains("is empty", StringComparison.OrdinalIgnoreCase);

            if ((mentionsThreadRead && mentionsEmptyRollout)
                || (mentionsThreadRead && message.Contains(".jsonl", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static TopicCreationRequest ParseTopicCreationRequest(string arguments)
    {
        string text = arguments.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return TopicCreationRequest.Empty;
        }

        int separatorIndex = text.IndexOf('|');
        if (separatorIndex < 0)
        {
            return new TopicCreationRequest(text, null, true);
        }

        string name = text[..separatorIndex].Trim();
        string? workingDirectory = text[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return TopicCreationRequest.Empty;
        }

        return new TopicCreationRequest(name, string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory, true);
    }

    private static ModelControlRequest TryExtractModelControl(string text)
    {
        string trimmed = text.Trim();
        foreach (string prefix in new[] { "codex settings", "codex use model", "codex model", "codex use" })
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.Length > prefix.Length && !char.IsWhiteSpace(trimmed[prefix.Length]) && trimmed[prefix.Length] != ':')
            {
                continue;
            }

            string remainder = trimmed[prefix.Length..].TrimStart();
            if (remainder.StartsWith(':'))
            {
                remainder = remainder[1..].TrimStart();
            }

            int separatorIndex = IndexOfControlSeparator(remainder);
            string directive = separatorIndex >= 0 ? remainder[..separatorIndex] : remainder;
            string remainingText = separatorIndex >= 0 ? remainder[(separatorIndex + 1)..].Trim() : string.Empty;
            ModelControlRequest parsed = ParseModelControlArguments(directive);
            return parsed with { RemainingText = remainingText };
        }

        return ModelControlRequest.Empty;
    }

    private static ModelControlRequest ParseModelControlArguments(string arguments)
    {
        string text = arguments.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return ModelControlRequest.Empty;
        }

        if (TryRemoveLeadingKeyword(ref text, "model"))
        {
            text = text.Trim();
        }

        if (TryRemoveLeadingKeyword(ref text, "thinking")
            || TryRemoveLeadingKeyword(ref text, "reasoning")
            || TryRemoveLeadingKeyword(ref text, "effort"))
        {
            return new ModelControlRequest(null, text.Trim(), string.Empty, !string.IsNullOrWhiteSpace(text));
        }

        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int thinkingIndex = Array.FindIndex(parts, IsThinkingKeyword);
        if (thinkingIndex >= 0)
        {
            string model = string.Join(' ', parts.Take(thinkingIndex));
            string effort = string.Join(' ', parts.Skip(thinkingIndex + 1));
            return new ModelControlRequest(
                string.IsNullOrWhiteSpace(model) ? null : model,
                string.IsNullOrWhiteSpace(effort) ? null : effort,
                string.Empty,
                !string.IsNullOrWhiteSpace(model) || !string.IsNullOrWhiteSpace(effort));
        }

        if (parts.Length >= 2 && IsReasoningEffort(string.Join(' ', parts.TakeLast(2))))
        {
            return new ModelControlRequest(
                string.Join(' ', parts.Take(parts.Length - 2)),
                string.Join(' ', parts.TakeLast(2)),
                string.Empty,
                true);
        }

        if (parts.Length >= 2 && IsReasoningEffort(parts[^1]))
        {
            return new ModelControlRequest(
                string.Join(' ', parts.Take(parts.Length - 1)),
                parts[^1],
                string.Empty,
                true);
        }

        return new ModelControlRequest(text, null, string.Empty, true);
    }

    private static int IndexOfControlSeparator(string text)
    {
        int index = -1;
        foreach (char separator in new[] { ':', ';', '\n' })
        {
            int candidate = text.IndexOf(separator, StringComparison.Ordinal);
            if (candidate >= 0 && (index < 0 || candidate < index))
            {
                index = candidate;
            }
        }

        return index;
    }

    private static bool TryRemoveLeadingKeyword(ref string text, string keyword)
    {
        if (!text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.Length > keyword.Length && !char.IsWhiteSpace(text[keyword.Length]))
        {
            return false;
        }

        text = text[keyword.Length..].TrimStart();
        return true;
    }

    private static bool IsThinkingKeyword(string value)
        => value.Equals("thinking", StringComparison.OrdinalIgnoreCase)
            || value.Equals("reasoning", StringComparison.OrdinalIgnoreCase)
            || value.Equals("effort", StringComparison.OrdinalIgnoreCase);

    private static bool IsReasoningEffort(string value)
        => NormalizeToken(value) is "none" or "minimal" or "min" or "low" or "medium" or "med" or "high" or "xhigh" or "extra" or "extrahigh" or "veryhigh" or "max" or "maximum";

    private static string NormalizeToken(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int index = 0;
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[index++] = char.ToLowerInvariant(ch);
            }
        }

        return new string(buffer[..index]);
    }

    private static string[] SplitArguments(string arguments, int count)
        => string.IsNullOrWhiteSpace(arguments)
            ? []
            : arguments.Trim().Split((char[]?)null, count, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] SplitCallbackData(string? data)
        => string.IsNullOrWhiteSpace(data)
            ? []
            : data.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task CreateTopicAndSessionAsync(
        TelegramInboundMessage message,
        string topicName,
        string? workingDirectory,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        if (!IsForumTopicChat(message))
        {
            await ReplyAsync(sender, message, BuildTopicCreationUnsupportedMessage(message), null, cancellationToken).ConfigureAwait(false);
            return;
        }

        TelegramForumTopicCreationResult topic;
        try
        {
            topic = await _topicService.CreateForumTopicAsync(message.ChatId, topicName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsChatNotForumError(exception))
        {
            await ReplyAsync(
                sender,
                message.ConversationScope,
                "This Telegram chat does not have Topics enabled, so /topic new cannot create a forum topic here. Enable Topics in the group settings or use /new [name] to create a Codex session in this chat.",
                null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        TelegramConversationScope topicScope = new(message.ChatId, topic.MessageThreadId);

        CodexSessionSummary session = await CreateAndSelectSessionAsync(
            topicScope,
            topicName,
            workingDirectory,
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            await _stateStore.SetActiveProjectWorkingDirectoryAsync(topicScope, workingDirectory, cancellationToken).ConfigureAwait(false);
        }

        CodexSessionModelSettings settings = await _sessionManager.GetModelSettingsAsync(session.Id, cancellationToken).ConfigureAwait(false);
        string? usageSummary = await TryBuildAccountUsageSummaryAsync(cancellationToken).ConfigureAwait(false);
        string projectName = CodexTextFormatting.ResolveProjectName(workingDirectory);
        await ReplyAsync(
            sender,
            message,
            $"Created topic {topic.Name} and session {session.Name}. Open the new topic to continue.",
            null,
            cancellationToken).ConfigureAwait(false);
        await ReplyAsync(
            sender,
            topicScope,
            BuildSelectedSessionReply("Created and selected", session, settings, projectName, usageSummary),
            BuildSessionButtons([session], includeUse: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static TelegramInboundMessage ToMessage(TelegramInboundCallback callback)
        => new(callback.UserId, callback.ChatId, callback.ChatType, null, callback.MessageThreadId, SourceMessageId: callback.SourceMessageId);

    private static bool IsLive(CodexSessionStatus status)
        => status is CodexSessionStatus.Running or CodexSessionStatus.Starting;

    private sealed record ResolvedSession(CodexSessionSummary? Session, string Message);

    private sealed record ResolvedQueuedPrompt(TelegramQueuedPrompt? Prompt, string Message);

    private sealed record ResolvedProject(ProjectChoice? Project, string Message);

    private sealed record ProjectChoice(string Key, string Name, string WorkingDirectory, DateTimeOffset AddedAt);

    private sealed record TopicCreationRequest(string Name, string? WorkingDirectory, bool IsValid)
    {
        public static TopicCreationRequest Empty { get; } = new(string.Empty, null, false);
    }

    private sealed record ModelControlRequest(string? Model, string? ReasoningEffort, string RemainingText, bool HasControl)
    {
        public static ModelControlRequest Empty { get; } = new(null, null, string.Empty, false);
    }

    private sealed record GoalCommandRequest(GoalCommandAction Action, string Objective, long? TokenBudget)
    {
        public static GoalCommandRequest Invalid { get; } = new(GoalCommandAction.Invalid, string.Empty, null);
    }

    private enum GoalCommandAction
    {
        Invalid,
        Show,
        Set,
        Clear,
        Pause,
        Resume,
        Complete,
    }

    private sealed record SessionListRequest(bool IncludeAll, int Limit);

    private sealed record SessionListView(
        IReadOnlyList<CodexSessionSummary> Sessions,
        int TotalSessionCount,
        int FilteredSessionCount,
        bool IncludeAll,
        int Limit,
        string? ActiveSessionId);
}
