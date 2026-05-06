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
    public async Task ListConversationStatesReturnsPersistedConversationScopes()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        TelegramConversationScope privateConversation = new(1234, null);
        TelegramConversationScope topicConversation = new(1234, 55);
        TelegramBotStateStore store = CreateStore(temp.Path);

        await store.SetActiveSessionIdAsync(privateConversation, "thread-private", CancellationToken.None);
        await store.SetActiveProjectWorkingDirectoryAsync(privateConversation, Path.Combine(temp.Path, "private"), CancellationToken.None);
        await store.SetActiveSessionIdAsync(topicConversation, "thread-topic", CancellationToken.None);

        TelegramBotStateStore reloaded = CreateStore(temp.Path);
        IReadOnlyCollection<TelegramConversationState> states = await reloaded.ListConversationStatesAsync(CancellationToken.None);

        Assert.Contains(states, state =>
            state.Scope == privateConversation
            && state.ActiveSessionId == "thread-private"
            && state.ActiveProjectWorkingDirectory == Path.GetFullPath(Path.Combine(temp.Path, "private")));
        Assert.Contains(states, state =>
            state.Scope == topicConversation
            && state.ActiveSessionId == "thread-topic");
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

    [Fact]
    public async Task ListQueuedPromptsFiltersByUserAndConversationInQueueOrder()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        TelegramConversationScope conversation = new(55, 9);
        TelegramBotStateStore store = CreateStore(temp.Path);
        TelegramQueuedPrompt first = CreatePrompt("prompt-1", userId: 1234, conversation, "first", DateTimeOffset.Parse("2026-05-06T12:00:00Z"));
        TelegramQueuedPrompt second = CreatePrompt("prompt-2", userId: 1234, conversation, "second", DateTimeOffset.Parse("2026-05-06T12:01:00Z"));
        TelegramQueuedPrompt otherUser = CreatePrompt("prompt-3", userId: 9999, conversation, "other", DateTimeOffset.Parse("2026-05-06T12:02:00Z"));
        TelegramQueuedPrompt otherConversation = CreatePrompt("prompt-4", userId: 1234, new TelegramConversationScope(55, 10), "topic", DateTimeOffset.Parse("2026-05-06T12:03:00Z"));

        await store.EnqueueQueuedPromptAsync(second, CancellationToken.None);
        await store.EnqueueQueuedPromptAsync(otherUser, CancellationToken.None);
        await store.EnqueueQueuedPromptAsync(otherConversation, CancellationToken.None);
        await store.EnqueueQueuedPromptAsync(first, CancellationToken.None);

        IReadOnlyList<TelegramQueuedPrompt> prompts = await store.ListQueuedPromptsAsync(1234, conversation, CancellationToken.None);

        Assert.Equal(["prompt-1", "prompt-2"], prompts.Select(prompt => prompt.Id));
    }

    [Fact]
    public async Task TryUpdateQueuedPromptTextRequiresOwnerAndPreservesQueueItem()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        TelegramConversationScope conversation = new(55, null);
        TelegramBotStateStore store = CreateStore(temp.Path);
        TelegramQueuedPrompt prompt = CreatePrompt("prompt-1", userId: 1234, conversation, "original", DateTimeOffset.Parse("2026-05-06T12:00:00Z"));

        await store.EnqueueQueuedPromptAsync(prompt, CancellationToken.None);

        Assert.Null(await store.TryUpdateQueuedPromptTextAsync("prompt-1", 9999, "blocked", CancellationToken.None));
        TelegramQueuedPrompt? updated = await store.TryUpdateQueuedPromptTextAsync("prompt-1", 1234, "updated", CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("updated", updated.Text);
        TelegramQueuedPrompt persisted = Assert.Single(await store.ListQueuedPromptsAsync(1234, conversation, CancellationToken.None));
        Assert.Equal("updated", persisted.Text);
        Assert.Equal(prompt.EnqueuedAt, persisted.EnqueuedAt);
    }

    [Fact]
    public async Task TryRemoveQueuedPromptRequiresOwnerAndReturnsRemovedPrompt()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        TelegramConversationScope conversation = new(55, null);
        TelegramBotStateStore store = CreateStore(temp.Path);
        TelegramQueuedPrompt prompt = CreatePrompt("prompt-1", userId: 1234, conversation, "text", DateTimeOffset.Parse("2026-05-06T12:00:00Z"));

        await store.EnqueueQueuedPromptAsync(prompt, CancellationToken.None);

        Assert.Null(await store.TryRemoveQueuedPromptAsync("prompt-1", 9999, CancellationToken.None));
        TelegramQueuedPrompt? removed = await store.TryRemoveQueuedPromptAsync("prompt-1", 1234, CancellationToken.None);

        Assert.Equal(prompt, removed);
        Assert.Empty(await store.ListQueuedPromptsAsync(1234, conversation, CancellationToken.None));
    }

    private static TelegramQueuedPrompt CreatePrompt(
        string id,
        long userId,
        TelegramConversationScope conversation,
        string text,
        DateTimeOffset enqueuedAt)
        => new(
            id,
            userId,
            conversation.ChatId,
            "thread-1",
            "Session",
            text,
            enqueuedAt,
            conversation.MessageThreadId,
            null);

    private static TelegramBotStateStore CreateStore(string dataRoot)
        => new(Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions
            {
                DataRoot = dataRoot,
            },
        }));
}
