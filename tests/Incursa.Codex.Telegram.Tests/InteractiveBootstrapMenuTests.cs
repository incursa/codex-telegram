using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class InteractiveBootstrapMenuTests
{
    [Fact]
    public void BuildReadinessCheckMessagesReportsIdentityModeRepositoryAndDataRoot()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string repositoryRoot = temp.CreateDirectory("repository");
        string dataRoot = temp.CreateDirectory("data");
        LocalSettingsStore store = LocalSettingsStore.Load(Path.Combine(temp.Path, "appsettings.Local.json"));
        store.SetTelegramToken("123456:token");
        store.SetAllowedUserIds([1234]);
        store.SetMode(CodexTelegramMode.Repository);
        store.SetRepositoryRoot(repositoryRoot);
        store.SetRepositoryDisplayLabel("Demo repository");
        store.SetDataRoot(dataRoot);

        IReadOnlyList<string> checks = InteractiveBootstrapMenu.BuildReadinessCheckMessages(
            store.GetSnapshot(),
            new TelegramBotIdentity(99, "demo_bot", "Demo Bot", true, true));

        Assert.Contains(checks, message => message.Contains("Telegram bot identity - @demo_bot (Demo Bot)", StringComparison.Ordinal));
        Assert.Contains(checks, message => message.Contains("Workspace mode - Repository-only", StringComparison.Ordinal));
        Assert.Contains(checks, message => message.Contains("Local data root -", StringComparison.Ordinal) && message.StartsWith("PASS:", StringComparison.Ordinal));
        Assert.Contains(checks, message => message.Contains("Repository root -", StringComparison.Ordinal) && message.StartsWith("PASS:", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildReadinessCheckMessagesFlagsMissingRepositoryWithoutFallingBack()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        LocalSettingsStore store = LocalSettingsStore.Load(Path.Combine(temp.Path, "appsettings.Local.json"));
        store.SetTelegramToken("123456:token");
        store.SetMode(CodexTelegramMode.Repository);
        store.SetRepositoryRoot(Path.Combine(temp.Path, "does-not-exist"));

        IReadOnlyList<string> checks = InteractiveBootstrapMenu.BuildReadinessCheckMessages(store.GetSnapshot());

        Assert.Contains(checks, message => message.StartsWith("CHECK: Repository root -", StringComparison.Ordinal));
        Assert.DoesNotContain(checks, message => message.Contains("process directory fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildReadinessCheckMessagesAllowsMissingDataRootForRuntimeCreation()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        LocalSettingsStore store = LocalSettingsStore.Load(Path.Combine(temp.Path, "appsettings.Local.json"));
        store.SetDataRoot(Path.Combine(temp.Path, "new", "data"));

        IReadOnlyList<string> checks = InteractiveBootstrapMenu.BuildReadinessCheckMessages(store.GetSnapshot());

        Assert.Contains(checks, message => message.StartsWith("PASS: Local data root -", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "new")));
    }
}
