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

ApplicationCommandLine commandLine = ApplicationCommandLine.Parse(args);
if (commandLine.ShowHelp)
{
    InteractiveBootstrapMenu.WriteHelp();
    return;
}

if (ShouldRunInteractiveMenu(commandLine))
{
    LocalSettingsStore store;
    try
    {
        store = LocalSettingsStore.Load();
    }
    catch (InvalidOperationException exception)
    {
        Console.WriteLine(exception.Message);
        return;
    }

    CodexModelCatalog modelCatalog = await CodexModelDiscovery.DiscoverAsync(store.GetSnapshot(), CancellationToken.None);

    if (InteractiveBootstrapMenu.Run(store, modelCatalog) == BootstrapMenuResult.Quit)
    {
        return;
    }
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(commandLine.ConfigurationArgs);
builder.Logging.ClearProviders();
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
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
builder.Services.AddOptions<TelegramOutboundOptions>()
    .Bind(builder.Configuration.GetSection("TelegramBot:Outbound"));
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
    options.ClientVersion = DefaultIfWhiteSpace(options.ClientVersion, "0.1.0");
    options.CodexPathOverride = DefaultIfWhiteSpace(
        options.CodexPathOverride,
        DefaultIfWhiteSpace(builder.Configuration["TelegramBot:CodexExecutablePath"], Environment.GetEnvironmentVariable("CODEX_PATH")));
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

    options.PollingTimeoutSeconds = Math.Clamp(options.PollingTimeoutSeconds, 1, 120);
    options.MaxTelegramMessageLength = Math.Clamp(options.MaxTelegramMessageLength, 1, 4000);
    options.MinAudioDurationSeconds = Math.Clamp(options.MinAudioDurationSeconds, 0, 60);
    options.MaxAudioDurationSeconds = Math.Clamp(options.MaxAudioDurationSeconds, 1, 6 * 60 * 60);
    if (options.MaxAudioDurationSeconds < options.MinAudioDurationSeconds)
    {
        options.MaxAudioDurationSeconds = options.MinAudioDurationSeconds;
    }
});

builder.Services.PostConfigure<TelegramOutboundOptions>(options =>
{
    options.GroupMinimumSendIntervalSeconds = Math.Clamp(options.GroupMinimumSendIntervalSeconds, 1, 120);
    options.PrivateMinimumSendIntervalSeconds = Math.Clamp(options.PrivateMinimumSendIntervalSeconds, 1, 120);
    options.GlobalMaxMessagesPerSecond = Math.Clamp(options.GlobalMaxMessagesPerSecond, 1, 25);
    options.MaxMessageChars = Math.Clamp(options.MaxMessageChars, 500, 3500);
    options.MaxBufferedCharsPerDestination = Math.Clamp(options.MaxBufferedCharsPerDestination, options.MaxMessageChars, 250000);
    options.MaxBufferedMessagesPerDestination = Math.Clamp(options.MaxBufferedMessagesPerDestination, 1, 1000);
    options.FlushIntervalMilliseconds = Math.Clamp(options.FlushIntervalMilliseconds, 50, 10000);
    options.AgentMessageUpdateMinChars = Math.Clamp(options.AgentMessageUpdateMinChars, 12, options.MaxMessageChars);
    options.AgentMessageUpdateMaxChars = Math.Clamp(options.AgentMessageUpdateMaxChars, options.AgentMessageUpdateMinChars, options.MaxMessageChars);
    options.BatchWindowSeconds = Math.Clamp(options.BatchWindowSeconds, 0, 120);
});

builder.Services.PostConfigure<OpenAiSpeechToTextOptions>(options =>
{
    options.ApiKey = DefaultIfWhiteSpace(options.ApiKey, Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
    options.Model = DefaultIfWhiteSpace(options.Model, "whisper-1");
    options.BaseUrl = DefaultIfWhiteSpace(options.BaseUrl, "https://api.openai.com/v1/");
    options.FfmpegPath = DefaultIfWhiteSpace(options.FfmpegPath, "ffmpeg");
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICodexRealtimeBroadcaster, NullCodexRealtimeBroadcaster>();
builder.Services.AddSingleton<CodexThreadManifestStore>();
builder.Services.AddSingleton<ICodexThreadManifestStore>(sp => sp.GetRequiredService<CodexThreadManifestStore>());
builder.Services.AddSingleton<CodexProjectCatalogStore>();
builder.Services.AddSingleton<ICodexProjectCatalogStore>(sp => sp.GetRequiredService<CodexProjectCatalogStore>());
builder.Services.AddSingleton<CodexWorkspaceBrowser>();
builder.Services.AddSingleton<ITelegramThreadFollowRegistry, TelegramThreadFollowRegistry>();
builder.Services.AddSingleton<ITelegramForumTopicService, TelegramForumTopicService>();
builder.Services.AddSingleton<ITelegramBotMessageSender, TelegramBotClientMessageSender>();
builder.Services.AddSingleton<IOutboundTelegramMessageSender>(sp => (TelegramBotClientMessageSender)sp.GetRequiredService<ITelegramBotMessageSender>());
builder.Services.AddSingleton<OutboundTelegramScheduler>();
builder.Services.AddSingleton<IOutboundTelegramQueue>(sp => sp.GetRequiredService<OutboundTelegramScheduler>());
builder.Services.AddSingleton<ITelegramTurnOutputRelay, TelegramTurnOutputRelay>();
builder.Services.AddHttpClient<OpenAiSpeechToTextService>();
builder.Services.AddSingleton<IAudioTranscriptionService>(sp => sp.GetRequiredService<OpenAiSpeechToTextService>());
builder.Services.AddSingleton<CodexSessionRuntimeRegistry>();
builder.Services.AddSingleton<ICodexTurnExecutionCoordinator>(sp => sp.GetRequiredService<CodexSessionRuntimeRegistry>());
builder.Services.AddSingleton<ICodexGateway, CodexGateway>();
builder.Services.AddSingleton<TelegramCommandParser>();
builder.Services.AddSingleton<TelegramMessageChunker>();
builder.Services.AddSingleton<ITelegramBotStateStore, TelegramBotStateStore>();
builder.Services.AddSingleton<ICodexSessionManager, CodexGatewaySessionManager>();
builder.Services.AddSingleton<ITelegramQueuedPromptProcessor, TelegramQueuedPromptProcessor>();
builder.Services.AddSingleton<TelegramCodexBotCommandHandler>();
builder.Services.AddSingleton<ITelegramCodexBotUpdateHandler>(sp => sp.GetRequiredService<TelegramCodexBotCommandHandler>());
builder.Services.AddHostedService<CodexWarmupHostedService>();
builder.Services.AddHostedService<TelegramCodexBotHostedService>();
builder.Services.AddHostedService<TelegramQueuedPromptProcessorHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboundTelegramScheduler>());

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
