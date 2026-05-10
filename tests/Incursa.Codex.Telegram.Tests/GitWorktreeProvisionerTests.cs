using System.Diagnostics;
using System.Globalization;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Incursa.Codex.Telegram.Tests;

public sealed class GitWorktreeProvisionerTests
{
    [Fact]
    public async Task CreateLaunchWorktreeAsync_CreatesDetachedWorktreeAndPreservesSourceSubdirectory()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "codex-telegram-worktree-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string repoRoot = Path.Combine(tempRoot, "source-repo");
            string sourceSubdirectory = Path.Combine(repoRoot, "src");
            Directory.CreateDirectory(sourceSubdirectory);

            RunGit(repoRoot, "init", "-q");
            RunGit(repoRoot, "config", "user.email", "test@example.com");
            RunGit(repoRoot, "config", "user.name", "Test User");
            await File.WriteAllTextAsync(Path.Combine(sourceSubdirectory, "README.md"), "hello", CancellationToken.None);
            RunGit(repoRoot, "add", ".");
            RunGit(repoRoot, "commit", "-q", "-m", "init");

            GitWorktreeProvisioner provisioner = new(NullLogger<GitWorktreeProvisioner>.Instance);
            GitWorktreeProvisioningResult result = await provisioner.CreateLaunchWorktreeAsync(
                sourceSubdirectory,
                tempRoot,
                "Release readiness",
                CancellationToken.None);

            Assert.StartsWith(tempRoot, result.WorktreeRoot, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(result.WorktreeRoot));
            Assert.True(File.Exists(Path.Combine(result.WorktreeRoot, ".git")));
            Assert.True(Directory.Exists(result.WorkingDirectory));
            Assert.Equal(Path.Combine(result.WorktreeRoot, "src"), result.WorkingDirectory);
            Assert.Equal("HEAD", RunGit(result.WorktreeRoot, "rev-parse", "--abbrev-ref", "HEAD").Trim());
            string gitWorktreeList = RunGit(repoRoot, "worktree", "list", "--porcelain").Replace('\\', '/');
            Assert.Contains(result.WorktreeRoot.Replace('\\', '/'), gitWorktreeList);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
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

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git failed with exit code {process.ExitCode}: {stderr}{Environment.NewLine}{stdout}");
        }

        return stdout;
    }
}
