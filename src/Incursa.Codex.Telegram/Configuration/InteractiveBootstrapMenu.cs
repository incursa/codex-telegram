using System.Globalization;
using System.Text;
using Incursa.Codex.Telegram.Models;

namespace Incursa.Codex.Telegram.Configuration;

internal enum BootstrapMenuResult
{
    Start,
    Quit,
}

internal static class InteractiveBootstrapMenu
{
    public static BootstrapMenuResult Run(LocalSettingsStore store)
        => Run(store, CodexModelDiscovery.CreateFallbackCatalog());

    public static BootstrapMenuResult Run(LocalSettingsStore store, CodexModelCatalog modelCatalog)
    {
        while (true)
        {
            ClearScreen();
            WriteStatus(store, modelCatalog);
            Console.WriteLine();
            Console.WriteLine("1. Start bot");
            Console.WriteLine("2. Telegram and admins");
            Console.WriteLine("3. OpenAI transcription");
            Console.WriteLine("4. Codex runtime");
            Console.WriteLine("5. Workspaces");
            Console.WriteLine("6. Show settings path");
            Console.WriteLine("Q. Quit");
            Console.WriteLine();

            string choice = ReadLine("Select: ");
            switch (NormalizeChoice(choice))
            {
                case "1":
                case "start":
                    if (ConfirmStart(store, modelCatalog))
                    {
                        return BootstrapMenuResult.Start;
                    }

                    break;

                case "2":
                case "telegram":
                    ConfigureTelegram(store);
                    break;

                case "3":
                case "openai":
                    ConfigureOpenAi(store);
                    break;

                case "4":
                case "codex":
                    modelCatalog = ConfigureCodex(store, modelCatalog);
                    break;

                case "5":
                case "workspaces":
                    ConfigureWorkspaces(store);
                    break;

                case "6":
                case "path":
                    Console.WriteLine(store.FilePath);
                    Pause();
                    break;

                case "q":
                case "quit":
                case "exit":
                    return BootstrapMenuResult.Quit;
            }
        }
    }

    public static void WriteHelp()
    {
        Console.WriteLine("Incursa.Codex.Telegram");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  Incursa.Codex.Telegram            Open the bootstrap/admin menu.");
        Console.WriteLine("  Incursa.Codex.Telegram --run      Start directly with no console logging.");
        Console.WriteLine("  Incursa.Codex.Telegram --menu     Force the bootstrap/admin menu.");
        Console.WriteLine("  Incursa.Codex.Telegram --help     Show this help.");
        Console.WriteLine();
        Console.WriteLine($"The menu writes {LocalSettingsStore.FileName} in the current directory.");
        Console.WriteLine("The startup screen shows the resolved settings file, local state root, and common model pickers before launch.");
        Console.WriteLine("When Codex is reachable, the menu queries it for live model lists and falls back to curated examples if discovery fails.");
        Console.WriteLine("Environment variables and command-line configuration still override that file.");
    }

    private static void ConfigureTelegram(LocalSettingsStore store)
    {
        while (true)
        {
            ClearScreen();
            LocalSettingsSnapshot snapshot = store.GetSnapshot();
            Console.WriteLine("Telegram And Admins");
            Console.WriteLine();
            Console.WriteLine($"Telegram polling: {FormatEnabled(snapshot.TelegramEnabled)}");
            Console.WriteLine($"Bot token: {FormatConfigured(snapshot.TelegramTokenConfigured)}");
            Console.WriteLine($"Admin user IDs: {FormatLongList(snapshot.AllowedUserIds)}");
            Console.WriteLine($"Allowed chat IDs: {FormatLongList(snapshot.AllowedChatIds)}");
            Console.WriteLine();
            Console.WriteLine("1. Toggle Telegram polling");
            Console.WriteLine("2. Set bot token");
            Console.WriteLine("3. Set admin user IDs");
            Console.WriteLine("4. Set allowed chat IDs");
            Console.WriteLine("B. Back");
            Console.WriteLine();

            switch (NormalizeChoice(ReadLine("Select: ")))
            {
                case "1":
                    store.SetTelegramEnabled(!snapshot.TelegramEnabled);
                    SaveAndPause(store);
                    break;

                case "2":
                    SetSecret(
                        "Bot token",
                        snapshot.TelegramTokenConfigured,
                        store.SetTelegramToken,
                        store);
                    break;

                case "3":
                    SetLongList(
                        "Admin user IDs",
                        snapshot.AllowedUserIds,
                        store.SetAllowedUserIds,
                        store);
                    break;

                case "4":
                    SetLongList(
                        "Allowed chat IDs",
                        snapshot.AllowedChatIds,
                        store.SetAllowedChatIds,
                        store);
                    break;

                case "b":
                case "back":
                    return;
            }
        }
    }

    private static void ConfigureOpenAi(LocalSettingsStore store)
    {
        while (true)
        {
            ClearScreen();
            LocalSettingsSnapshot snapshot = store.GetSnapshot();
            Console.WriteLine("OpenAI Transcription");
            Console.WriteLine();
            Console.WriteLine($"API key: {FormatConfigured(snapshot.OpenAiApiKeyConfigured)}");
            Console.WriteLine($"Model: {FormatValue(snapshot.OpenAiModel, "whisper-1")}");
            Console.WriteLine($"ffmpeg path: {FormatValue(snapshot.OpenAiFfmpegPath, "ffmpeg")}");
            Console.WriteLine($"Audio duration limits: {FormatSeconds(snapshot.MinAudioDurationSeconds ?? 1)} to {FormatSeconds(snapshot.MaxAudioDurationSeconds ?? 600)}");
            Console.WriteLine();
            Console.WriteLine("1. Set API key");
            Console.WriteLine("2. Pick transcription model");
            Console.WriteLine("3. Set ffmpeg path");
            Console.WriteLine("4. Set minimum audio duration");
            Console.WriteLine("5. Set maximum audio duration");
            Console.WriteLine("B. Back");
            Console.WriteLine();

            switch (NormalizeChoice(ReadLine("Select: ")))
            {
                case "1":
                    SetSecret(
                        "OpenAI API key",
                        snapshot.OpenAiApiKeyConfigured,
                        store.SetOpenAiApiKey,
                        store);
                    break;

                case "2":
                    PickOpenAiModel(snapshot, store);
                    break;

                case "3":
                    SetString(
                        "ffmpeg path",
                        snapshot.OpenAiFfmpegPath,
                        store.SetOpenAiFfmpegPath,
                        store,
                        "ffmpeg");
                    break;

                case "4":
                    SetInt32(
                        "minimum audio duration seconds",
                        snapshot.MinAudioDurationSeconds,
                        1,
                        0,
                        60,
                        store.SetMinAudioDurationSeconds,
                        store);
                    break;

                case "5":
                    SetInt32(
                        "maximum audio duration seconds",
                        snapshot.MaxAudioDurationSeconds,
                        600,
                        1,
                        6 * 60 * 60,
                        store.SetMaxAudioDurationSeconds,
                        store);
                    break;

                case "b":
                case "back":
                    return;
            }
        }
    }

    private static CodexModelCatalog ConfigureCodex(LocalSettingsStore store, CodexModelCatalog modelCatalog)
    {
        while (true)
        {
            ClearScreen();
            LocalSettingsSnapshot snapshot = store.GetSnapshot();
            Console.WriteLine("Codex Runtime");
            Console.WriteLine();
            Console.WriteLine($"Codex executable: {FormatValue(ResolveCodexExecutablePath(snapshot), "PATH")}");
            Console.WriteLine($"Initialize on start: {FormatEnabled(snapshot.InitializeOnStart)}");
            Console.WriteLine($"Model: {FormatModelValue(snapshot.CodexModel, modelCatalog.Models, "Codex default")}");
            Console.WriteLine($"Thinking: {FormatValue(snapshot.ReasoningEffort, "Codex default")}");
            Console.WriteLine($"Sandbox: {FormatValue(snapshot.Sandbox, "workspace-write")}");
            Console.WriteLine($"Approval mode: {FormatValue(snapshot.ApprovalMode, "on-request")}");
            Console.WriteLine($"Network access: {FormatNullableBool(snapshot.NetworkAccessEnabled, "Codex default")}");
            Console.WriteLine($"Model catalog: {DescribeModelCatalog(modelCatalog)}");
            Console.WriteLine();
            Console.WriteLine("1. Set Codex executable path");
            Console.WriteLine("2. Toggle initialize on start");
            Console.WriteLine("3. Pick default model");
            Console.WriteLine("4. Pick default thinking effort");
            Console.WriteLine("5. Set sandbox");
            Console.WriteLine("6. Set approval mode");
            Console.WriteLine("7. Cycle network access default");
            Console.WriteLine("B. Back");
            Console.WriteLine();

            switch (NormalizeChoice(ReadLine("Select: ")))
            {
                case "1":
                    SetString(
                        "Codex executable path",
                        snapshot.CodexPathOverride,
                        store.SetCodexPathOverride,
                        store,
                        "PATH");
                    modelCatalog = CodexModelDiscovery.DiscoverAsync(store.GetSnapshot(), CancellationToken.None).GetAwaiter().GetResult();
                    break;

                case "2":
                    store.SetInitializeOnStart(!snapshot.InitializeOnStart);
                    SaveAndPause(store);
                    break;

                case "3":
                    PickCodexModel(snapshot, store, modelCatalog);
                    break;

                case "4":
                    PickReasoningEffort(snapshot, store, modelCatalog);
                    break;

                case "5":
                    SetString(
                        "Sandbox (read-only, workspace-write, danger-full-access)",
                        snapshot.Sandbox,
                        store.SetSandbox,
                        store,
                        "workspace-write");
                    break;

                case "6":
                    SetString(
                        "Approval mode (never, on-request, on-failure, untrusted)",
                        snapshot.ApprovalMode,
                        store.SetApprovalMode,
                        store,
                        "on-request");
                    break;

                case "7":
                    store.SetNetworkAccessEnabled(NextNullableBool(snapshot.NetworkAccessEnabled));
                    SaveAndPause(store);
                    break;

                case "b":
                case "back":
                    return modelCatalog;
            }
        }
    }

    private static void ConfigureWorkspaces(LocalSettingsStore store)
    {
        while (true)
        {
            ClearScreen();
            LocalSettingsSnapshot snapshot = store.GetSnapshot();
            Console.WriteLine("Workspaces");
            Console.WriteLine();
            Console.WriteLine($"Workspace roots: {FormatStringList(snapshot.WorkspaceRoots, Environment.CurrentDirectory)}");
            Console.WriteLine($"Default working directory: {FormatValue(snapshot.WorkingDirectory, Environment.CurrentDirectory)}");
            Console.WriteLine($"Data root: {FormatValue(snapshot.DataRoot, GetDefaultDataRoot())}");
            Console.WriteLine();
            Console.WriteLine("1. Set workspace roots");
            Console.WriteLine("2. Set default working directory");
            Console.WriteLine("3. Set local data root");
            Console.WriteLine("B. Back");
            Console.WriteLine();

            switch (NormalizeChoice(ReadLine("Select: ")))
            {
                case "1":
                    SetPathList(
                        "Workspace roots",
                        snapshot.WorkspaceRoots,
                        store.SetWorkspaceRoots,
                        store);
                    break;

                case "2":
                    SetPath(
                        "Default working directory",
                        snapshot.WorkingDirectory,
                        store.SetWorkingDirectory,
                        store,
                        Environment.CurrentDirectory);
                    break;

                case "3":
                    SetPath(
                        "Local data root",
                        snapshot.DataRoot,
                        store.SetDataRoot,
                        store,
                        "User application data");
                    break;

                case "b":
                case "back":
                    return;
            }
        }
    }

    private static bool ConfirmStart(LocalSettingsSnapshot snapshot)
        => ConfirmStartInternal(snapshot, null, CodexModelDiscovery.CreateFallbackCatalog());

    private static bool ConfirmStart(LocalSettingsStore store)
        => ConfirmStartInternal(store.GetSnapshot(), store.FilePath, CodexModelDiscovery.CreateFallbackCatalog());

    private static bool ConfirmStart(LocalSettingsStore store, CodexModelCatalog modelCatalog)
        => ConfirmStartInternal(store.GetSnapshot(), store.FilePath, modelCatalog);

    private static bool ConfirmStartInternal(LocalSettingsSnapshot snapshot, string? settingsPath, CodexModelCatalog modelCatalog)
    {
        Console.WriteLine();
        Console.WriteLine("Startup summary:");
        if (!string.IsNullOrWhiteSpace(settingsPath))
        {
            Console.WriteLine($"Settings file: {settingsPath}");
        }

        Console.WriteLine($"Local state: {FormatValue(snapshot.DataRoot, GetDefaultDataRoot())}");
        Console.WriteLine($"Workspace roots: {FormatStringList(snapshot.WorkspaceRoots, Environment.CurrentDirectory)}");
        Console.WriteLine($"Default working directory: {FormatValue(snapshot.WorkingDirectory, Environment.CurrentDirectory)}");
        Console.WriteLine($"Telegram polling: {FormatEnabled(snapshot.TelegramEnabled)}, token {FormatConfigured(snapshot.TelegramTokenConfigured)}, admins {snapshot.AllowedUserIds.Count}, chats {snapshot.AllowedChatIds.Count}");
        Console.WriteLine($"OpenAI: key {FormatConfigured(snapshot.OpenAiApiKeyConfigured)}, model {FormatValue(snapshot.OpenAiModel, "whisper-1")}");
        Console.WriteLine($"Codex: executable {FormatValue(ResolveCodexExecutablePath(snapshot), "PATH")}, model {FormatModelValue(snapshot.CodexModel, modelCatalog.Models, "Codex default")}, thinking {FormatValue(snapshot.ReasoningEffort, "Codex default")}");
        Console.WriteLine($"Codex model catalog: {DescribeModelCatalog(modelCatalog)}");
        Console.WriteLine();

        List<string> warnings = BuildStartWarnings(snapshot).ToList();
        if (warnings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Startup warnings:");
            foreach (string warning in warnings)
            {
                Console.WriteLine($"- {warning}");
            }

            Console.WriteLine();
            if (!Confirm("Start anyway?"))
            {
                return false;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Starting bot. Console logging is disabled; press Ctrl+C to stop.");
        return true;
    }

    private static void PickOpenAiModel(LocalSettingsSnapshot snapshot, LocalSettingsStore store)
    {
        while (true)
        {
            ClearScreen();
            Console.WriteLine("OpenAI Transcription Model");
            Console.WriteLine();
            Console.WriteLine($"Current: {FormatValue(snapshot.OpenAiModel, "whisper-1")}");
            Console.WriteLine();
            Console.WriteLine("1. whisper-1 (default)");
            Console.WriteLine("2. gpt-4o-mini-transcribe");
            Console.WriteLine("3. gpt-4o-transcribe");
            Console.WriteLine("4. Enter a custom model");
            Console.WriteLine("5. Clear and use the default");
            Console.WriteLine("B. Back");
            Console.WriteLine();

            switch (NormalizeChoice(ReadLine("Select: ")))
            {
                case "1":
                    store.SetOpenAiModel("whisper-1");
                    SaveAndPause(store);
                    return;

                case "2":
                    store.SetOpenAiModel("gpt-4o-mini-transcribe");
                    SaveAndPause(store);
                    return;

                case "3":
                    store.SetOpenAiModel("gpt-4o-transcribe");
                    SaveAndPause(store);
                    return;

                case "4":
                    SetString(
                        "Transcription model",
                        snapshot.OpenAiModel,
                        store.SetOpenAiModel,
                        store,
                        "whisper-1");
                    return;

                case "5":
                    store.SetOpenAiModel(null);
                    SaveAndPause(store);
                    return;

                case "b":
                case "back":
                    return;
            }
        }
    }

    private static void PickCodexModel(LocalSettingsSnapshot snapshot, LocalSettingsStore store, CodexModelCatalog modelCatalog)
    {
        IReadOnlyList<CodexModelVm> models = modelCatalog.Models;
        while (true)
        {
            ClearScreen();
            Console.WriteLine("Codex Default Model");
            Console.WriteLine();
            Console.WriteLine($"Current: {FormatModelValue(snapshot.CodexModel, models, "Codex default")}");
            Console.WriteLine(modelCatalog.IsLive ? "Catalog: live Codex discovery." : "Catalog: curated fallback examples.");
            if (!string.IsNullOrWhiteSpace(modelCatalog.Message))
            {
                Console.WriteLine(modelCatalog.Message);
            }

            Console.WriteLine();
            Console.WriteLine("1. Use the Codex default");
            for (int index = 0; index < models.Count; index++)
            {
                CodexModelVm model = models[index];
                string label = FormatModelChoice(model);
                if (IsSelectedModel(snapshot.CodexModel, model))
                {
                    label = "[x] " + label;
                }

                Console.WriteLine($"{index + 2}. {label}");
            }

            Console.WriteLine($"{models.Count + 2}. Enter a custom model");
            Console.WriteLine("B. Back");
            Console.WriteLine();

            string choice = NormalizeChoice(ReadLine("Select: "));
            switch (choice)
            {
                case "1":
                    store.SetCodexModel(null);
                    SaveAndPause(store);
                    return;

                case "b":
                case "back":
                    return;

                default:
                    if (int.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out int selectedIndex))
                    {
                        if (selectedIndex >= 2 && selectedIndex < models.Count + 2)
                        {
                            store.SetCodexModel(models[selectedIndex - 2].Id);
                            SaveAndPause(store);
                            return;
                        }

                        if (selectedIndex == models.Count + 2)
                        {
                            SetString(
                                "Default model",
                                snapshot.CodexModel,
                                store.SetCodexModel,
                                store,
                                "Codex default");
                            return;
                        }
                    }

                    break;
            }
        }
    }

    private static void PickReasoningEffort(LocalSettingsSnapshot snapshot, LocalSettingsStore store, CodexModelCatalog modelCatalog)
    {
        IReadOnlyList<CodexModelVm> models = modelCatalog.Models;
        CodexModelVm? selectedModel = ResolveModel(models, snapshot.CodexModel);
        IReadOnlyList<string> efforts = BuildReasoningEffortChoices(selectedModel);
        bool hasModelSpecificEfforts = selectedModel is not null
            && selectedModel.SupportedEfforts.Any(option => !string.Equals(option.ToString(), "None", StringComparison.OrdinalIgnoreCase));

        while (true)
        {
            ClearScreen();
            Console.WriteLine("Codex Default Thinking Effort");
            Console.WriteLine();
            Console.WriteLine($"Current: {FormatValue(snapshot.ReasoningEffort, "Codex default")}");
            Console.WriteLine($"Model: {FormatModelValue(snapshot.CodexModel, models, "Codex default")}");
            if (selectedModel is null)
            {
                Console.WriteLine(modelCatalog.IsLive
                    ? "Live discovery found models, but the configured model was not matched; showing generic effort choices."
                    : "Live discovery is unavailable; showing generic effort choices.");
            }
            else if (hasModelSpecificEfforts)
            {
                Console.WriteLine($"Efforts reported by Codex: {string.Join(", ", efforts)}");
            }
            else
            {
                Console.WriteLine(modelCatalog.IsLive
                    ? "Live discovery did not report a model-specific effort list; showing generic effort choices."
                    : "Curated fallback examples do not report a specific effort list; showing generic effort choices.");
            }

            Console.WriteLine();
            Console.WriteLine("1. Use the Codex default");
            for (int index = 0; index < efforts.Count; index++)
            {
                string effort = efforts[index];
                string label = effort;
                if (!string.IsNullOrWhiteSpace(snapshot.ReasoningEffort) && string.Equals(snapshot.ReasoningEffort, effort, StringComparison.OrdinalIgnoreCase))
                {
                    label = "[x] " + label;
                }

                Console.WriteLine($"{index + 2}. {label}");
            }

            Console.WriteLine($"{efforts.Count + 2}. Enter a custom value");
            Console.WriteLine("B. Back");
            Console.WriteLine();

            string choice = NormalizeChoice(ReadLine("Select: "));
            switch (choice)
            {
                case "1":
                    store.SetReasoningEffort(null);
                    SaveAndPause(store);
                    return;

                case "b":
                case "back":
                    return;

                default:
                    if (int.TryParse(choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out int selectedIndex))
                    {
                        if (selectedIndex >= 2 && selectedIndex < efforts.Count + 2)
                        {
                            store.SetReasoningEffort(efforts[selectedIndex - 2]);
                            SaveAndPause(store);
                            return;
                        }

                        if (selectedIndex == efforts.Count + 2)
                        {
                            SetString(
                                "Default thinking effort (minimal, low, medium, high, xhigh)",
                                snapshot.ReasoningEffort,
                                store.SetReasoningEffort,
                                store,
                                "Codex default");
                            return;
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<string> BuildStartWarnings(LocalSettingsSnapshot snapshot)
    {
        if (!snapshot.TelegramEnabled)
        {
            yield return "Telegram polling is disabled.";
        }

        if (snapshot.TelegramEnabled && !snapshot.TelegramTokenConfigured)
        {
            yield return "Telegram polling is enabled, but the bot token is missing.";
        }

        if (snapshot.TelegramEnabled && snapshot.AllowedUserIds.Count == 0)
        {
            yield return "No admin user IDs are configured; only /whoami is reachable before the allowlist is set.";
        }

        if (!snapshot.OpenAiApiKeyConfigured)
        {
            yield return "OpenAI API key is missing; voice transcription will fail until it is configured.";
        }

        if (string.IsNullOrWhiteSpace(snapshot.DataRoot))
        {
            yield return $"Local state is using the default AppData folder at {GetDefaultDataRoot()}. Set a local data root only if you need the files somewhere else.";
        }

        if (snapshot.WorkspaceRoots.Count == 0)
        {
            yield return $"No workspace roots are configured; the app will use {Environment.CurrentDirectory}.";
        }
    }

    private static void WriteStatus(LocalSettingsStore store, CodexModelCatalog modelCatalog)
    {
        LocalSettingsSnapshot snapshot = store.GetSnapshot();
        Console.WriteLine("Incursa.Codex.Telegram");
        Console.WriteLine();
        Console.WriteLine($"Settings: {store.FilePath}");
        Console.WriteLine($"Telegram: {FormatEnabled(snapshot.TelegramEnabled)}, token {FormatConfigured(snapshot.TelegramTokenConfigured)}, admins {snapshot.AllowedUserIds.Count}, chats {snapshot.AllowedChatIds.Count}");
        Console.WriteLine($"OpenAI: key {FormatConfigured(snapshot.OpenAiApiKeyConfigured)}, model {FormatValue(snapshot.OpenAiModel, "whisper-1")}");
        Console.WriteLine($"Codex: executable {FormatValue(ResolveCodexExecutablePath(snapshot), "PATH")}, model {FormatModelValue(snapshot.CodexModel, modelCatalog.Models, "Codex default")}, sandbox {FormatValue(snapshot.Sandbox, "workspace-write")}, approval {FormatValue(snapshot.ApprovalMode, "on-request")}");
        Console.WriteLine($"Codex catalog: {DescribeModelCatalog(modelCatalog)}");
        Console.WriteLine($"Local state: {FormatValue(snapshot.DataRoot, GetDefaultDataRoot())}");
        Console.WriteLine($"Workspace: {FormatStringList(snapshot.WorkspaceRoots, Environment.CurrentDirectory)}");
    }

    private static string DescribeModelCatalog(CodexModelCatalog modelCatalog)
        => modelCatalog.IsLive
            ? $"live discovery with {modelCatalog.Models.Count} model{(modelCatalog.Models.Count == 1 ? string.Empty : "s")}"
            : $"curated fallback with {modelCatalog.Models.Count} example{(modelCatalog.Models.Count == 1 ? string.Empty : "s")}";

    private static string FormatModelValue(string? value, IReadOnlyList<CodexModelVm> models, string fallback)
    {
        CodexModelVm? model = ResolveModel(models, value);
        if (model is not null)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return $"{model.DisplayName} (default)";
            }

            return model.DisplayName;
        }

        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string FormatModelChoice(CodexModelVm model)
    {
        StringBuilder builder = new(model.DisplayName);
        if (!string.Equals(model.DisplayName, model.Id, StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(" [");
            builder.Append(model.Id);
            builder.Append(']');
        }

        string defaultEffort = model.DefaultReasoningEffort.ToString();
        if (!string.Equals(defaultEffort, "None", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append(" (default effort ");
            builder.Append(defaultEffort.ToLowerInvariant());
            builder.Append(')');
        }

        if (model.Hidden)
        {
            builder.Append(" [hidden]");
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> BuildReasoningEffortChoices(CodexModelVm? model)
    {
        if (model is not null && model.SupportedEfforts.Count > 0)
        {
            List<string> efforts = [];
            foreach (string effort in model.SupportedEfforts
                .Select(option => option.ToString().ToLowerInvariant())
                .Where(effort => !string.Equals(effort, "none", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                efforts.Add(effort);
            }

            if (efforts.Count > 0)
            {
                return efforts;
            }
        }

        return ["minimal", "low", "medium", "high", "xhigh"];
    }

    private static CodexModelVm? ResolveModel(IReadOnlyList<CodexModelVm> models, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return models.FirstOrDefault(candidate => candidate.IsDefault) ?? models.FirstOrDefault();
        }

        string normalized = NormalizeModelToken(value);
        return models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.DisplayName, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeModelToken(candidate.Id), normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeModelToken(candidate.DisplayName), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSelectedModel(string? currentModel, CodexModelVm candidate)
    {
        if (string.IsNullOrWhiteSpace(currentModel))
        {
            return candidate.IsDefault;
        }

        string normalized = NormalizeModelToken(currentModel);
        return string.Equals(candidate.Id, currentModel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.DisplayName, currentModel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeModelToken(candidate.Id), normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeModelToken(candidate.DisplayName), normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeModelToken(string value)
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

    private static string? ResolveCodexExecutablePath(LocalSettingsSnapshot snapshot)
        => !string.IsNullOrWhiteSpace(snapshot.CodexPathOverride)
            ? snapshot.CodexPathOverride
            : !string.IsNullOrWhiteSpace(snapshot.TelegramBotCodexExecutablePath)
                ? snapshot.TelegramBotCodexExecutablePath
                : Environment.GetEnvironmentVariable("CODEX_PATH");

    private static void SetSecret(
        string label,
        bool isConfigured,
        Action<string?> assign,
        LocalSettingsStore store)
    {
        string? input = ReadSecret($"{label} ({FormatConfigured(isConfigured)}; blank keeps current; !clear clears): ");
        if (string.IsNullOrEmpty(input))
        {
            return;
        }

        assign(IsClear(input) ? null : input);
        SaveAndPause(store);
    }

    private static void SetString(
        string label,
        string? currentValue,
        Action<string?> assign,
        LocalSettingsStore store,
        string fallback)
    {
        string input = ReadLine($"{label} [{FormatValue(currentValue, fallback)}] (blank keeps current; !clear clears): ");
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        assign(IsClear(input) ? null : input);
        SaveAndPause(store);
    }

    private static void SetPath(
        string label,
        string? currentValue,
        Action<string?> assign,
        LocalSettingsStore store,
        string fallback)
    {
        string input = ReadLine($"{label} [{FormatValue(currentValue, fallback)}] (blank keeps current; !clear clears): ");
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        if (IsClear(input))
        {
            assign(null);
            SaveAndPause(store);
            return;
        }

        try
        {
            assign(NormalizePath(input));
            SaveAndPause(store);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.WriteLine($"Invalid path: {exception.Message}");
            Pause();
        }
    }

    private static void SetPathList(
        string label,
        IReadOnlyList<string> currentValues,
        Action<IEnumerable<string>> assign,
        LocalSettingsStore store)
    {
        Console.WriteLine($"{label}: {FormatStringList(currentValues, Environment.CurrentDirectory)}");
        string input = ReadLine("Enter paths separated by semicolons (blank keeps current; !clear clears): ");
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        if (IsClear(input))
        {
            assign([]);
            SaveAndPause(store);
            return;
        }

        try
        {
            assign(SplitPathList(input).Select(NormalizePath).ToArray());
            SaveAndPause(store);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.WriteLine($"Invalid path: {exception.Message}");
            Pause();
        }
    }

    private static void SetLongList(
        string label,
        IReadOnlyList<long> currentValues,
        Action<IEnumerable<long>> assign,
        LocalSettingsStore store)
    {
        string input = ReadLine($"{label} [{FormatLongList(currentValues)}] (comma/space separated; blank keeps; !clear clears): ");
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        if (IsClear(input))
        {
            assign([]);
            SaveAndPause(store);
            return;
        }

        if (!TryParseLongList(input, out long[] values, out string error))
        {
            Console.WriteLine(error);
            Pause();
            return;
        }

        assign(values);
        SaveAndPause(store);
    }

    private static void SetInt32(
        string label,
        int? currentValue,
        int defaultValue,
        int minValue,
        int maxValue,
        Action<int?> assign,
        LocalSettingsStore store)
    {
        string input = ReadLine($"{label} [{currentValue?.ToString(CultureInfo.InvariantCulture) ?? defaultValue.ToString(CultureInfo.InvariantCulture)}] (blank keeps; !clear restores default): ");
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        if (IsClear(input))
        {
            assign(null);
            SaveAndPause(store);
            return;
        }

        if (!int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            Console.WriteLine($"'{input}' is not a valid integer.");
            Pause();
            return;
        }

        if (value < minValue || value > maxValue)
        {
            Console.WriteLine($"{label} must be between {minValue.ToString(CultureInfo.InvariantCulture)} and {maxValue.ToString(CultureInfo.InvariantCulture)}.");
            Pause();
            return;
        }

        assign(value);
        SaveAndPause(store);
    }

    private static bool TryParseLongList(string input, out long[] values, out string error)
    {
        string[] tokens = input.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<long> parsedValues = new(tokens.Length);
        foreach (string token in tokens)
        {
            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            {
                values = [];
                error = $"'{token}' is not a valid integer ID.";
                return false;
            }

            parsedValues.Add(value);
        }

        values = parsedValues.Distinct().ToArray();
        error = string.Empty;
        return true;
    }

    private static string ReadLine(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine()?.Trim() ?? string.Empty;
    }

    private static string? ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        StringBuilder builder = new();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                return null;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }

    private static bool Confirm(string prompt)
    {
        string input = ReadLine($"{prompt} [y/N]: ");
        return input.Equals("y", StringComparison.OrdinalIgnoreCase)
            || input.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static void SaveAndPause(LocalSettingsStore store)
    {
        try
        {
            store.Save();
            Console.WriteLine($"Saved {store.FilePath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.WriteLine($"Could not save settings: {exception.Message}");
        }

        Pause();
    }

    private static void Pause()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        Console.WriteLine();
        Console.Write("Press Enter to continue.");
        _ = Console.ReadLine();
    }

    private static void ClearScreen()
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
        }
    }

    private static string NormalizeChoice(string value)
        => value.Trim().ToLowerInvariant();

    private static bool IsClear(string value)
        => value.Equals("!clear", StringComparison.OrdinalIgnoreCase)
            || value.Equals("clear", StringComparison.OrdinalIgnoreCase);

    private static bool? NextNullableBool(bool? value)
        => value switch
        {
            null => true,
            true => false,
            false => null,
        };

    private static IReadOnlyList<string> SplitPathList(string value)
        => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizePath(string value)
    {
        string expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded.StartsWith("~", StringComparison.Ordinal))
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
            {
                expanded = expanded.Length == 1
                    ? profile
                    : Path.Combine(profile, expanded[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }

        return Path.GetFullPath(expanded);
    }

    private static string FormatEnabled(bool enabled)
        => enabled ? "enabled" : "disabled";

    private static string FormatConfigured(bool configured)
        => configured ? "set" : "missing";

    private static string FormatNullableBool(bool? value, string fallback)
        => value.HasValue ? value.Value.ToString().ToLowerInvariant() : fallback;

    private static string FormatValue(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string FormatLongList(IReadOnlyList<long> values)
        => values.Count == 0 ? "none" : string.Join(", ", values);

    private static string FormatStringList(IReadOnlyList<string> values, string fallback)
        => values.Count == 0 ? fallback : string.Join("; ", values);

    private static string FormatSeconds(int seconds)
        => seconds == 1 ? "1 second" : $"{seconds.ToString(CultureInfo.InvariantCulture)} seconds";

    private static string GetDefaultDataRoot()
    {
        string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".incursa");
        }

        return Path.Combine(baseDirectory, "Incursa", "CodexTelegram");
    }
}
