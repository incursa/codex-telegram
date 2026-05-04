using System.Text;
using Incursa.Codex.Telegram.Telegram;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramFuzzCorpusTests
{
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void CorpusSeed_DoesNotBreakTelegramParsingChunkingOrInputMapping(string seedPath)
    {
        byte[] data = File.ReadAllBytes(seedPath);
        string text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(data);

        TelegramCommandParser parser = new();
        ParsedTelegramCommand parsed = parser.Parse(text);

        TelegramMessageChunker chunker = new();
        IReadOnlyList<string> chunks = chunker.Split(text, ResolveChunkSize(data));

        IReadOnlyList<TelegramAttachmentDescriptor> attachments = BuildAttachmentDescriptors(seedPath, data);
        IReadOnlyList<Incursa.OpenAI.Codex.CodexInputItem> inputItems = TelegramAttachmentInputBuilder.BuildInputItems(
            parsed.IsCommand ? parsed.Text : text,
            attachments);

        Assert.NotNull(parsed.Text);
        Assert.All(chunks, chunk => Assert.NotNull(chunk));
        Assert.All(inputItems, item => Assert.NotNull(item));
    }

    public static IEnumerable<object[]> CorpusFiles()
        => Directory.EnumerateFiles(GetCorpusRoot(), "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".txt" or ".bin")
            .Order(StringComparer.Ordinal)
            .Select(path => new object[] { path });

    private static string GetCorpusRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            string candidate = Path.Combine(directory, "fuzz", "corpus");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate fuzz/corpus from the test output directory.");
    }

    private static int ResolveChunkSize(byte[] data)
        => data.Length == 0 ? 1 : Math.Clamp((int)data[0], 1, 4096);

    private static IReadOnlyList<TelegramAttachmentDescriptor> BuildAttachmentDescriptors(string seedPath, byte[] data)
    {
        if (data.Length == 0)
        {
            return [];
        }

        List<TelegramAttachmentDescriptor> attachments = [];
        string baseName = Path.GetFileNameWithoutExtension(seedPath);

        if ((data[0] & 0b0000_0001) != 0)
        {
            attachments.Add(new TelegramAttachmentDescriptor(
                Path.Combine(Path.GetTempPath(), "codex-telegram-fuzz", baseName + ".png"),
                "telegram-photo.png",
                "image/png",
                IsImage: true));
        }

        if ((data[0] & 0b0000_0010) != 0)
        {
            attachments.Add(new TelegramAttachmentDescriptor(
                Path.Combine(Path.GetTempPath(), "codex-telegram-fuzz", baseName + ".pdf"),
                "telegram-document.pdf",
                "application/pdf",
                IsImage: false));
        }

        if ((data[0] & 0b0000_0100) != 0)
        {
            attachments.Add(new TelegramAttachmentDescriptor(
                Path.Combine(Path.GetTempPath(), "codex-telegram-fuzz", baseName + ".webp"),
                "telegram-sticker.webp",
                "image/webp",
                IsImage: true));
        }

        return attachments;
    }
}
