using System.Diagnostics;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Reads a small, read-only snapshot of the repository configured for the Telegram bot.
/// </summary>
internal sealed class RepositorySummaryService
{
    private static readonly string[] GuidanceCandidates =
    [
        "README.md",
        "README",
        "AGENTS.md",
        "DEVELOPMENT.md",
        "DEVELOPING.md",
        "CONTRIBUTING.md",
        "docs/README.md",
        "docs/development.md",
        "docs/development-guide.md",
    ];

    public async Task<RepositorySummary> ReadAsync(string? workingDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return RepositorySummary.Unconfigured;
        }

        string root;
        try
        {
            root = Path.GetFullPath(workingDirectory.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new RepositorySummary(workingDirectory.Trim(), workingDirectory.Trim(), "unknown", "invalid path: " + exception.Message, []);
        }

        IReadOnlyList<string> guidance = DiscoverGuidance(root);
        if (!Directory.Exists(root))
        {
            return new RepositorySummary(root, ResolveName(root), "unknown", "directory is unavailable", guidance);
        }

        GitSnapshot git = await ReadGitSnapshotAsync(root, cancellationToken).ConfigureAwait(false);
        return new RepositorySummary(root, ResolveName(root), git.Branch, git.Status, guidance);
    }

    private static IReadOnlyList<string> DiscoverGuidance(string root)
        => GuidanceCandidates
            .Where(candidate => File.Exists(Path.Combine(root, candidate.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();

    private static string ResolveName(string root)
    {
        string trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrWhiteSpace(trimmed)
            ? root
            : new DirectoryInfo(trimmed).Name;
    }

    private static async Task<GitSnapshot> ReadGitSnapshotAsync(string root, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--short");
        startInfo.ArgumentList.Add("--branch");

        try
        {
            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                return new GitSnapshot("unknown", "git could not be started");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return new GitSnapshot("unknown", FirstLine(error) ?? "not a Git repository");
            }

            string[] lines = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string branch = ParseBranch(lines.FirstOrDefault()) ?? "(detached or unknown)";
            string[] changes = lines.Skip(1).Take(20).ToArray();
            string status = changes.Length == 0
                ? "clean"
                : changes.Length == 20 && lines.Length > 21
                    ? "20+ changed paths"
                    : $"{changes.Length} changed path{(changes.Length == 1 ? string.Empty : "s")}";
            return new GitSnapshot(branch, status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GitSnapshot("unknown", "git status timed out");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new GitSnapshot("unknown", "git status unavailable: " + exception.Message);
        }
    }

    private static string? ParseBranch(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("## ", StringComparison.Ordinal))
        {
            return null;
        }

        string branch = line[3..].Trim();
        int separator = branch.IndexOf("...", StringComparison.Ordinal);
        if (separator >= 0)
        {
            branch = branch[..separator];
        }

        return string.IsNullOrWhiteSpace(branch) ? null : branch;
    }

    private static string? FirstLine(string? text)
        => text?.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private sealed record GitSnapshot(string Branch, string Status);
}

internal sealed record RepositorySummary(
    string RootPath,
    string Name,
    string Branch,
    string Status,
    IReadOnlyList<string> GuidanceFiles)
{
    public static RepositorySummary Unconfigured { get; } = new(string.Empty, string.Empty, "unknown", "not configured", []);
}
