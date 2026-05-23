using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramInputBundleStoreTests
{
    [Fact]
    public async Task GetOrCreateActiveAsync_ReusesOpenBundleAndPersistsPayload()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        ManualTimeProvider clock = new(DateTimeOffset.Parse("2026-05-23T10:00:00Z"));
        TelegramInputBundleStore store = CreateStore(temp.Path, clock);
        TelegramConversationScope conversation = new(1234, 55);

        TelegramInputBundle created = await store.GetOrCreateActiveAsync(conversation, 42, CancellationToken.None);
        TelegramInputBundle reused = await store.GetOrCreateActiveAsync(conversation, 42, CancellationToken.None);

        Assert.Equal(created.Id, reused.Id);

        string attachmentPath = CreateAttachment(temp, "note.txt");
        clock.Advance(TimeSpan.FromMinutes(5));
        TelegramInputBundle? appended = await store.TryAppendAsync(
            created.Id,
            42,
            "  hello bundle  ",
            [new TelegramAttachmentDescriptor(attachmentPath, "note.txt", "text/plain", IsImage: false)],
            [100, 100, 0],
            CancellationToken.None);
        Assert.NotNull(appended);

        TelegramInputBundle? withCard = await store.TrySetStatusCardMessageIdAsync(created.Id, 42, 77, CancellationToken.None);
        Assert.NotNull(withCard);

        TelegramInputBundleStore reloaded = CreateStore(temp.Path, clock);
        TelegramInputBundle? persisted = await reloaded.TryGetAsync(created.Id, CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(conversation, persisted.Conversation);
        Assert.Equal(42, persisted.UserId);
        Assert.Equal("hello bundle", Assert.Single(persisted.TextParts).Text);
        Assert.Equal([100], persisted.SourceMessageIds);
        Assert.Equal(attachmentPath, Assert.Single(persisted.Attachments).FilePath);
        Assert.Equal(77, persisted.StatusCardMessageId);
        Assert.Equal(DateTimeOffset.Parse("2026-05-23T10:35:00Z"), persisted.ExpiresAt);
    }

    [Fact]
    public async Task AppendAsync_ReusesOpenBundleWithoutRetargetingExistingDraft()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        ManualTimeProvider clock = new(DateTimeOffset.Parse("2026-05-23T10:00:00Z"));
        TelegramInputBundleStore store = CreateStore(temp.Path, clock);
        TelegramConversationScope conversation = new(1234, 55);

        TelegramInputBundle first = await store.AppendAsync(
            new TelegramInputBundleAppendRequest(
                conversation,
                42,
                "thread-1",
                "First session",
                TelegramInputBundleIntent.SendNow,
                "first note",
                "text",
                null,
                1,
                "trace-1"),
            CancellationToken.None);
        TelegramInputBundle second = await store.AppendAsync(
            new TelegramInputBundleAppendRequest(
                conversation,
                42,
                "thread-2",
                "Second session",
                TelegramInputBundleIntent.SteerCurrentTurn,
                "second note",
                "text",
                null,
                2,
                "trace-2"),
            CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("thread-1", second.SessionId);
        Assert.Equal("First session", second.SessionName);
        Assert.Equal(TelegramInputBundleIntent.SendNow, second.Intent);
        Assert.Equal("trace-1", second.TraceId);
        Assert.Equal(["first note", "second note"], second.TextParts.Select(part => part.Text).ToArray());
        Assert.Equal([1, 2], second.SourceMessageIds);
    }

    [Fact]
    public async Task TryUpdateAndCompleteRequireOwner()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        ManualTimeProvider clock = new(DateTimeOffset.Parse("2026-05-23T10:00:00Z"));
        TelegramInputBundleStore store = CreateStore(temp.Path, clock);
        TelegramInputBundle bundle = await store.CreateAsync(new TelegramConversationScope(1234, null), 42, CancellationToken.None);
        string attachmentPath = CreateAttachment(temp, "queued.txt");
        await store.TryAppendAsync(
            bundle.Id,
            42,
            "queue me",
            [new TelegramAttachmentDescriptor(attachmentPath, "queued.txt", "text/plain", IsImage: false)],
            [12],
            CancellationToken.None);

        Assert.Null(await store.TrySetIntentAsync(bundle.Id, 99, TelegramInputBundleIntent.QueueNext, CancellationToken.None));
        Assert.Null(await store.TryCompleteBundleAsync(
            bundle.Id,
            99,
            TelegramInputBundleIntent.QueueNext,
            TelegramInputBundleStatus.Queued,
            deleteAttachments: false,
            CancellationToken.None));

        TelegramInputBundle? updated = await store.TrySetIntentAsync(bundle.Id, 42, TelegramInputBundleIntent.QueueNext, CancellationToken.None);
        TelegramInputBundle? completed = await store.TryCompleteBundleAsync(
            bundle.Id,
            42,
            TelegramInputBundleIntent.QueueNext,
            TelegramInputBundleStatus.Queued,
            deleteAttachments: false,
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.NotNull(completed);
        Assert.Equal(TelegramInputBundleIntent.QueueNext, completed.Intent);
        Assert.Equal(TelegramInputBundleStatus.Queued, completed.Status);
        Assert.True(File.Exists(attachmentPath));
    }

    [Fact]
    public async Task TryCancelAsync_DeletesTempAttachmentsAndPersistsCancelledState()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        ManualTimeProvider clock = new(DateTimeOffset.Parse("2026-05-23T10:00:00Z"));
        TelegramInputBundleStore store = CreateStore(temp.Path, clock);
        TelegramInputBundle bundle = await store.CreateAsync(new TelegramConversationScope(1234, null), 42, CancellationToken.None);
        string attachmentPath = CreateAttachment(temp, "cancel.txt");
        await store.TryAppendAsync(
            bundle.Id,
            42,
            null,
            [new TelegramAttachmentDescriptor(attachmentPath, "cancel.txt", "text/plain", IsImage: false)],
            [20],
            CancellationToken.None);

        TelegramInputBundle? cancelled = await store.TryCancelAsync(bundle.Id, 42, CancellationToken.None);

        Assert.NotNull(cancelled);
        Assert.Equal(TelegramInputBundleStatus.Cancelled, cancelled.Status);
        Assert.Empty(cancelled.Attachments);
        Assert.False(File.Exists(attachmentPath));

        TelegramInputBundleStore reloaded = CreateStore(temp.Path, clock);
        TelegramInputBundle? persisted = await reloaded.TryGetAsync(bundle.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(TelegramInputBundleStatus.Cancelled, persisted.Status);
        Assert.Empty(persisted.Attachments);
    }

    [Fact]
    public async Task ExpireAsync_DeletesTempAttachmentsAndNextGetCreatesFreshBundle()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        ManualTimeProvider clock = new(DateTimeOffset.Parse("2026-05-23T10:00:00Z"));
        TelegramInputBundleStore store = CreateStore(temp.Path, clock, expirationMinutes: 1);
        TelegramConversationScope conversation = new(1234, 55);
        TelegramInputBundle bundle = await store.GetOrCreateActiveAsync(conversation, 42, CancellationToken.None);
        string attachmentPath = CreateAttachment(temp, "expired.txt");
        await store.TryAppendAsync(
            bundle.Id,
            42,
            "expires",
            [new TelegramAttachmentDescriptor(attachmentPath, "expired.txt", "text/plain", IsImage: false)],
            [30],
            CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(2));
        IReadOnlyList<TelegramInputBundle> expired = await store.ExpireAsync(CancellationToken.None);
        TelegramInputBundle next = await store.GetOrCreateActiveAsync(conversation, 42, CancellationToken.None);

        TelegramInputBundle expiredBundle = Assert.Single(expired);
        Assert.Equal(bundle.Id, expiredBundle.Id);
        Assert.Equal(TelegramInputBundleStatus.Expired, expiredBundle.Status);
        Assert.Empty(expiredBundle.Attachments);
        Assert.False(File.Exists(attachmentPath));
        Assert.NotEqual(bundle.Id, next.Id);
    }

    private static TelegramInputBundleStore CreateStore(
        string dataRoot,
        TimeProvider timeProvider,
        int expirationMinutes = 30)
        => new(
            Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
            {
                Workspace = new CodexWorkspaceOptions
                {
                    DataRoot = dataRoot,
                },
            }),
            Microsoft.Extensions.Options.Options.Create(new TelegramInputOptions
            {
                BundleExpirationMinutes = expirationMinutes,
            }),
            timeProvider);

    private static string CreateAttachment(TemporaryDirectory temp, string fileName)
    {
        string directory = temp.CreateDirectory("attachments");
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "temporary attachment");
        return path;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
            => _utcNow;

        public void Advance(TimeSpan delta)
            => _utcNow = _utcNow.Add(delta);
    }
}
