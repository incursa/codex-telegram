using Incursa.Codex.Telegram.Telegram;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramAuthorizationTests
{
    [Fact]
    public void IsAuthorized_AllowsAllowedUserInPrivateChat()
    {
        bool authorized = TelegramAuthorization.IsAuthorized(
            userId: 1234,
            chatId: 1234,
            chatType: "private",
            allowedUserIds: [1234],
            allowedChatIds: []);

        Assert.True(authorized);
    }

    [Fact]
    public void IsAuthorized_RequiresAllowedChatForGroups()
    {
        bool authorized = TelegramAuthorization.IsAuthorized(
            userId: 1234,
            chatId: -1001234,
            chatType: "supergroup",
            allowedUserIds: [1234],
            allowedChatIds: []);

        Assert.False(authorized);
    }

    [Fact]
    public void IsAuthorized_RequiresAllowedUserEvenWhenGroupIsAllowed()
    {
        bool authorized = TelegramAuthorization.IsAuthorized(
            userId: 9999,
            chatId: -1001234,
            chatType: "group",
            allowedUserIds: [1234],
            allowedChatIds: [-1001234]);

        Assert.False(authorized);
    }

    [Fact]
    public void IsAuthorized_AllowsGroupOnlyWhenUserAndChatAreAllowed()
    {
        bool authorized = TelegramAuthorization.IsAuthorized(
            userId: 1234,
            chatId: -1001234,
            chatType: "group",
            allowedUserIds: [1234],
            allowedChatIds: [-1001234]);

        Assert.True(authorized);
    }

    [Fact]
    public void IsAuthorized_AllowsGroupWhenAllowedUserAndChatIsTrustedAtRuntime()
    {
        bool authorized = TelegramAuthorization.IsAuthorized(
            userId: 1234,
            chatId: -1001234,
            chatType: "supergroup",
            allowedUserIds: [1234],
            allowedChatIds: [],
            trustedChatIds: [-1001234]);

        Assert.True(authorized);
    }
}
