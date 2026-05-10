using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;

namespace Incursa.Codex.Telegram.Services;

internal sealed class CodexRuntimeState
{
    private readonly object _gate = new();
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();

    private CodexRuntimeMetadata? _metadata;
    private CodexRuntimeCapabilities? _capabilities;
    private IReadOnlyList<CodexModel> _models = [];

    public bool Initialized { get; private set; }

    public string? Message { get; private set; }

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_gate)
            {
                return _errors.ToArray();
            }
        }
    }

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_gate)
            {
                return _warnings.ToArray();
            }
        }
    }

    public CodexRuntimeMetadata? Metadata
    {
        get
        {
            lock (_gate)
            {
                return _metadata;
            }
        }
    }

    public CodexRuntimeCapabilities? Capabilities
    {
        get
        {
            lock (_gate)
            {
                return _capabilities;
            }
        }
    }

    public IReadOnlyList<CodexModel> Models
    {
        get
        {
            lock (_gate)
            {
                return _models;
            }
        }
    }

    public void SetReady(
        CodexRuntimeMetadata metadata,
        CodexRuntimeCapabilities capabilities,
        IReadOnlyList<CodexModel>? models = null,
        string? message = null)
    {
        lock (_gate)
        {
            _metadata = metadata;
            _capabilities = capabilities;
            _models = models ?? _models;
            Message = RepairText(message ?? DescribeRuntime(metadata));
            Initialized = true;
            _errors.Clear();
            _warnings.Clear();
            AddCapabilityWarnings(capabilities, _warnings);
        }
    }

    public void SetModels(IReadOnlyList<CodexModel> models)
    {
        lock (_gate)
        {
            _models = models.ToArray();
        }
    }

    public void SetError(Exception exception)
    {
        lock (_gate)
        {
            Initialized = false;
            Message = "Codex runtime initialization failed.";
            _metadata = null;
            _capabilities = null;
            _models = [];
            _errors.Clear();
            _errors.Add(RepairText(exception.Message));
        }
    }

    public CodexRuntimeStateVm ToViewModel()
    {
        lock (_gate)
        {
            return new CodexRuntimeStateVm(
                Initialized,
                RepairTextOrNull(Message),
                RepairTextOrNull(_metadata?.ServerInfo?.Name),
                RepairTextOrNull(_metadata?.ServerInfo?.Version),
                RepairTextOrNull(_metadata?.PlatformFamily),
                RepairTextOrNull(_metadata?.PlatformOs),
                RepairTextOrNull(_metadata?.UserAgent),
                _warnings.Select(RepairText).ToArray(),
                _errors.Select(RepairText).ToArray(),
                _capabilities?.SupportsThreadStreaming == true,
                _capabilities?.SupportsTurnSteering == true,
                _capabilities?.SupportsTurnInterruption == true,
                _capabilities?.SupportsStartThread == true,
                _capabilities?.SupportsResumeThread == true,
                _capabilities?.SupportsForkThread == true,
                _capabilities?.SupportsListThreads == true,
                _capabilities?.SupportsCompactThread == true,
                _capabilities?.SupportsArchiveThread == true,
                _capabilities?.SupportsUnarchiveThread == true,
                _capabilities?.SupportsListModels == true,
                _capabilities?.SupportsThreadGoals == true);
        }
    }

    private static string DescribeRuntime(CodexRuntimeMetadata metadata)
        => metadata.ServerInfo is { Name: not null, Version: not null }
            ? $"{RepairText(metadata.ServerInfo.Name)} {RepairText(metadata.ServerInfo.Version)}"
            : "Codex runtime ready";

    private static void AddCapabilityWarnings(CodexRuntimeCapabilities capabilities, ICollection<string> warnings)
    {
        if (!capabilities.SupportsListThreads)
        {
            warnings.Add("Thread listing is unavailable.");
        }

        if (!capabilities.SupportsThreadStreaming)
        {
            warnings.Add("Live turn streaming is unavailable.");
        }

        if (!capabilities.SupportsTurnSteering)
        {
            warnings.Add("Steering active turns is unavailable.");
        }

        if (!capabilities.SupportsTurnInterruption)
        {
            warnings.Add("Interrupting active turns is unavailable.");
        }

        if (!capabilities.SupportsListModels)
        {
            warnings.Add("Model discovery is unavailable.");
        }

        if (!capabilities.SupportsThreadGoals)
        {
            warnings.Add("Thread goals are unavailable.");
        }
    }

    private static string RepairText(string? value)
        => CodexTextFormatting.RepairUtf8Mojibake(value) ?? string.Empty;

    private static string? RepairTextOrNull(string? value)
        => CodexTextFormatting.RepairUtf8Mojibake(value);
}
