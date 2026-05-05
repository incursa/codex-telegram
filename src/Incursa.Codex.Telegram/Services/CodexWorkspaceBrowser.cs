using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal sealed class CodexWorkspaceBrowser
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly IOptions<CodexTelegramOptions> _options;

    public CodexWorkspaceBrowser(IOptions<CodexTelegramOptions> options)
    {
        _options = options;
    }

    public IReadOnlyList<string> GetWorkspaceRoots()
        => GetWorkspaceRootPaths();

    public CodexWorkspaceOverviewVm GetWorkspaceOverview()
        => new(
            ServerPlatform: GetServerPlatform(),
            PathFormatHint: GetPathFormatHint(),
            CurrentWorkingDirectory: Path.GetFullPath(Environment.CurrentDirectory),
            ConfiguredWorkingDirectory: GetConfiguredWorkingDirectory(),
            WorkspaceRoots: GetWorkspaceRootDetails());

    public IReadOnlyList<CodexWorkspaceRootVm> GetWorkspaceRootDetails()
        => GetWorkspaceRootPaths()
            .Select(DescribeRoot)
            .ToArray();

    public Task<IReadOnlyList<CodexWorkspaceEntryVm>> SearchAsync(string? query, CancellationToken cancellationToken)
    {
        List<CodexWorkspaceEntryVm> entries = new();
        string? search = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        int maxEntries = _options.Value.Workspace.MaxWorkspaceEntries;
        int maxDepth = _options.Value.Workspace.WorkspaceSearchDepth;

        foreach (string root in GetWorkspaceRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            SearchRoot(root, root, 0, search, maxDepth, maxEntries, entries, cancellationToken);
            if (entries.Count >= maxEntries)
            {
                break;
            }
        }

        return Task.FromResult<IReadOnlyList<CodexWorkspaceEntryVm>>(entries);
    }

    public CodexWorkspaceValidationVm ValidateWorkingDirectory(string? workingDirectory)
    {
        IReadOnlyList<string> allowedRoots = GetWorkspaceRoots();
        string pathFormatHint = GetPathFormatHint();
        string serverPlatform = GetServerPlatform();

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return new CodexWorkspaceValidationVm(
                InputPath: null,
                NormalizedPath: null,
                IsValid: true,
                Reason: "default",
                Message: "No working directory was provided. The configured default working directory will be used.",
                IsAllowlisted: false,
                Exists: false,
                IsDirectory: false,
                AllowlistedRoot: null,
                AllowedRoots: allowedRoots,
                PathFormatHint: pathFormatHint,
                ServerPlatform: serverPlatform);
        }

        string candidate = workingDirectory.Trim();
        if (!IsSupportedAbsolutePathSyntax(candidate))
        {
            return BuildFailure(
                candidate,
                null,
                "invalid_format",
                BuildInvalidPathMessage(candidate, pathFormatHint),
                isAllowlisted: false,
                exists: false,
                isDirectory: false,
                null,
                allowedRoots,
                pathFormatHint,
                serverPlatform);
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BuildFailure(
                candidate,
                null,
                "invalid_format",
                $"The working directory '{candidate}' could not be normalized: {exception.Message}",
                isAllowlisted: false,
                exists: false,
                isDirectory: false,
                null,
                allowedRoots,
                pathFormatHint,
                serverPlatform);
        }

        if (allowedRoots.Count == 0)
        {
            return BuildFailure(
                candidate,
                normalized,
                "no_workspace_roots",
                "No workspace roots are configured on the server.",
                isAllowlisted: false,
                exists: false,
                isDirectory: false,
                null,
                allowedRoots,
                pathFormatHint,
                serverPlatform);
        }

        string? matchedRoot = allowedRoots.FirstOrDefault(root => IsPathUnderRoot(normalized, root));
        if (matchedRoot is null)
        {
            return BuildFailure(
                candidate,
                normalized,
                "not_allowlisted",
                $"The working directory '{normalized}' is not inside any allowlisted workspace root.",
                isAllowlisted: false,
                exists: false,
                isDirectory: false,
                null,
                allowedRoots,
                pathFormatHint,
                serverPlatform);
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(normalized);
            bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
            if (!isDirectory)
            {
                return BuildFailure(
                    candidate,
                    normalized,
                    "not_directory",
                    $"The working directory '{normalized}' points to a file, not a directory.",
                    isAllowlisted: true,
                    exists: true,
                    isDirectory: false,
                    matchedRoot,
                    allowedRoots,
                    pathFormatHint,
                    serverPlatform);
            }

            return new CodexWorkspaceValidationVm(
                InputPath: candidate,
                NormalizedPath: normalized,
                IsValid: true,
                Reason: "valid",
                Message: $"The working directory '{normalized}' is allowlisted under '{matchedRoot}'.",
                IsAllowlisted: true,
                Exists: true,
                IsDirectory: true,
                AllowlistedRoot: matchedRoot,
                AllowedRoots: allowedRoots,
                PathFormatHint: pathFormatHint,
                ServerPlatform: serverPlatform);
        }
        catch (UnauthorizedAccessException exception)
        {
            return BuildFailure(
                candidate,
                normalized,
                "permission_denied",
                $"Permission denied while accessing the working directory '{normalized}': {exception.Message}",
                isAllowlisted: true,
                exists: false,
                isDirectory: false,
                matchedRoot,
                allowedRoots,
                pathFormatHint,
                serverPlatform);
        }
        catch (DirectoryNotFoundException exception)
        {
            return BuildFailure(
                candidate,
                normalized,
                "not_found",
                $"The working directory '{normalized}' does not exist: {exception.Message}",
                isAllowlisted: true,
                exists: false,
                isDirectory: false,
                matchedRoot,
                allowedRoots,
                pathFormatHint,
                serverPlatform);
        }
        catch (FileNotFoundException exception)
        {
            return BuildFailure(
                candidate,
                normalized,
                "not_found",
                $"The working directory '{normalized}' does not exist: {exception.Message}",
                isAllowlisted: true,
                exists: false,
                isDirectory: false,
                matchedRoot,
                allowedRoots,
                pathFormatHint,
                serverPlatform);
        }
        catch (IOException exception)
        {
            return BuildFailure(
                candidate,
                normalized,
                "io_failure",
                $"I/O failed while validating the working directory '{normalized}': {exception.Message}",
                isAllowlisted: true,
                exists: false,
                isDirectory: false,
                matchedRoot,
                allowedRoots,
                pathFormatHint,
                serverPlatform);
        }
    }

    private IReadOnlyList<string> GetWorkspaceRootPaths()
        => _options.Value.Workspace.WorkspaceRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetFullPath(root))
            .Distinct(PathComparer)
            .ToArray();

    private CodexWorkspaceRootVm DescribeRoot(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
            string status = isDirectory ? "available" : "not_directory";
            string message = isDirectory
                ? "The workspace root is available."
                : "The configured workspace root points to a file, not a directory.";

            return new CodexWorkspaceRootVm(path, true, isDirectory, status, RepairText(message));
        }
        catch (UnauthorizedAccessException exception)
        {
            return new CodexWorkspaceRootVm(path, false, false, "permission_denied", RepairText(exception.Message));
        }
        catch (DirectoryNotFoundException exception)
        {
            return new CodexWorkspaceRootVm(path, false, false, "missing", RepairText(exception.Message));
        }
        catch (FileNotFoundException exception)
        {
            return new CodexWorkspaceRootVm(path, false, false, "missing", RepairText(exception.Message));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new CodexWorkspaceRootVm(path, false, false, "invalid_format", RepairText(exception.Message));
        }
        catch (IOException exception)
        {
            return new CodexWorkspaceRootVm(path, false, false, "io_failure", RepairText(exception.Message));
        }
    }

    private static CodexWorkspaceValidationVm BuildFailure(
        string inputPath,
        string? normalizedPath,
        string reason,
        string message,
        bool isAllowlisted,
        bool exists,
        bool isDirectory,
        string? allowlistedRoot,
        IReadOnlyList<string> allowedRoots,
        string pathFormatHint,
        string serverPlatform)
        => new(
            InputPath: inputPath,
            NormalizedPath: normalizedPath,
            IsValid: false,
            Reason: reason,
            Message: RepairText(message),
            IsAllowlisted: isAllowlisted,
            Exists: exists,
            IsDirectory: isDirectory,
            AllowlistedRoot: allowlistedRoot,
            AllowedRoots: allowedRoots,
            PathFormatHint: pathFormatHint,
            ServerPlatform: serverPlatform);

    private static bool IsPathUnderRoot(string candidate, string root)
    {
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;

        return candidate.Equals(normalizedRoot, PathComparison)
            || candidate.StartsWith(rootPrefix, PathComparison);
    }

    private static bool IsSupportedAbsolutePathSyntax(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return IsWindowsAbsolutePath(path) || IsWindowsUncPath(path);
        }

        return IsPosixAbsolutePath(path);
    }

    private static bool IsWindowsAbsolutePath(string path)
        => path.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');

    private static bool IsWindowsUncPath(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool IsPosixAbsolutePath(string path)
        => path.StartsWith("/", StringComparison.Ordinal);

    private static string BuildInvalidPathMessage(string candidate, string pathFormatHint)
        => $"The working directory '{candidate}' is not a supported absolute path. {pathFormatHint}";

    private static string GetPathFormatHint()
    {
        if (OperatingSystem.IsWindows())
        {
            return "This server runs on Windows. Use an absolute Windows path such as C:\\src\\repo or a UNC path such as \\\\server\\share\\repo. Linux or WSL-style paths like /mnt/c/... are not accepted.";
        }

        return "This server runs on a non-Windows OS. Use an absolute POSIX path such as /home/user/repo or /mnt/c/... when that mount exists on this machine. Windows drive-letter paths like C:\\src\\repo are not accepted.";
    }

    private static string GetServerPlatform()
        => OperatingSystem.IsWindows()
            ? "Windows"
            : OperatingSystem.IsMacOS()
                ? "macOS"
                : "Linux";

    private string? GetConfiguredWorkingDirectory()
    {
        string? configuredWorkingDirectory = _options.Value.Context.WorkingDirectory;
        return string.IsNullOrWhiteSpace(configuredWorkingDirectory) ? null : Path.GetFullPath(configuredWorkingDirectory);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static void SearchRoot(
        string root,
        string currentPath,
        int depth,
        string? search,
        int maxDepth,
        int maxEntries,
        ICollection<CodexWorkspaceEntryVm> entries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(currentPath)
                .OrderByDescending(path => Directory.Exists(path))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return;
        }

        foreach (string child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entries.Count >= maxEntries)
            {
                return;
            }

            if (ShouldInclude(child, search))
            {
                entries.Add(CodexViewModelMapper.ToWorkspaceEntryVm(root, child));
            }

            if (Directory.Exists(child) && depth < maxDepth)
            {
                SearchRoot(root, child, depth + 1, search, maxDepth, maxEntries, entries, cancellationToken);
            }
        }
    }

    private static bool ShouldInclude(string path, string? search)
    {
        if (search is null)
        {
            return true;
        }

        return path.Contains(search, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepairText(string? value)
        => CodexTextFormatting.RepairUtf8Mojibake(value) ?? string.Empty;
}
