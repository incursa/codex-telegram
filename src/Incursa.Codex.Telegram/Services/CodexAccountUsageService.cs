using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Reads account-level Codex usage windows from the local Codex app-server.
/// </summary>
internal interface ICodexAccountUsageService
{
    /// <summary>
    /// Gets the latest account usage snapshot reported by Codex.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the app-server query.</param>
    /// <returns>Current Codex rate-limit usage.</returns>
    Task<CodexAccountUsageVm> GetUsageAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Queries the Codex app-server account/rateLimits endpoint without waiting on SDK package support.
/// </summary>
internal sealed class CodexAccountUsageService : ICodexAccountUsageService
{
    private const string ApiKeyEnvironmentVariable = "CODEX_API_KEY";
    private const string OriginatorEnvironmentVariable = "CODEX_INTERNAL_ORIGINATOR_OVERRIDE";
    private const string OriginatorMarker = "codex_telegram_rate_limits";
    private const string StdioEndpoint = "stdio://";
    private const string InitializeMethod = "initialize";
    private const string InitializedMethod = "initialized";
    private const string RateLimitsReadMethod = "account/rateLimits/read";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly CodexClientOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CodexAccountUsageService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodexAccountUsageService"/> class.
    /// </summary>
    /// <param name="options">Codex client options used to launch the local app-server.</param>
    /// <param name="timeProvider">Clock used to stamp returned snapshots.</param>
    /// <param name="logger">Logger for app-server diagnostics.</param>
    public CodexAccountUsageService(
        IOptions<CodexClientOptions> options,
        TimeProvider timeProvider,
        ILogger<CodexAccountUsageService> logger)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CodexAccountUsageVm> GetUsageAsync(CancellationToken cancellationToken)
    {
        string executablePath = ResolveCodexExecutable(_options);
        ProcessStartInfo startInfo = BuildStartInfo(executablePath, _options);

        using Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start Codex process '{executablePath}'.");
        }

        await using JsonRpcProcessClient rpc = new(process, _logger);
        JsonObject initializeResponse = await rpc.RequestObjectAsync(InitializeMethod, CreateInitializeRequest(_options), cancellationToken).ConfigureAwait(false);
        if (initializeResponse.Count == 0)
        {
            _logger.LogDebug("Codex app-server returned an empty initialize response before reading account usage.");
        }

        await rpc.NotifyAsync(InitializedMethod, new JsonObject(), cancellationToken).ConfigureAwait(false);
        JsonObject response = await rpc.RequestObjectAsync(RateLimitsReadMethod, new JsonObject(), cancellationToken).ConfigureAwait(false);
        return ParseRateLimitResponse(response, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Parses the app-server rate-limit response into Telegram view models.
    /// </summary>
    /// <param name="payload">Raw JSON-RPC result payload.</param>
    /// <param name="retrievedAtUtc">UTC time when the payload was retrieved.</param>
    /// <returns>Parsed account usage snapshot.</returns>
    internal static CodexAccountUsageVm ParseRateLimitResponse(JsonObject payload, DateTimeOffset retrievedAtUtc)
    {
        List<CodexRateLimitSnapshotVm> limits = [];

        if (GetObject(payload, "rateLimitsByLimitId", "rate_limits_by_limit_id") is { Count: > 0 } byLimitId)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in byLimitId)
            {
                if (pair.Value is JsonObject snapshot)
                {
                    limits.Add(ParseRateLimitSnapshot(snapshot, pair.Key));
                }
            }
        }

        if (limits.Count == 0 && GetObject(payload, "rateLimits", "rate_limits") is { } legacySnapshot)
        {
            limits.Add(ParseRateLimitSnapshot(legacySnapshot, fallbackLimitId: null));
        }

        return new CodexAccountUsageVm(retrievedAtUtc.ToUniversalTime(), limits);
    }

    private static CodexRateLimitSnapshotVm ParseRateLimitSnapshot(JsonObject payload, string? fallbackLimitId)
        => new(
            GetString(payload, "limitId", "limit_id") ?? fallbackLimitId,
            GetString(payload, "limitName", "limit_name"),
            GetString(payload, "planType", "plan_type"),
            GetString(payload, "rateLimitReachedType", "rate_limit_reached_type"),
            ParseWindow(GetObject(payload, "primary")),
            ParseWindow(GetObject(payload, "secondary")));

    private static CodexRateLimitWindowVm? ParseWindow(JsonObject? payload)
    {
        if (payload is null)
        {
            return null;
        }

        int usedPercent = Math.Clamp(GetInt(payload, "usedPercent", "used_percent") ?? 0, 0, 100);
        long? resetSeconds = GetLong(payload, "resetsAt", "resets_at");
        DateTimeOffset? resetsAtUtc = resetSeconds.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(resetSeconds.Value)
            : null;

        return new CodexRateLimitWindowVm(
            usedPercent,
            resetsAtUtc,
            GetLong(payload, "windowDurationMins", "windowDurationMinutes", "window_minutes"));
    }

    private static JsonObject CreateInitializeRequest(CodexClientOptions options)
        => new()
        {
            ["clientInfo"] = new JsonObject
            {
                ["name"] = string.IsNullOrWhiteSpace(options.ClientName) ? "Incursa.Codex.Telegram" : options.ClientName!,
                ["title"] = string.IsNullOrWhiteSpace(options.ClientTitle) ? "Incursa Codex Telegram" : options.ClientTitle!,
                ["version"] = string.IsNullOrWhiteSpace(options.ClientVersion)
                    ? typeof(CodexAccountUsageService).Assembly.GetName().Version?.ToString() ?? "0.0.0"
                    : options.ClientVersion!,
            },
            ["capabilities"] = new JsonObject
            {
                ["experimentalApi"] = true,
            },
        };

    private static ProcessStartInfo BuildStartInfo(string executablePath, CodexClientOptions options)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };

        foreach (string argument in BuildLaunchArguments(options))
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (KeyValuePair<string, string> pair in BuildEnvironment(options))
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return startInfo;
    }

    private static IReadOnlyList<string> BuildLaunchArguments(CodexClientOptions options)
    {
        List<string> args = [];
        foreach (string overrideValue in FlattenConfigOverrides(options.Config))
        {
            args.Add("--config");
            args.Add(overrideValue);
        }

        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            args.Add("--config");
            args.Add($"openai_base_url={JsonSerializer.Serialize(options.BaseUrl)}");
        }

        args.Add("app-server");
        args.Add("--listen");
        args.Add(StdioEndpoint);
        return args;
    }

    private static IReadOnlyDictionary<string, string> BuildEnvironment(CodexClientOptions options)
    {
        Dictionary<string, string> environment = options.Environment is null
            ? Environment.GetEnvironmentVariables()
                .Cast<DictionaryEntry>()
                .Where(entry => entry.Key is string && entry.Value is string)
                .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal)
            : new Dictionary<string, string>(options.Environment, StringComparer.Ordinal);

        environment[OriginatorEnvironmentVariable] = OriginatorMarker;
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            environment[ApiKeyEnvironmentVariable] = options.ApiKey!;
        }

        return environment;
    }

    private static string ResolveCodexExecutable(CodexClientOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CodexPathOverride))
        {
            return options.CodexPathOverride!;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new FileNotFoundException("Unable to locate the Codex executable. Set Codex:CodexPathOverride or ensure `codex` is on PATH.");
        }

        string[] candidates = OperatingSystem.IsWindows()
            ? ["codex.exe", "codex.cmd", "codex.bat", "codex"]
            : ["codex"];

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string candidate in candidates)
            {
                string fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        throw new FileNotFoundException("Unable to locate the Codex executable. Set Codex:CodexPathOverride or ensure `codex` is on PATH.");
    }

    private static IReadOnlyList<string> FlattenConfigOverrides(CodexConfigObject? config)
    {
        if (config is null)
        {
            return [];
        }

        List<string> overrides = [];
        foreach (KeyValuePair<string, CodexConfigValue> pair in config.Values)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                FlattenConfigValue(pair.Key, pair.Value, overrides);
            }
        }

        return overrides;
    }

    private static void FlattenConfigValue(string path, CodexConfigValue value, List<string> overrides)
    {
        if (value is CodexConfigObject obj)
        {
            foreach (KeyValuePair<string, CodexConfigValue> pair in obj.Values)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    FlattenConfigValue($"{path}.{pair.Key}", pair.Value, overrides);
                }
            }

            return;
        }

        overrides.Add($"{path}={ToTomlLiteral(value)}");
    }

    private static string ToTomlLiteral(CodexConfigValue value)
        => value switch
        {
            CodexConfigStringValue text => JsonSerializer.Serialize(text.Value),
            CodexConfigNumberValue number => number.Value.ToString("R", CultureInfo.InvariantCulture),
            CodexConfigBooleanValue boolean => boolean.Value ? "true" : "false",
            CodexConfigArrayValue array => ToJsonNode(array).ToJsonString(),
            CodexConfigObject obj => ToJsonNode(obj).ToJsonString(),
            _ => JsonSerializer.Serialize(value.ToString()),
        };

    private static JsonNode ToJsonNode(CodexConfigValue value)
        => value switch
        {
            CodexConfigStringValue text => JsonValue.Create(text.Value)!,
            CodexConfigNumberValue number => JsonValue.Create(number.Value)!,
            CodexConfigBooleanValue boolean => JsonValue.Create(boolean.Value)!,
            CodexConfigArrayValue array => new JsonArray(array.Items.Select(ToJsonNode).ToArray()),
            CodexConfigObject obj => ToJsonObject(obj),
            _ => JsonValue.Create(value.ToString())!,
        };

    private static JsonObject ToJsonObject(CodexConfigObject obj)
    {
        JsonObject json = new();
        foreach (KeyValuePair<string, CodexConfigValue> pair in obj.Values)
        {
            json[pair.Key] = ToJsonNode(pair.Value);
        }

        return json;
    }

    private static JsonObject? GetObject(JsonObject payload, params string[] names)
    {
        foreach (string name in names)
        {
            if (payload.TryGetPropertyValue(name, out JsonNode? node) && node is JsonObject obj)
            {
                return obj;
            }
        }

        return null;
    }

    private static string? GetString(JsonObject payload, params string[] names)
    {
        foreach (string name in names)
        {
            if (payload.TryGetPropertyValue(name, out JsonNode? node)
                && node is JsonValue value
                && value.TryGetValue(out string? result))
            {
                return result;
            }
        }

        return null;
    }

    private static int? GetInt(JsonObject payload, params string[] names)
    {
        long? value = GetLong(payload, names);
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value > int.MaxValue
            ? int.MaxValue
            : value.Value < int.MinValue ? int.MinValue : (int)value.Value;
    }

    private static long? GetLong(JsonObject payload, params string[] names)
    {
        foreach (string name in names)
        {
            if (!payload.TryGetPropertyValue(name, out JsonNode? node) || node is not JsonValue value)
            {
                continue;
            }

            if (value.TryGetValue(out long longValue))
            {
                return longValue;
            }

            if (value.TryGetValue(out int intValue))
            {
                return intValue;
            }

            if (value.TryGetValue(out double doubleValue))
            {
                return (long)Math.Round(doubleValue);
            }

            if (value.TryGetValue(out string? stringValue)
                && long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private sealed class JsonRpcProcessClient : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly List<string> _stderrLines = [];
        private readonly Task _stderrTask;
        private bool _disposed;

        public JsonRpcProcessClient(Process process, ILogger logger)
        {
            _process = process;
            _logger = logger;
            _stderrTask = Task.Run(() => DrainStderrAsync(_shutdown.Token));
        }

        public async Task<JsonObject> RequestObjectAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
        {
            JsonNode? result = await RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            return result as JsonObject
                ?? throw new InvalidOperationException($"Codex app-server method '{method}' returned a non-object response.");
        }

        public async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
        {
            string id = Guid.NewGuid().ToString("N");
            await WriteMessageAsync(new JsonObject
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            }, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new InvalidOperationException($"Codex app-server closed stdout before responding to '{method}'. stderr_tail={GetStderrTail()}");
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonObject message = ParseMessage(line);
                if (TryHandleServerRequest(message, cancellationToken))
                {
                    continue;
                }

                if (TryGetResponseId(message, out string? responseId) && string.Equals(responseId, id, StringComparison.Ordinal))
                {
                    if (message.TryGetPropertyValue("error", out JsonNode? errorNode) && errorNode is JsonObject error)
                    {
                        throw new InvalidOperationException($"Codex app-server method '{method}' failed: {FormatJsonRpcError(error)}");
                    }

                    return message.TryGetPropertyValue("result", out JsonNode? resultNode) ? resultNode : null;
                }
            }
        }

        public async Task NotifyAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
            => await WriteMessageAsync(new JsonObject
            {
                ["method"] = method,
                ["params"] = parameters,
            }, cancellationToken).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            _writeGate.Dispose();

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to stop Codex app-server usage probe cleanly.");
            }

            try
            {
                await _stderrTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // The process is already being torn down; stderr drain completion is best-effort.
            }
            finally
            {
                _shutdown.Dispose();
            }
        }

        private async Task WriteMessageAsync(JsonObject message, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _process.StandardInput.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private bool TryHandleServerRequest(JsonObject message, CancellationToken cancellationToken)
        {
            if (!message.TryGetPropertyValue("method", out _)
                || !TryGetResponseId(message, out string? requestId)
                || string.IsNullOrWhiteSpace(requestId))
            {
                return false;
            }

            WriteMessageAsync(new JsonObject
            {
                ["id"] = requestId,
                ["result"] = new JsonObject(),
            }, cancellationToken).GetAwaiter().GetResult();
            return true;
        }

        private async Task DrainStderrAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    lock (_stderrLines)
                    {
                        _stderrLines.Add(line);
                        if (_stderrLines.Count > 80)
                        {
                            _stderrLines.RemoveAt(0);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (_stderrLines)
                {
                    _stderrLines.Add($"[stderr reader failed] {exception.Message}");
                }
            }
        }

        private string GetStderrTail()
        {
            lock (_stderrLines)
            {
                return string.Join(Environment.NewLine, _stderrLines.TakeLast(20));
            }
        }

        private static JsonObject ParseMessage(string line)
        {
            JsonNode? node = JsonNode.Parse(line);
            return node as JsonObject
                ?? throw new InvalidOperationException("Codex app-server returned a malformed JSON-RPC message.");
        }

        private static bool TryGetResponseId(JsonObject message, out string? id)
        {
            id = null;
            if (!message.TryGetPropertyValue("id", out JsonNode? idNode) || idNode is null)
            {
                return false;
            }

            id = idNode.GetValue<string>();
            return true;
        }

        private static string FormatJsonRpcError(JsonObject error)
        {
            string? message = GetString(error, "message");
            int? code = GetInt(error, "code");
            return code.HasValue
                ? $"{code.Value.ToString(CultureInfo.InvariantCulture)} {message ?? "unknown error"}"
                : message ?? error.ToJsonString();
        }
    }
}
