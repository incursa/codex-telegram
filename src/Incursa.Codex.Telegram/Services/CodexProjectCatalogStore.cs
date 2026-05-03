using System.Text.Json;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

public interface ICodexProjectCatalogStore
{
    Task<IReadOnlyList<CodexProjectCatalogRecord>> ListAsync(CancellationToken cancellationToken);

    Task<CodexProjectCatalogRecord> AddAsync(string workingDirectory, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(string workingDirectory, CancellationToken cancellationToken);
}

internal sealed class CodexProjectCatalogStore : ICodexProjectCatalogStore
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly TimeProvider _timeProvider;

    public CodexProjectCatalogStore(IOptions<CodexTelegramOptions> options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<CodexProjectCatalogRecord>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CodexProjectCatalogRecord> projects = await LoadProjectsAsync(cancellationToken).ConfigureAwait(false);
        return projects
            .OrderByDescending(project => project.AddedAt)
            .ToArray();
    }

    public async Task<CodexProjectCatalogRecord> AddAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        string normalizedWorkingDirectory = NormalizeWorkingDirectory(workingDirectory)
            ?? throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<CodexProjectCatalogRecord> projects = (await LoadProjectsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            CodexProjectCatalogRecord? existing = projects.FirstOrDefault(project => PathComparer.Equals(project.WorkingDirectory, normalizedWorkingDirectory));
            if (existing is not null)
            {
                return existing;
            }

            CodexProjectCatalogRecord record = new()
            {
                WorkingDirectory = normalizedWorkingDirectory,
                AddedAt = _timeProvider.GetUtcNow(),
            };

            projects.Add(record);
            await SaveProjectsAsync(projects, cancellationToken).ConfigureAwait(false);
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        string? normalizedWorkingDirectory = NormalizeWorkingDirectory(workingDirectory);
        if (normalizedWorkingDirectory is null)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<CodexProjectCatalogRecord> projects = (await LoadProjectsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            int removedCount = projects.RemoveAll(project => PathComparer.Equals(project.WorkingDirectory, normalizedWorkingDirectory));
            if (removedCount == 0)
            {
                return false;
            }

            await SaveProjectsAsync(projects, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<CodexProjectCatalogRecord>> LoadProjectsAsync(CancellationToken cancellationToken)
    {
        string catalogPath = GetCatalogPath();
        if (!File.Exists(catalogPath))
        {
            return Array.Empty<CodexProjectCatalogRecord>();
        }

        await using FileStream stream = File.OpenRead(catalogPath);
        List<CodexProjectCatalogRecord>? projects = await JsonSerializer.DeserializeAsync<List<CodexProjectCatalogRecord>>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        if (projects is null || projects.Count == 0)
        {
            return Array.Empty<CodexProjectCatalogRecord>();
        }

        List<CodexProjectCatalogRecord> normalized = new(projects.Count);
        HashSet<string> seen = new(PathComparer);
        foreach (CodexProjectCatalogRecord project in projects)
        {
            string? normalizedWorkingDirectory = NormalizeWorkingDirectory(project.WorkingDirectory);
            if (normalizedWorkingDirectory is null || !seen.Add(normalizedWorkingDirectory))
            {
                continue;
            }

            normalized.Add(new CodexProjectCatalogRecord
            {
                WorkingDirectory = normalizedWorkingDirectory,
                AddedAt = project.AddedAt == default ? _timeProvider.GetUtcNow() : project.AddedAt,
            });
        }

        return normalized;
    }

    private async Task SaveProjectsAsync(IReadOnlyList<CodexProjectCatalogRecord> projects, CancellationToken cancellationToken)
    {
        string catalogPath = GetCatalogPath();
        string? directory = Path.GetDirectoryName(catalogPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = Path.Combine(directory ?? Path.GetTempPath(), $"{Guid.NewGuid():N}.json.tmp");
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, projects, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, catalogPath, overwrite: true);
    }

    private string GetCatalogPath()
        => Path.Combine(GetDataRoot(), "projects.json");

    private string GetDataRoot()
    {
        string? configuredRoot = _options.Value.Workspace.DataRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        return Path.Combine(AppContext.BaseDirectory, "App_Data", "codex-telegram");
    }

    private static string? NormalizeWorkingDirectory(string? workingDirectory)
        => string.IsNullOrWhiteSpace(workingDirectory) ? null : Path.GetFullPath(workingDirectory);
}

public sealed record CodexProjectCatalogRecord
{
    public string WorkingDirectory { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; }
}
