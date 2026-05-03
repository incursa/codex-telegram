using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramBotStateStoreTests
{
    [Fact]
    public async Task StatePersistsActiveSessionAndProjectByConversationScope()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        TelegramConversationScope conversation = new(1234, 55);
        TelegramBotStateStore store = CreateStore(temp.Path);

        await store.SetActiveSessionIdAsync(conversation, "thread-1", CancellationToken.None);
        await store.SetActiveProjectWorkingDirectoryAsync(conversation, temp.Path, CancellationToken.None);

        TelegramBotStateStore reloaded = CreateStore(temp.Path);

        Assert.Equal("thread-1", await reloaded.GetActiveSessionIdAsync(conversation, CancellationToken.None));
        Assert.Equal(temp.Path, await reloaded.GetActiveProjectWorkingDirectoryAsync(conversation, CancellationToken.None));
        Assert.Contains("thread-1", await reloaded.GetTrackedSessionIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task QueuedPromptSkipsUnavailableSessions()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        TelegramBotStateStore store = CreateStore(temp.Path);
        TelegramQueuedPrompt prompt = new(
            "prompt-1",
            1234,
            55,
            "thread-1",
            "Session",
            "hello",
            DateTimeOffset.UtcNow,
            null,
            null);

        await store.EnqueueQueuedPromptAsync(prompt, CancellationToken.None);

        Assert.Null(await store.DequeueNextQueuedPromptAsync(["thread-1"], CancellationToken.None));
        Assert.Equal(prompt, await store.DequeueNextQueuedPromptAsync([], CancellationToken.None));
    }

    private static TelegramBotStateStore CreateStore(string dataRoot)
        => new(Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions
            {
                DataRoot = dataRoot,
            },
        }));
}
