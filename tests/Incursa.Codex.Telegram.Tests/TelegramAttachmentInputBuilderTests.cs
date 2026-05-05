using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Telegram;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramAttachmentInputBuilderTests
{
    [Fact]
    public void BuildInputItems_PreservesTelegramEmojiText()
    {
        IReadOnlyList<CodexInputItem> items = TelegramAttachmentInputBuilder.BuildInputItems(
            "  Please inspect this 🚀 ✅ 👩‍💻  ",
            null);

        CodexTextInput text = Assert.IsType<CodexTextInput>(Assert.Single(items));
        Assert.Equal("Please inspect this 🚀 ✅ 👩‍💻", text.Text);
    }

    [Fact]
    public void BuildInputItems_MapsImagesAndDocumentsToCodexInputs()
    {
        TelegramAttachmentDescriptor image = new(
            @"C:\temp\codex-telegram\image.webp",
            "sticker.webp",
            "image/webp",
            IsImage: true);
        TelegramAttachmentDescriptor document = new(
            @"C:\temp\codex-telegram\notes.pdf",
            "notes.pdf",
            "application/pdf",
            IsImage: false);

        IReadOnlyList<CodexInputItem> items = TelegramAttachmentInputBuilder.BuildInputItems(
            "read both files",
            [image, document]);

        Assert.Collection(
            items,
            item => Assert.Equal("read both files", Assert.IsType<CodexTextInput>(item).Text),
            item => Assert.Equal(image.FilePath, Assert.IsType<CodexLocalImageInput>(item).Path),
            item =>
            {
                CodexMentionInput mention = Assert.IsType<CodexMentionInput>(item);
                Assert.Equal("notes.pdf", mention.Name);
                Assert.Equal(document.FilePath, mention.Path);
            });
    }

    [Fact]
    public void BuildInputItems_AllowsAttachmentOnlyMessages()
    {
        TelegramAttachmentDescriptor document = new(
            Path.Combine(Path.GetTempPath(), "codex-telegram", "report.bin"),
            "",
            null,
            IsImage: false);

        IReadOnlyList<CodexInputItem> items = TelegramAttachmentInputBuilder.BuildInputItems(
            "   ",
            [document]);

        CodexMentionInput mention = Assert.IsType<CodexMentionInput>(Assert.Single(items));
        Assert.Equal("report.bin", mention.Name);
        Assert.Equal(document.FilePath, mention.Path);
    }

    [Fact]
    public void BuildInputItems_UsesTelegramDisplayNameOverTemporaryFileName()
    {
        TelegramAttachmentDescriptor document = new(
            Path.Combine(Path.GetTempPath(), "codex-telegram", "upload-random.bin"),
            "operator-notes.pdf",
            "application/pdf",
            IsImage: false);

        IReadOnlyList<CodexInputItem> items = TelegramAttachmentInputBuilder.BuildInputItems(null, [document]);

        CodexMentionInput mention = Assert.IsType<CodexMentionInput>(Assert.Single(items));
        Assert.Equal("operator-notes.pdf", mention.Name);
        Assert.Equal(document.FilePath, mention.Path);
    }

    [Theory]
    [MemberData(nameof(TelegramTextCorpus))]
    public void BuildInputItems_DoesNotCorruptTelegramTextCorpus(string text)
    {
        IReadOnlyList<CodexInputItem> items = TelegramAttachmentInputBuilder.BuildInputItems(text, null);

        if (string.IsNullOrWhiteSpace(text))
        {
            Assert.Empty(items);
            return;
        }

        CodexTextInput input = Assert.IsType<CodexTextInput>(Assert.Single(items));
        Assert.Equal(text.Trim(), input.Text);
    }

    public static IEnumerable<object[]> TelegramTextCorpus()
    {
        yield return ["/send please keep /slashes and @mentions intact"];
        yield return ["Emoji-only intent: ✅ ❌ 🚫 🔁 📎 🧵"];
        yield return ["Family emoji with joiners: 👨‍👩‍👧‍👦"];
        yield return ["Combining marks: cafe\u0301 re\u0301sume\u0301"];
        yield return ["Right-to-left sample: שלום עולם"];
        yield return ["Markdown-ish text: *bold* _italic_ `code` [link](https://example.invalid)"];
        yield return ["HTML-ish text: <b>bold</b> & <script>not code</script>"];
        yield return ["Multiline\r\ntext\nwith Telegram-style line breaks"];
        yield return ["   "];
    }
}
