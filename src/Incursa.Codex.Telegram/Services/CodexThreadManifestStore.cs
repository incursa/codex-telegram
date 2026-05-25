using System.Collections.Concurrent;
using System.Text.Json;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal interface ICodexThreadManifestStore
{
    Task<CodexThreadManifestRecord?> ReadAsync(string threadId, CancellationToken cancellationToken);

    Task<CodexThreadManifestRecord> GetOrCreateAsync(string threadId, CancellationToken cancellationToken);

    Task<CodexThreadManifestRecord> SetContextAsync(
        string threadId,
        CodexThreadContextSubmission submission,
        CancellationToken cancellationToken);

    Task<CodexThreadManifestRecord> SetSelectedFilesAsync(
        string threadId,
        IReadOnlyCollection<string> selectedFileIds,
        CancellationToken cancellationToken);

    Task<CodexThreadManifestRecord> UpdateAsync(
        string threadId,
        Func<CodexThreadManifestRecord, CodexThreadManifestRecord> updater,
        CancellationToken cancellationToken);
}

internal sealed class CodexThreadManifestStore
    : ICodexThreadManifestStore
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly TimeProvider _timeProvider;

    public CodexThreadManifestStore(IOptions<CodexTelegramOptions> options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<CodexThreadManifestRecord> GetOrCreateAsync(string threadId, CancellationToken cancellationToken)
    {
        string manifestPath = GetManifestPath(threadId);
        if (!File.Exists(manifestPath))
        {
            return await SaveAsync(new CodexThreadManifestRecord
            {
                ThreadId = threadId,
                CreatedAt = _timeProvider.GetUtcNow(),
                UpdatedAt = _timeProvider.GetUtcNow(),
            }, cancellationToken).ConfigureAwait(false);
        }

        return await LoadAsync(threadId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexThreadManifestRecord?> ReadAsync(string threadId, CancellationToken cancellationToken)
    {
        string manifestPath = GetManifestPath(threadId);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        await using FileStream stream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<CodexThreadManifestRecord>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexThreadManifestRecord> LoadAsync(string threadId, CancellationToken cancellationToken)
    {
        CodexThreadManifestRecord? manifest = await ReadAsync(threadId, cancellationToken).ConfigureAwait(false);
        return manifest ?? throw new FileNotFoundException($"Manifest for thread '{threadId}' was not found.", GetManifestPath(threadId));
    }

    public async Task<CodexThreadManifestRecord> SaveAsync(CodexThreadManifestRecord manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.ThreadId))
        {
            throw new ArgumentException("A manifest must have a thread id.", nameof(manifest));
        }

        string threadRoot = GetThreadRoot(manifest.ThreadId);
        Directory.CreateDirectory(threadRoot);
        string manifestPath = GetManifestPath(manifest.ThreadId);
        manifest.UpdatedAt = _timeProvider.GetUtcNow();

        string tempPath = Path.Combine(threadRoot, $"{Guid.NewGuid():N}.json.tmp");
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, manifestPath, overwrite: true);
        return manifest;
    }

    public async Task<CodexThreadManifestRecord> UpdateAsync(
        string threadId,
        Func<CodexThreadManifestRecord, CodexThreadManifestRecord> updater,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = _gates.GetOrAdd(NormalizeThreadId(threadId), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CodexThreadManifestRecord manifest = await GetOrCreateAsync(threadId, cancellationToken).ConfigureAwait(false);
            manifest = updater(manifest);
            manifest.ThreadId = threadId;
            return await SaveAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<CodexThreadManifestRecord> SetArchiveStateAsync(string threadId, bool archived, CancellationToken cancellationToken)
        => UpdateAsync(threadId, manifest =>
        {
            manifest.IsArchived = archived;
            return manifest;
        }, cancellationToken);

    public Task<CodexThreadManifestRecord> SetThreadNameAsync(string threadId, string? name, CancellationToken cancellationToken)
        => UpdateAsync(threadId, manifest =>
        {
            manifest.ThreadName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            return manifest;
        }, cancellationToken);

    public Task<CodexThreadManifestRecord> SetContextAsync(
        string threadId,
        CodexThreadContextSubmission submission,
        CancellationToken cancellationToken)
        => UpdateAsync(threadId, manifest =>
        {
            if (!string.IsNullOrWhiteSpace(submission.ThreadName))
            {
                manifest.ThreadName = submission.ThreadName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(submission.WorkingDirectory))
            {
                manifest.WorkingDirectory = NormalizeNullablePath(submission.WorkingDirectory);
            }

            if (!string.IsNullOrWhiteSpace(submission.BaseInstructions))
            {
                manifest.BaseInstructions = submission.BaseInstructions;
            }

            if (!string.IsNullOrWhiteSpace(submission.DeveloperInstructions))
            {
                manifest.DeveloperInstructions = submission.DeveloperInstructions;
            }

            if (!string.IsNullOrWhiteSpace(submission.Model))
            {
                manifest.Model = submission.Model;
            }

            if (!string.IsNullOrWhiteSpace(submission.ModelProvider))
            {
                manifest.ModelProvider = submission.ModelProvider;
            }

            if (!string.IsNullOrWhiteSpace(submission.Personality))
            {
                manifest.Personality = submission.Personality;
            }

            if (!string.IsNullOrWhiteSpace(submission.Sandbox))
            {
                manifest.Sandbox = submission.Sandbox;
            }

            if (!string.IsNullOrWhiteSpace(submission.ServiceTier))
            {
                manifest.ServiceTier = submission.ServiceTier;
            }

            if (!string.IsNullOrWhiteSpace(submission.ApprovalMode))
            {
                manifest.ApprovalMode = submission.ApprovalMode;
            }

            if (!string.IsNullOrWhiteSpace(submission.ApprovalsReviewer))
            {
                manifest.ApprovalsReviewer = submission.ApprovalsReviewer;
            }

            if (!string.IsNullOrWhiteSpace(submission.ReasoningEffort))
            {
                manifest.ReasoningEffort = submission.ReasoningEffort;
            }

            if (!string.IsNullOrWhiteSpace(submission.ReasoningSummary))
            {
                manifest.ReasoningSummary = submission.ReasoningSummary;
            }

            if (!string.IsNullOrWhiteSpace(submission.WebSearchMode))
            {
                manifest.WebSearchMode = submission.WebSearchMode;
            }

            if (submission.NetworkAccessEnabled.HasValue)
            {
                manifest.NetworkAccessEnabled = submission.NetworkAccessEnabled;
            }

            if (submission.WebSearchEnabled.HasValue)
            {
                manifest.WebSearchEnabled = submission.WebSearchEnabled;
            }

            if (submission.SkipGitRepoCheck.HasValue)
            {
                manifest.SkipGitRepoCheck = submission.SkipGitRepoCheck;
            }

            if (submission.Ephemeral.HasValue)
            {
                manifest.Ephemeral = submission.Ephemeral;
            }

            if (submission.AdditionalDirectories.Count > 0)
            {
                manifest.AdditionalDirectories = submission.AdditionalDirectories
                    .Where(directory => !string.IsNullOrWhiteSpace(directory))
                    .Select(NormalizeNullablePath)
                    .Where(directory => directory is not null)
                    .Select(directory => directory!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return manifest;
        }, cancellationToken);

    public Task<CodexThreadManifestRecord> SetSelectedFilesAsync(
        string threadId,
        IReadOnlyCollection<string> selectedFileIds,
        CancellationToken cancellationToken)
        => UpdateAsync(threadId, manifest =>
        {
            HashSet<string> selected = new(selectedFileIds.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
            foreach (CodexThreadFileRecord file in manifest.Files)
            {
                file.Selected = selected.Contains(file.Id);
            }

            return manifest;
        }, cancellationToken);

    public string GetThreadRoot(string threadId)
        => Path.Combine(GetDataRoot(), NormalizeThreadId(threadId));

    public string GetManifestPath(string threadId)
        => Path.Combine(GetThreadRoot(threadId), "manifest.json");

    public string ResolveFilePath(string threadId, string relativePath)
        => Path.Combine(GetThreadRoot(threadId), relativePath);

    private string GetDataRoot()
    {
        string? configuredRoot = _options.Value.Workspace.DataRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        return Path.Combine(AppContext.BaseDirectory, "App_Data", "codex-telegram");
    }

    private static string NormalizeThreadId(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("Thread id cannot be empty.", nameof(threadId));
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[threadId.Length];
        for (int index = 0; index < threadId.Length; index++)
        {
            buffer[index] = invalid.Contains(threadId[index]) ? '_' : threadId[index];
        }

        return new string(buffer);
    }

    private static string? NormalizeNullablePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

}

internal sealed record CodexThreadManifestRecord
{
    public string ThreadId { get; set; } = string.Empty;

    public string? ThreadName { get; set; }

    public string? WorkingDirectory { get; set; }

    public string? BaseInstructions { get; set; }

    public string? DeveloperInstructions { get; set; }

    public string? Model { get; set; }

    public string? ModelProvider { get; set; }

    public string? Personality { get; set; }

    public string? Sandbox { get; set; }

    public string? ServiceTier { get; set; }

    public string? ApprovalMode { get; set; }

    public string? ApprovalsReviewer { get; set; }

    public string? ReasoningEffort { get; set; }

    public string? ReasoningSummary { get; set; }

    public string? WebSearchMode { get; set; }

    public bool? NetworkAccessEnabled { get; set; }

    public bool? WebSearchEnabled { get; set; }

    public bool? SkipGitRepoCheck { get; set; }

    public bool? Ephemeral { get; set; }

    public List<string> AdditionalDirectories { get; set; } = [];

    public bool IsArchived { get; set; }

    public string? LastTurnId { get; set; }

    public CodexInterruptedTurnRecord? InterruptedTurn { get; set; }

    public List<CodexThreadFileRecord> Files { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed record CodexInterruptedTurnRecord
{
    public string TurnId { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? LastEventType { get; set; }

    public string? LastEventTitle { get; set; }

    public string? LastEventSummary { get; set; }
}

internal sealed record CodexThreadFileRecord
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string? ContentType { get; set; }

    public long Length { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public bool Selected { get; set; } = true;

    public bool IsImage { get; set; }
}
