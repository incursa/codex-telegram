using System.Globalization;
using System.Text;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Exceptions;

namespace Incursa.Codex.Telegram.Configuration;

internal enum BootstrapMenuResult
{
    Start,
    Quit,
}

internal static class InteractiveBootstrapMenu
{
    private static readonly TimeSpan TelegramUserCaptureTimeout = TimeSpan.FromMinutes(2);

    private readonly record struct BootstrapReadinessCheck(string Name, bool Passed, string Detail);

    private sealed record TelegramTokenPromptResult(string Token, TelegramBotIdentity? Identity);

    public static BootstrapMenuResult Run(LocalSettingsStore store)
        => Run(store, CodexModelDiscovery.CreateFallbackCatalog());

    public static Task<LocalSettingsStore?> RunFirstRunSetupAsync(LocalSettingsStore store, CancellationToken cancellationToken)
        => RunFirstRunSetupAsync(store, new TelegramSetupClient(), cancellationToken);

    internal static async Task<LocalSettingsStore?> RunFirstRunSetupAsync(
        LocalSettingsStore initialStore,
        ITelegramSetupClient telegramSetupClient,
        CancellationToken cancellationToken)
    {
        ClearScreen();
        Console.WriteLine("First-Time Setup");
        Console.WriteLine();
        Console.WriteLine("No local settings file was found, so this wizard will create one before the normal menu opens.");
        Console.WriteLine("The default location is the app folder so command-line launches from another directory still use the same settings.");
        WriteCurrentDirectorySettingsNotice(initialStore.FilePath);
        Console.WriteLine();

        LocalSettingsStore? store = PromptForSettingsStore(initialStore);
        if (store is null)
        {
            return null;
        }

        if (!PromptForWorkspaceMode(store))
        {
            Console.WriteLine("Setup cancelled. Any settings already saved remain in place; run the app again to retry.");
            Pause();
            return null;
        }

        if (!PromptForDataRoot(store))
        {
            Console.WriteLine("Setup cancelled. Any settings already saved remain in place; run the app again to retry.");
            Pause();
            return null;
        }
        if (store.GetSnapshot().Mode == CodexTelegramMode.GeneralPurpose)
        {
            PromptForWorkspaceRoots(store);
            PromptForDefaultWorkingDirectory(store);
        }

        TelegramTokenPromptResult? tokenResult = await PromptForTelegramTokenAsync(store, telegramSetupClient, cancellationToken).ConfigureAwait(false);
        if (tokenResult is null)
        {
            Console.WriteLine("Setup cancelled. Any settings already saved remain in place; run the app again to retry.");
            Pause();
            return null;
        }

        string telegramToken = tokenResult.Token;
        await PromptForTelegramAdminAsync(store, telegramToken, telegramSetupClient, cancellationToken).ConfigureAwait(false);
        PromptForOpenAiTranscription(store);
        await PromptForOptionalTelegramProfileSetupAsync(
            telegramToken,
            store.GetSnapshot().Mode,
            store.GetSnapshot().RepositoryDisplayLabel,
            telegramSetupClient,
            cancellationToken).ConfigureAwait(false);
        if (!SaveSettings(store))
        {
            Console.WriteLine("Setup cannot continue until the settings file can be written beside the executable.");
            Pause();
            return null;
        }

        Console.WriteLine();
        CodexModelCatalog codexCatalog = await CodexModelDiscovery.DiscoverAsync(store.GetSnapshot(), cancellationToken).ConfigureAwait(false);
        WriteReadinessSummary(store, tokenResult.Identity, codexCatalog);
        Console.WriteLine();
        Console.WriteLine("First-time setup is saved. The normal setup menu will open next so you can review or start the bot.");
        Pause();
        return store;
    }

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
            Console.WriteLine("6. Workspace mode and repository");
            Console.WriteLine("7. Show settings path");
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
                case "mode":
                case "repository":
                    ConfigureWorkspaceMode(store);
                    break;

                case "7":
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
        Console.WriteLine($"The menu writes {LocalSettingsStore.FileName} in the app folder by default.");
        Console.WriteLine("The startup screen shows the resolved settings file, local state root, and common model pickers before launch.");
        Console.WriteLine("When Codex is reachable, the menu queries it for live model lists and falls back to curated examples if discovery fails.");
        Console.WriteLine("Environment variables and command-line configuration still override that file.");
    }

    private static bool PromptForWorkspaceMode(LocalSettingsStore store)
    {
        while (true)
        {
            LocalSettingsSnapshot snapshot = store.GetSnapshot();
            Console.WriteLine("Workspace mode");
            Console.WriteLine();
            Console.WriteLine("1. General-purpose bot: browse and select projects from several local repositories.");
            Console.WriteLine("2. Repository-only bot: keep this bot bound to one repository.");
            Console.WriteLine($"Current: {DescribeWorkspaceMode(snapshot.Mode)}");
            if (!string.IsNullOrWhiteSpace(snapshot.RepositoryRoot))
            {
                Console.WriteLine($"Repository: {snapshot.RepositoryDisplayLabel ?? snapshot.RepositoryRoot} ({snapshot.RepositoryRoot})");
            }

            string choice = NormalizeChoice(ReadLine("Choose 1 or 2 (!skip keeps the general-purpose default; !quit cancels): "));
            if (IsQuit(choice))
            {
                return false;
            }

            if (choice is "" or "!skip" or "skip")
            {
                if (string.IsNullOrWhiteSpace(choice))
                {
                    Console.WriteLine("Choose a mode, or type !skip to use the general-purpose mode.");
                    continue;
                }

                ExplainModeChange(snapshot, CodexTelegramMode.GeneralPurpose, null);
                store.SetMode(CodexTelegramMode.GeneralPurpose);
                if (!SaveSettings(store))
                {
                    Console.WriteLine("The mode could not be saved. Fix the settings-file permission and retry, or type !quit to cancel.");
                    continue;
                }

                Console.WriteLine("Skipped repository mode; the bot will use the general-purpose workspace flow.");
                return true;
            }

            if (choice is "1" or "general" or "generalpurpose")
            {
                ExplainModeChange(snapshot, CodexTelegramMode.GeneralPurpose, null);
                store.SetMode(CodexTelegramMode.GeneralPurpose);
                store.SetRepositoryRoot(null);
                store.SetRepositoryDisplayLabel(null);
                if (!SaveSettings(store))
                {
                    Console.WriteLine("The mode could not be saved. Fix the settings-file permission and retry, or type !quit to cancel.");
                    continue;
                }

                Console.WriteLine("General-purpose mode selected. You can add one or more workspace roots later.");
                return true;
            }

            if (choice is not ("2" or "repository" or "repo" or "repositoryonly"))
            {
                Console.WriteLine("That is not a workspace mode. Enter 1, 2, !skip, or !quit.");
                continue;
            }

            if (!TryPromptForRepository(store))
            {
                return false;
            }

            return true;
        }
    }

    private static bool TryPromptForRepository(LocalSettingsStore store)
    {
        LocalSettingsSnapshot snapshot = store.GetSnapshot();
        while (true)
        {
            string input = ReadLine("Repository root (existing directory; !skip uses general-purpose mode; !quit cancels): ");
            if (IsQuit(input))
            {
                return false;
            }

            if (input.Equals("!skip", StringComparison.OrdinalIgnoreCase)
                || input.Equals("skip", StringComparison.OrdinalIgnoreCase))
            {
                ExplainModeChange(snapshot, CodexTelegramMode.GeneralPurpose, null);
                Console.WriteLine("Repository mode skipped; the bot will remain in general-purpose mode.");
                store.SetMode(CodexTelegramMode.GeneralPurpose);
                store.SetRepositoryRoot(null);
                store.SetRepositoryDisplayLabel(null);
                SaveSettings(store);
                return true;
            }

            if (!TryValidateExistingDirectory(input, "Repository root", out string root, out string error))
            {
                Console.WriteLine(error);
                Console.WriteLine("Try another existing repository directory, !skip, or !quit.");
                continue;
            }

            ExplainModeChange(snapshot, CodexTelegramMode.Repository, root);
            store.SetMode(CodexTelegramMode.Repository);
            store.SetRepositoryRoot(root);
            string label = ReadLine($"Repository display label [{Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}] (blank keeps the name; !clear clears it): ");
            if (IsQuit(label))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(label) && !IsClear(label))
            {
                if (!TryNormalizeDisplayLabel(label, out string normalizedLabel, out string labelError))
                {
                    Console.WriteLine(labelError);
                    continue;
                }

                store.SetRepositoryDisplayLabel(normalizedLabel);
            }
            else if (IsClear(label))
            {
                store.SetRepositoryDisplayLabel(null);
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.RepositoryDisplayLabel))
            {
                store.SetRepositoryDisplayLabel(snapshot.RepositoryDisplayLabel);
            }

            // Repository mode is an explicit boundary. Keep the legacy
            // workspace list and working-directory fallback from widening it.
            store.SetWorkspaceRoots([root]);
            store.SetWorkingDirectory(root);

            if (!SaveSettings(store))
            {
                Console.WriteLine("Repository mode was not fully saved. Retry after fixing the settings-file permission.");
                return false;
            }

            Console.WriteLine($"Repository mode selected: {root}");
            return true;
        }
    }

    private static void ConfigureWorkspaceMode(LocalSettingsStore store)
    {
        while (true)
        {
            ClearScreen();
            LocalSettingsSnapshot snapshot = store.GetSnapshot();
            Console.WriteLine("Workspace Mode And Repository");
            Console.WriteLine();
            Console.WriteLine($"Mode: {DescribeWorkspaceMode(snapshot.Mode)}");
            Console.WriteLine($"Repository root: {FormatValue(snapshot.RepositoryRoot, "not configured")}");
            Console.WriteLine($"Repository label: {FormatValue(snapshot.RepositoryDisplayLabel, "folder name")}");
            Console.WriteLine($"Data root: {FormatValue(snapshot.DataRoot, GetDefaultDataRoot())}");
            Console.WriteLine();
            Console.WriteLine("1. Choose workspace mode");
            Console.WriteLine("2. Set repository root (existing directory)");
            Console.WriteLine("3. Set repository display label");
            Console.WriteLine("4. Set local data root");
            Console.WriteLine("B. Back");
            Console.WriteLine();

            switch (NormalizeChoice(ReadLine("Select: ")))
            {
                case "1":
                    _ = PromptForWorkspaceMode(store);
                    break;

                case "2":
                    SetRepositoryRoot(store);
                    break;

                case "3":
                    SetString(
                        "Repository display label",
                        snapshot.RepositoryDisplayLabel,
                        store.SetRepositoryDisplayLabel,
                        store,
                        "folder name");
                    break;

                case "4":
                    SetDataRoot(store);
                    break;

                case "b":
                case "back":
                    return;
            }
        }
    }

    private static void SetRepositoryRoot(LocalSettingsStore store)
    {
        LocalSettingsSnapshot snapshot = store.GetSnapshot();
        string input = ReadLine($"Repository root [{FormatValue(snapshot.RepositoryRoot, "not configured")}] (existing directory; blank keeps; !clear clears): ");
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        if (IsClear(input))
        {
            ExplainModeChange(snapshot, CodexTelegramMode.GeneralPurpose, null);
            store.SetRepositoryRoot(null);
            store.SetMode(CodexTelegramMode.GeneralPurpose);
            SaveAndPause(store);
            return;
        }

        if (!TryValidateExistingDirectory(input, "Repository root", out string root, out string error))
        {
            Console.WriteLine(error);
            Pause();
            return;
        }

        ExplainModeChange(snapshot, CodexTelegramMode.Repository, root);
        store.SetRepositoryRoot(root);
        store.SetMode(CodexTelegramMode.Repository);
        store.SetWorkspaceRoots([root]);
        store.SetWorkingDirectory(root);
        SaveAndPause(store);
    }

    private static void ExplainModeChange(
        LocalSettingsSnapshot snapshot,
        CodexTelegramMode targetMode,
        string? repositoryRoot)
    {
        if (snapshot.Mode == targetMode
            && (targetMode != CodexTelegramMode.Repository
                || string.Equals(snapshot.RepositoryRoot, repositoryRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"Changing workspace mode to {DescribeWorkspaceMode(targetMode)} is an explicit administrative action.");
        Console.WriteLine("Existing sessions and queued work keep their recorded repository/session identity; they are not rebound or erased.");
        if (targetMode == CodexTelegramMode.Repository)
        {
            Console.WriteLine($"New sessions will use {repositoryRoot}; sessions recorded for another root remain stored but are not selectable from this bot.");
        }
        else
        {
            Console.WriteLine("General-purpose mode restores project selection; previously recorded sessions remain available when their projects are configured.");
        }
    }

    private static void SetDataRoot(LocalSettingsStore store)
    {
        LocalSettingsSnapshot snapshot = store.GetSnapshot();
        string input = ReadLine($"Local data root [{FormatValue(snapshot.DataRoot, GetDefaultDataRoot())}] (blank keeps; !clear restores default): ");
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        if (IsClear(input))
        {
            store.SetDataRoot(null);
            SaveAndPause(store);
            return;
        }

        if (!TryValidateDataRoot(input, out string dataRoot, out string error))
        {
            Console.WriteLine(error);
            Pause();
            return;
        }

        store.SetDataRoot(dataRoot);
        SaveAndPause(store);
    }

    private static LocalSettingsStore? PromptForSettingsStore(LocalSettingsStore initialStore)
    {
        Console.WriteLine($"Settings file: {initialStore.FilePath}");
        Console.WriteLine();
        string input = ReadLine("Press Enter to save settings beside the executable, or type !quit to cancel: ");
        if (IsQuit(input))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(input))
        {
            Console.WriteLine("Custom settings locations are not supported by the first-run wizard because normal command-line launches resolve settings beside the executable.");
            return PromptForSettingsStore(initialStore);
        }

        return initialStore;
    }

    private static void WriteCurrentDirectorySettingsNotice(string defaultSettingsPath)
    {
        string currentDirectorySettingsPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, LocalSettingsStore.FileName));
        if (File.Exists(currentDirectorySettingsPath)
            && !string.Equals(currentDirectorySettingsPath, defaultSettingsPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            Console.WriteLine($"Note: {currentDirectorySettingsPath} exists, but this app now uses {defaultSettingsPath} by default.");
            Console.WriteLine("Move or copy the file beside the executable if that is the settings file you intended to use.");
        }
    }

    private static async Task<TelegramTokenPromptResult?> PromptForTelegramTokenAsync(
        LocalSettingsStore store,
        ITelegramSetupClient telegramSetupClient,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("Telegram Bot Token");
        Console.WriteLine();
        Console.WriteLine("If you do not have a token yet:");
        Console.WriteLine("1. Open Telegram and chat with @BotFather.");
        Console.WriteLine("2. Send /newbot.");
        Console.WriteLine("3. Choose the bot display name and a username ending in bot.");
        Console.WriteLine("4. Copy the token BotFather returns.");
        Console.WriteLine();

        while (true)
        {
            string? input = ReadSecret("Paste Telegram bot token (!quit cancels): ");
            if (input is null || IsQuit(input))
            {
                return null;
            }

            if (!TelegramBotToken.TryNormalize(input, out string token, out string tokenError))
            {
                Console.WriteLine(tokenError);
                continue;
            }

            Console.WriteLine("Validating token with Telegram...");
            try
            {
                TelegramBotIdentity bot = await telegramSetupClient.ValidateBotTokenAsync(token, cancellationToken).ConfigureAwait(false);
                store.SetTelegramToken(token);
                store.SetTelegramEnabled(true);
                SaveSettings(store);
                Console.WriteLine($"Validated @{bot.Username ?? bot.Id.ToString(CultureInfo.InvariantCulture)} ({bot.DisplayName}).");
                WriteBotFatherHint(bot);
                return new TelegramTokenPromptResult(token, bot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is RequestException or HttpRequestException or ArgumentException or InvalidOperationException)
            {
                Console.WriteLine($"Telegram could not validate that token: {SanitizeTelegramError(exception.Message, token)}");
                if (Confirm("Save this token anyway?"))
                {
                    store.SetTelegramToken(token);
                    store.SetTelegramEnabled(true);
                    SaveSettings(store);
                    Console.WriteLine("Token saved without validation. Retry validation from the menu before treating Telegram as ready.");
                    return new TelegramTokenPromptResult(token, null);
                }

                Console.WriteLine("Token was not saved. Try again, or type !quit to cancel setup.");
            }
        }
    }

    private static async Task PromptForTelegramAdminAsync(
        LocalSettingsStore store,
        string telegramToken,
        ITelegramSetupClient telegramSetupClient,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("Admin User ID");
        Console.WriteLine();
        Console.WriteLine("BotFather cannot tell you your personal Telegram user ID, and most Telegram clients do not show it directly.");
        Console.WriteLine("The easiest setup path is to send one private message containing a terminal challenge to your new bot while this wizard waits.");
        Console.WriteLine();

        if (Confirm("Capture your user ID automatically now?"))
        {
            string setupChallenge = TelegramSetupClient.CreateSetupChallenge();
            Console.WriteLine("Open a private chat with the bot and send this exact setup code:");
            Console.WriteLine();
            Console.WriteLine($"    {setupChallenge}");
            Console.WriteLine();
            Console.WriteLine($"Waiting up to {TelegramUserCaptureTimeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes for a private message containing that code...");
            try
            {
                TelegramSetupUser? user = await telegramSetupClient.WaitForPrivateUserMessageAsync(telegramToken, setupChallenge, TelegramUserCaptureTimeout, cancellationToken).ConfigureAwait(false);
                if (user is not null)
                {
                    Console.WriteLine($"Captured {user.DisplayName}: {user.UserId.ToString(CultureInfo.InvariantCulture)} from a message containing the setup code.");
                    if (Confirm("Save this user as the Telegram admin?"))
                    {
                        store.SetAllowedUserIds([user.UserId]);
                        SaveSettings(store);
                        return;
                    }
                }
                else
                {
                    Console.WriteLine("No private message containing the setup code was received before the setup wait expired.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is RequestException or HttpRequestException or ArgumentException or InvalidOperationException)
            {
                Console.WriteLine($"Could not capture the user ID automatically: {SanitizeTelegramError(exception.Message, telegramToken)}");
            }
        }

        Console.WriteLine("You can still finish setup by entering the numeric user ID manually.");
        SetLongList(
            "Admin user IDs",
            store.GetSnapshot().AllowedUserIds,
            store.SetAllowedUserIds,
            store);
    }

    private static void PromptForOpenAiTranscription(LocalSettingsStore store)
    {
        Console.WriteLine();
        Console.WriteLine("Voice Transcription");
        Console.WriteLine();
        Console.WriteLine("Voice notes are optional. If enabled, Telegram audio is transcribed with OpenAI before text is sent to Codex.");
        if (!Confirm("Enable voice-note transcription now?"))
        {
            return;
        }

        string? input = ReadSecret("OpenAI API key (blank skips for now): ");
        if (!string.IsNullOrWhiteSpace(input) && !IsQuit(input))
        {
            store.SetOpenAiApiKey(input);
        }

        store.SetOpenAiFfmpegPath("ffmpeg");
        SaveSettings(store);
        Console.WriteLine("ffmpeg is configured as 'ffmpeg'. Install it or update OpenAI:FfmpegPath later if voice messages need transcoding.");
    }

    private static void PromptForWorkspaceRoots(LocalSettingsStore store)
    {
        Console.WriteLine();
        Console.WriteLine("Workspaces");
        Console.WriteLine();
        Console.WriteLine("Workspace roots are the local folders this bot may show and use for project selection.");
        Console.WriteLine("Use a parent source directory if your repositories live under one folder, such as C:\\src, ~/src, or /Users/you/src.");
        Console.WriteLine("Use semicolons for multiple roots.");
        while (true)
        {
            string input = ReadLine("Workspace roots (semicolon-separated; !skip configures later): ");
            if (IsQuit(input))
            {
                Console.WriteLine("Workspace roots skipped; configure them later from Workspaces.");
                return;
            }

            if (input.Equals("!skip", StringComparison.OrdinalIgnoreCase) || input.Equals("skip", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Workspace roots skipped; configure them later from Workspaces.");
                return;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("Enter at least one workspace root, or type !skip to configure it later.");
                continue;
            }

            try
            {
                string[] candidates = SplitPathList(input).ToArray();
                List<string> roots = [];
                List<string> errors = [];
                foreach (string candidate in candidates)
                {
                    if (TryValidateExistingDirectory(candidate, "Workspace root", out string normalizedRoot, out string validationError))
                    {
                        roots.Add(normalizedRoot);
                    }
                    else
                    {
                        errors.Add(validationError);
                    }
                }

                if (roots.Count == 0)
                {
                    Console.WriteLine("Enter at least one existing workspace root, or type !skip to configure it later.");
                    continue;
                }

                if (errors.Count > 0)
                {
                    foreach (string validationError in errors)
                    {
                        Console.WriteLine(validationError);
                    }

                    Console.WriteLine("Fix the workspace roots, or type !skip to configure them later.");
                    continue;
                }

                store.SetWorkspaceRoots(roots);
                SaveSettings(store);
                return;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Console.WriteLine($"Invalid path: {exception.Message}");
            }
        }
    }

    private static bool PromptForDataRoot(LocalSettingsStore store)
    {
        LocalSettingsSnapshot snapshot = store.GetSnapshot();
        string defaultRoot = GetDefaultDataRoot();
        Console.WriteLine();
        Console.WriteLine("Local instance storage");
        Console.WriteLine("This folder stores conversation bindings, sessions, queued work, attachments, and diagnostics for this bot instance.");
        Console.WriteLine("Use a different folder (or InstanceId) for every independently running bot process.");
        while (true)
        {
            string input = ReadLine($"Local data root [{FormatValue(snapshot.DataRoot, defaultRoot)}] (!skip keeps the default): ");
            if (IsQuit(input))
            {
                return false;
            }

            if (input.Equals("!skip", StringComparison.OrdinalIgnoreCase) || input.Equals("skip", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Local data root skipped; the default AppData location will be used.");
                return true;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                store.SetDataRoot(null);
                SaveSettings(store);
                return true;
            }

            if (!TryValidateDataRoot(input, out string normalized, out string error))
            {
                Console.WriteLine(error);
                continue;
            }

            store.SetDataRoot(normalized);
            SaveSettings(store);
            return true;
        }
    }

    private static void PromptForDefaultWorkingDirectory(LocalSettingsStore store)
    {
        Console.WriteLine();
        Console.WriteLine("Default Working Directory");
        Console.WriteLine();
        Console.WriteLine("This is the fallback directory Codex uses before you select a project in Telegram.");
        string fallback = store.GetSnapshot().WorkspaceRoots.FirstOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string input = ReadLine($"Default working directory [{fallback}] (!skip configures later): ");
        if (IsQuit(input) || input.Equals("!skip", StringComparison.OrdinalIgnoreCase) || input.Equals("skip", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Default working directory skipped; configure it later from Workspaces.");
            return;
        }

        try
        {
            store.SetWorkingDirectory(string.IsNullOrWhiteSpace(input) ? fallback : NormalizePath(input));
            SaveSettings(store);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.WriteLine($"Invalid path: {exception.Message}");
        }
    }

    private static async Task PromptForOptionalTelegramProfileSetupAsync(
        string telegramToken,
        CodexTelegramMode mode,
        string? repositoryDisplayLabel,
        ITelegramSetupClient telegramSetupClient,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("Telegram command menu and profile");
        Console.WriteLine("The app can optionally synchronize its built-in command menu and Telegram menu button.");
        Console.WriteLine("This is a remote Telegram change and is independent of the local settings file.");

        if (telegramSetupClient is not ITelegramProfileSetupClient profileSetupClient)
        {
            Console.WriteLine("Optional command/profile setup is unavailable on this setup client; use BotFather manually if needed.");
            return;
        }

        if (!Confirm("Apply the app-owned command menu and menu button now?"))
        {
            Console.WriteLine("Skipped command/profile setup. You can configure Telegram commands manually later.");
            return;
        }

        string label = string.IsNullOrWhiteSpace(repositoryDisplayLabel) ? "your workspace" : repositoryDisplayLabel.Trim();
        string suggestedName = mode == CodexTelegramMode.Repository
            ? LimitTelegramProfileText($"Codex Telegram · {label}", 64)
            : "Codex Telegram";
        string suggestedDescription = LimitTelegramProfileText(
            mode == CodexTelegramMode.Repository
                ? $"A private Codex workspace assistant for {label}."
                : "A private Codex workspace assistant for your configured projects.",
            512);
        string suggestedShortDescription = LimitTelegramProfileText(
            mode == CodexTelegramMode.Repository ? $"Codex assistant for {label}" : "Codex workspace assistant",
            120);
        Console.WriteLine("Optional public profile text (nothing is changed unless you approve each suggestion):");
        string? name = PromptProfileText("Display name", suggestedName, 64);
        string? description = PromptProfileText("Description", suggestedDescription, 512);
        string? shortDescription = PromptProfileText("Short description", suggestedShortDescription, 120);
        string? profilePhotoPath = PromptProfilePhotoPath();

        TelegramProfileSetupResult result = await new TelegramProfileSetup(profileSetupClient)
            .ApplyAsync(
                telegramToken,
                new TelegramProfileSetupOptions
                {
                    // The mode was saved before the Telegram step, so the
                    // picker never advertises project switching for a
                    // repository-only bot.
                    Commands = TelegramCommandCatalog.CreateDefaultCommands(mode),
                    Name = name,
                    Description = description,
                    ShortDescription = shortDescription,
                    ProfilePhotoPath = profilePhotoPath,
                },
                cancellationToken)
            .ConfigureAwait(false);
        foreach (TelegramSetupOperationResult operation in result.Operations)
        {
            Console.WriteLine(operation.Status switch
            {
                TelegramSetupOperationStatus.Succeeded => $"{operation.Operation}: applied.",
                TelegramSetupOperationStatus.Skipped => $"{operation.Operation}: skipped ({operation.FailureReason}).",
                _ => $"{operation.Operation}: failed ({operation.FailureReason}).",
            });
            if (!string.IsNullOrWhiteSpace(operation.BotFatherFallback))
            {
                Console.WriteLine(operation.BotFatherFallback);
            }
        }
    }

    private static string? PromptProfileText(string label, string suggestion, int maxLength)
    {
        string input = ReadLine($"{label} [{suggestion}] (!skip leaves it unchanged): ");
        if (IsQuit(input) || input.Equals("!skip", StringComparison.OrdinalIgnoreCase) || input.Equals("skip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string value = string.IsNullOrWhiteSpace(input) ? suggestion : input.Trim();
        if (value.Length > maxLength)
        {
            Console.WriteLine($"{label} is limited to {maxLength} characters; it was skipped. You can set it manually in BotFather.");
            return null;
        }

        return value;
    }

    private static string? PromptProfilePhotoPath()
    {
        string packagedAvatar = Path.Combine(AppContext.BaseDirectory, "Assets", "codex-telegram-logo.png");
        if (File.Exists(packagedAvatar)
            && Confirm($"Use the packaged Incursa Codex Telegram avatar ({packagedAvatar})?"))
        {
            return packagedAvatar;
        }

        string input = ReadLine("Optional custom avatar path (.jpg/.png; !skip leaves the current avatar unchanged): ");
        if (IsQuit(input) || input.Equals("!skip", StringComparison.OrdinalIgnoreCase) || input.Equals("skip", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string path;
        try
        {
            path = NormalizePath(input);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.WriteLine($"Avatar path is invalid ({exception.Message}); the avatar step was skipped.");
            return null;
        }
        if (!File.Exists(path))
        {
            Console.WriteLine("Avatar path does not exist; the avatar step was skipped. Upload a suitable image with /setuserpic in @BotFather later.");
            return null;
        }

        return path;
    }

    private static string LimitTelegramProfileText(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd();

    private static bool TryNormalizeDisplayLabel(string input, out string normalized, out string error)
    {
        normalized = input.Trim();
        if (normalized.Length == 0)
        {
            error = "The repository display label cannot be empty when supplied.";
            return false;
        }

        if (normalized.Length > 80 || normalized.Any(char.IsControl))
        {
            error = "The repository display label must be at most 80 characters and cannot contain control characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string SanitizeTelegramError(string? message, string token)
        => string.IsNullOrWhiteSpace(message)
            ? "Telegram rejected the request."
            : message.Replace(token, "[redacted]", StringComparison.Ordinal);

    private static bool TryValidateExistingDirectory(
        string input,
        string label,
        out string normalized,
        out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = $"{label} is required.";
            return false;
        }

        try
        {
            normalized = NormalizePath(input);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            error = $"{label} is not a valid path: {exception.Message}";
            return false;
        }

        if (!Directory.Exists(normalized))
        {
            error = $"{label} '{normalized}' does not name an existing directory.";
            return false;
        }

        try
        {
            using IEnumerator<FileSystemInfo> entries = new DirectoryInfo(normalized).EnumerateFileSystemInfos().GetEnumerator();
            _ = entries.MoveNext();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            error = $"{label} '{normalized}' cannot be accessed: {exception.Message}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateDataRoot(string input, out string normalized, out string error)
    {
        normalized = string.Empty;
        try
        {
            normalized = NormalizePath(input);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            error = $"Local data root is not a valid path: {exception.Message}";
            return false;
        }

        if (Directory.Exists(normalized))
        {
            return TryValidateExistingDirectory(normalized, "Local data root", out normalized, out error);
        }

        if (File.Exists(normalized))
        {
            error = $"Local data root '{normalized}' is a file; choose a directory path.";
            return false;
        }

        // The runtime creates a missing data-root directory on first use. Do
        // not create it from the menu; a syntactically valid, non-file path is
        // enough for configuration and keeps first-run setup side-effect free.
        error = string.Empty;
        return true;
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
            Console.WriteLine("5. Reapply command menu and optional profile setup");
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

                case "5":
                    ReapplyTelegramProfileSetup(store);
                    break;

                case "b":
                case "back":
                    return;
            }
        }
    }

    private static void ReapplyTelegramProfileSetup(LocalSettingsStore store)
    {
        string? token = store.GetTelegramTokenForSetup();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("A Telegram bot token must be saved before command/profile setup can be reapplied.");
            Pause();
            return;
        }

        try
        {
            PromptForOptionalTelegramProfileSetupAsync(
                token,
                store.GetSnapshot().Mode,
                store.GetSnapshot().RepositoryDisplayLabel,
                new TelegramSetupClient(),
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is RequestException or HttpRequestException or ArgumentException or InvalidOperationException)
        {
            Console.WriteLine($"Telegram command/profile repair failed: {SanitizeTelegramError(exception.Message, token)}");
        }

        Pause();
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
            Console.WriteLine($"Plan mode thinking: {FormatValue(snapshot.PlanModeReasoningEffort, "Codex default")}");
            Console.WriteLine($"Sandbox: {FormatValue(snapshot.Sandbox, "workspace-write")}");
            Console.WriteLine($"Approval mode: {FormatValue(snapshot.ApprovalMode, "on-request")}");
            Console.WriteLine($"Network access: {FormatNullableBool(snapshot.NetworkAccessEnabled, "Codex default")}");
            Console.WriteLine($"Model catalog: {DescribeModelCatalog(modelCatalog)}");
            Console.WriteLine();
            Console.WriteLine("1. Set Codex executable path");
            Console.WriteLine("2. Toggle initialize on start");
            Console.WriteLine("3. Pick default model");
            Console.WriteLine("4. Pick default thinking effort");
            Console.WriteLine("5. Pick plan-mode thinking effort");
            Console.WriteLine("6. Set sandbox");
            Console.WriteLine("7. Set approval mode");
            Console.WriteLine("8. Cycle network access default");
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
                    PickPlanModeReasoningEffort(snapshot, store, modelCatalog);
                    break;

                case "6":
                    SetString(
                        "Sandbox (read-only, workspace-write, danger-full-access)",
                        snapshot.Sandbox,
                        store.SetSandbox,
                        store,
                        "workspace-write");
                    break;

                case "7":
                    SetString(
                        "Approval mode (never, on-request, on-failure, untrusted)",
                        snapshot.ApprovalMode,
                        store.SetApprovalMode,
                        store,
                        "on-request");
                    break;

                case "8":
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
                    SetDataRoot(store);
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
        Console.WriteLine($"Bot identity: {FormatBotIdentity(null, snapshot.TelegramTokenConfigured)}");
        Console.WriteLine($"Workspace mode: {DescribeWorkspaceMode(snapshot.Mode)}");
        Console.WriteLine($"Repository: {FormatValue(snapshot.RepositoryDisplayLabel, snapshot.RepositoryRoot ?? "not configured")}" +
            (string.IsNullOrWhiteSpace(snapshot.RepositoryRoot) ? string.Empty : $" ({snapshot.RepositoryRoot})"));
        Console.WriteLine($"Telegram polling: {FormatEnabled(snapshot.TelegramEnabled)}, token {FormatConfigured(snapshot.TelegramTokenConfigured)}, admins {snapshot.AllowedUserIds.Count}, chats {snapshot.AllowedChatIds.Count}");
        Console.WriteLine($"OpenAI: key {FormatConfigured(snapshot.OpenAiApiKeyConfigured)}, model {FormatValue(snapshot.OpenAiModel, "whisper-1")}");
        Console.WriteLine($"Codex: executable {FormatValue(ResolveCodexExecutablePath(snapshot), "PATH")}, model {FormatModelValue(snapshot.CodexModel, modelCatalog.Models, "Codex default")}, thinking {FormatValue(snapshot.ReasoningEffort, "Codex default")}, plan thinking {FormatValue(snapshot.PlanModeReasoningEffort, "Codex default")}");
        Console.WriteLine($"Codex model catalog: {DescribeModelCatalog(modelCatalog)}");
        Console.WriteLine("Readiness checks:");
        foreach (BootstrapReadinessCheck check in BuildReadinessChecks(snapshot, null, modelCatalog))
        {
            Console.WriteLine($"- {(check.Passed ? "PASS" : "CHECK")}: {check.Name} - {check.Detail}");
        }
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

    private static void PickPlanModeReasoningEffort(LocalSettingsSnapshot snapshot, LocalSettingsStore store, CodexModelCatalog modelCatalog)
    {
        IReadOnlyList<CodexModelVm> models = modelCatalog.Models;
        CodexModelVm? selectedModel = ResolveModel(models, snapshot.CodexModel);
        IReadOnlyList<string> efforts = BuildReasoningEffortChoices(selectedModel);
        bool hasModelSpecificEfforts = selectedModel is not null
            && selectedModel.SupportedEfforts.Any(option => !string.Equals(option.ToString(), "None", StringComparison.OrdinalIgnoreCase));

        while (true)
        {
            ClearScreen();
            Console.WriteLine("Codex Plan Mode Thinking Effort");
            Console.WriteLine();
            Console.WriteLine($"Current: {FormatValue(snapshot.PlanModeReasoningEffort, "Codex default")}");
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
                if (!string.IsNullOrWhiteSpace(snapshot.PlanModeReasoningEffort) && string.Equals(snapshot.PlanModeReasoningEffort, effort, StringComparison.OrdinalIgnoreCase))
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
                    store.SetPlanModeReasoningEffort(null);
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
                            store.SetPlanModeReasoningEffort(efforts[selectedIndex - 2]);
                            SaveAndPause(store);
                            return;
                        }

                        if (selectedIndex == efforts.Count + 2)
                        {
                            SetString(
                                "Plan mode thinking effort (minimal, low, medium, high, xhigh)",
                                snapshot.PlanModeReasoningEffort,
                                store.SetPlanModeReasoningEffort,
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

        if (snapshot.Mode == CodexTelegramMode.Repository)
        {
            if (!TryValidateExistingDirectory(snapshot.RepositoryRoot ?? string.Empty, "Repository root", out _, out string repositoryError))
            {
                yield return repositoryError;
            }
        }

        if (!string.IsNullOrWhiteSpace(snapshot.DataRoot)
            && !TryValidateDataRoot(snapshot.DataRoot, out _, out string dataRootError))
        {
            yield return dataRootError;
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
        Console.WriteLine($"Workspace mode: {DescribeWorkspaceMode(snapshot.Mode)}");
        Console.WriteLine($"Repository: {FormatValue(snapshot.RepositoryDisplayLabel, snapshot.RepositoryRoot ?? "not configured")}" +
            (string.IsNullOrWhiteSpace(snapshot.RepositoryRoot) ? string.Empty : $" ({snapshot.RepositoryRoot})"));
        Console.WriteLine($"Workspace: {FormatStringList(snapshot.WorkspaceRoots, Environment.CurrentDirectory)}");
    }

    private static void WriteReadinessSummary(
        LocalSettingsStore store,
        TelegramBotIdentity? botIdentity,
        CodexModelCatalog? codexCatalog = null)
    {
        LocalSettingsSnapshot snapshot = store.GetSnapshot();
        Console.WriteLine("Readiness summary:");
        Console.WriteLine($"Settings file: {store.FilePath}");
        Console.WriteLine($"Bot identity: {FormatBotIdentity(botIdentity, snapshot.TelegramTokenConfigured)}");
        Console.WriteLine($"Mode: {DescribeWorkspaceMode(snapshot.Mode)}");
        Console.WriteLine($"Repository root: {FormatValue(snapshot.RepositoryRoot, "not configured")}");
        Console.WriteLine($"Repository label: {FormatValue(snapshot.RepositoryDisplayLabel, "folder name")}");
        Console.WriteLine($"Workspace roots: {FormatStringList(snapshot.WorkspaceRoots, Environment.CurrentDirectory)}");
        Console.WriteLine($"Default working directory: {FormatValue(snapshot.WorkingDirectory, Environment.CurrentDirectory)}");
        Console.WriteLine($"Data root: {FormatValue(snapshot.DataRoot, GetDefaultDataRoot())}");
        Console.WriteLine("Checks:");
        foreach (BootstrapReadinessCheck check in BuildReadinessChecks(snapshot, botIdentity, codexCatalog))
        {
            Console.WriteLine($"- {(check.Passed ? "PASS" : "CHECK")}: {check.Name} - {check.Detail}");
        }
        Console.WriteLine("Smoke test (not run automatically): after starting, open the private chat and send /home, then /status. Send a harmless Codex prompt only when you explicitly want to start an agent turn.");
    }

    internal static IReadOnlyList<string> BuildReadinessCheckMessages(
        LocalSettingsSnapshot snapshot,
        TelegramBotIdentity? botIdentity = null)
        => BuildReadinessChecks(snapshot, botIdentity, null)
            .Select(check => $"{(check.Passed ? "PASS" : "CHECK")}: {check.Name} - {check.Detail}")
            .ToArray();

    private static IReadOnlyList<BootstrapReadinessCheck> BuildReadinessChecks(
        LocalSettingsSnapshot snapshot,
        TelegramBotIdentity? botIdentity,
        CodexModelCatalog? codexCatalog)
    {
        string dataRoot = snapshot.DataRoot ?? GetDefaultDataRoot();
        bool validDataRoot = TryValidateDataRoot(dataRoot, out _, out string dataRootError);
        List<BootstrapReadinessCheck> checks =
        [
            new("Telegram bot identity", botIdentity is not null, botIdentity is null
                ? snapshot.TelegramTokenConfigured ? "token is saved but was not validated in this run" : "bot token is missing"
                : $"@{botIdentity.Username ?? botIdentity.Id.ToString(CultureInfo.InvariantCulture)} ({botIdentity.DisplayName})"),
            new("Telegram admin allowlist", snapshot.AllowedUserIds.Count > 0, snapshot.AllowedUserIds.Count > 0
                ? $"{snapshot.AllowedUserIds.Count} admin user ID(s) configured"
                : "no admin user IDs configured; use /whoami only for initial discovery"),
            new("Workspace mode", snapshot.Mode is CodexTelegramMode.GeneralPurpose or CodexTelegramMode.Repository, DescribeWorkspaceMode(snapshot.Mode)),
            new("Local data root", validDataRoot, validDataRoot ? FormatValue(snapshot.DataRoot, "default AppData root") : dataRootError),
            new("Codex runtime", codexCatalog?.IsLive == true, codexCatalog is null
                ? "not checked in this menu view"
                : codexCatalog.Message ?? (codexCatalog.IsLive ? "Codex responded to model discovery" : "Codex was not verified")),
        ];

        if (snapshot.Mode == CodexTelegramMode.Repository)
        {
            bool validRepository = TryValidateExistingDirectory(snapshot.RepositoryRoot ?? string.Empty, "Repository root", out _, out string repositoryError);
            checks.Add(new("Repository root", validRepository, validRepository ? snapshot.RepositoryRoot! : repositoryError));
        }
        else
        {
            string[] invalidRoots = snapshot.WorkspaceRoots
                .Where(root => !TryValidateExistingDirectory(root, "Workspace root", out _, out _))
                .ToArray();
            checks.Add(new("Workspace roots", snapshot.WorkspaceRoots.Count > 0 && invalidRoots.Length == 0,
                snapshot.WorkspaceRoots.Count == 0
                    ? "none configured; process directory fallback is active"
                    : invalidRoots.Length == 0
                        ? $"{snapshot.WorkspaceRoots.Count} root(s) configured and accessible"
                        : $"{invalidRoots.Length} configured root(s) are missing or inaccessible"));
        }

        return checks;
    }

    private static string FormatBotIdentity(TelegramBotIdentity? identity, bool tokenConfigured)
        => identity is not null
            ? $"@{identity.Username ?? identity.Id.ToString(CultureInfo.InvariantCulture)} ({identity.DisplayName})"
            : tokenConfigured ? "token saved; validation not performed in this run" : "not configured";

    private static string DescribeWorkspaceMode(CodexTelegramMode mode)
        => mode switch
        {
            CodexTelegramMode.Repository => "Repository-only (bound to one repository)",
            _ => "General-purpose (browse and select local repositories)",
        };

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

    private static bool IsQuit(string value)
        => value.Equals("!quit", StringComparison.OrdinalIgnoreCase)
            || value.Equals("quit", StringComparison.OrdinalIgnoreCase)
            || value.Equals("exit", StringComparison.OrdinalIgnoreCase);

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

    private static void WriteBotFatherHint(TelegramBotIdentity bot)
    {
        if (bot.CanJoinGroups is false)
        {
            Console.WriteLine("BotFather currently reports group joins disabled, which is fine for private-chat setup.");
        }

        if (bot.CanReadAllGroupMessages is false)
        {
            Console.WriteLine("BotFather privacy mode appears enabled, which is recommended unless you intentionally want ordinary group text.");
        }
    }

    private static bool SaveSettings(LocalSettingsStore store)
    {
        try
        {
            store.Save();
            Console.WriteLine($"Saved {store.FilePath}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.WriteLine($"Could not save settings: {exception.Message}");
            return false;
        }
    }
}
