using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal interface ICodexHostUpdateManager
{
    Task<CodexHostUpdateSnapshot> GetStatusAsync(CancellationToken cancellationToken);

    Task<CodexHostUpdateSnapshot> RequestAsync(
        CodexHostUpdateAction action,
        TelegramConversationScope conversation,
        long userId,
        CancellationToken cancellationToken);

    Task<CodexHostUpdateSnapshot?> TryGetPendingNotificationAsync(CancellationToken cancellationToken);

    Task<string?> GetConversationKeyAsync(string requestId, CancellationToken cancellationToken);

    Task<bool> MarkNotificationSentAsync(string requestId, CancellationToken cancellationToken);
}

internal enum CodexHostUpdateAction
{
    Update,
    Rollback,
}

internal enum CodexHostUpdateState
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

internal sealed record CodexHostUpdateSnapshot(
    bool Enabled,
    CodexHostUpdateState State,
    CodexHostUpdateAction? Action,
    string? RequestId,
    string CurrentVersion,
    string? TargetVersion,
    DateTimeOffset? RequestedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    string? OutcomeCode,
    bool NotificationPending);

/// <summary>
/// Persists an authenticated operator request for an external host updater.
/// This process never invokes a package manager, writes the protected installation,
/// or stops its own service.
/// </summary>
internal sealed class CodexHostUpdateManager : ICodexHostUpdateManager, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _dataRoot;

    public CodexHostUpdateManager(
        IOptions<CodexTelegramOptions> options,
        TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        _dataRoot = GetDataRoot(options.Value);
    }

    internal CodexHostUpdateManager(
        IOptions<CodexTelegramOptions> options,
        TimeProvider timeProvider,
        string dataRootOverride)
    {
        _options = options;
        _timeProvider = timeProvider;
        _dataRoot = Path.GetFullPath(dataRootOverride);
    }

    public async Task<CodexHostUpdateSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ToSnapshot(await LoadAsync(cancellationToken).ConfigureAwait(false), _options.Value.HostUpdate.Enabled);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexHostUpdateSnapshot> RequestAsync(
        CodexHostUpdateAction action,
        TelegramConversationScope conversation,
        long userId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CodexHostUpdateOptions options = _options.Value.HostUpdate;
            HostUpdateState current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!options.Enabled)
            {
                return ToSnapshot(current, enabled: false, outcomeCode: "host_updates_disabled");
            }

            if (current.State is CodexHostUpdateState.Requested
                or CodexHostUpdateState.Applying
                or CodexHostUpdateState.RollbackRequested
                or CodexHostUpdateState.RollbackApplying)
            {
                return ToSnapshot(current, enabled: true, outcomeCode: "update_already_in_progress");
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            string requestId = Guid.NewGuid().ToString("N");
            string currentVersion = GetCurrentVersion();
            HostUpdateState requested = new(
                CurrentSchemaVersion,
                action == CodexHostUpdateAction.Rollback
                    ? CodexHostUpdateState.RollbackRequested
                    : CodexHostUpdateState.Requested,
                action,
                requestId,
                userId,
                conversation.ToStorageKey(),
                currentVersion,
                NormalizeOptional(options.TargetVersion),
                now,
                now,
                null,
                false);

            await SaveAsync(requested, cancellationToken).ConfigureAwait(false);
            await SaveRequestAsync(requested, options, cancellationToken).ConfigureAwait(false);
            return ToSnapshot(requested, enabled: true, outcomeCode: "update_request_written");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexHostUpdateSnapshot?> TryGetPendingNotificationAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_options.Value.HostUpdate.Enabled)
            {
                return null;
            }

            HostUpdateState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!state.NotificationPending || state.State is not (
                CodexHostUpdateState.Applying
                or CodexHostUpdateState.RollbackApplying
                or
                CodexHostUpdateState.Active
                or CodexHostUpdateState.Failed
                or CodexHostUpdateState.RollbackActive
                or CodexHostUpdateState.RollbackFailed))
            {
                return null;
            }

            return ToSnapshot(state, enabled: _options.Value.HostUpdate.Enabled, outcomeCode: state.OutcomeCode);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> MarkNotificationSentAsync(string requestId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HostUpdateState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!state.NotificationPending || !string.Equals(state.RequestId, requestId, StringComparison.Ordinal))
            {
                return false;
            }

            await SaveAsync(state with { NotificationPending = false, UpdatedAtUtc = _timeProvider.GetUtcNow() }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetConversationKeyAsync(string requestId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HostUpdateState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return string.Equals(state.RequestId, requestId, StringComparison.Ordinal)
                ? state.ConversationKey
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<HostUpdateState> LoadAsync(CancellationToken cancellationToken)
    {
        string path = GetStatePath();
        if (!File.Exists(path))
        {
            return EmptyState();
        }

        await using FileStream stream = File.OpenRead(path);
        HostUpdateState? state = await JsonSerializer.DeserializeAsync<HostUpdateState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        return state ?? throw new InvalidDataException("The host update state file was empty.");
    }

    private async Task SaveAsync(HostUpdateState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataRoot);
        await SaveFileAsync(GetStatePath(), state, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveRequestAsync(
        HostUpdateState state,
        CodexHostUpdateOptions options,
        CancellationToken cancellationToken)
    {
        string path = GetRequestPath(options);
        HostUpdateRequest request = new(
            CurrentSchemaVersion,
            state.RequestId!,
            state.Action!.Value,
            state.RequestedAtUtc ?? _timeProvider.GetUtcNow(),
            state.RequestedByUserId ?? 0,
            state.ConversationKey ?? string.Empty,
            state.CurrentVersion ?? GetCurrentVersion(),
            state.TargetVersion,
            NormalizeOptional(options.ExpectedSha256));
        await SaveFileAsync(path, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveFileAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private string GetStatePath() => Path.Combine(_dataRoot, "codex-host-update-state.json");

    private string GetRequestPath(CodexHostUpdateOptions options)
        => string.IsNullOrWhiteSpace(options.RequestPath)
            ? Path.Combine(_dataRoot, "codex-host-update-request.json")
            : Path.GetFullPath(options.RequestPath);

    private static string GetDataRoot(CodexTelegramOptions options)
        => string.IsNullOrWhiteSpace(options.Workspace.DataRoot)
            ? CodexTelegramDataRoot.GetDefaultDataRoot(options.InstanceId)
            : Path.GetFullPath(options.Workspace.DataRoot);

    private string GetCurrentVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static HostUpdateState EmptyState()
        => new(CurrentSchemaVersion, CodexHostUpdateState.None, null, null, null, null, null, null, null, null, null, false);

    private CodexHostUpdateSnapshot ToSnapshot(HostUpdateState state, bool enabled, string? outcomeCode = null)
        => new(
            enabled,
            state.State,
            state.Action,
            state.RequestId,
            state.CurrentVersion ?? GetCurrentVersion(),
            state.TargetVersion ?? NormalizeOptional(_options.Value.HostUpdate.TargetVersion),
            state.RequestedAtUtc,
            state.UpdatedAtUtc,
            outcomeCode ?? state.OutcomeCode,
            state.NotificationPending);

    private sealed record HostUpdateRequest(
        int SchemaVersion,
        string RequestId,
        CodexHostUpdateAction Action,
        DateTimeOffset RequestedAtUtc,
        long RequestedByUserId,
        string ConversationKey,
        string CurrentVersion,
        string? TargetVersion,
        string? ExpectedSha256);

    internal sealed record HostUpdateState(
        int SchemaVersion,
        CodexHostUpdateState State,
        CodexHostUpdateAction? Action,
        string? RequestId,
        long? RequestedByUserId,
        string? ConversationKey,
        string? CurrentVersion,
        string? TargetVersion,
        DateTimeOffset? RequestedAtUtc,
        DateTimeOffset? UpdatedAtUtc,
        string? OutcomeCode,
        bool NotificationPending);
}
