using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Telegram;
using Telegram.Bot.Types;

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

    [Fact]
    public void SetupUpdateBufferKeepsUnrelatedUpdatesForTheHostedReceiver()
    {
        Update update = new() { Id = Random.Shared.Next(1, int.MaxValue) };
        string token = $"123456:{Guid.NewGuid():N}";

        TelegramSetupUpdateBuffer.Enqueue(token, update);

        Assert.Same(update, Assert.Single(TelegramSetupUpdateBuffer.Drain(token)));
        Assert.Empty(TelegramSetupUpdateBuffer.Drain(token));
    }
}
