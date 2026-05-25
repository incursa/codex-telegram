using System.Globalization;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Incursa.OpenAI.Codex;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Top-level statements synthesize a Program type, but there is no source declaration to XML-document.
#pragma warning disable CS1591

ApplicationCommandLine commandLine = ApplicationCommandLine.Parse(args);
if (commandLine.ShowHelp)
{
    InteractiveBootstrapMenu.WriteHelp();
    return;
}

string localSettingsPath = LocalSettingsStore.ResolveDefaultPath();
if (ShouldRunInteractiveMenu(commandLine))
{
    LocalSettingsStore store;
    try
    {
        store = LocalSettingsStore.Load(localSettingsPath);
    }
    catch (InvalidOperationException exception)
    {
        Console.WriteLine(exception.Message);
        return;
    }

    if (!store.Exists)
    {
        LocalSettingsStore? configuredStore = await InteractiveBootstrapMenu.RunFirstRunSetupAsync(store, CancellationToken.None);
        if (configuredStore is null)
        {
            return;
        }

        store = configuredStore;
    }

    localSettingsPath = store.FilePath;
    CodexModelCatalog modelCatalog = await CodexModelDiscovery.DiscoverAsync(store.GetSnapshot(), CancellationToken.None);

    if (InteractiveBootstrapMenu.Run(store, modelCatalog) == BootstrapMenuResult.Quit)
    {
        return;
    }
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(commandLine.ConfigurationArgs);
builder.Logging.ClearProviders();
builder.Configuration.AddJsonFile(localSettingsPath, optional: true, reloadOnChange: true);
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables(prefix: "CODEX_TELEGRAM_");
builder.Configuration.AddCommandLine(commandLine.ConfigurationArgs);

builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

builder.Services.AddOptions<CodexClientOptions>()
    .Bind(builder.Configuration.GetSection("Codex"));
builder.Services.AddOptions<CodexTelegramOptions>()
    .Bind(builder.Configuration.GetSection("CodexTelegram"));
builder.Services.AddOptions<TelegramBotOptions>()
    .Bind(builder.Configuration.GetSection("TelegramBot"));
builder.Services.AddOptions<TelegramInputOptions>()
    .Bind(builder.Configuration.GetSection("TelegramInput"));
builder.Services.AddOptions<TelegramDebugTraceOptions>()
    .Bind(builder.Configuration.GetSection("TelegramDebugTrace"));
builder.Services.AddOptions<TelegramOutboundOptions>()
    .Bind(builder.Configuration.GetSection("TelegramBot:Outbound"));
builder.Services.AddOptions<TelegramOutputOptions>()
    .Bind(builder.Configuration.GetSection("TelegramOutput"));
builder.Services.AddOptions<OpenAiSpeechToTextOptions>()
    .Bind(builder.Configuration.GetSection("OpenAI"));

builder.Services.PostConfigure<CodexClientOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(builder.Configuration["Codex:BackendSelection"]))
    {
        options.BackendSelection = CodexBackendSelection.AppServer;
    }

    options.ClientName = DefaultIfWhiteSpace(options.ClientName, "Incursa.Codex.Telegram");
    options.ClientTitle = DefaultIfWhiteSpace(options.ClientTitle, "Incursa Codex Telegram");
    options.ClientVersion = DefaultIfWhiteSpace(options.ClientVersion, "1.0.15");
    options.CodexPathOverride = DefaultIfWhiteSpace(
        options.CodexPathOverride,
        DefaultIfWhiteSpace(builder.Configuration["TelegramBot:CodexExecutablePath"], Environment.GetEnvironmentVariable("CODEX_PATH")));

    CodexClientOptionsPlanModeBridge.ApplyReasoningEffort(
        options,
        DefaultIfWhiteSpace(builder.Configuration["Codex:PlanMode:ReasoningEffort"], null));
});

builder.Services.PostConfigure<CodexTelegramOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.Workspace.DataRoot))
    {
        options.Workspace.DataRoot = GetDefaultDataRoot();
    }

    if (options.Workspace.WorkspaceRoots.Count == 0)
    {
        options.Workspace.WorkspaceRoots.Add(Environment.CurrentDirectory);
    }

    if (string.IsNullOrWhiteSpace(options.Context.WorkingDirectory))
    {
        options.Context.WorkingDirectory = Environment.CurrentDirectory;
    }

    options.TerminalEventHoldMilliseconds = Math.Clamp(
        options.TerminalEventHoldMilliseconds,
        CodexTurnStreamingDefaults.MinTerminalEventHoldMilliseconds,
        CodexTurnStreamingDefaults.MaxTerminalEventHoldMilliseconds);
    options.Workspace.DataRoot = Path.GetFullPath(options.Workspace.DataRoot);
    options.Workspace.WorkspaceRoots = NormalizeDistinctPaths(options.Workspace.WorkspaceRoots).ToList();
    options.Context.AdditionalDirectories = NormalizeDistinctPaths(options.Context.AdditionalDirectories).ToList();

    if (!string.IsNullOrWhiteSpace(options.Context.WorkingDirectory))
    {
        options.Context.WorkingDirectory = Path.GetFullPath(options.Context.WorkingDirectory);
    }
});

builder.Services.PostConfigure<TelegramBotOptions>(options =>
{
    options.Token = DefaultIfWhiteSpace(options.Token, Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"));

    if (options.AllowedUserIds.Length == 0)
    {
        options.AllowedUserIds = ParseInt64List(Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_USER_IDS"));
    }

    if (options.AllowedChatIds.Length == 0)
    {
        options.AllowedChatIds = ParseInt64List(Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_CHAT_IDS"));
    }

    options.PollingTimeoutSeconds = Math.Clamp(
        options.PollingTimeoutSeconds,
        TelegramBotLimits.MinPollingTimeoutSeconds,
        TelegramBotLimits.MaxPollingTimeoutSeconds);
    options.MaxTelegramMessageLength = Math.Clamp(
        options.MaxTelegramMessageLength,
        TelegramBotLimits.MinTelegramMessageLength,
        TelegramBotLimits.MaxTelegramMessageLength);
    options.MinAudioDurationSeconds = Math.Clamp(
        options.MinAudioDurationSeconds,
        TelegramBotLimits.MinAudioDurationSeconds,
        TelegramBotLimits.MaxMinimumAudioDurationSeconds);
    options.MaxAudioDurationSeconds = Math.Clamp(
        options.MaxAudioDurationSeconds,
        TelegramBotLimits.MinMaximumAudioDurationSeconds,
        TelegramBotLimits.MaxAudioDurationSeconds);
    if (options.MaxAudioDurationSeconds < options.MinAudioDurationSeconds)
    {
        options.MaxAudioDurationSeconds = options.MinAudioDurationSeconds;
    }
});

builder.Services.PostConfigure<TelegramOutboundOptions>(options =>
{
    options.GroupMinimumSendIntervalSeconds = Math.Clamp(
        options.GroupMinimumSendIntervalSeconds,
        TelegramOutboundLimits.MinGroupSendIntervalSeconds,
        TelegramOutboundLimits.MaxGroupSendIntervalSeconds);
    options.PrivateMinimumSendIntervalSeconds = Math.Clamp(
        options.PrivateMinimumSendIntervalSeconds,
        TelegramOutboundLimits.MinPrivateSendIntervalSeconds,
        TelegramOutboundLimits.MaxPrivateSendIntervalSeconds);
    options.GlobalMaxMessagesPerSecond = Math.Clamp(
        options.GlobalMaxMessagesPerSecond,
        TelegramOutboundLimits.MinGlobalMessagesPerSecond,
        TelegramOutboundLimits.MaxGlobalMessagesPerSecond);
    options.MaxMessageChars = Math.Clamp(
        options.MaxMessageChars,
        TelegramOutboundLimits.MinMessageChars,
        TelegramOutboundLimits.MaxMessageChars);
    options.MaxBufferedCharsPerDestination = Math.Clamp(
        options.MaxBufferedCharsPerDestination,
        options.MaxMessageChars,
        TelegramOutboundLimits.MaxBufferedCharsPerDestination);
    options.MaxBufferedMessagesPerDestination = Math.Clamp(
        options.MaxBufferedMessagesPerDestination,
        TelegramOutboundLimits.MinBufferedMessagesPerDestination,
        TelegramOutboundLimits.MaxBufferedMessagesPerDestination);
    options.FlushIntervalMilliseconds = Math.Clamp(
        options.FlushIntervalMilliseconds,
        TelegramOutboundLimits.MinFlushIntervalMilliseconds,
        TelegramOutboundLimits.MaxFlushIntervalMilliseconds);
    options.SendTimeoutSeconds = Math.Clamp(
        options.SendTimeoutSeconds,
        TelegramOutboundLimits.MinSendTimeoutSeconds,
        TelegramOutboundLimits.MaxSendTimeoutSeconds);
    options.AgentMessageUpdateMinChars = Math.Clamp(
        options.AgentMessageUpdateMinChars,
        TelegramOutboundLimits.MinAgentMessageUpdateChars,
        options.MaxMessageChars);
    options.AgentMessageUpdateMaxChars = Math.Clamp(options.AgentMessageUpdateMaxChars, options.AgentMessageUpdateMinChars, options.MaxMessageChars);
    options.BatchWindowSeconds = Math.Clamp(
        options.BatchWindowSeconds,
        TelegramOutboundLimits.MinBatchWindowSeconds,
        TelegramOutboundLimits.MaxBatchWindowSeconds);
});

builder.Services.PostConfigure<TelegramInputOptions>(options =>
{
    options.BundleExpirationMinutes = Math.Clamp(
        options.BundleExpirationMinutes,
        TelegramInputLimits.MinBundleExpirationMinutes,
        TelegramInputLimits.MaxBundleExpirationMinutes);
    options.PreviewCharacters = Math.Clamp(
        options.PreviewCharacters,
        TelegramInputLimits.MinPreviewCharacters,
        TelegramInputLimits.MaxPreviewCharacters);
    options.MediaGroupDebounceMilliseconds = Math.Clamp(
        options.MediaGroupDebounceMilliseconds,
        TelegramInputLimits.MinMediaGroupDebounceMilliseconds,
        TelegramInputLimits.MaxMediaGroupDebounceMilliseconds);
    if (options.AutoDispatchAfterSeconds > 0)
    {
        options.AutoDispatchAfterSeconds = Math.Clamp(
            options.AutoDispatchAfterSeconds,
            TelegramInputLimits.MinAutoDispatchAfterSeconds,
            TelegramInputLimits.MaxAutoDispatchAfterSeconds);
    }
});

builder.Services.PostConfigure<TelegramOutputOptions>(options =>
{
    options.HistoryRetentionDays = Math.Clamp(
        options.HistoryRetentionDays,
        TelegramOutputLimits.MinHistoryRetentionDays,
        TelegramOutputLimits.MaxHistoryRetentionDays);
    options.MaxHistoryEventsPerTurn = Math.Clamp(
        options.MaxHistoryEventsPerTurn,
        TelegramOutputLimits.MinHistoryEventsPerTurn,
        TelegramOutputLimits.MaxHistoryEventsPerTurn);
    options.LiveCardMinEditIntervalSeconds = Math.Clamp(
        options.LiveCardMinEditIntervalSeconds,
        TelegramOutputLimits.MinLiveCardEditIntervalSeconds,
        TelegramOutputLimits.MaxLiveCardEditIntervalSeconds);
});

builder.Services.PostConfigure<TelegramDebugTraceOptions>(options =>
{
    options.MaxTraceFileBytes = Math.Clamp(
        options.MaxTraceFileBytes,
        TelegramDebugTraceLimits.MinTraceFileBytes,
        TelegramDebugTraceLimits.MaxTraceFileBytes);
    options.RetentionDays = Math.Clamp(
        options.RetentionDays,
        TelegramDebugTraceLimits.MinRetentionDays,
        TelegramDebugTraceLimits.MaxRetentionDays);
    options.FullCaptureTtlMinutes = Math.Clamp(
        options.FullCaptureTtlMinutes,
        TelegramDebugTraceLimits.MinFullCaptureTtlMinutes,
        TelegramDebugTraceLimits.MaxFullCaptureTtlMinutes);
    if (!string.IsNullOrWhiteSpace(options.TraceDirectory))
    {
        options.TraceDirectory = Path.GetFullPath(options.TraceDirectory);
    }
});

builder.Services.PostConfigure<OpenAiSpeechToTextOptions>(options =>
{
    options.ApiKey = DefaultIfWhiteSpace(options.ApiKey, Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
    options.Model = DefaultIfWhiteSpace(options.Model, OpenAiSpeechToTextDefaults.Model);
    options.BaseUrl = DefaultIfWhiteSpace(options.BaseUrl, OpenAiSpeechToTextDefaults.BaseUrl);
    options.FfmpegPath = DefaultIfWhiteSpace(options.FfmpegPath, OpenAiSpeechToTextDefaults.FfmpegPath);
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICodexRealtimeBroadcaster, NullCodexRealtimeBroadcaster>();
builder.Services.AddSingleton<CodexThreadManifestStore>();
builder.Services.AddSingleton<ICodexThreadManifestStore>(sp => sp.GetRequiredService<CodexThreadManifestStore>());
builder.Services.AddSingleton<CodexProjectCatalogStore>();
builder.Services.AddSingleton<ICodexProjectCatalogStore>(sp => sp.GetRequiredService<CodexProjectCatalogStore>());
builder.Services.AddSingleton<CodexWorkspaceBrowser>();
builder.Services.AddSingleton<ITelegramThreadFollowRegistry, TelegramThreadFollowRegistry>();
builder.Services.AddSingleton<ITelegramTypingIndicatorRegistry, TelegramTypingIndicatorRegistry>();
builder.Services.AddSingleton<ITelegramTurnReactionRegistry, TelegramTurnReactionRegistry>();
builder.Services.AddSingleton<ITelegramDebugPreambleMode, TelegramDebugPreambleMode>();
builder.Services.AddSingleton<ITelegramOutputModeState, TelegramOutputModeState>();
builder.Services.AddSingleton<ITelegramForumTopicService, TelegramForumTopicService>();
builder.Services.AddSingleton<ITelegramMessageContextStore, TelegramMessageContextStore>();
builder.Services.AddSingleton<ITelegramBotMessageSender, TelegramBotClientMessageSender>();
builder.Services.AddSingleton<IOutboundTelegramMessageSender>(sp => (TelegramBotClientMessageSender)sp.GetRequiredService<ITelegramBotMessageSender>());
builder.Services.AddSingleton<ITelegramAttachmentStore, TelegramAttachmentStore>();
builder.Services.AddSingleton<ITelegramInputBundleStore, TelegramInputBundleStore>();
builder.Services.AddSingleton<TelegramInputBundleCardRenderer>();
builder.Services.AddSingleton<ITelegramDebugTraceStore, TelegramDebugTraceStore>();
builder.Services.AddSingleton<ITelegramPlanInputCoordinator, TelegramPlanInputCoordinator>();
builder.Services.AddSingleton<OutboundTelegramScheduler>();
builder.Services.AddSingleton<IOutboundTelegramQueue>(sp => sp.GetRequiredService<OutboundTelegramScheduler>());
builder.Services.AddSingleton<IOutboundTelegramDeliveryStream>(sp => sp.GetRequiredService<OutboundTelegramScheduler>());
builder.Services.AddSingleton<ITelegramTurnOutputRelay, TelegramTurnOutputRelay>();
builder.Services.AddHttpClient<OpenAiSpeechToTextService>();
builder.Services.AddSingleton<IAudioTranscriptionService>(sp => sp.GetRequiredService<OpenAiSpeechToTextService>());
builder.Services.AddSingleton<ICodexRuntimeClientFactory, CodexRuntimeClientFactory>();
builder.Services.AddSingleton<ICodexSessionEventLog, CodexSessionEventLog>();
builder.Services.AddSingleton<CodexSessionRuntimeRegistry>();
builder.Services.AddSingleton<ICodexTurnExecutionCoordinator>(sp => sp.GetRequiredService<CodexSessionRuntimeRegistry>());
builder.Services.AddSingleton<ICodexGateway, CodexGateway>();
builder.Services.AddSingleton<ICodexAccountUsageService, CodexAccountUsageService>();
builder.Services.AddSingleton<TelegramCommandParser>();
builder.Services.AddSingleton<TelegramMessageChunker>();
builder.Services.AddSingleton<ITelegramBotStateStore, TelegramBotStateStore>();
builder.Services.AddSingleton<ICodexSessionManager, CodexGatewaySessionManager>();
builder.Services.AddSingleton<ITelegramQueuedPromptProcessor, TelegramQueuedPromptProcessor>();
builder.Services.AddSingleton<TelegramCodexBotCommandHandler>();
builder.Services.AddSingleton<ITelegramCodexBotUpdateHandler>(sp => sp.GetRequiredService<TelegramCodexBotCommandHandler>());
builder.Services.AddHostedService<CodexWarmupHostedService>();
builder.Services.AddHostedService<TelegramCodexBotHostedService>();
builder.Services.AddHostedService<TelegramInputBundleAutoDispatchHostedService>();
builder.Services.AddHostedService<TelegramQueuedPromptProcessorHostedService>();
builder.Services.AddHostedService<TelegramTypingHeartbeatHostedService>();
builder.Services.AddHostedService<OutboundTelegramDeliveryHostedService>();

IHost host = builder.Build();
await RehydrateTelegramThreadFollowsAsync(host.Services, CancellationToken.None);
await host.RunAsync();

static string? DefaultIfWhiteSpace(string? value, string? fallback)
    => string.IsNullOrWhiteSpace(value) ? fallback : value;

static string GetDefaultDataRoot()
{
    string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    if (string.IsNullOrWhiteSpace(baseDirectory))
    {
        baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".incursa");
    }

    return Path.Combine(baseDirectory, "Incursa", "CodexTelegram");
}

static IReadOnlyList<string> NormalizeDistinctPaths(IEnumerable<string> paths)
    => paths
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => Path.GetFullPath(path))
        .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        .ToArray();

static long[] ParseInt64List(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return [];
    }

    return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => long.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : (long?)null)
        .Where(item => item.HasValue)
        .Select(item => item!.Value)
        .ToArray();
}

static async Task RehydrateTelegramThreadFollowsAsync(IServiceProvider services, CancellationToken cancellationToken)
{
    ITelegramBotStateStore stateStore = services.GetRequiredService<ITelegramBotStateStore>();
    ITelegramThreadFollowRegistry followRegistry = services.GetRequiredService<ITelegramThreadFollowRegistry>();

    IReadOnlyCollection<TelegramConversationState> conversationStates = await stateStore.ListConversationStatesAsync(cancellationToken).ConfigureAwait(false);
    foreach (TelegramConversationState state in conversationStates)
    {
        if (!string.IsNullOrWhiteSpace(state.ActiveSessionId))
        {
            followRegistry.FollowThread(state.Scope, state.ActiveSessionId);
        }
    }
}

static bool ShouldRunInteractiveMenu(ApplicationCommandLine commandLine)
{
    if (commandLine.ShowMenu)
    {
        return true;
    }

    return !commandLine.RunDirectly
        && !Console.IsInputRedirected
        && !Console.IsOutputRedirected;
}

internal sealed record ApplicationCommandLine(
    bool RunDirectly,
    bool ShowMenu,
    bool ShowHelp,
    string[] ConfigurationArgs)
{
    public static ApplicationCommandLine Parse(string[] args)
    {
        bool runDirectly = false;
        bool showMenu = false;
        bool showHelp = false;
        List<string> configurationArgs = new(args.Length);

        foreach (string arg in args)
        {
            if (Is(arg, "--run"))
            {
                runDirectly = true;
                continue;
            }

            if (Is(arg, "--menu"))
            {
                showMenu = true;
                continue;
            }

            if (Is(arg, "--help") || Is(arg, "-h") || Is(arg, "/?"))
            {
                showHelp = true;
                continue;
            }

            configurationArgs.Add(arg);
        }

        return new ApplicationCommandLine(runDirectly, showMenu, showHelp, configurationArgs.ToArray());
    }

    private static bool Is(string arg, string expected)
        => arg.Equals(expected, StringComparison.OrdinalIgnoreCase);
}

public partial class Program { }
