using Incursa.Codex.Telegram.Configuration;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramSetupClientTests
{
    [Fact]
    public void CreateSetupChallengeReturnsRandomTerminalCode()
    {
        string first = TelegramSetupClient.CreateSetupChallenge();
        string second = TelegramSetupClient.CreateSetupChallenge();

        Assert.Matches("^CT-[0-9A-F]{8}$", first);
        Assert.Matches("^CT-[0-9A-F]{8}$", second);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("CT-12AB34CD", "CT-12AB34CD", true)]
    [InlineData("setup code ct-12ab34cd", "CT-12AB34CD", true)]
    [InlineData("/whoami", "CT-12AB34CD", false)]
    [InlineData("", "CT-12AB34CD", false)]
    [InlineData(null, "CT-12AB34CD", false)]
    [InlineData("CT-12AB34CD", "", false)]
    public void TextContainsSetupChallengeRequiresChallengeText(string? messageText, string expectedChallenge, bool expected)
    {
        bool actual = TelegramSetupClient.TextContainsSetupChallenge(messageText, expectedChallenge);

        Assert.Equal(expected, actual);
    }
}
