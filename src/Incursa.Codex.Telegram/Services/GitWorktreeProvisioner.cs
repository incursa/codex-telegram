using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Incursa.Codex.Telegram.Services;

internal interface IGitWorktreeProvisioner
{
    Task<GitWorktreeProvisioningResult> CreateLaunchWorktreeAsync(
        string sourceWorkingDirectory,
        string allowlistedRoot,
        string launchName,
        CancellationToken cancellationToken);
}

internal sealed record GitWorktreeProvisioningResult(
    string RepositoryRoot,
    string WorktreeRoot,
    string WorkingDirectory);

internal sealed class GitWorktreeProvisioner : IGitWorktreeProvisioner
{
    private readonly ILogger<GitWorktreeProvisioner> _logger;

    public GitWorktreeProvisioner(ILogger<GitWorktreeProvisioner> logger)
    {
        _logger = logger;
    }

    public async Task<GitWorktreeProvisioningResult> CreateLaunchWorktreeAsync(
        string sourceWorkingDirectory,
        string allowlistedRoot,
        string launchName,
        CancellationToken cancellationToken)
    {
        string normalizedSource = Path.GetFullPath(sourceWorkingDirectory);
        string repositoryRoot = await ResolveRepositoryRootAsync(normalizedSource, cancellationToken).ConfigureAwait(false);
        string normalizedAllowedRoot = Path.GetFullPath(allowlistedRoot);
        string repoFolderName = GetPathLeaf(repositoryRoot);
        string launchSlug = BuildSlug(launchName);
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string suffix = Guid.NewGuid().ToString("n")[..8];
        string worktreeRoot = Path.Combine(normalizedAllowedRoot, ".codex-worktrees", repoFolderName, $"{launchSlug}-{stamp}-{suffix}");

        Directory.CreateDirectory(Path.GetDirectoryName(worktreeRoot)!);
        GitCommandResult worktreeResult = await RunGitAsync(repositoryRoot, cancellationToken, "worktree", "add", "--detach", worktreeRoot, "HEAD").ConfigureAwait(false);
        if (worktreeResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git worktree add failed while provisioning '{worktreeRoot}' from '{repositoryRoot}'.{Environment.NewLine}{worktreeResult.GetCombinedOutput()}");
        }

        string relativeSource = Path.GetRelativePath(repositoryRoot, normalizedSource);
        string workingDirectory = string.Equals(relativeSource, ".", StringComparison.Ordinal)
            ? worktreeRoot
            : Path.GetFullPath(Path.Combine(worktreeRoot, relativeSource));

        _logger.LogInformation(
            "Provisioned git worktree {WorktreeRoot} for source {SourceWorkingDirectory} at repository root {RepositoryRoot}.",
            worktreeRoot,
            normalizedSource,
            repositoryRoot);

        return new GitWorktreeProvisioningResult(repositoryRoot, worktreeRoot, workingDirectory);
    }

    private async Task<string> ResolveRepositoryRootAsync(string sourceWorkingDirectory, CancellationToken cancellationToken)
    {
        GitCommandResult result = await RunGitAsync(sourceWorkingDirectory, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The working directory '{sourceWorkingDirectory}' is not inside a git repository.{Environment.NewLine}{result.GetCombinedOutput()}");
        }

        string repositoryRoot = result.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            throw new InvalidOperationException($"git did not return a repository root for '{sourceWorkingDirectory}'.");
        }

        return Path.GetFullPath(repositoryRoot);
    }

    private async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
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

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git.");
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
            });

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new GitCommandResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("git is unavailable or could not be started.", exception);
        }
    }

    private static string BuildSlug(string value)
    {
        StringBuilder builder = new(value.Length);
        bool appendedDash = false;
        foreach (char ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                appendedDash = false;
                continue;
            }

            if (builder.Length == 0 || appendedDash)
            {
                continue;
            }

            builder.Append('-');
            appendedDash = true;
        }

        string slug = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "lane";
        }

        return slug.Length <= 48 ? slug : slug[..48].Trim('-');
    }

    private static string GetPathLeaf(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(leaf) ? "repo" : leaf;
    }

    private sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string GetCombinedOutput()
        {
            StringBuilder builder = new();
            if (!string.IsNullOrWhiteSpace(StandardError))
            {
                builder.AppendLine(StandardError.Trim());
            }

            if (!string.IsNullOrWhiteSpace(StandardOutput))
            {
                builder.AppendLine(StandardOutput.Trim());
            }

            return builder.ToString().TrimEnd();
        }
    }
}
