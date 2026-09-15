using System.Security.Cryptography;
using System.Text;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Configuration;

/// <summary>
/// Applies the repository-mode contract at startup.
/// </summary>
internal sealed class CodexTelegramOptionsValidator : IValidateOptions<CodexTelegramOptions>
{
    public ValidateOptionsResult Validate(string? name, CodexTelegramOptions options)
    {
        IReadOnlyList<string> failures = [.. ValidateRepository(options), .. ValidateTaskWorkspace(options)];
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static IReadOnlyList<string> ValidateTaskWorkspace(CodexTelegramOptions options)
    {
        int start = options.Workspace.TaskDevelopmentPortRangeStart;
        int end = options.Workspace.TaskDevelopmentPortRangeEnd;
        return start is < 1024 or > 65535 || end is < 1024 or > 65535 || start > end
            ? [$"CodexTelegram:Workspace task development port range must be within 1024-65535 and start no later than end (received {start}-{end})."]
            : [];
    }

    internal static IReadOnlyList<string> ValidateRepository(CodexTelegramOptions options)
    {
        if (options.Mode == CodexTelegramMode.GeneralPurpose)
        {
            return [];
        }

        if (options.Mode != CodexTelegramMode.Repository)
        {
            return [$"CodexTelegram:Mode '{options.Mode}' is not supported. Use GeneralPurpose or Repository."];
        }

        if (string.IsNullOrWhiteSpace(options.RepositoryRoot))
        {
            return ["CodexTelegram:RepositoryRoot is required when CodexTelegram:Mode is Repository; no workspace fallback is used."];
        }

        if (!string.IsNullOrWhiteSpace(options.RepositoryDisplayLabel))
        {
            string label = options.RepositoryDisplayLabel.Trim();
            if (label.Length > 80 || label.Any(char.IsControl))
            {
                return ["CodexTelegram:RepositoryDisplayLabel must be at most 80 characters and cannot contain control characters."];
            }
        }

        string root;
        try
        {
            root = Path.GetFullPath(options.RepositoryRoot.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return [$"CodexTelegram:RepositoryRoot '{options.RepositoryRoot}' is not a valid path: {exception.Message}"];
        }

        DirectoryInfo directory = new(root);
        if (!directory.Exists)
        {
            return [$"CodexTelegram:RepositoryRoot '{root}' does not name an existing directory or is inaccessible; no workspace fallback is used."];
        }

        try
        {
            // MoveNext forces an access check while still accepting an empty repository.
            using IEnumerator<FileSystemInfo> entries = directory.EnumerateFileSystemInfos().GetEnumerator();
            _ = entries.MoveNext();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return [$"CodexTelegram:RepositoryRoot '{root}' cannot be accessed: {exception.Message}"];
        }

        // A Git worktree has a .git file rather than a .git directory. Do not reject it.
        return [];
    }
}

/// <summary>
/// Resolves local state defaults without changing the legacy path unless an instance is explicitly selected.
/// </summary>
internal static class CodexTelegramDataRoot
{
    internal static string GetDefaultDataRoot(string? instanceId = null)
    {
        string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".incursa");
        }

        string root = Path.Combine(baseDirectory, "Incursa", "CodexTelegram");
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return root;
        }

        string safeInstanceId = NormalizeInstanceId(instanceId);
        return Path.Combine(root, safeInstanceId);
    }

    internal static string GetTempRoot(CodexTelegramOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? identity = !string.IsNullOrWhiteSpace(options.Workspace.DataRoot)
            ? Path.GetFullPath(options.Workspace.DataRoot)
            : options.InstanceId;
        if (string.IsNullOrWhiteSpace(identity))
        {
            return Path.Combine(Path.GetTempPath(), "codex-telegram");
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "codex-telegram", hash[..16]);
    }

    private static string NormalizeInstanceId(string value)
    {
        HashSet<char> invalid = new(Path.GetInvalidFileNameChars())
        {
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar,
        };

        string normalized = new(value.Trim().Select(character =>
            character == '\0' || char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(normalized) || normalized is "." or ".."
            ? "instance"
            : normalized;
    }
}
