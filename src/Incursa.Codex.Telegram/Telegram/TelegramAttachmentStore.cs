using System.Globalization;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

internal interface ITelegramAttachmentStore
{
    Task<IReadOnlyList<TelegramAttachmentDescriptor>?> PersistAsync(
        IReadOnlyList<TelegramAttachmentDescriptor>? attachments,
        bool deleteSource,
        CancellationToken cancellationToken);
}

internal sealed class TelegramAttachmentStore : ITelegramAttachmentStore
{
    private readonly IOptions<CodexTelegramOptions> _options;

    public TelegramAttachmentStore(IOptions<CodexTelegramOptions> options)
    {
        _options = options;
    }

    public async Task<IReadOnlyList<TelegramAttachmentDescriptor>?> PersistAsync(
        IReadOnlyList<TelegramAttachmentDescriptor>? attachments,
        bool deleteSource,
        CancellationToken cancellationToken)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return attachments;
        }

        string root = GetAttachmentRoot();
        string fullRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        List<TelegramAttachmentDescriptor> durable = new(attachments.Count);
        foreach (TelegramAttachmentDescriptor attachment in attachments)
        {
            string sourcePath = Path.GetFullPath(attachment.FilePath);
            if (IsUnderRoot(sourcePath, fullRoot))
            {
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException("Durable Telegram attachment file is missing.", sourcePath);
                }

                durable.Add(attachment with { FilePath = sourcePath });
                continue;
            }

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Telegram attachment file is missing before it could be persisted.", sourcePath);
            }

            string directory = Path.Combine(
                fullRoot,
                DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            string destinationPath = Path.Combine(directory, BuildDurableFileName(attachment));

            await using (FileStream source = File.OpenRead(sourcePath))
            await using (FileStream destination = File.Create(destinationPath))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            durable.Add(attachment with { FilePath = destinationPath });
            if (deleteSource)
            {
                TryDelete(sourcePath);
            }
        }

        return durable;
    }

    private string GetAttachmentRoot()
    {
        string? dataRoot = _options.Value.Workspace.DataRoot;
        string root = string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(AppContext.BaseDirectory, "App_Data", "codex-telegram")
            : dataRoot;
        return Path.Combine(root, "telegram-attachments");
    }

    private static string BuildDurableFileName(TelegramAttachmentDescriptor attachment)
    {
        string sourceName = string.IsNullOrWhiteSpace(attachment.FileName)
            ? Path.GetFileName(attachment.FilePath)
            : attachment.FileName;
        string extension = Path.GetExtension(sourceName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = Path.GetExtension(attachment.FilePath);
        }

        string stem = Path.GetFileNameWithoutExtension(sourceName);
        stem = SanitizeFileNameStem(stem);
        return $"{Guid.NewGuid():n}-{stem}{extension}";
    }

    private static string SanitizeFileNameStem(string value)
    {
        string trimmed = string.IsNullOrWhiteSpace(value) ? "attachment" : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '-');
        }

        return trimmed.Length <= 80 ? trimmed : trimmed[..80];
    }

    private static bool IsUnderRoot(string path, string fullRoot)
        => EnsureTrailingSeparator(path).StartsWith(fullRoot, PathComparison);

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

internal sealed class NullTelegramAttachmentStore : ITelegramAttachmentStore
{
    public static NullTelegramAttachmentStore Instance { get; } = new();

    private NullTelegramAttachmentStore()
    {
    }

    public Task<IReadOnlyList<TelegramAttachmentDescriptor>?> PersistAsync(
        IReadOnlyList<TelegramAttachmentDescriptor>? attachments,
        bool deleteSource,
        CancellationToken cancellationToken)
        => Task.FromResult(attachments);
}
