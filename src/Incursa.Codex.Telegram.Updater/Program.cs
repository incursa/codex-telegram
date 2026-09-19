using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Incursa.Codex.Telegram.Updater;

internal static class Program
{
    private const int SuccessExitCode = 0;
    private const int FailedExitCode = 1;
    private const int AlreadyRunningExitCode = 2;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            CommandLine commandLine = CommandLine.Parse(args);
            if (commandLine.ShowHelp)
            {
                Console.WriteLine("codex-telegram-updater --config /etc/codex-telegram/updater.json");
                return SuccessExitCode;
            }

            UpdaterOptions options = await UpdaterOptions.LoadAsync(commandLine.ConfigPath, CancellationToken.None).ConfigureAwait(false);
            using FileStream? updateLock = TryAcquireLock(options.LockPath);
            if (updateLock is null)
            {
                Console.Error.WriteLine("Another host update is already in progress.");
                return AlreadyRunningExitCode;
            }

            HostUpdateUpdater updater = new(options);
            return await updater.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Host update was cancelled.");
            return FailedExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Host update failed: {exception.Message}");
            return FailedExitCode;
        }
    }

    private static FileStream? TryAcquireLock(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}

internal sealed class HostUpdateUpdater
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly UpdaterOptions _options;
    private readonly ProcessRunner _processRunner;
    private readonly HttpClient _httpClient;

    public HostUpdateUpdater(UpdaterOptions options)
    {
        _options = options;
        _processRunner = new ProcessRunner();
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(options.HealthRequestTimeoutSeconds, 1, 60)),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("codex-telegram-updater", "1.0"));
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.RequestPath))
        {
            return 0;
        }

        HostUpdateRequest request = await ReadAsync<HostUpdateRequest>(_options.RequestPath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The host update request was empty.");
        HostUpdateState? existingState = await ReadAsync<HostUpdateState>(_options.StatePath, cancellationToken).ConfigureAwait(false);

        if (existingState is not null
            && string.Equals(existingState.RequestId, request.RequestId, StringComparison.Ordinal)
            && IsTerminal(existingState.State))
        {
            DeleteRequest();
            return 0;
        }

        ValidateRequest(request);
        HostUpdateState applying = new(
            1,
            request.Action == HostUpdateAction.Rollback
                ? HostUpdateStateValue.RollbackApplying
                : HostUpdateStateValue.Applying,
            request.Action,
            request.RequestId,
            request.RequestedByUserId,
            request.ConversationKey,
            request.CurrentVersion,
            request.TargetVersion,
            request.RequestedAtUtc,
            DateTimeOffset.UtcNow,
            null,
            true);
        await WriteStateAsync(applying, cancellationToken).ConfigureAwait(false);
        await WaitForStartNotificationAsync(request, cancellationToken).ConfigureAwait(false);

        if (request.Action == HostUpdateAction.Rollback)
        {
            return await ApplyRollbackAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await ApplyUpdateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ApplyUpdateAsync(HostUpdateRequest request, CancellationToken cancellationToken)
    {
        string installedVersion = await GetInstalledVersionAsync(cancellationToken).ConfigureAwait(false);
        string? rollbackPackage = null;
        string? targetVersion = null;
        try
        {
            await RefreshAptIndexesAsync(cancellationToken).ConfigureAwait(false);
            rollbackPackage = await CacheInstalledPackageAsync(installedVersion, cancellationToken).ConfigureAwait(false);
            targetVersion = await ResolveTargetVersionAsync(request.TargetVersion, cancellationToken).ConfigureAwait(false);
            string? candidatePackage = await DownloadPackageAsync(targetVersion, request.ExpectedSha256, cancellationToken).ConfigureAwait(false);

            await StopServiceAsync(cancellationToken).ConfigureAwait(false);
            await InstallPackageAsync($"{_options.PackageName}={targetVersion}", allowDowngrade: true, cancellationToken).ConfigureAwait(false);
            await StartAndVerifyServiceAsync(cancellationToken).ConfigureAwait(false);

            await CompleteAsync(
                request,
                HostUpdateStateValue.Active,
                "health_verified",
                notificationPending: true,
                runningVersion: targetVersion,
                targetVersion: targetVersion,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            DeleteRequest();
            Console.WriteLine($"Updated {_options.PackageName} from {installedVersion} to {targetVersion}. Package: {candidatePackage ?? "APT cache"}.");
            return 0;
        }
        catch (Exception updateException) when (updateException is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Update attempt failed: {updateException.Message}");
            if (rollbackPackage is null)
            {
                await FailAsync(request, HostUpdateStateValue.Failed, "update_failed_before_rollback", cancellationToken).ConfigureAwait(false);
                DeleteRequest();
                return 1;
            }

            try
            {
                await StopServiceAsync(cancellationToken).ConfigureAwait(false);
                await InstallPackageAsync(rollbackPackage, allowDowngrade: true, cancellationToken).ConfigureAwait(false);
                await StartAndVerifyServiceAsync(cancellationToken).ConfigureAwait(false);
                await CompleteAsync(
                    request,
                    HostUpdateStateValue.RollbackActive,
                    "update_failed_rolled_back",
                    notificationPending: true,
                    runningVersion: installedVersion,
                    targetVersion: targetVersion ?? request.TargetVersion,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                DeleteRequest();
                Console.Error.WriteLine("The previous package was restored successfully.");
                return 1;
            }
            catch (Exception rollbackException) when (rollbackException is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Automatic rollback failed: {rollbackException.Message}");
                await FailAsync(request, HostUpdateStateValue.RollbackFailed, "update_and_rollback_failed", cancellationToken).ConfigureAwait(false);
                DeleteRequest();
                return 1;
            }
        }
    }

    private async Task<int> ApplyRollbackAsync(HostUpdateRequest request, CancellationToken cancellationToken)
    {
        RollbackManifest manifest = await ReadAsync<RollbackManifest>(_options.RollbackManifestPath, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No last-known-good package is available for rollback.");
        if (!File.Exists(manifest.PackagePath))
        {
            await FailAsync(request, HostUpdateStateValue.RollbackFailed, "rollback_package_missing", cancellationToken).ConfigureAwait(false);
            DeleteRequest();
            return 1;
        }

        string actualSha256 = await ComputeSha256Async(manifest.PackagePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualSha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await FailAsync(request, HostUpdateStateValue.RollbackFailed, "rollback_package_digest_mismatch", cancellationToken).ConfigureAwait(false);
            DeleteRequest();
            return 1;
        }

        try
        {
            await StopServiceAsync(cancellationToken).ConfigureAwait(false);
            await InstallPackageAsync(manifest.PackagePath, allowDowngrade: true, cancellationToken).ConfigureAwait(false);
            await StartAndVerifyServiceAsync(cancellationToken).ConfigureAwait(false);
            await CompleteAsync(
                request,
                HostUpdateStateValue.RollbackActive,
                "rollback_health_verified",
                notificationPending: true,
                runningVersion: manifest.Version,
                targetVersion: manifest.Version,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            DeleteRequest();
            Console.WriteLine($"Rolled back {_options.PackageName} to {manifest.Version}.");
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Rollback failed: {exception.Message}");
            await FailAsync(request, HostUpdateStateValue.RollbackFailed, "rollback_failed", cancellationToken).ConfigureAwait(false);
            DeleteRequest();
            return 1;
        }
    }

    private async Task WaitForStartNotificationAsync(
        HostUpdateRequest request,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(_options.StartNotificationTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            HostUpdateState? state = await ReadAsync<HostUpdateState>(_options.StatePath, cancellationToken).ConfigureAwait(false);
            if (state is not null
                && string.Equals(state.RequestId, request.RequestId, StringComparison.Ordinal)
                && !state.NotificationPending)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        Console.Error.WriteLine(
            $"The pre-update Telegram notification was not acknowledged within {_options.StartNotificationTimeoutSeconds} seconds; continuing with the host update.");
    }

    private async Task<string> CacheInstalledPackageAsync(string installedVersion, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.RollbackRoot);
        string? archivePath = await FindPackageArchiveAsync(installedVersion, cancellationToken).ConfigureAwait(false);
        if (archivePath is null)
        {
            await _processRunner.RunRequiredAsync(
                "/usr/bin/apt-get",
                ["--yes", "--download-only", "--reinstall", "--allow-downgrades", "--no-install-recommends", "install", $"{_options.PackageName}={installedVersion}"],
                TimeSpan.FromMinutes(_options.AptTimeoutMinutes),
                cancellationToken).ConfigureAwait(false);
            archivePath = await FindPackageArchiveAsync(installedVersion, cancellationToken).ConfigureAwait(false);
        }

        if (archivePath is null)
        {
            throw new FileNotFoundException($"APT did not leave a .deb for {_options.PackageName} version {installedVersion} in its cache.");
        }
        string destination = Path.Combine(_options.RollbackRoot, "last-known-good.deb");
        File.Copy(archivePath, destination, overwrite: true);
        string sha256 = await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);
        RollbackManifest manifest = new(
            _options.PackageName,
            installedVersion,
            await GetArchitectureAsync(cancellationToken).ConfigureAwait(false),
            destination,
            sha256,
            DateTimeOffset.UtcNow);
        await WriteAsync(_options.RollbackManifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    private async Task RefreshAptIndexesAsync(CancellationToken cancellationToken)
        => await _processRunner.RunRequiredAsync(
            "/usr/bin/apt-get",
            ["update"],
            TimeSpan.FromMinutes(_options.AptTimeoutMinutes),
            cancellationToken).ConfigureAwait(false);

    private async Task<string?> DownloadPackageAsync(
        string targetVersion,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        await _processRunner.RunRequiredAsync(
            "/usr/bin/apt-get",
            ["--yes", "--download-only", "--reinstall", "--allow-downgrades", "--no-install-recommends", "install", $"{_options.PackageName}={targetVersion}"],
            TimeSpan.FromMinutes(_options.AptTimeoutMinutes),
            cancellationToken).ConfigureAwait(false);

        string? archivePath = await FindPackageArchiveAsync(targetVersion, cancellationToken).ConfigureAwait(false);
        if (archivePath is null)
        {
            throw new FileNotFoundException($"APT did not leave a .deb for {_options.PackageName} version {targetVersion} in its cache.");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string actualSha256 = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualSha256, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The downloaded package digest {actualSha256} did not match the configured expected digest.");
            }
        }

        return archivePath;
    }

    private async Task<string> ResolveTargetVersionAsync(string? requestedVersion, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedVersion))
        {
            ValidateSafeVersion(requestedVersion);
            return requestedVersion.Trim();
        }

        ProcessResult result = await _processRunner.RunRequiredAsync(
            "/usr/bin/apt-cache",
            ["policy", _options.PackageName],
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        string? candidate = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("Candidate:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line["Candidate:".Length..].Trim())
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(candidate) || string.Equals(candidate, "(none)", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"APT has no candidate version for {_options.PackageName}.");
        }

        ValidateSafeVersion(candidate);
        return candidate;
    }

    private async Task<string?> FindPackageArchiveAsync(string version, CancellationToken cancellationToken)
    {
        string cacheDirectory = "/var/cache/apt/archives";
        if (!Directory.Exists(cacheDirectory))
        {
            return null;
        }

        foreach (string path in Directory.EnumerateFiles(cacheDirectory, "*.deb"))
        {
            ProcessResult result;
            try
            {
                result = await _processRunner.RunAsync(
                    "/usr/bin/dpkg-deb",
                    ["--field", path, "Package", "Version"],
                    TimeSpan.FromSeconds(30),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Ignoring unreadable APT archive {path}: {exception.Message}");
                continue;
            }

            if (result.ExitCode != 0)
            {
                continue;
            }

            string[] fields = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2
                && string.Equals(fields[0].Trim(), _options.PackageName, StringComparison.Ordinal)
                && string.Equals(fields[1].Trim(), version, StringComparison.Ordinal))
            {
                return path;
            }
        }

        return null;
    }

    private async Task<string> GetInstalledVersionAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await _processRunner.RunRequiredAsync(
            "/usr/bin/dpkg-query",
            ["--show", "--showformat=${Version}", _options.PackageName],
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        string version = result.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException($"The installed version of {_options.PackageName} could not be determined.");
        }

        ValidateSafeVersion(version);
        return version;
    }

    private async Task<string> GetArchitectureAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await _processRunner.RunRequiredAsync(
            "/usr/bin/dpkg",
            ["--print-architecture"],
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Trim();
    }

    private async Task InstallPackageAsync(string packageSpec, bool allowDowngrade, CancellationToken cancellationToken)
    {
        List<string> arguments = ["--yes", "--reinstall", "--no-install-recommends"];
        if (allowDowngrade)
        {
            arguments.Add("--allow-downgrades");
        }

        arguments.Add("install");
        arguments.Add(packageSpec);
        await _processRunner.RunRequiredAsync(
            "/usr/bin/apt-get",
            arguments,
            TimeSpan.FromMinutes(_options.AptTimeoutMinutes),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task StopServiceAsync(CancellationToken cancellationToken)
    {
        ProcessResult status = await _processRunner.RunAsync(
            "/usr/bin/systemctl",
            ["is-active", "--quiet", _options.ServiceName],
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (status.ExitCode == 0)
        {
            await _processRunner.RunRequiredAsync(
                "/usr/bin/systemctl",
                ["stop", _options.ServiceName],
                TimeSpan.FromSeconds(60),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartAndVerifyServiceAsync(CancellationToken cancellationToken)
    {
        await _processRunner.RunRequiredAsync(
            "/usr/bin/systemctl",
            ["restart", _options.ServiceName],
            TimeSpan.FromSeconds(120),
            cancellationToken).ConfigureAwait(false);

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(_options.HealthCheckTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ProcessResult active = await _processRunner.RunAsync(
                "/usr/bin/systemctl",
                ["is-active", "--quiet", _options.ServiceName],
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            if (active.ExitCode == 0 && await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.HealthCheckIntervalSeconds), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Service {_options.ServiceName} did not pass its post-restart health check within {_options.HealthCheckTimeoutSeconds} seconds.");
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.HealthCheckUrl))
        {
            return true;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(_options.HealthCheckUrl, cancellationToken).ConfigureAwait(false);
            return response.StatusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task CompleteAsync(
        HostUpdateRequest request,
        HostUpdateStateValue state,
        string outcomeCode,
        bool notificationPending,
        string? runningVersion,
        string? targetVersion,
        CancellationToken cancellationToken)
        => await WriteStateAsync(new HostUpdateState(
            1,
            state,
            request.Action,
            request.RequestId,
            request.RequestedByUserId,
            request.ConversationKey,
            runningVersion ?? request.CurrentVersion,
            targetVersion ?? request.TargetVersion,
            request.RequestedAtUtc,
            DateTimeOffset.UtcNow,
            outcomeCode,
            notificationPending), cancellationToken).ConfigureAwait(false);

    private async Task FailAsync(
        HostUpdateRequest request,
        HostUpdateStateValue state,
        string outcomeCode,
        CancellationToken cancellationToken)
        => await CompleteAsync(
            request,
            state,
            outcomeCode,
            notificationPending: true,
            runningVersion: request.CurrentVersion,
            targetVersion: request.TargetVersion,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    private async Task WriteStateAsync(HostUpdateState state, CancellationToken cancellationToken)
        => await WriteAsync(_options.StatePath, state, cancellationToken).ConfigureAwait(false);

    private async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void DeleteRequest()
    {
        if (File.Exists(_options.RequestPath))
        {
            File.Delete(_options.RequestPath);
        }
    }

    private static void ValidateRequest(HostUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId)
            || request.RequestId.Length > 80
            || request.RequestId.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
        {
            throw new InvalidDataException("The host update request ID is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.ConversationKey) || request.ConversationKey.Length > 200)
        {
            throw new InvalidDataException("The host update conversation key is invalid.");
        }

        if (!string.IsNullOrWhiteSpace(request.TargetVersion))
        {
            ValidateSafeVersion(request.TargetVersion);
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256)
            && (request.ExpectedSha256.Length != 64 || request.ExpectedSha256.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new InvalidDataException("The host update SHA-256 value is invalid.");
        }
    }

    private static void ValidateSafeVersion(string version)
    {
        string trimmed = version.Trim();
        if (trimmed.Length is 0 or > 80
            || trimmed.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '+' or ':' or '~' or '-' or '_')))
        {
            throw new InvalidDataException("The Debian package version contains unsupported characters.");
        }
    }

    private static bool IsTerminal(HostUpdateStateValue state)
        => state is HostUpdateStateValue.Active
            or HostUpdateStateValue.Failed
            or HostUpdateStateValue.RollbackActive
            or HostUpdateStateValue.RollbackFailed;

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal sealed class ProcessRunner
{
    public async Task<ProcessResult> RunRequiredAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(fileName, arguments, timeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited with code {result.ExitCode}: {detail}");
        }

        return result;
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start {fileName}.");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process may have exited at the same time as the timeout.
            }

            throw;
        }

        string standardOutput = await outputTask.ConfigureAwait(false);
        string standardError = await errorTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class UpdaterOptions
{
    public string PackageName { get; init; } = "codex-telegram";
    public string ServiceName { get; init; } = "codex-telegram.service";
    public string DataRoot { get; set; } = "/var/lib/codex-telegram";
    public string RollbackRoot { get; set; } = "/var/cache/codex-telegram/rollback";
    public string HealthCheckUrl { get; init; } = "http://127.0.0.1:5287/health";
    public int HealthCheckTimeoutSeconds { get; init; } = 120;
    public int HealthCheckIntervalSeconds { get; init; } = 2;
    public int HealthRequestTimeoutSeconds { get; init; } = 5;
    public int AptTimeoutMinutes { get; init; } = 30;
    public int StartNotificationTimeoutSeconds { get; init; } = 15;

    [JsonIgnore]
    public string RequestPath => Path.Combine(DataRoot, "codex-host-update-request.json");

    [JsonIgnore]
    public string StatePath => Path.Combine(DataRoot, "codex-host-update-state.json");

    [JsonIgnore]
    public string LockPath => Path.Combine(DataRoot, "codex-host-update.lock");

    [JsonIgnore]
    public string RollbackManifestPath => Path.Combine(RollbackRoot, "last-known-good.json");

    public static async Task<UpdaterOptions> LoadAsync(string? configPath, CancellationToken cancellationToken)
    {
        string path = string.IsNullOrWhiteSpace(configPath)
            ? "/etc/codex-telegram/updater.json"
            : Path.GetFullPath(configPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Updater configuration was not found: {path}");
        }

        await using FileStream stream = File.OpenRead(path);
        UpdaterOptions options = await JsonSerializer.DeserializeAsync<UpdaterOptions>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Updater configuration was empty.");
        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(PackageName)
            || PackageName.Length > 100
            || PackageName.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '+' or '.')))
        {
            throw new InvalidDataException("Updater PackageName is invalid.");
        }

        if (string.IsNullOrWhiteSpace(ServiceName)
            || ServiceName.Length > 200
            || ServiceName.Any(char.IsWhiteSpace)
            || ServiceName.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Updater ServiceName is invalid.");
        }

        DataRoot = Path.GetFullPath(DataRoot);
        RollbackRoot = Path.GetFullPath(RollbackRoot);
        if (HealthCheckTimeoutSeconds is < 5 or > 3600
            || HealthCheckIntervalSeconds is < 1 or > 60
            || HealthRequestTimeoutSeconds is < 1 or > 60
            || AptTimeoutMinutes is < 1 or > 240
            || StartNotificationTimeoutSeconds is < 1 or > 120)
        {
            throw new InvalidDataException("Updater timing values are outside their supported bounds.");
        }

        if (!string.IsNullOrWhiteSpace(HealthCheckUrl)
            && (!Uri.TryCreate(HealthCheckUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")))
        {
            throw new InvalidDataException("Updater HealthCheckUrl must be an absolute HTTP or HTTPS URL when configured.");
        }
    }
}

internal sealed record RollbackManifest(
    string PackageName,
    string Version,
    string Architecture,
    string PackagePath,
    string Sha256,
    DateTimeOffset CachedAtUtc);

internal sealed record HostUpdateRequest(
    int SchemaVersion,
    string RequestId,
    HostUpdateAction Action,
    DateTimeOffset RequestedAtUtc,
    long RequestedByUserId,
    string ConversationKey,
    string CurrentVersion,
    string? TargetVersion,
    string? ExpectedSha256);

internal sealed record HostUpdateState(
    int SchemaVersion,
    HostUpdateStateValue State,
    HostUpdateAction? Action,
    string? RequestId,
    long? RequestedByUserId,
    string? ConversationKey,
    string? CurrentVersion,
    string? TargetVersion,
    DateTimeOffset? RequestedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    string? OutcomeCode,
    bool NotificationPending);

internal enum HostUpdateAction
{
    Update,
    Rollback,
}

internal enum HostUpdateStateValue
{
    None,
    Requested,
    Applying,
    Active,
    Failed,
    RollbackRequested,
    RollbackApplying,
    RollbackActive,
    RollbackFailed,
}

internal sealed record CommandLine(string? ConfigPath, bool ShowHelp)
{
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        string? configPath = null;
        bool showHelp = false;
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            if (arg is "--help" or "-h")
            {
                showHelp = true;
            }
            else if (arg == "--config")
            {
                if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
                {
                    throw new ArgumentException("--config requires a path.");
                }

                configPath = args[index];
            }
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
            {
                configPath = arg["--config=".Length..];
            }
            else if (arg == "--once")
            {
                // The updater is intentionally one-shot. The flag makes service intent explicit.
            }
            else
            {
                throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return new CommandLine(configPath, showHelp);
    }
}
