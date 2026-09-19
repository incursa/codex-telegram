using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class ConfigurationPolicyTests
{
    [Fact]
    public void OptionsDefaultToLegacyGeneralPurposeMode()
    {
        CodexTelegramOptions options = new();

        Assert.Equal(CodexTelegramMode.GeneralPurpose, options.Mode);
        Assert.Empty(CodexTelegramOptionsValidator.ValidateRepository(options));
        Assert.Equal("CodexTelegram", Path.GetFileName(CodexTelegramDataRoot.GetDefaultDataRoot()));
    }

    [Fact]
    public void RepositoryModeRequiresAnExistingDirectory()
    {
        CodexTelegramOptions options = new()
        {
            Mode = CodexTelegramMode.Repository,
            RepositoryRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        };

        string failure = Assert.Single(CodexTelegramOptionsValidator.ValidateRepository(options));

        Assert.Contains("RepositoryRoot", failure, StringComparison.Ordinal);
        Assert.Contains("existing directory", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryModeAcceptsAnExistingDirectory()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        CodexTelegramOptions options = new()
        {
            Mode = CodexTelegramMode.Repository,
            RepositoryRoot = temp.Path,
        };

        Assert.Empty(CodexTelegramOptionsValidator.ValidateRepository(options));
    }

    [Fact]
    public void RepositoryModeAcceptsGitWorktreeMetadataFile()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string worktree = temp.CreateDirectory("worktree");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: C:/source/repository/.git/worktrees/worktree");
        CodexTelegramOptions options = new()
        {
            Mode = CodexTelegramMode.Repository,
            RepositoryRoot = worktree,
        };

        Assert.Empty(CodexTelegramOptionsValidator.ValidateRepository(options));
    }

    [Fact]
    public void TaskDevelopmentPortRangeMustBeValid()
    {
        CodexTelegramOptions options = new();
        options.Workspace.TaskDevelopmentPortRangeStart = 45000;
        options.Workspace.TaskDevelopmentPortRangeEnd = 44999;

        ValidateOptionsResult result = new CodexTelegramOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("port range", Assert.Single(result.Failures!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostUpdateDefaultsToDisabledAndAcceptsExternalUpdaterConfiguration()
    {
        Assert.False(new CodexTelegramOptions().HostUpdate.Enabled);

        CodexTelegramOptions options = new()
        {
            HostUpdate = new CodexHostUpdateOptions
            {
                Enabled = true,
                RequestPath = "/var/lib/codex-telegram/codex-host-update-request.json",
                TargetVersion = "1.0.57",
                ExpectedSha256 = new string('a', 64),
            },
        };

        ValidateOptionsResult result = new CodexTelegramOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void HostUpdateRejectsMalformedExpectedDigest()
    {
        CodexTelegramOptions options = new()
        {
            HostUpdate = new CodexHostUpdateOptions
            {
                Enabled = true,
                ExpectedSha256 = "not-a-sha256",
            },
        };

        ValidateOptionsResult result = new CodexTelegramOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("ExpectedSha256", string.Join("; ", result.Failures ?? []), StringComparison.Ordinal);
    }

    [Fact]
    public void InstanceIdPartitionsOnlyTheOptedInDefaultDataRoot()
    {
        string legacyRoot = CodexTelegramDataRoot.GetDefaultDataRoot();
        string instanceRoot = CodexTelegramDataRoot.GetDefaultDataRoot("worker-a");

        Assert.NotEqual(legacyRoot, instanceRoot);
        Assert.EndsWith(Path.Combine("CodexTelegram", "worker-a"), instanceRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigSelectorIsRemovedFromForwardedArguments()
    {
        ApplicationCommandLine commandLine = ApplicationCommandLine.Parse(
            ["--config", "settings.json", "--run", "--CodexTelegram:Mode", "Repository"]);

        Assert.True(commandLine.RunDirectly);
        Assert.Equal("settings.json", commandLine.ConfigPath);
        Assert.Equal(["--CodexTelegram:Mode", "Repository"], commandLine.ConfigurationArgs);
    }

    [Fact]
    public void ConfigSelectorRequiresAPath()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => ApplicationCommandLine.Parse(["--config"]));

        Assert.Contains("requires", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
