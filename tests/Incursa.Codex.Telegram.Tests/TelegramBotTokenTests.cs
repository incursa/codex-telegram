using Incursa.Codex.Telegram.Configuration;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramBotTokenTests
{
    [Fact]
    public void TryNormalize_AcceptsTrimmedBotFatherTokenShape()
    {
        bool valid = TelegramBotToken.TryNormalize(
            "  123456789:ABC_def-123  ",
            out string token,
            out string error);

        Assert.True(valid);
        Assert.Equal("123456789:ABC_def-123", token);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("abc:secret")]
    [InlineData("123:has space")]
    [InlineData("123:")]
    public void TryNormalize_RejectsInvalidTokenShapes(string value)
    {
        bool valid = TelegramBotToken.TryNormalize(value, out string token, out string error);

        Assert.False(valid);
        Assert.Equal(string.Empty, token);
        Assert.NotEmpty(error);
    }
}
