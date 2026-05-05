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

public interface ITelegramBotMessageSender
{
    Task SendTextMessageAsync(
        TelegramConversationScope conversation,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken);

    Task EditTextMessageAsync(
        TelegramConversationScope conversation,
        int messageId,
        string text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
        CancellationToken cancellationToken);

    Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken);
}

public sealed record TelegramInboundMessage(
    long UserId,
    long ChatId,
    string ChatType,
    string? Text,
    int? MessageThreadId = null,
    string? AudioFilePath = null,
    IReadOnlyList<TelegramAttachmentDescriptor>? Attachments = null,
    int? SourceMessageId = null)
{
    public TelegramConversationScope ConversationScope => new(ChatId, MessageThreadId);
}

public sealed record TelegramInboundCallback(
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

public sealed record TelegramReplyButton(string Text, string CallbackData);

public sealed class TelegramCodexBotCommandHandler
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private const int DefaultTailLineCount = 40;
    private const int DefaultSessionListLimit = 8;
    private static readonly TimeSpan TelegramSendStartTimeout = TimeSpan.FromSeconds(5);

    private readonly TelegramCommandParser _parser;
    private readonly TelegramMessageChunker _chunker;
    private readonly ICodexSessionManager _sessionManager;
    private readonly ICodexProjectCatalogStore _projectCatalogStore;
    private readonly CodexWorkspaceBrowser _workspaceBrowser;
    private readonly ITelegramBotStateStore _stateStore;
    private readonly ICodexTurnExecutionCoordinator _turnCoordinator;
    private readonly ITelegramThreadFollowRegistry _followRegistry;
    private readonly ITelegramForumTopicService _topicService;
    private readonly IAudioTranscriptionService _audioTranscriptionService;
    private readonly IOutboundTelegramQueue _outboundQueue;
    private readonly TelegramBotOptions _options;
    private readonly ILogger<TelegramCodexBotCommandHandler> _logger;

    public TelegramCodexBotCommandHandler(
        TelegramCommandParser parser,
        TelegramMessageChunker chunker,
        ICodexSessionManager sessionManager,
        ICodexProjectCatalogStore projectCatalogStore,
        CodexWorkspaceBrowser workspaceBrowser,
        ITelegramBotStateStore stateStore,
        ICodexTurnExecutionCoordinator turnCoordinator,
        ITelegramThreadFollowRegistry followRegistry,
        ITelegramForumTopicService topicService,
        IAudioTranscriptionService audioTranscriptionService,
        IOutboundTelegramQueue outboundQueue,
        IOptions<TelegramBotOptions> options,
        ILogger<TelegramCodexBotCommandHandler> logger)
    {
        _parser = parser;
        _chunker = chunker;
        _sessionManager = sessionManager;
        _projectCatalogStore = projectCatalogStore;
        _workspaceBrowser = workspaceBrowser;
        _stateStore = stateStore;
        _turnCoordinator = turnCoordinator;
        _followRegistry = followRegistry;
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
        bool isAuthorized = IsAuthorized(message);
        _logger.LogDebug(
            "Parsed Telegram message for chat {ChatId} topic {MessageThreadId}; command: {IsCommand}; command name: {CommandName}; text length: {TextLength}; has audio path: {HasAudioPath}; attachments: {AttachmentCount}.",
            message.ChatId,
            message.MessageThreadId,
            command.IsCommand,
            string.IsNullOrWhiteSpace(command.Name) ? "<none>" : command.Name,
            command.Text.Length,
            !string.IsNullOrWhiteSpace(message.AudioFilePath),
            message.Attachments?.Count ?? 0);

        if (!isAuthorized && !IsWhoAmI(command))
        {
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
                case "model":
                    await HandleModelAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "thinking":
                    await HandleThinkingAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "tail":
                    await HandleTailAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "status":
                    await HandleStatusAsync(message, command.Arguments, sender, cancellationToken).ConfigureAwait(false);
                    break;
                case "doctor":
                case "diag":
                case "diagnostics":
                    await HandleDoctorAsync(message, sender, cancellationToken).ConfigureAwait(false);
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
            await ReplyAsync(sender, message, $"{FormatActionFailurePrefix(command, message)} failed: {exception.Message}", null, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task HandleCallbackAsync(
        TelegramInboundCallback callback,
        ITelegramBotMessageSender sender,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(callback))
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
        StringBuilder builder = new();
        builder.AppendLine($"Topic thread ID: {message.MessageThreadId?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
        builder.AppendLine(FormatStatus(session.Session));
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
        await ReplyAsync(
            sender,
            message,
            BuildSelectedSessionReply("Attached this topic to", resolved.Session, settings, CodexTextFormatting.ResolveProjectName(resolved.Session.WorkingDirectory)),
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
        if (string.IsNullOrWhiteSpace(arguments))
        {
            await ReplyAsync(sender, message, "Usage: /new <name>", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        ResolvedProject resolvedProject = await ResolveActiveProjectAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
        if (resolvedProject.Project is null)
        {
            await ReplyAsync(sender, message, resolvedProject.Message, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        CodexSessionSummary session = await CreateAndSelectSessionAsync(
            message.ConversationScope,
            arguments.Trim(),
            resolvedProject.Project.WorkingDirectory,
            cancellationToken).ConfigureAwait(false);
        CodexSessionModelSettings settings = await _sessionManager.GetModelSettingsAsync(session.Id, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, BuildSelectedSessionReply("Created and selected", session, settings), BuildSessionButtons([session], includeUse: false), cancellationToken).ConfigureAwait(false);
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
        await ReplyAsync(sender, message, BuildSelectedSessionReply("Selected", resolved.Session, settings), BuildSessionButtons([resolved.Session], includeUse: false), cancellationToken).ConfigureAwait(false);
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
                await ReplyAsync(sender, message, "Updated model settings:" + Environment.NewLine + FormatModelSettings(settings), null, cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(modelControl.RemainingText) && !hasAttachments)
                {
                    return;
                }

                text = modelControl.RemainingText;
            }

            retainAttachments = await SendOrQueueAsync(message, session, text, sender, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
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
        CancellationToken cancellationToken)
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
        try
        {
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
                _ = ObserveSlowTelegramSendAsync(sendTask, message, session, trimmed, sender);
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
            _logger.LogDebug(
                "Telegram message from chat {ChatId} topic {MessageThreadId} started turn {TurnId} on session {SessionId}.",
                message.ChatId,
                message.MessageThreadId,
                execution.TurnId,
                execution.ThreadId);
            await ReplyAsync(sender, message, $"Sent to {session.Name}. Live updates will stream here.", BuildSessionButtons([session], includeUse: false), cancellationToken).ConfigureAwait(false);
            return false;
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

    private async Task ObserveSlowTelegramSendAsync(
        Task<CodexThreadExecutionVm> sendTask,
        TelegramInboundMessage message,
        CodexSessionSummary session,
        string text,
        ITelegramBotMessageSender sender)
    {
        try
        {
            CodexThreadExecutionVm execution = await sendTask.ConfigureAwait(false);
            _followRegistry.FollowThread(message.ConversationScope, execution.ThreadId);
            _logger.LogDebug(
                "Slow Telegram message from chat {ChatId} topic {MessageThreadId} eventually started turn {TurnId} on session {SessionId}.",
                message.ChatId,
                message.MessageThreadId,
                execution.TurnId,
                execution.ThreadId);
            TryDeleteAttachments(message.Attachments);
            await ReplyAsync(
                sender,
                message.ConversationScope,
                $"Started turn for {session.Name}. Live updates will stream here.",
                BuildSessionButtons([session], includeUse: false),
                CancellationToken.None).ConfigureAwait(false);
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
            await ReplyAsync(
                sender,
                message.ConversationScope,
                $"Message for {session.Name} failed to start: {exception.Message}",
                BuildSessionButtons([session], includeUse: false),
                CancellationToken.None).ConfigureAwait(false);
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
            await _sessionManager.SteerAsync(resolved.Session.Id, arguments, cancellationToken).ConfigureAwait(false);
            _followRegistry.FollowThread(message.ConversationScope, resolved.Session.Id);
            await ReplyAsync(sender, message, $"Steered {resolved.Session.Name}.", BuildSessionButtons([resolved.Session], includeUse: false), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            await ReplyAsync(sender, message, exception.Message, null, cancellationToken).ConfigureAwait(false);
        }
    }

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
            await ReplyAsync(sender, message, "Model settings:" + Environment.NewLine + FormatModelSettings(current), BuildModelSelectionButtons(current), cancellationToken, includeNavigationButtons: false).ConfigureAwait(false);
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
            await ReplyAsync(sender, message, "Updated model settings:" + Environment.NewLine + FormatModelSettings(settings), BuildSessionButtons([resolved.Session], includeUse: false), cancellationToken).ConfigureAwait(false);
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
            await ReplyAsync(sender, message, "Thinking settings:" + Environment.NewLine + FormatModelSettings(current), BuildThinkingSelectionButtons(current), cancellationToken, includeNavigationButtons: false).ConfigureAwait(false);
            return;
        }

        await HandleModelAsync(message, "thinking " + arguments, sender, cancellationToken).ConfigureAwait(false);
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

        CodexSessionModelSettings settings = await _sessionManager.GetModelSettingsAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, "Model settings:" + Environment.NewLine + FormatModelSettings(settings), BuildModelSelectionButtons(settings), cancellationToken, includeNavigationButtons: false).ConfigureAwait(false);
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
        await ReplyAsync(sender, message, "Model settings:" + Environment.NewLine + FormatModelSettings(settings), BuildModelSelectionButtons(settings), cancellationToken, includeNavigationButtons: false).ConfigureAwait(false);
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

        CodexSessionModelSettings settings = await _sessionManager.GetModelSettingsAsync(resolved.Session.Id, cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, "Thinking settings:" + Environment.NewLine + FormatModelSettings(settings), BuildThinkingSelectionButtons(settings), cancellationToken, includeNavigationButtons: false).ConfigureAwait(false);
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

        CodexSessionModelSettings settings = await _sessionManager.UpdateModelSettingsAsync(resolved.Session.Id, null, parts[1], cancellationToken).ConfigureAwait(false);
        await ReplyAsync(sender, message, "Thinking settings:" + Environment.NewLine + FormatModelSettings(settings), BuildThinkingSelectionButtons(settings), cancellationToken, includeNavigationButtons: false).ConfigureAwait(false);
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
        await ReplyAsync(sender, message, FormatStatus(resolved.Session, settings), BuildSessionButtons([resolved.Session]), cancellationToken).ConfigureAwait(false);
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

    private async Task HandleDoctorAsync(TelegramInboundMessage message, ITelegramBotMessageSender sender, CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        builder.AppendLine("Codex Telegram doctor");
        builder.AppendLine();
        builder.AppendLine(FormatDoctorConversation(message));
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
            return new ResolvedSession(null, "No active session is selected in this conversation. Use /sessions, /new <name>, /use <sessionId>, or just send a message to start a new session.");
        }

        CodexSessionSummary? session = await _sessionManager.GetSessionAsync(activeSessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            await _stateStore.ClearActiveSessionAsync(conversation, cancellationToken).ConfigureAwait(false);
            return new ResolvedSession(null, "The selected session is no longer known. Use /sessions, /new <name>, /use <sessionId>, or send a message to start a new session.");
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
            new CreateCodexSessionRequest("Telegram session", workingDirectory),
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
        IReadOnlyList<string> chunks = _chunker.Split(text, _options.MaxTelegramMessageLength);
        for (int index = 0; index < chunks.Count; index++)
        {
            bool isLastChunk = index == chunks.Count - 1;
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? chunkButtons = isLastChunk
                ? MergeButtons(buttons, includeNavigationButtons ? BuildNavigationButtons() : null)
                : null;

            if (index == 0 && editMessageId.HasValue && chunks.Count == 1)
            {
                await sender.EditTextMessageAsync(conversation, editMessageId.Value, chunks[index], chunkButtons, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await sender.SendTextMessageAsync(conversation, chunks[index], chunkButtons, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsAuthorized(TelegramInboundCallback callback)
        => TelegramAuthorization.IsAuthorized(
            callback.UserId,
            callback.ChatId,
            callback.ChatType,
            _options.AllowedUserIds,
            _options.AllowedChatIds);

    private bool IsAuthorized(TelegramInboundMessage message)
        => TelegramAuthorization.IsAuthorized(
            message.UserId,
            message.ChatId,
            message.ChatType,
            _options.AllowedUserIds,
            _options.AllowedChatIds);

    private static bool IsWhoAmI(ParsedTelegramCommand command)
        => command.IsCommand && string.Equals(command.Name, "whoami", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivateChat(TelegramInboundMessage message)
        => string.Equals(message.ChatType, "private", StringComparison.OrdinalIgnoreCase);

    private static bool IsForumTopicChat(TelegramInboundMessage message)
        => string.Equals(message.ChatType, "supergroup", StringComparison.OrdinalIgnoreCase);

    private static bool CanRoutePlainText(TelegramInboundMessage message)
        => IsPrivateChat(message) || message.MessageThreadId is not null;

    private static string BuildNotRoutedMessage(TelegramInboundMessage message)
        => string.Equals(message.ChatType, "supergroup", StringComparison.OrdinalIgnoreCase)
            ? "I only auto-route messages from private chats or forum topics. This message was sent to the group root, so I did not send it to Codex. Use /send <text>, open a topic, or message me privately."
            : $"I only auto-route messages from private chats or topic threads. This {message.ChatType} message was not sent to Codex. Use /send <text> or message me privately.";

    private static bool IsChatNotForumError(Exception exception)
        => exception.Message.Contains("chat is not a forum", StringComparison.OrdinalIgnoreCase);

    private static string BuildTopicCreationUnsupportedMessage(TelegramInboundMessage message)
        => IsPrivateChat(message)
            ? "This is a private chat, so /topic new cannot create a Telegram forum topic here. Use /new <name> to create a Codex session in this chat, or run /topic new inside a forum-enabled supergroup."
            : $"This is a {message.ChatType} chat, so /topic new only works in a forum-enabled supergroup. Use /new <name> to create a Codex session here.";

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
            "/new <name> - create and select a Codex session in the active project for this conversation",
            "/use <sessionId> - select the active session for this conversation",
            "/send <text> - send text to the active session",
            "/steer <text> - steer the active turn in the selected session",
            "/model [model] [thinking <effort>] - show or change the selected session model",
            "/thinking <minimal|low|medium|high|xhigh> - change the selected session thinking effort",
            "/tail [count] - show recent output and keep following the session live",
            "/status [sessionId] - show session status",
            "/doctor - explain authorization, routing, active project/session, workspace roots, and queue state",
            "/outbound - show outbound Telegram queue status",
            "/stop [sessionId] - gracefully stop a session",
            "/restart confirm - explain how to restart this standalone process",
            "/kill <sessionId> confirm - hard-stop a session",
            "/rename <sessionId> <new name> - rename a session",
            "/forget <sessionId> - hide a stopped/exited session without deleting logs",
            "Plain text and audio in a private chat or topic stay on that conversation's session; if the conversation has none yet, the first message starts one and live output follows automatically.",
            "In forum topics, if plain text gets no response, Telegram bot privacy is likely hiding non-command messages; use /send <text> or disable privacy for this bot.",
            "Images, documents, and other attachments are forwarded to Codex; voice notes are transcribed with Whisper first.",
            "Voice/text control phrase: Codex settings model gpt-5.4-mini thinking high: <prompt>"
        ]);

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

    private static string BuildSelectedProjectReply(string action, ProjectChoice project)
        => string.Join(Environment.NewLine, [
            $"{action} project {project.Name}.",
            project.WorkingDirectory,
            "Use /new <name> to start a Codex session there."
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
                ? "No Codex sessions are known yet. Use /new <name> to create one."
                : "No active or Telegram-managed sessions. Use /new <name>, or /sessions all 10 to browse recent Codex history.";
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

    private static string FormatStatus(CodexSessionSummary session, CodexSessionModelSettings? settings = null)
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

    private string FormatDoctorConversation(TelegramInboundMessage message)
    {
        bool userAllowed = _options.AllowedUserIds.Contains(message.UserId);
        bool chatNeedsAllowlist = !IsPrivateChat(message);
        bool chatAllowed = !chatNeedsAllowlist || _options.AllowedChatIds.Contains(message.ChatId);
        bool authorized = IsAuthorized(message);
        string routing = CanRoutePlainText(message)
            ? "Plain text, audio, and attachments can auto-route in this conversation."
            : "Plain text and attachments do not auto-route from this chat root. Use /send <text>, open a forum topic, or message me privately.";

        return string.Join(Environment.NewLine, [
            "Conversation:",
            $"- Chat: {message.ChatId.ToString(CultureInfo.InvariantCulture)} ({DescribeChat(message)})",
            $"- Topic thread: {message.MessageThreadId?.ToString(CultureInfo.InvariantCulture) ?? "<none>"}",
            $"- User allowlist: {(userAllowed ? "allowed" : "not allowed")}",
            $"- Chat allowlist: {(chatNeedsAllowlist ? chatAllowed ? "allowed" : "not allowed" : "not required for private chat")}",
            $"- Effective access: {(authorized ? "allowed" : "blocked except /whoami")}",
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
        bool userAllowed = _options.AllowedUserIds.Contains(message.UserId);
        if (!userAllowed)
        {
            return "Next: add this Telegram user ID to TelegramBot:AllowedUserIds, then restart or relaunch if your configuration source does not reload.";
        }

        if (!IsPrivateChat(message) && !_options.AllowedChatIds.Contains(message.ChatId))
        {
            return "Next: add this chat ID to TelegramBot:AllowedChatIds, or continue in a private chat.";
        }

        if (!CanRoutePlainText(message))
        {
            return "Next: use /send <text>, open a forum topic, or message me privately. I will not silently send group-root messages to Codex.";
        }

        try
        {
            string? activeProject = await _stateStore.GetActiveProjectWorkingDirectoryAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(activeProject))
            {
                return "Next: use /projects or /project add <absolute directory path>, then /new <name>.";
            }

            string? activeSessionId = await _stateStore.GetActiveSessionIdAsync(message.ConversationScope, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(activeSessionId))
            {
                return "Next: use /new <name>, or just send a message to start a session in the active project.";
            }
        }
        catch
        {
            return "Next: use /projects and /sessions to refresh local state; check the terminal logs if either command fails.";
        }

        return "Next: send a normal message to continue, or use /status, /tail, /model, /thinking, and /outbound if something seems off.";
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

    private static string FormatModelSettings(CodexSessionModelSettings settings)
    {
        StringBuilder builder = new();
        builder.AppendLine($"Session: {settings.SessionName}");
        builder.AppendLine($"Model: {FormatModelDisplay(settings)}");
        builder.AppendLine($"Thinking: {FormatValue(settings.ReasoningEffort)}");
        if (settings.AvailableReasoningEfforts.Count > 0)
        {
            builder.AppendLine($"Available thinking: {string.Join(", ", settings.AvailableReasoningEfforts)}");
        }

        if (settings.AvailableModels.Count > 0)
        {
            builder.AppendLine("Use /model <model> <thinking>. Examples:");
            foreach (CodexModelVm model in settings.AvailableModels.Take(8))
            {
                builder.AppendLine($"- /model {model.Id} {model.DefaultReasoningEffort}");
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

    private static string BuildSelectedSessionReply(string action, CodexSessionSummary session, CodexSessionModelSettings settings, string? projectName = null)
        => string.Join(Environment.NewLine, [
            $"{action} {session.Name}.",
            $"Project: {projectName ?? CodexTextFormatting.ResolveProjectName(session.WorkingDirectory)}",
            $"Model: {FormatModelDisplay(settings)}",
            $"Thinking: {FormatValue(settings.ReasoningEffort)}",
            "Send a message to continue, or use /tail, /status, /model, or /thinking when you need a control."
        ]);

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

    private static string GetShortSessionId(string sessionId)
        => sessionId.Length <= 8 ? sessionId : sessionId[..8];

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
                "This Telegram chat does not have Topics enabled, so /topic new cannot create a forum topic here. Enable Topics in the group settings or use /new <name> to create a Codex session in this chat.",
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
            BuildSelectedSessionReply("Created and selected", session, settings, projectName),
            BuildSessionButtons([session], includeUse: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static TelegramInboundMessage ToMessage(TelegramInboundCallback callback)
        => new(callback.UserId, callback.ChatId, callback.ChatType, null, callback.MessageThreadId, SourceMessageId: callback.SourceMessageId);

    private static bool IsLive(CodexSessionStatus status)
        => status is CodexSessionStatus.Running or CodexSessionStatus.Starting;

    private sealed record ResolvedSession(CodexSessionSummary? Session, string Message);

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

    private sealed record SessionListRequest(bool IncludeAll, int Limit);

    private sealed record SessionListView(
        IReadOnlyList<CodexSessionSummary> Sessions,
        int TotalSessionCount,
        int FilteredSessionCount,
        bool IncludeAll,
        int Limit,
        string? ActiveSessionId);
}
