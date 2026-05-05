using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Incursa.Codex.Telegram.Configuration;

internal sealed class LocalSettingsStore
{
    public const string FileName = "appsettings.Local.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly JsonObject _root;

    public LocalSettingsStore(string filePath, JsonObject? root = null)
    {
        FilePath = Path.GetFullPath(filePath);
        _root = root ?? [];
    }

    public string FilePath { get; }

    public static string ResolveDefaultPath()
        => Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, FileName));

    public static LocalSettingsStore Load(string? filePath = null)
    {
        string resolvedPath = Path.GetFullPath(filePath ?? ResolveDefaultPath());
        if (!File.Exists(resolvedPath))
        {
            return new LocalSettingsStore(resolvedPath);
        }

        try
        {
            JsonNode? node = JsonNode.Parse(File.ReadAllText(resolvedPath));
            if (node is not JsonObject root)
            {
                throw new InvalidOperationException($"{resolvedPath} must contain a JSON object.");
            }

            return new LocalSettingsStore(resolvedPath, root);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"{resolvedPath} is not valid JSON.", exception);
        }
    }

    public LocalSettingsSnapshot GetSnapshot()
        => new(
            GetBool("TelegramBot", "Enabled") ?? false,
            !string.IsNullOrWhiteSpace(GetString("TelegramBot", "Token")),
            GetInt64Array("TelegramBot", "AllowedUserIds"),
            GetInt64Array("TelegramBot", "AllowedChatIds"),
            !string.IsNullOrWhiteSpace(GetString("OpenAI", "ApiKey")),
            GetString("OpenAI", "Model"),
            GetString("OpenAI", "FfmpegPath"),
            GetInt32("TelegramBot", "MinAudioDurationSeconds"),
            GetInt32("TelegramBot", "MaxAudioDurationSeconds"),
            GetString("Codex", "CodexPathOverride"),
            GetString("TelegramBot", "CodexExecutablePath"),
            GetBool("CodexTelegram", "InitializeOnStart") ?? true,
            GetString("CodexTelegram", "Context", "WorkingDirectory"),
            GetString("CodexTelegram", "Workspace", "DataRoot"),
            GetStringArray("CodexTelegram", "Workspace", "WorkspaceRoots"),
            GetString("CodexTelegram", "Context", "Model"),
            GetString("CodexTelegram", "Context", "ReasoningEffort"),
            GetString("CodexTelegram", "Context", "Sandbox"),
            GetString("CodexTelegram", "Context", "ApprovalMode"),
            GetBool("CodexTelegram", "Context", "NetworkAccessEnabled"));

    public void Save()
    {
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(FilePath, _root.ToJsonString(JsonOptions) + Environment.NewLine);
    }

    public void SetTelegramEnabled(bool enabled)
        => SetBool(enabled, "TelegramBot", "Enabled");

    public void SetTelegramToken(string? value)
        => SetString(value, "TelegramBot", "Token");

    public void SetAllowedUserIds(IEnumerable<long> values)
        => SetInt64Array(values, "TelegramBot", "AllowedUserIds");

    public void SetAllowedChatIds(IEnumerable<long> values)
        => SetInt64Array(values, "TelegramBot", "AllowedChatIds");

    public void SetOpenAiApiKey(string? value)
        => SetString(value, "OpenAI", "ApiKey");

    public void SetOpenAiModel(string? value)
        => SetString(value, "OpenAI", "Model");

    public void SetOpenAiFfmpegPath(string? value)
        => SetString(value, "OpenAI", "FfmpegPath");

    public void SetMinAudioDurationSeconds(int? value)
        => SetNullableInt32(value, "TelegramBot", "MinAudioDurationSeconds");

    public void SetMaxAudioDurationSeconds(int? value)
        => SetNullableInt32(value, "TelegramBot", "MaxAudioDurationSeconds");

    public void SetCodexPathOverride(string? value)
        => SetString(value, "Codex", "CodexPathOverride");

    public void SetInitializeOnStart(bool enabled)
        => SetBool(enabled, "CodexTelegram", "InitializeOnStart");

    public void SetWorkingDirectory(string? value)
        => SetString(value, "CodexTelegram", "Context", "WorkingDirectory");

    public void SetDataRoot(string? value)
        => SetString(value, "CodexTelegram", "Workspace", "DataRoot");

    public void SetWorkspaceRoots(IEnumerable<string> values)
        => SetStringArray(values, "CodexTelegram", "Workspace", "WorkspaceRoots");

    public void SetCodexModel(string? value)
        => SetString(value, "CodexTelegram", "Context", "Model");

    public void SetReasoningEffort(string? value)
        => SetString(value, "CodexTelegram", "Context", "ReasoningEffort");

    public void SetSandbox(string? value)
        => SetString(value, "CodexTelegram", "Context", "Sandbox");

    public void SetApprovalMode(string? value)
        => SetString(value, "CodexTelegram", "Context", "ApprovalMode");

    public void SetNetworkAccessEnabled(bool? value)
        => SetNullableBool(value, "CodexTelegram", "Context", "NetworkAccessEnabled");

    private string? GetString(params string[] path)
    {
        JsonNode? node = GetNode(path);
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out string? stringValue))
        {
            return string.IsNullOrWhiteSpace(stringValue) ? null : stringValue.Trim();
        }

        return null;
    }

    private bool? GetBool(params string[] path)
    {
        JsonNode? node = GetNode(path);
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out bool boolValue))
        {
            return boolValue;
        }

        if (value.TryGetValue(out string? stringValue)
            && bool.TryParse(stringValue, out bool parsed))
        {
            return parsed;
        }

        return null;
    }

    private int? GetInt32(params string[] path)
    {
        JsonNode? node = GetNode(path);
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out int intValue))
        {
            return intValue;
        }

        if (value.TryGetValue(out string? stringValue)
            && int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        return null;
    }

    private IReadOnlyList<string> GetStringArray(params string[] path)
    {
        if (GetNode(path) is not JsonArray array)
        {
            return [];
        }

        List<string> values = new(array.Count);
        foreach (JsonNode? item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out string? stringValue))
            {
                string? normalized = NormalizeString(stringValue);
                if (normalized is not null)
                {
                    values.Add(normalized);
                }
            }
        }

        return values;
    }

    private IReadOnlyList<long> GetInt64Array(params string[] path)
    {
        if (GetNode(path) is not JsonArray array)
        {
            return [];
        }

        List<long> values = new(array.Count);
        foreach (JsonNode? item in array)
        {
            if (item is not JsonValue value)
            {
                continue;
            }

            if (value.TryGetValue(out long longValue))
            {
                values.Add(longValue);
                continue;
            }

            if (value.TryGetValue(out string? stringValue)
                && long.TryParse(stringValue, out long parsed))
            {
                values.Add(parsed);
            }
        }

        return values;
    }

    private JsonNode? GetNode(IReadOnlyList<string> path)
    {
        JsonNode? current = _root;
        foreach (string segment in path)
        {
            if (current is not JsonObject currentObject)
            {
                return null;
            }

            current = currentObject[segment];
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private void SetString(string? value, params string[] path)
    {
        string? normalized = NormalizeString(value);
        SetNode(normalized is null ? null : JsonValue.Create(normalized), path);
    }

    private void SetBool(bool value, params string[] path)
        => SetNode(JsonValue.Create(value), path);

    private void SetNullableBool(bool? value, params string[] path)
        => SetNode(value.HasValue ? JsonValue.Create(value.Value) : null, path);

    private void SetNullableInt32(int? value, params string[] path)
        => SetNode(value.HasValue ? JsonValue.Create(value.Value) : null, path);

    private void SetStringArray(IEnumerable<string> values, params string[] path)
    {
        JsonArray array = [];
        foreach (string value in NormalizeDistinctStrings(values))
        {
            array.Add(value);
        }

        SetNode(array, path);
    }

    private void SetInt64Array(IEnumerable<long> values, params string[] path)
    {
        JsonArray array = [];
        foreach (long value in values.Distinct())
        {
            array.Add(value);
        }

        SetNode(array, path);
    }

    private void SetNode(JsonNode? node, IReadOnlyList<string> path)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("A configuration path is required.", nameof(path));
        }

        JsonObject parent = GetOrCreateParent(path);
        string propertyName = path[^1];
        if (node is null)
        {
            parent.Remove(propertyName);
            return;
        }

        parent[propertyName] = node;
    }

    private JsonObject GetOrCreateParent(IReadOnlyList<string> path)
    {
        JsonObject current = _root;
        for (int index = 0; index < path.Count - 1; index++)
        {
            string segment = path[index];
            if (current[segment] is not JsonObject child)
            {
                child = [];
                current[segment] = child;
            }

            current = child;
        }

        return current;
    }

    private static string? NormalizeString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IEnumerable<string> NormalizeDistinctStrings(IEnumerable<string> values)
    {
        HashSet<string> seen = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string value in values)
        {
            string? normalized = NormalizeString(value);
            if (normalized is not null && seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }
}
