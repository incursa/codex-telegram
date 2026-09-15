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
        IReadOnlyList<string> failures = [.. ValidateRepository(options), .. ValidateTaskWorkspace(options), .. ValidateWorker(options), .. ValidateRecipes(options), .. ValidateCoordinator(options), .. ValidateUpdates(options)];
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

    private static IReadOnlyList<string> ValidateWorker(CodexTelegramOptions options)
    {
        CodexWorkerOptions worker = options.Worker;
        if (worker.MaxConcurrentTasks is < 1 or > 256)
        {
            return [$"CodexTelegram:Worker:MaxConcurrentTasks must be between 1 and 256 (received {worker.MaxConcurrentTasks})."];
        }

        if (!string.IsNullOrWhiteSpace(worker.WorkerId) && (worker.WorkerId.Trim().Length > 120 || worker.WorkerId.Any(char.IsControl)))
        {
            return ["CodexTelegram:Worker:WorkerId must be at most 120 characters and cannot contain control characters."];
        }

        if (!string.IsNullOrWhiteSpace(worker.DisplayName) && (worker.DisplayName.Trim().Length > 120 || worker.DisplayName.Any(char.IsControl)))
        {
            return ["CodexTelegram:Worker:DisplayName must be at most 120 characters and cannot contain control characters."];
        }

        return string.IsNullOrWhiteSpace(worker.ControlPlaneUrl)
            || Uri.TryCreate(worker.ControlPlaneUrl, UriKind.Absolute, out Uri? uri) && (uri.Scheme is "http" or "https")
            ? []
            : ["CodexTelegram:Worker:ControlPlaneUrl must be an absolute HTTP or HTTPS URL when configured."];
    }

    private static IReadOnlyList<string> ValidateRecipes(CodexTelegramOptions options)
    {
        if (options.Recipes.Count > 50)
        {
            return ["CodexTelegram:Recipes may contain at most 50 definitions."];
        }

        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (CodexTaskRecipeDefinition recipe in options.Recipes)
        {
            if (string.IsNullOrWhiteSpace(recipe.Id) || !ids.Add(recipe.Id.Trim()))
            {
                return ["CodexTelegram:Recipes must contain unique, non-empty IDs."];
            }

            if (!IsSafeRecipeToken(recipe.Id, 80) || !IsSafeRecipeToken(recipe.Version, 40))
            {
                return ["CodexTelegram:Recipes IDs and versions must use at most 80/40 safe characters and cannot contain controls."];
            }

            if (string.IsNullOrWhiteSpace(recipe.DisplayName) || recipe.DisplayName.Trim().Length > 120 || recipe.DisplayName.Any(char.IsControl))
            {
                return ["CodexTelegram:Recipes display names are required, limited to 120 characters, and cannot contain controls."];
            }

            if (string.IsNullOrWhiteSpace(recipe.Objective) || recipe.Objective.Length > 4_000 || recipe.Objective.Any(char.IsControl))
            {
                return ["CodexTelegram:Recipes objectives are required, limited to 4000 characters, and cannot contain controls."];
            }

            if (recipe.ExpectedOutputs.Count > 12 || recipe.RequiredCapabilities.Count > 12)
            {
                return ["CodexTelegram:Recipes may contain at most 12 expected outputs and required capabilities."];
            }
        }

        return [];
    }

    private static bool IsSafeRecipeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static IReadOnlyList<string> ValidateCoordinator(CodexTelegramOptions options)
    {
        CodexCoordinatorOptions coordinator = options.Coordinator;
        if (coordinator.HeartbeatIntervalSeconds is < 5 or > 300
            || coordinator.RequestTimeoutSeconds is < 1 or > 60
            || coordinator.MaximumRegisteredWorkers is < 1 or > 256)
        {
            return ["CodexTelegram:Coordinator timing values or MaximumRegisteredWorkers are outside their supported bounds."];
        }

        if (coordinator.Enabled || coordinator.WorkerRegistrationEnabled)
        {
            if (string.IsNullOrWhiteSpace(coordinator.AuthenticationToken) || coordinator.AuthenticationToken.Trim().Length < 32)
            {
                return ["CodexTelegram:Coordinator:AuthenticationToken must be at least 32 characters when coordinator features are enabled."];
            }
        }

        if (coordinator.WorkerRegistrationEnabled)
        {
            if (!Uri.TryCreate(coordinator.Url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            {
                return ["CodexTelegram:Coordinator:Url must be an absolute HTTP or HTTPS URL when worker registration is enabled."];
            }
        }

        return coordinator.AllowedWorkerIds.Any(id => !IsSafeRecipeToken(id, 120))
            ? ["CodexTelegram:Coordinator:AllowedWorkerIds must contain only safe IDs of at most 120 characters."]
            : [];
    }

    private static IReadOnlyList<string> ValidateUpdates(CodexTelegramOptions options)
    {
        CodexWorkerUpdateOptions updates = options.Updates;
        if (!updates.Enabled)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(updates.PackagePath)
            || string.IsNullOrWhiteSpace(updates.TargetVersion)
            || !IsSafeRecipeToken(updates.TargetVersion, 40)
            || !RegexLikeSha256(updates.ExpectedSha256))
        {
            return ["CodexTelegram:Updates requires a package path, safe target version, and 64-character hexadecimal ExpectedSha256."];
        }

        return updates.RequiredCapabilities.Any(capability => !IsSafeRecipeToken(capability, 120))
            ? ["CodexTelegram:Updates:RequiredCapabilities must contain only safe IDs of at most 120 characters."]
            : [];
    }

    private static bool RegexLikeSha256(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length == 64
            && value.All(character => char.IsAsciiHexDigit(character));

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
