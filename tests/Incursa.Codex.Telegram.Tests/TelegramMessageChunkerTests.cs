using Incursa.Codex.Telegram.Telegram;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramMessageChunkerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Split_EmptyInputProducesNonEmptyTelegramMessage(string? text)
    {
        TelegramMessageChunker chunker = new();

        IReadOnlyList<string> chunks = chunker.Split(text, 100);

        Assert.Equal([" "], chunks);
    }

    [Fact]
    public void Split_NormalizesCrLfBeforeChunking()
    {
        TelegramMessageChunker chunker = new();

        IReadOnlyList<string> chunks = chunker.Split("alpha\r\nbeta", 100);

        Assert.Equal(["alpha\nbeta"], chunks);
    }

    [Fact]
    public void Split_PreservesLineBoundariesWhenPossible()
    {
        TelegramMessageChunker chunker = new();

        IReadOnlyList<string> chunks = chunker.Split("alpha\nbeta\ngamma", 10);

        Assert.Equal(["alpha\nbeta", "gamma"], chunks);
    }

    [Fact]
    public void Split_SplitsLongLinesAtMaxLength()
    {
        TelegramMessageChunker chunker = new();

        IReadOnlyList<string> chunks = chunker.Split("abcdefghi", 4);

        Assert.Equal(["abcd", "efgh", "i"], chunks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Split_ClampsTooSmallMaxLengthToOne(int maxLength)
    {
        TelegramMessageChunker chunker = new();

        IReadOnlyList<string> chunks = chunker.Split("abc", maxLength);

        Assert.Equal(["a", "b", "c"], chunks);
    }

    [Fact]
    public void Split_ClampsTooLargeMaxLengthToTelegramLimit()
    {
        TelegramMessageChunker chunker = new();

        IReadOnlyList<string> chunks = chunker.Split(new string('x', 4001), 5000);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(4000, chunks[0].Length);
        Assert.Equal("x", chunks[1]);
    }
}
