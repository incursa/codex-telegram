using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal interface ICodexWorkerUpdateManager
{
    Task<CodexWorkerUpdateSnapshot> GetStatusAsync(CancellationToken cancellationToken);

    Task<CodexWorkerUpdateSnapshot> StageAsync(CancellationToken cancellationToken);

    Task<CodexWorkerUpdateSnapshot> StageRollbackAsync(CancellationToken cancellationToken);

    Task<CodexWorkerUpdateSnapshot> CompleteAsync(
        CodexWorkerUpdateCompletion completion,
        CancellationToken cancellationToken);
}

internal sealed record CodexWorkerUpdateCompletion(string Version, string Sha256, bool Healthy);

internal enum CodexWorkerUpdateState
{
    None,
    Staged,
    RollbackStaged,
    Active,
    HealthFailed,
    RollbackActive,
    Rejected,
}

internal sealed record CodexWorkerUpdateSnapshot(
    CodexWorkerUpdateState State,
    string? TargetVersion,
    string? ExpectedSha256,
    string? StagedPackageName,
    string? RollbackPackageName,
    DateTimeOffset? UpdatedAtUtc,
    string? OutcomeCode);

/// <summary>
/// Verifies and stages worker packages without modifying the protected running installation.
/// An external service installer consumes the staged package after the worker has drained.
/// </summary>
internal sealed class CodexWorkerUpdateManager : ICodexWorkerUpdateManager, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly ICodexWorkerRegistry _workerRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly string _dataRoot;

    public CodexWorkerUpdateManager(
        IOptions<CodexTelegramOptions> options,
        ICodexWorkerRegistry workerRegistry,
        TimeProvider timeProvider)
    {
        _options = options;
        _workerRegistry = workerRegistry;
        _timeProvider = timeProvider;
        _dataRoot = GetDataRoot();
    }

    internal CodexWorkerUpdateManager(
        IOptions<CodexTelegramOptions> options,
        ICodexWorkerRegistry workerRegistry,
        TimeProvider timeProvider,
        string dataRootOverride)
    {
        _options = options;
        _workerRegistry = workerRegistry;
        _timeProvider = timeProvider;
        _dataRoot = Path.GetFullPath(dataRootOverride);
    }

    public async Task<CodexWorkerUpdateSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ToSnapshot(await LoadAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexWorkerUpdateSnapshot> StageAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CodexWorkerUpdateOptions options = _options.Value.Updates;
            WorkerUpdateState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!options.Enabled)
            {
                return ToSnapshot(state with { State = CodexWorkerUpdateState.Rejected, OutcomeCode = "updates_disabled" });
            }

            CodexWorkerSnapshot worker = await _workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (worker.State != CodexWorkerState.Draining || worker.ActiveLeaseCount != 0)
            {
                return await RejectAsync(state, "worker_must_be_drained", cancellationToken).ConfigureAwait(false);
            }

            string packagePath = Path.GetFullPath(options.PackagePath!.Trim());
            if (!File.Exists(packagePath))
            {
                return await RejectAsync(state, "package_not_found", cancellationToken).ConfigureAwait(false);
            }

            await using FileStream packageStream = File.OpenRead(packagePath);
            string actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            string expectedSha256 = options.ExpectedSha256!.Trim().ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualSha256),
                    Convert.FromHexString(expectedSha256)))
            {
                return await RejectAsync(state, "package_hash_mismatch", cancellationToken).ConfigureAwait(false);
            }

            string? packageVersion = AssemblyName.GetAssemblyName(packagePath).Version?.ToString();
            if (!VersionsMatch(packageVersion, options.TargetVersion!))
            {
                return await RejectAsync(state, "package_version_mismatch", cancellationToken).ConfigureAwait(false);
            }

            if (options.RequiredCapabilities.Any(required => !worker.Capabilities.Contains(required, StringComparer.Ordinal)))
            {
                return await RejectAsync(state, "worker_capability_mismatch", cancellationToken).ConfigureAwait(false);
            }

            string stageRoot = GetStageRoot(options);
            Directory.CreateDirectory(stageRoot);
            string stagedPackageName = $"codex-telegram-{ToSafeToken(options.TargetVersion!, 40)}.staged";
            string stagedPackagePath = Path.Combine(stageRoot, stagedPackageName);
            CopyIfDifferent(packagePath, stagedPackagePath);

            string currentBinaryPath = Environment.ProcessPath ?? throw new InvalidOperationException("The current worker executable path is unavailable.");
            string rollbackVersion = GetCurrentVersion();
            string rollbackSha256 = await ComputeSha256Async(currentBinaryPath, cancellationToken).ConfigureAwait(false);
            string rollbackPackageName = $"codex-telegram-last-known-good-{ToSafeToken(rollbackVersion, 40)}.bin";
            string rollbackPackagePath = Path.Combine(stageRoot, rollbackPackageName);
            CopyIfDifferent(currentBinaryPath, rollbackPackagePath);

            WorkerUpdateState staged = new(
                CurrentSchemaVersion,
                CodexWorkerUpdateState.Staged,
                options.TargetVersion!.Trim(),
                expectedSha256,
                stagedPackageName,
                rollbackPackageName,
                rollbackVersion,
                rollbackSha256,
                _timeProvider.GetUtcNow(),
                "package_verified_and_staged");
            await SaveAsync(staged, cancellationToken).ConfigureAwait(false);
            return ToSnapshot(staged);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or FileNotFoundException or FormatException)
        {
            WorkerUpdateState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return await RejectAsync(state, "staging_failed", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexWorkerUpdateSnapshot> StageRollbackAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkerUpdateState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state.State is not (CodexWorkerUpdateState.Staged or CodexWorkerUpdateState.HealthFailed)
                || string.IsNullOrWhiteSpace(state.RollbackPackageName))
            {
                return await RejectAsync(state, "no_staged_update", cancellationToken).ConfigureAwait(false);
            }

            CodexWorkerSnapshot worker = await _workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (worker.State != CodexWorkerState.Draining || worker.ActiveLeaseCount != 0)
            {
                return await RejectAsync(state, "worker_must_be_drained", cancellationToken).ConfigureAwait(false);
            }

            string rollbackPath = Path.Combine(GetStageRoot(_options.Value.Updates), state.RollbackPackageName);
            if (!File.Exists(rollbackPath))
            {
                return await RejectAsync(state, "rollback_package_missing", cancellationToken).ConfigureAwait(false);
            }

            WorkerUpdateState rollback = state with
            {
                State = CodexWorkerUpdateState.RollbackStaged,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                OutcomeCode = "rollback_package_staged",
            };
            await SaveAsync(rollback, cancellationToken).ConfigureAwait(false);
            return ToSnapshot(rollback);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexWorkerUpdateSnapshot> CompleteAsync(
        CodexWorkerUpdateCompletion completion,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkerUpdateState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            bool rollback = state.State == CodexWorkerUpdateState.RollbackStaged;
            if (state.State is not (CodexWorkerUpdateState.Staged or CodexWorkerUpdateState.RollbackStaged))
            {
                return await RejectAsync(state, "no_staged_update", cancellationToken).ConfigureAwait(false);
            }

            string expectedVersion = rollback ? state.RollbackVersion ?? string.Empty : state.TargetVersion ?? string.Empty;
            string expectedSha256 = rollback ? state.RollbackSha256 ?? string.Empty : state.ExpectedSha256 ?? string.Empty;
            CodexWorkerSnapshot worker = await _workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            bool verified = completion.Healthy
                && worker.State == CodexWorkerState.Online
                && string.Equals(worker.Readiness, "ready", StringComparison.OrdinalIgnoreCase)
                && VersionsMatch(completion.Version, expectedVersion)
                && HashesMatch(completion.Sha256, expectedSha256);
            WorkerUpdateState completed = state with
            {
                State = verified
                    ? rollback ? CodexWorkerUpdateState.RollbackActive : CodexWorkerUpdateState.Active
                    : CodexWorkerUpdateState.HealthFailed,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                OutcomeCode = verified
                    ? rollback ? "rollback_health_verified" : "health_verified"
                    : rollback ? "rollback_health_verification_failed" : "health_verification_failed",
            };
            await SaveAsync(completed, cancellationToken).ConfigureAwait(false);
            return ToSnapshot(completed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<CodexWorkerUpdateSnapshot> RejectAsync(
        WorkerUpdateState state,
        string outcomeCode,
        CancellationToken cancellationToken)
    {
        WorkerUpdateState rejected = state with
        {
            State = CodexWorkerUpdateState.Rejected,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            OutcomeCode = outcomeCode,
        };
        await SaveAsync(rejected, cancellationToken).ConfigureAwait(false);
        return ToSnapshot(rejected);
    }

    private async Task<WorkerUpdateState> LoadAsync(CancellationToken cancellationToken)
    {
        string path = GetStatePath();
        if (!File.Exists(path))
        {
            return new WorkerUpdateState(CurrentSchemaVersion, CodexWorkerUpdateState.None, null, null, null, null, null, null, null, null);
        }

        await using FileStream stream = File.OpenRead(path);
        WorkerUpdateState? state = await JsonSerializer.DeserializeAsync<WorkerUpdateState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        return state ?? throw new InvalidDataException("The worker update state file was empty.");
    }

    private async Task SaveAsync(WorkerUpdateState state, CancellationToken cancellationToken)
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

    private string GetDataRoot()
    {
        string? configuredRoot = _options.Value.Workspace.DataRoot;
        return string.IsNullOrWhiteSpace(configuredRoot)
            ? CodexTelegramDataRoot.GetDefaultDataRoot(_options.Value.InstanceId)
            : Path.GetFullPath(configuredRoot);
    }

    private string GetStageRoot(CodexWorkerUpdateOptions options)
        => string.IsNullOrWhiteSpace(options.StageRoot)
            ? Path.Combine(_dataRoot, "worker-updates")
            : Path.GetFullPath(options.StageRoot);

    private string GetStatePath() => Path.Combine(_dataRoot, "codex-worker-update-state.json");

    private string GetCurrentVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static CodexWorkerUpdateSnapshot ToSnapshot(WorkerUpdateState state)
        => new(state.State, state.TargetVersion, state.ExpectedSha256, state.StagedPackageName, state.RollbackPackageName, state.UpdatedAtUtc, state.OutcomeCode);

    private static bool VersionsMatch(string? actual, string expected)
        => Version.TryParse(actual, out Version? actualVersion)
            && Version.TryParse(expected, out Version? expectedVersion)
            && actualVersion.Major == expectedVersion.Major
            && actualVersion.Minor == expectedVersion.Minor
            && actualVersion.Build == expectedVersion.Build;

    private static bool HashesMatch(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected)
            || actual.Trim().Length != 64 || expected.Trim().Length != 64
            || !actual.Trim().All(char.IsAsciiHexDigit) || !expected.Trim().All(char.IsAsciiHexDigit))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual.Trim()),
            Convert.FromHexString(expected.Trim()));
    }

    private static void CopyIfDifferent(string sourcePath, string destinationPath)
    {
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static string ToSafeToken(string value, int maxLength)
        => new(value.Trim().Take(maxLength).Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '-').ToArray());

    private sealed record WorkerUpdateState(
        int SchemaVersion,
        CodexWorkerUpdateState State,
        string? TargetVersion,
        string? ExpectedSha256,
        string? StagedPackageName,
        string? RollbackPackageName,
        string? RollbackVersion,
        string? RollbackSha256,
        DateTimeOffset? UpdatedAtUtc,
        string? OutcomeCode);
}
