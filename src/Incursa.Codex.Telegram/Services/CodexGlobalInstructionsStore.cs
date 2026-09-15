using System.Text.Json;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Persists the operator's shared instruction profile separately from repository
/// state. The profile is read when a thread is started or resumed, which makes
/// the next safe Codex boundary explicit without interrupting an active turn.
/// </summary>
internal interface ICodexGlobalInstructionsStore
{
    Task<CodexGlobalInstructionsSnapshot> GetAsync(CancellationToken cancellationToken);

    Task<CodexGlobalInstructionsSnapshot> SaveAsync(string text, CancellationToken cancellationToken);
}

internal sealed record CodexGlobalInstructionsSnapshot(
    string Text,
    DateTimeOffset? UpdatedAtUtc,
    bool HasSavedValue);

internal sealed class CodexGlobalInstructionsStore : ICodexGlobalInstructionsStore
{
    private const string FileName = "global-instructions.json";
    private const int MaximumLength = 8_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly TimeProvider _timeProvider;

    public CodexGlobalInstructionsStore(IOptions<CodexTelegramOptions> options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<CodexGlobalInstructionsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        string path = GetPath();
        if (!File.Exists(path))
        {
            return new CodexGlobalInstructionsSnapshot(
                _options.Value.Context.BaseInstructions?.Trim() ?? string.Empty,
                null,
                false);
        }

        await using FileStream stream = File.OpenRead(path);
        PersistedInstructions? persisted = await JsonSerializer.DeserializeAsync<PersistedInstructions>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        return new CodexGlobalInstructionsSnapshot(
            Normalize(persisted?.Text),
            persisted?.UpdatedAtUtc,
            true);
    }

    public async Task<CodexGlobalInstructionsSnapshot> SaveAsync(string text, CancellationToken cancellationToken)
    {
        string normalized = Normalize(text);
        if (normalized.Length > MaximumLength)
        {
            throw new ArgumentException($"Global instructions cannot exceed {MaximumLength} characters.", nameof(text));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset updatedAtUtc = _timeProvider.GetUtcNow();
            string path = GetPath();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = Path.Combine(directory ?? AppContext.BaseDirectory, $"{Guid.NewGuid():N}.tmp");
            await using (FileStream stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new PersistedInstructions(normalized, updatedAtUtc),
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
            return new CodexGlobalInstructionsSnapshot(normalized, updatedAtUtc, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath()
    {
        string root = string.IsNullOrWhiteSpace(_options.Value.Workspace.DataRoot)
            ? CodexTelegramDataRoot.GetDefaultDataRoot(_options.Value.InstanceId)
            : Path.GetFullPath(_options.Value.Workspace.DataRoot);
        return Path.Combine(root, FileName);
    }

    private static string Normalize(string? value)
        => (value ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal).Trim();

    private sealed record PersistedInstructions(string Text, DateTimeOffset UpdatedAtUtc);
}
