using Incursa.Codex.Telegram.Configuration;

namespace Incursa.Codex.Telegram.Tests;

public sealed class LocalSettingsStoreTests
{
    [Fact]
    public void SaveAndLoadRoundTripsBootstrapSettings()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "appsettings.Local.json");
        string workspaceRoot = Path.Combine(temp.Path, "workspace");
        string workingDirectory = Path.Combine(workspaceRoot, "repo");
        string dataRoot = Path.Combine(temp.Path, "data");

        LocalSettingsStore store = LocalSettingsStore.Load(settingsPath);
        store.SetTelegramEnabled(true);
        store.SetTelegramToken("telegram-token");
        store.SetAllowedUserIds([1234, 5678]);
        store.SetAllowedChatIds([-1001234]);
        store.SetOpenAiApiKey("openai-key");
        store.SetOpenAiModel("gpt-4o-mini-transcribe");
        store.SetOpenAiFfmpegPath("C:\\tools\\ffmpeg.exe");
        store.SetCodexPathOverride("C:\\tools\\codex.exe");
        store.SetInitializeOnStart(false);
        store.SetWorkingDirectory(workingDirectory);
        store.SetDataRoot(dataRoot);
        store.SetWorkspaceRoots([workspaceRoot]);
        store.SetCodexModel("gpt-5.4");
        store.SetReasoningEffort("high");
        store.SetSandbox("workspace-write");
        store.SetApprovalMode("on-request");
        store.SetNetworkAccessEnabled(true);
        store.Save();

        LocalSettingsSnapshot snapshot = LocalSettingsStore.Load(settingsPath).GetSnapshot();

        Assert.True(snapshot.TelegramEnabled);
        Assert.True(snapshot.TelegramTokenConfigured);
        Assert.Equal([1234, 5678], snapshot.AllowedUserIds);
        Assert.Equal([-1001234], snapshot.AllowedChatIds);
        Assert.True(snapshot.OpenAiApiKeyConfigured);
        Assert.Equal("gpt-4o-mini-transcribe", snapshot.OpenAiModel);
        Assert.Equal("C:\\tools\\ffmpeg.exe", snapshot.OpenAiFfmpegPath);
        Assert.Equal("C:\\tools\\codex.exe", snapshot.CodexPathOverride);
        Assert.False(snapshot.InitializeOnStart);
        Assert.Equal(workingDirectory, snapshot.WorkingDirectory);
        Assert.Equal(dataRoot, snapshot.DataRoot);
        Assert.Equal([workspaceRoot], snapshot.WorkspaceRoots);
        Assert.Equal("gpt-5.4", snapshot.CodexModel);
        Assert.Equal("high", snapshot.ReasoningEffort);
        Assert.Equal("workspace-write", snapshot.Sandbox);
        Assert.Equal("on-request", snapshot.ApprovalMode);
        Assert.True(snapshot.NetworkAccessEnabled);
    }

    [Fact]
    public void ClearingSecretsRemovesConfiguredStatus()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "appsettings.Local.json");

        LocalSettingsStore store = LocalSettingsStore.Load(settingsPath);
        store.SetTelegramToken("telegram-token");
        store.SetOpenAiApiKey("openai-key");
        store.Save();

        store.SetTelegramToken(null);
        store.SetOpenAiApiKey(null);
        store.Save();

        LocalSettingsSnapshot snapshot = LocalSettingsStore.Load(settingsPath).GetSnapshot();
        string settingsJson = File.ReadAllText(settingsPath);

        Assert.False(snapshot.TelegramTokenConfigured);
        Assert.False(snapshot.OpenAiApiKeyConfigured);
        Assert.DoesNotContain("telegram-token", settingsJson);
        Assert.DoesNotContain("openai-key", settingsJson);
    }

    [Fact]
    public void LoadReadsTelegramBotCodexExecutablePathFallback()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string settingsPath = Path.Combine(temp.Path, "appsettings.Local.json");

        File.WriteAllText(
            settingsPath,
            """
            {
              "TelegramBot": {
                "CodexExecutablePath": "C:\\tools\\codex.exe"
              }
            }
            """);

        LocalSettingsSnapshot snapshot = LocalSettingsStore.Load(settingsPath).GetSnapshot();

        Assert.Equal("C:\\tools\\codex.exe", snapshot.TelegramBotCodexExecutablePath);
    }
}
