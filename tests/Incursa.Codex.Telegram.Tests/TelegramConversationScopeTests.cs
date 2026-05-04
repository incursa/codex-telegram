using Incursa.Codex.Telegram.Telegram;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramConversationScopeTests
{
    [Fact]
    public void ToStorageKey_PrivateChatUsesChatIdOnly()
    {
        TelegramConversationScope scope = new(1234, null);

        Assert.Equal("1234", scope.ToStorageKey());
        Assert.Equal("1234", scope.ToString());
    }

    [Fact]
    public void ToStorageKey_TopicIncludesThreadId()
    {
        TelegramConversationScope scope = new(-1005555, 77);

        Assert.Equal("-1005555:77", scope.ToStorageKey());
        Assert.Equal("-1005555:77", scope.ToString());
    }

    [Theory]
    [InlineData("1234", 1234, null)]
    [InlineData("-1005555:77", -1005555, 77)]
    [InlineData(" -1005555 : 77 ", -1005555, 77)]
    public void TryParseStorageKey_ParsesValidKeys(string value, long chatId, int? threadId)
    {
        bool parsed = TelegramConversationScope.TryParseStorageKey(value, out TelegramConversationScope scope);

        Assert.True(parsed);
        Assert.Equal(chatId, scope.ChatId);
        Assert.Equal(threadId, scope.MessageThreadId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-chat")]
    [InlineData("1234:not-a-thread")]
    public void TryParseStorageKey_RejectsInvalidKeys(string? value)
    {
        bool parsed = TelegramConversationScope.TryParseStorageKey(value, out TelegramConversationScope scope);

        Assert.False(parsed);
        Assert.Equal(default, scope);
    }
}
