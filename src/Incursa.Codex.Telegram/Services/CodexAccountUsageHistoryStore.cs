using System.Text.Json;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Keeps a bounded, de-duplicated history of account quota observations for
/// sparklines. Windows are retained as reported; readings are never summed.
/// </summary>
internal sealed class CodexAccountUsageHistoryStore
{
    private const string FileName = "account-usage-history.json";
    private const int MaximumSamples = 5_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IOptions<CodexTelegramOptions> _options;

    public CodexAccountUsageHistoryStore(IOptions<CodexTelegramOptions> options)
    {
        _options = options;
    }

    public async Task<IReadOnlyList<CodexAccountUsageSampleVm>> AppendAsync(
        CodexAccountUsageVm usage,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<CodexAccountUsageSampleVm> samples = await LoadAsync(cancellationToken).ConfigureAwait(false);
            foreach (CodexRateLimitSnapshotVm limit in usage.RateLimits)
            {
                AddSample(samples, limit.LimitId, "primary", limit.Primary, usage.RetrievedAtUtc);
                AddSample(samples, limit.LimitId, "secondary", limit.Secondary, usage.RetrievedAtUtc);
            }

            DateTimeOffset cutoff = usage.RetrievedAtUtc.AddDays(-30);
            samples.RemoveAll(sample => sample.ObservedAtUtc < cutoff);
            if (samples.Count > MaximumSamples)
            {
                samples = samples
                    .OrderByDescending(sample => sample.ObservedAtUtc)
                    .Take(MaximumSamples)
                    .OrderBy(sample => sample.ObservedAtUtc)
                    .ToList();
            }

            await SaveAsync(samples, cancellationToken).ConfigureAwait(false);
            return samples;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void AddSample(
        ICollection<CodexAccountUsageSampleVm> samples,
        string? limitId,
        string window,
        CodexRateLimitWindowVm? value,
        DateTimeOffset observedAtUtc)
    {
        if (value is null)
        {
            return;
        }

        CodexAccountUsageSampleVm? previous = samples
            .Where(sample => string.Equals(sample.LimitId, limitId, StringComparison.Ordinal)
                && string.Equals(sample.Window, window, StringComparison.Ordinal))
            .OrderByDescending(sample => sample.ObservedAtUtc)
            .FirstOrDefault();
        if (previous is not null
            && previous.UsedPercent == value.UsedPercent
            && previous.ResetsAtUtc == value.ResetsAtUtc
            && previous.WindowDurationMinutes == value.WindowDurationMinutes)
        {
            return;
        }

        samples.Add(new CodexAccountUsageSampleVm(
            limitId,
            window,
            value.UsedPercent,
            observedAtUtc,
            value.ResetsAtUtc,
            value.WindowDurationMinutes));
    }

    private async Task<List<CodexAccountUsageSampleVm>> LoadAsync(CancellationToken cancellationToken)
    {
        string path = GetPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<CodexAccountUsageSampleVm>>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? [];
    }

    private async Task SaveAsync(IReadOnlyList<CodexAccountUsageSampleVm> samples, CancellationToken cancellationToken)
    {
        string path = GetPath();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = Path.Combine(directory ?? AppContext.BaseDirectory, $"{Guid.NewGuid():N}.tmp");
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, samples, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private string GetPath()
    {
        string root = string.IsNullOrWhiteSpace(_options.Value.Workspace.DataRoot)
            ? CodexTelegramDataRoot.GetDefaultDataRoot(_options.Value.InstanceId)
            : Path.GetFullPath(_options.Value.Workspace.DataRoot);
        return Path.Combine(root, FileName);
    }
}
