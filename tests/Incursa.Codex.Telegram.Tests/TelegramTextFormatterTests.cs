using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramTextFormatterTests
{
    [Fact]
    public void OutputDefaultsToLegacyPlainText()
    {
        Assert.Equal(TelegramTextFormat.PlainText, new TelegramOutputOptions().TextFormat);
        Assert.Equal("**bold** [docs](https://example.test)", TelegramTextFormatter.Format("**bold** [docs](https://example.test)", TelegramTextFormat.PlainText));
        Assert.Equal("line1\r\nline2", TelegramTextFormatter.Format("line1\r\nline2", TelegramTextFormat.PlainText));
    }

    [Fact]
    public void SafeMarkdownPreservesConstrainedMarkersAndEscapesSpecialText()
    {
        string formatted = TelegramTextFormatter.Format(
            "**bold** *italic* [docs](https://example.test/docs_(v2)) `inline`\n# heading\n- item\n1. numbered\nprice: 5.00!",
            TelegramTextFormat.SafeMarkdownV2);

        Assert.Contains("*bold*", formatted);
        Assert.Contains("_italic_", formatted);
        Assert.Contains("[docs](https://example.test/docs_\\(v2\\))", formatted);
        Assert.Contains("`inline`", formatted);
        Assert.Contains("*heading*", formatted);
        Assert.Contains("• item", formatted);
        Assert.Contains("• numbered", formatted);
        Assert.Contains("5\\.00\\!", formatted);
    }

    [Fact]
    public void MalformedMarkupAndUnsupportedLinksBecomeLiteralText()
    {
        string formatted = TelegramTextFormatter.Format(
            "**unterminated [bad](javascript:alert(1)) [credential](https://user:secret@example.test) `unterminated",
            TelegramTextFormat.SafeMarkdownV2);

        Assert.Contains("\\*\\*unterminated", formatted);
        Assert.Contains("\\[bad\\]", formatted);
        Assert.Contains("javascript", formatted);
        Assert.Contains("credential", formatted);
        Assert.DoesNotContain("[credential](https://user:secret", formatted);
        Assert.Contains("\\`unterminated", formatted);
        Assert.DoesNotContain("[bad](javascript", formatted);
    }

    [Fact]
    public void FencedCodeEscapesEmbeddedFenceCharacters()
    {
        string formatted = TelegramTextFormatter.Format("```csharp\nvar value = `x`;\n```", TelegramTextFormat.SafeMarkdownV2);

        Assert.StartsWith("```\n", formatted);
        Assert.Contains("var value = \\`x\\`;", formatted);
        Assert.EndsWith("\n```", formatted);
    }

    [Fact]
    public void ChunkerFallsBackToPlainTextWhenMarkupWouldCrossBoundary()
    {
        TelegramMessageChunker chunker = new();

        IReadOnlyList<TelegramTextChunk> chunks = chunker.SplitFormatted(
            "```\n" + new string('x', 40) + "\n```",
            20,
            TelegramTextFormat.SafeMarkdownV2);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.Equal(TelegramTextFormat.PlainText, chunk.Format));
        // The chunker retains natural newline boundaries while falling back to
        // plain text rather than emitting incomplete MarkdownV2.
        Assert.Equal(48, string.Concat(chunks.Select(chunk => chunk.Text)).Length);
    }

    [Fact]
    public void ChunkerFallsBackWhenEmphasisMarkerWouldCrossBoundary()
    {
        TelegramMessageChunker chunker = new();

        IReadOnlyList<TelegramTextChunk> chunks = chunker.SplitFormatted(
            "**" + new string('x', 40) + "**",
            20,
            TelegramTextFormat.SafeMarkdownV2);

        Assert.All(chunks, chunk => Assert.Equal(TelegramTextFormat.PlainText, chunk.Format));
    }
}
