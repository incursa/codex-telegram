using Incursa.Codex.Telegram.Telegram;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramCommandParserTests
{
    [Fact]
    public void Parse_StripsBotMentionAndKeepsArguments()
    {
        TelegramCommandParser parser = new();

        ParsedTelegramCommand parsed = parser.Parse("/new@my_bot Build release notes");

        Assert.True(parsed.IsCommand);
        Assert.Equal("new", parsed.Name);
        Assert.Equal("Build release notes", parsed.Arguments);
        Assert.Equal("/new@my_bot Build release notes", parsed.Text);
    }

    [Fact]
    public void Parse_PlainTextIsNotCommand()
    {
        TelegramCommandParser parser = new();

        ParsedTelegramCommand parsed = parser.Parse("continue the thread");

        Assert.False(parsed.IsCommand);
        Assert.Equal("continue the thread", parsed.Text);
        Assert.Equal(string.Empty, parsed.Name);
    }
}
