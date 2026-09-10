using System.Diagnostics;
using Incursa.Codex.Telegram.Telegram;

namespace Incursa.Codex.Telegram.Tests;

public sealed class RepositorySummaryServiceTests
{
    [Fact]
    public async Task ReadAsync_DiscoversGuidanceWithoutChangingRepository()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "README.md"), "# Test repository");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "AGENTS.md"), "# Guidance");
        await RunGitAsync(temp.Path, "init");
        await RunGitAsync(temp.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "add", ".");
        await RunGitAsync(temp.Path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "initial");

        RepositorySummary summary = await new RepositorySummaryService().ReadAsync(temp.Path, CancellationToken.None);

        Assert.Equal(["README.md", "AGENTS.md"], summary.GuidanceFiles);
        Assert.NotEqual("unknown", summary.Branch);
        Assert.Equal("clean", summary.Status);
        Assert.True(File.Exists(Path.Combine(temp.Path, "README.md")));
    }

    [Fact]
    public async Task ReadAsync_UnconfiguredIsExplicitAndDoesNotTouchFilesystem()
    {
        RepositorySummary summary = await new RepositorySummaryService().ReadAsync(null, CancellationToken.None);

        Assert.Equal(string.Empty, summary.RootPath);
        Assert.Equal("not configured", summary.Status);
        Assert.Empty(summary.GuidanceFiles);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
    }
}
