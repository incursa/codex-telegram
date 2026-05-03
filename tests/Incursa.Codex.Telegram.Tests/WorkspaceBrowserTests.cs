using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class WorkspaceBrowserTests
{
    [Fact]
    public void ValidateWorkingDirectory_AcceptsExistingDirectoryUnderAllowedRoot()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string project = temp.CreateDirectory("project");
        CodexWorkspaceBrowser browser = CreateBrowser(temp.Path);

        var result = browser.ValidateWorkingDirectory(project);

        Assert.True(result.IsValid);
        Assert.True(result.IsAllowlisted);
        Assert.True(result.Exists);
        Assert.True(result.IsDirectory);
        Assert.Equal(project, result.NormalizedPath);
    }

    [Fact]
    public void ValidateWorkingDirectory_RejectsDirectoryOutsideAllowedRoot()
    {
        using TemporaryDirectory allowed = TemporaryDirectory.Create();
        using TemporaryDirectory outside = TemporaryDirectory.Create();
        CodexWorkspaceBrowser browser = CreateBrowser(allowed.Path);

        var result = browser.ValidateWorkingDirectory(outside.Path);

        Assert.False(result.IsValid);
        Assert.Equal("not_allowlisted", result.Reason);
    }

    [Fact]
    public void ValidateWorkingDirectory_RewritesMojibakeInFailureMessage()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        CodexWorkspaceBrowser browser = CreateBrowser(temp.Path);

        var result = browser.ValidateWorkingDirectory("repoΓÇÖs");

        Assert.False(result.IsValid);
        Assert.Contains("repo’s", result.Message);
        Assert.DoesNotContain("ΓÇÖ", result.Message);
    }

    private static CodexWorkspaceBrowser CreateBrowser(string root)
        => new(Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions
            {
                WorkspaceRoots = [root],
            },
        }));
}
