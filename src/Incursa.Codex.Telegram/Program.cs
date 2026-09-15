using System.Globalization;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Incursa.OpenAI.Codex;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Top-level statements synthesize a Program type, but there is no source declaration to XML-document.
#pragma warning disable CS1591

ApplicationCommandLine commandLine;
try
{
    commandLine = ApplicationCommandLine.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"Invalid command-line configuration: {exception.Message}");
    Environment.ExitCode = 2;
    return;
}
if (commandLine.ShowHelp)
{
    InteractiveBootstrapMenu.WriteHelp();
    return;
}

string localSettingsPath = commandLine.ConfigPath is null
    ? LocalSettingsStore.ResolveDefaultPath()
    : Path.GetFullPath(commandLine.ConfigPath);
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

WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = commandLine.ConfigurationArgs,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Logging.ClearProviders();
builder.Configuration.AddJsonFile(localSettingsPath, optional: true, reloadOnChange: true);
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables(prefix: "CODEX_TELEGRAM_");
builder.Configuration.AddCommandLine(commandLine.ConfigurationArgs);
builder.WebHost.UseUrls(builder.Configuration["TelegramMiniApp:ListenUrl"] ?? "http://127.0.0.1:5287");

builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
    options.ShutdownTimeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddOptions<CodexClientOptions>()
    .Bind(builder.Configuration.GetSection("Codex"));
builder.Services.AddOptions<CodexTelegramOptions>()
    .Bind(builder.Configuration.GetSection("CodexTelegram"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<CodexTelegramOptions>, CodexTelegramOptionsValidator>();
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
builder.Services.AddOptions<TelegramMiniAppOptions>()
    .Bind(builder.Configuration.GetSection("TelegramMiniApp"));

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
        options.Workspace.DataRoot = CodexTelegramDataRoot.GetDefaultDataRoot(options.InstanceId);
    }

    if (options.Mode == CodexTelegramMode.Repository
        && !string.IsNullOrWhiteSpace(options.RepositoryRoot)
        && TryGetFullPath(options.RepositoryRoot, out string repositoryRoot))
    {
        options.RepositoryRoot = repositoryRoot;
        // Repository mode is deliberately single-root: configured general-purpose
        // workspace roots must not widen the repository boundary.
        options.Workspace.WorkspaceRoots = [repositoryRoot];
        // A separately configured default directory must not override the
        // explicit repository binding. Existing manifests retain their
        // recorded directory; this only controls new/resumed option defaults.
        options.Context.WorkingDirectory = repositoryRoot;
    }
    else if (options.Mode == CodexTelegramMode.GeneralPurpose
        && options.Workspace.WorkspaceRoots.Count == 0)
    {
        options.Workspace.WorkspaceRoots.Add(Environment.CurrentDirectory);
    }

    if (options.Mode == CodexTelegramMode.GeneralPurpose
        && string.IsNullOrWhiteSpace(options.Context.WorkingDirectory))
    {
        options.Context.WorkingDirectory = Environment.CurrentDirectory;
    }

    options.TerminalEventHoldMilliseconds = Math.Clamp(
        options.TerminalEventHoldMilliseconds,
        CodexTurnStreamingDefaults.MinTerminalEventHoldMilliseconds,
        CodexTurnStreamingDefaults.MaxTerminalEventHoldMilliseconds);
    options.Workspace.DataRoot = Path.GetFullPath(options.Workspace.DataRoot);
    if (!string.IsNullOrWhiteSpace(options.Workspace.TaskWorktreeRoot))
    {
        options.Workspace.TaskWorktreeRoot = Path.GetFullPath(options.Workspace.TaskWorktreeRoot);
    }
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
    options.CompactPulseIntervalSeconds = Math.Clamp(
        options.CompactPulseIntervalSeconds,
        TelegramOutputLimits.MinCompactPulseIntervalSeconds,
        TelegramOutputLimits.MaxCompactPulseIntervalSeconds);
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

builder.Services.PostConfigure<TelegramMiniAppOptions>(options =>
{
    options.ListenUrl = DefaultIfWhiteSpace(options.ListenUrl, "http://127.0.0.1:5287")!;
    options.InitDataMaxAgeSeconds = Math.Clamp(options.InitDataMaxAgeSeconds, 60, 86_400);
    options.BrowserPairingLifetimeMinutes = Math.Clamp(options.BrowserPairingLifetimeMinutes, 1, 60);
    options.BrowserSessionLifetimeHours = Math.Clamp(options.BrowserSessionLifetimeHours, 1, 168);
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICodexRemoteTurnEventForwarder, CodexRemoteTurnEventForwarder>();
builder.Services.AddSingleton<ICodexRealtimeBroadcaster, CodexRemoteTurnEventBroadcaster>();
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
builder.Services.AddHttpClient(nameof(CodexWorkerCoordinatorHostedService));
builder.Services.AddHttpClient(nameof(CodexCoordinatorLeaseHandoffService));
builder.Services.AddHttpClient(nameof(CodexRemoteTaskProvisioningService));
builder.Services.AddHttpClient(nameof(CodexRemoteSessionRelay));
builder.Services.AddHttpClient(nameof(CodexRemoteSessionControlRelay));
builder.Services.AddHttpClient(nameof(CodexRemoteTaskDetailRelay));
builder.Services.AddHttpClient(nameof(CodexRemoteSessionSettingsRelay));
builder.Services.AddHttpClient(nameof(CodexRemoteTurnEventForwarder));
builder.Services.AddSingleton<IAudioTranscriptionService>(sp => sp.GetRequiredService<OpenAiSpeechToTextService>());
builder.Services.AddSingleton<ICodexRuntimeClientFactory, CodexRuntimeClientFactory>();
builder.Services.AddSingleton<ICodexSessionEventLog, CodexSessionEventLog>();
builder.Services.AddSingleton<ICodexSupervisionLedger, CodexSupervisionLedger>();
builder.Services.AddSingleton<ICodexTaskWorkspaceManager, CodexTaskWorkspaceManager>();
builder.Services.AddSingleton<CodexWorkerRegistry>();
builder.Services.AddSingleton<ICodexWorkerRegistry>(sp => sp.GetRequiredService<CodexWorkerRegistry>());
builder.Services.AddSingleton<ICodexCoordinatorWorkerStore, CodexCoordinatorWorkerStore>();
builder.Services.AddSingleton<ICodexCoordinatorLeaseStore, CodexCoordinatorLeaseStore>();
builder.Services.AddSingleton<CodexCoordinatorLeaseHandoffService>();
builder.Services.AddSingleton<ICodexCoordinatorTaskStore, CodexCoordinatorTaskStore>();
builder.Services.AddSingleton<CodexRemoteTaskProvisioningService>();
builder.Services.AddSingleton<CodexRemoteSessionRelay>();
builder.Services.AddSingleton<CodexRemoteSessionControlRelay>();
builder.Services.AddSingleton<CodexRemoteTaskDetailRelay>();
builder.Services.AddSingleton<CodexRemoteSessionSettingsRelay>();
builder.Services.AddSingleton<CodexRemoteWorkerControlRelay>();
builder.Services.AddSingleton<CodexWorkerUpdateManager>();
builder.Services.AddSingleton<ICodexWorkerUpdateManager>(sp => sp.GetRequiredService<CodexWorkerUpdateManager>());
builder.Services.AddSingleton<CodexRemoteWorkerUpdateRelay>();
builder.Services.AddSingleton<ICodexFleetRolloutStore, CodexFleetRolloutStore>();
builder.Services.AddSingleton<ICodexFleetRolloutCoordinator, CodexFleetRolloutCoordinator>();
builder.Services.AddSingleton<ICodexTaskRecipeCatalog, CodexTaskRecipeCatalog>();
builder.Services.AddSingleton<CodexSessionRuntimeRegistry>();
builder.Services.AddSingleton<ICodexTurnExecutionCoordinator>(sp => sp.GetRequiredService<CodexSessionRuntimeRegistry>());
builder.Services.AddSingleton<ICodexGateway, CodexGateway>();
builder.Services.AddSingleton<ICodexAccountUsageService, CodexAccountUsageService>();
builder.Services.AddSingleton<TelegramMiniAppAuth>();
builder.Services.AddSingleton<TelegramMiniAppBrowserPairingStore>();
builder.Services.AddSingleton<ITelegramMiniAppBrowserPairingStore>(sp => sp.GetRequiredService<TelegramMiniAppBrowserPairingStore>());
builder.Services.AddSingleton<TelegramMiniAppAcknowledgementStore>();
builder.Services.AddSingleton<ITelegramMiniAppAcknowledgementStore>(sp => sp.GetRequiredService<TelegramMiniAppAcknowledgementStore>());
builder.Services.AddSingleton<TelegramCommandParser>();
builder.Services.AddSingleton<TelegramMessageChunker>();
builder.Services.AddSingleton<ITelegramBotStateStore, TelegramBotStateStore>();
builder.Services.AddSingleton<ICodexSessionManager, CodexGatewaySessionManager>();
builder.Services.AddSingleton<ITelegramQueuedPromptProcessor, TelegramQueuedPromptProcessor>();
builder.Services.AddSingleton<TelegramCodexBotCommandHandler>();
builder.Services.AddSingleton<ITelegramCodexBotUpdateHandler>(sp => sp.GetRequiredService<TelegramCodexBotCommandHandler>());
builder.Services.AddHostedService<CodexWarmupHostedService>();
builder.Services.AddHostedService<CodexWorkerCoordinatorHostedService>();
builder.Services.AddHostedService<TelegramCodexBotHostedService>();
builder.Services.AddHostedService<TelegramInputBundleAutoDispatchHostedService>();
builder.Services.AddHostedService<TelegramQueuedPromptProcessorHostedService>();
builder.Services.AddHostedService<TelegramTypingHeartbeatHostedService>();
builder.Services.AddHostedService<OutboundTelegramDeliveryHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CodexSessionRuntimeRegistry>());

WebApplication app;
try
{
    app = builder.Build();
    _ = app.Services.GetRequiredService<IOptions<CodexTelegramOptions>>().Value;
}
catch (OptionsValidationException exception)
{
    Console.Error.WriteLine($"Codex Telegram configuration validation failed: {string.Join(" ", exception.Failures)}");
    Environment.ExitCode = 2;
    return;
}
catch (InvalidOperationException exception)
{
    // Configuration binding (for example, an invalid enum value) reports an
    // InvalidOperationException before the options validator can run. Keep
    // unattended service/container startup actionable and non-interactive.
    Console.Error.WriteLine($"Codex Telegram configuration could not be loaded: {exception.Message}");
    Environment.ExitCode = 2;
    return;
}

app.UseDefaultFiles();
app.UseStaticFiles();
CodexCoordinatorWorkerEndpoints.Map(app);
CodexCoordinatorTurnEventEndpoints.Map(app);
CodexRemoteTaskEndpoints.Map(app);
CodexRemoteSessionEndpoints.Map(app);
CodexRemoteSessionControlEndpoints.Map(app);
CodexRemoteTaskDetailEndpoints.Map(app);
CodexRemoteSessionSettingsEndpoints.Map(app);
CodexRemoteWorkerControlEndpoints.Map(app);
CodexWorkerUpdateEndpoints.Map(app);
TelegramMiniAppEndpoints.Map(app);

await RehydrateTelegramThreadFollowsAsync(app.Services, CancellationToken.None);
await ReattachPersistedCodexTurnsAsync(app.Services, CancellationToken.None);
await app.RunAsync();

static string? DefaultIfWhiteSpace(string? value, string? fallback)
    => string.IsNullOrWhiteSpace(value) ? fallback : value;

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

static async Task ReattachPersistedCodexTurnsAsync(IServiceProvider services, CancellationToken cancellationToken)
{
    ITelegramBotStateStore stateStore = services.GetRequiredService<ITelegramBotStateStore>();
    CodexSessionRuntimeRegistry runtimeRegistry = services.GetRequiredService<CodexSessionRuntimeRegistry>();

    IReadOnlyCollection<TelegramConversationState> conversationStates = await stateStore.ListConversationStatesAsync(cancellationToken).ConfigureAwait(false);
    IReadOnlyCollection<string> trackedSessionIds = await stateStore.GetTrackedSessionIdsAsync(cancellationToken).ConfigureAwait(false);
    string[] candidateThreadIds = conversationStates
        .Select(state => state.ActiveSessionId)
        .Concat(trackedSessionIds)
        .Where(sessionId => !string.IsNullOrWhiteSpace(sessionId))
        .Select(sessionId => sessionId!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    await runtimeRegistry.ReattachPersistedTurnsAsync(candidateThreadIds, cancellationToken).ConfigureAwait(false);
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

static bool TryGetFullPath(string? value, out string path)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        path = string.Empty;
        return false;
    }

    try
    {
        path = Path.GetFullPath(value.Trim());
        return true;
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
    {
        path = string.Empty;
        return false;
    }
}

internal sealed record ApplicationCommandLine(
    bool RunDirectly,
    bool ShowMenu,
    bool ShowHelp,
    string[] ConfigurationArgs)
{
    public string? ConfigPath { get; init; }

    public string? ConfigurationPath => ConfigPath;

    public static ApplicationCommandLine Parse(string[] args)
    {
        bool runDirectly = false;
        bool showMenu = false;
        bool showHelp = false;
        string? configPath = null;
        List<string> configurationArgs = new(args.Length);

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
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

            if (Is(arg, "--config"))
            {
                if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    throw new ArgumentException("The --config option requires a non-empty path.", nameof(args));
                }

                configPath = args[++index];
                continue;
            }

            if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
            {
                string value = arg["--config=".Length..];
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("The --config option requires a non-empty path.", nameof(args));
                }

                configPath = value;
                continue;
            }

            configurationArgs.Add(arg);
        }

        return new ApplicationCommandLine(runDirectly, showMenu, showHelp, configurationArgs.ToArray())
        {
            ConfigPath = configPath,
        };
    }

    private static bool Is(string arg, string expected)
        => arg.Equals(expected, StringComparison.OrdinalIgnoreCase);
}

public partial class Program { }
