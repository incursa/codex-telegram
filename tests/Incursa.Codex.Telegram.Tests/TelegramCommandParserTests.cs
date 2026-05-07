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
    public void Parse_StripsBotMentionFromQueueCommand()
    {
        TelegramCommandParser parser = new();

        ParsedTelegramCommand parsed = parser.Parse("/queue@codex_bot");

        Assert.True(parsed.IsCommand);
        Assert.Equal("queue", parsed.Name);
        Assert.Equal(string.Empty, parsed.Arguments);
        Assert.Equal("/queue@codex_bot", parsed.Text);
    }

    [Fact]
    public void Parse_CommandMentionWithoutCommandNameProducesEmptyCommandName()
    {
        TelegramCommandParser parser = new();

        ParsedTelegramCommand parsed = parser.Parse("/@my_bot diagnostics");

        Assert.True(parsed.IsCommand);
        Assert.Equal(string.Empty, parsed.Name);
        Assert.Equal("diagnostics", parsed.Arguments);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_BlankInputIsEmptyPlainText(string? text)
    {
        TelegramCommandParser parser = new();

        ParsedTelegramCommand parsed = parser.Parse(text);

        Assert.False(parsed.IsCommand);
        Assert.Equal(string.Empty, parsed.Name);
        Assert.Equal(string.Empty, parsed.Arguments);
        Assert.Equal(string.Empty, parsed.Text);
    }

    [Fact]
    public void Parse_CommandNameIsLowercaseAndArgumentsTrimmedAfterWhitespace()
    {
        TelegramCommandParser parser = new();

        ParsedTelegramCommand parsed = parser.Parse("  /MODEL\tgpt-5.4-mini  ");

        Assert.True(parsed.IsCommand);
        Assert.Equal("model", parsed.Name);
        Assert.Equal("gpt-5.4-mini", parsed.Arguments);
        Assert.Equal("/MODEL\tgpt-5.4-mini", parsed.Text);
    }

    [Fact]
    public void Parse_CommandArgumentsCanStartOnNextLine()
    {
        TelegramCommandParser parser = new();

        ParsedTelegramCommand parsed = parser.Parse("/send\r\nplease keep this");

        Assert.True(parsed.IsCommand);
        Assert.Equal("send", parsed.Name);
        Assert.Equal("please keep this", parsed.Arguments);
    }
}
