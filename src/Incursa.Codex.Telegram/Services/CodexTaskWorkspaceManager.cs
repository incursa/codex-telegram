using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Provisions and records task-owned Git worktrees plus bounded development resources.
/// The manager is intentionally independent of Codex execution: callers must pass the
/// already-authorized task and repository identity, and Codex remains the authority that
/// executes in the returned worktree.
/// </summary>
internal interface ICodexTaskWorkspaceManager
{
    Task<CodexTaskWorkspaceRecord> CreateAsync(
        string taskId,
        string repositoryRoot,
        string? baseRef,
        CancellationToken cancellationToken);

    Task<CodexTaskWorkspaceRecord?> GetAsync(string taskId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CodexTaskWorkspaceRecord>> ListAsync(CancellationToken cancellationToken);

    Task<CodexTaskWorkspaceRecord?> ReleaseAsync(
        string taskId,
        bool discardChanges,
        CancellationToken cancellationToken);
}

internal enum CodexTaskWorkspaceState
{
    Provisioned,
    Released,
}

/// <summary>
/// Bounded, redacted task workspace allocation record.
/// </summary>
internal sealed record CodexTaskWorkspaceRecord(
    string WorkspaceId,
    string TaskId,
    string RepositoryRoot,
    string WorktreePath,
    string Branch,
    string DatabaseNamespace,
    int DevelopmentPort,
    CodexTaskWorkspaceState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReleasedAt = null,
    string? OutcomeCode = null);

internal sealed class CodexTaskWorkspaceManager : ICodexTaskWorkspaceManager, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumRecords = 500;
    private static readonly TimeSpan GitCommandTimeout = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _dataRoot;

    public CodexTaskWorkspaceManager(
        IOptions<CodexTelegramOptions> options,
        TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        _dataRoot = GetDataRoot();
    }

    internal CodexTaskWorkspaceManager(
        IOptions<CodexTelegramOptions> options,
        TimeProvider timeProvider,
        string dataRootOverride)
    {
        _options = options;
        _timeProvider = timeProvider;
        _dataRoot = Path.GetFullPath(dataRootOverride);
    }

    public async Task<CodexTaskWorkspaceRecord> CreateAsync(
        string taskId,
        string repositoryRoot,
        string? baseRef,
        CancellationToken cancellationToken)
    {
        string normalizedTaskId = NormalizeRequiredToken(taskId, nameof(taskId), 160);
        string normalizedRepositoryRoot = NormalizeRepositoryRoot(repositoryRoot);
        string? normalizedBaseRef = NormalizeOptionalRef(baseRef);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkspaceState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            CodexTaskWorkspaceRecord? existing = state.Records
                .Where(record => string.Equals(record.TaskId, normalizedTaskId, StringComparison.Ordinal))
                .OrderByDescending(record => record.CreatedAt)
                .FirstOrDefault(record => record.State == CodexTaskWorkspaceState.Provisioned);
            if (existing is not null)
            {
                return existing;
            }

            int port = AllocatePort(state.Records);
            string workspaceId = $"workspace:{Guid.NewGuid():N}";
            string branch = BuildBranchName(normalizedTaskId, workspaceId);
            string worktreePath = BuildWorktreePath(normalizedTaskId, workspaceId);
            string databaseNamespace = BuildDatabaseNamespace(normalizedTaskId, workspaceId);
            Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

            try
            {
                await RunGitAsync(
                    normalizedRepositoryRoot,
                    ["worktree", "add", "-b", branch, worktreePath, normalizedBaseRef ?? "HEAD"],
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDeleteEmptyDirectory(worktreePath);
                throw;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            CodexTaskWorkspaceRecord record = new(
                workspaceId,
                normalizedTaskId,
                normalizedRepositoryRoot,
                worktreePath,
                branch,
                databaseNamespace,
                port,
                CodexTaskWorkspaceState.Provisioned,
                now,
                now);
            state.Records.Add(record);
            Trim(state);
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexTaskWorkspaceRecord?> GetAsync(string taskId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkspaceState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return state.Records
                .Where(record => string.Equals(record.TaskId, taskId.Trim(), StringComparison.Ordinal))
                .OrderByDescending(record => record.CreatedAt)
                .FirstOrDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CodexTaskWorkspaceRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkspaceState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return state.Records
                .OrderByDescending(record => record.UpdatedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexTaskWorkspaceRecord?> ReleaseAsync(
        string taskId,
        bool discardChanges,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkspaceState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            CodexTaskWorkspaceRecord? record = state.Records
                .Where(candidate => string.Equals(candidate.TaskId, taskId.Trim(), StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.CreatedAt)
                .FirstOrDefault(candidate => candidate.State == CodexTaskWorkspaceState.Provisioned);
            if (record is null)
            {
                return state.Records
                    .Where(candidate => string.Equals(candidate.TaskId, taskId.Trim(), StringComparison.Ordinal))
                    .OrderByDescending(candidate => candidate.CreatedAt)
                    .FirstOrDefault();
            }

            List<string> arguments = ["worktree", "remove"];
            if (discardChanges)
            {
                arguments.Add("--force");
            }

            arguments.Add(record.WorktreePath);
            await RunGitAsync(record.RepositoryRoot, arguments, cancellationToken).ConfigureAwait(false);

            DateTimeOffset now = _timeProvider.GetUtcNow();
            CodexTaskWorkspaceRecord released = record with
            {
                State = CodexTaskWorkspaceState.Released,
                UpdatedAt = now,
                ReleasedAt = now,
                OutcomeCode = discardChanges ? "released_discarded" : "released_clean",
            };
            int index = state.Records.IndexOf(record);
            state.Records[index] = released;
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return released;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private int AllocatePort(IReadOnlyList<CodexTaskWorkspaceRecord> records)
    {
        CodexWorkspaceOptions options = _options.Value.Workspace;
        int start = options.TaskDevelopmentPortRangeStart;
        int end = options.TaskDevelopmentPortRangeEnd;
        HashSet<int> reserved = records
            .Where(record => record.State == CodexTaskWorkspaceState.Provisioned)
            .Select(record => record.DevelopmentPort)
            .ToHashSet();
        HashSet<int> listening = GetListeningPorts();
        for (int port = start; port <= end; port++)
        {
            if (!reserved.Contains(port) && !listening.Contains(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException($"No task development port is available in the configured range {start}-{end}.");
    }

    private string BuildWorktreePath(string taskId, string workspaceId)
    {
        string configuredRoot = _options.Value.Workspace.TaskWorktreeRoot ?? Path.Combine(_dataRoot, "task-workspaces");
        string root = Path.GetFullPath(configuredRoot);
        string leaf = $"{ToSafeFileToken(taskId, 48)}-{workspaceId[^12..]}";
        return Path.Combine(root, leaf);
    }

    private static string BuildBranchName(string taskId, string workspaceId)
        => $"codex/task/{ToSafeBranchToken(taskId, 72)}-{workspaceId[^12..]}";

    private static string BuildDatabaseNamespace(string taskId, string workspaceId)
        => $"codex_task_{ToSafeFileToken(taskId, 40)}_{workspaceId[^12..]}";

    private async Task<WorkspaceState> LoadAsync(CancellationToken cancellationToken)
    {
        string path = GetStatePath();
        if (!File.Exists(path))
        {
            return new WorkspaceState { SchemaVersion = CurrentSchemaVersion };
        }

        await using FileStream stream = File.OpenRead(path);
        WorkspaceState? state = await JsonSerializer.DeserializeAsync<WorkspaceState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            throw new InvalidDataException("The task workspace state file was empty.");
        }

        state.Records ??= [];
        return state;
    }

    private async Task SaveAsync(WorkspaceState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataRoot);
        string path = GetStatePath();
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private void Trim(WorkspaceState state)
    {
        if (state.Records.Count <= MaximumRecords)
        {
            return;
        }

        state.Records = state.Records
            .OrderByDescending(record => record.UpdatedAt)
            .Take(MaximumRecords)
            .ToList();
    }

    private string GetStatePath() => Path.Combine(_dataRoot, "codex-task-workspaces.json");

    private string GetDataRoot()
    {
        string? configuredRoot = _options.Value.Workspace.DataRoot;
        return string.IsNullOrWhiteSpace(configuredRoot)
            ? CodexTelegramDataRoot.GetDefaultDataRoot(_options.Value.InstanceId)
            : Path.GetFullPath(configuredRoot);
    }

    private static string NormalizeRepositoryRoot(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            throw new ArgumentException("Repository root cannot be empty.", nameof(repositoryRoot));
        }

        string root = Path.GetFullPath(repositoryRoot.Trim());
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository root '{root}' does not exist.");
        }

        return root;
    }

    private static string NormalizeRequiredToken(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty.", parameterName);
        }

        string normalized = value.Trim();
        if (normalized.Length > maxLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException($"Value must be at most {maxLength} characters and cannot contain control characters.", parameterName);
        }

        return normalized;
    }

    private static string? NormalizeOptionalRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        return normalized.Length > 200 || normalized.Any(char.IsControl)
            ? throw new ArgumentException("Base ref must be at most 200 characters and cannot contain control characters.", nameof(value))
            : normalized;
    }

    private static string ToSafeFileToken(string value, int maxLength)
    {
        StringBuilder builder = new(maxLength);
        foreach (char character in value)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-');
        }

        string token = builder.ToString().Trim('.', '-', '_');
        return string.IsNullOrWhiteSpace(token) ? "task" : token;
    }

    private static string ToSafeBranchToken(string value, int maxLength)
        => ToSafeFileToken(value.Replace(':', '-'), maxLength);

    private static HashSet<int> GetListeningPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static async Task RunGitAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = repositoryRoot,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitCommandTimeout);
        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Git could not be started.");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                string detail = FirstLine(error) ?? FirstLine(output) ?? "unknown Git error";
                throw new InvalidOperationException($"Git worktree operation failed: {detail}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Git worktree operation timed out.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("Git is not available on PATH.", exception);
        }
    }

    private static string? FirstLine(string? text)
        => text?.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class WorkspaceState
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<CodexTaskWorkspaceRecord> Records { get; set; } = [];
    }
}
