using Incursa.Codex.Telegram.Tests.TestSupport;
using Incursa.Codex.Telegram.Services;
using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Tests;

public sealed class ScriptedCodexRuntimeClientTests
{
    [Fact]
    public async Task StartResumeForkAndStatusChangesAreScriptable()
    {
        ScriptedCodexRuntimeClient runtime = new();
        runtime.QueueStartThreadId("thread-1");
        runtime.QueueForkThreadId("thread-2");

        ICodexThreadHandle thread = await runtime.StartThreadAsync(new CodexThreadOptions
        {
            WorkingDirectory = @"C:\work",
        }, CancellationToken.None);

        Assert.Equal("thread-1", thread.Id);

        await thread.SetNameAsync("Alpha", CancellationToken.None);

        CodexThreadGoal goal = await thread.SetGoalAsync("Ship the scripted runtime seam", 99, CancellationToken.None);
        Assert.Equal("thread-1", goal.ThreadId);
        Assert.Equal("Ship the scripted runtime seam", goal.Objective);
        Assert.Equal(CodexThreadGoalStatus.Active, goal.Status);
        Assert.Equal(99, goal.TokenBudget);

        ICodexThreadHandle resumed = await runtime.ResumeThreadAsync("thread-1", null, CancellationToken.None);
        CodexThreadGoal? resumedGoal = await resumed.GetGoalAsync(CancellationToken.None);
        Assert.NotNull(resumedGoal);
        Assert.Equal(goal.Objective, resumedGoal.Objective);
        Assert.Equal(goal.Status, resumedGoal.Status);

        ICodexThreadHandle forked = await runtime.ForkThreadAsync("thread-1", new CodexThreadForkOptions(), CancellationToken.None);
        Assert.Equal("thread-2", forked.Id);

        CodexThreadGoal? forkedGoal = await forked.GetGoalAsync(CancellationToken.None);
        Assert.NotNull(forkedGoal);
        Assert.Equal(goal.Objective, forkedGoal.Objective);
        Assert.Equal(goal.Status, forkedGoal.Status);

        CodexThreadListResult listBeforeArchive = await runtime.ListThreadsAsync(new CodexThreadListOptions(), CancellationToken.None);
        Assert.Equal(new[] { "thread-1", "thread-2" }, listBeforeArchive.Threads.Select(threadSummary => threadSummary.Id).ToArray());
        Assert.Equal("Alpha", listBeforeArchive.Threads[0].Name);

        await runtime.ArchiveThreadAsync("thread-1", CancellationToken.None);
        CodexThreadSnapshot archivedSnapshot = await runtime.ReadThreadAsync("thread-1", new CodexThreadReadOptions
        {
            IncludeTurns = true,
        }, CancellationToken.None);
        Assert.Equal("archived", archivedSnapshot.Status.Type);

        await runtime.UnarchiveThreadAsync("thread-1", CancellationToken.None);
        CodexThreadSnapshot restoredSnapshot = await runtime.ReadThreadAsync("thread-1", new CodexThreadReadOptions
        {
            IncludeTurns = true,
        }, CancellationToken.None);
        Assert.Equal("idle", restoredSnapshot.Status.Type);

        Assert.True(await thread.ClearGoalAsync(CancellationToken.None));
        Assert.Null(await thread.GetGoalAsync(CancellationToken.None));
    }

    [Fact]
    public async Task QueuedListAndReadResultsOverrideDerivedState()
    {
        ScriptedCodexRuntimeClient runtime = new();
        DateTimeOffset createdAt = new(2026, 5, 12, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset updatedAt = createdAt.AddMinutes(5);

        runtime.QueueListThreadsResult(new CodexThreadListResult
        {
            Threads = new[]
            {
                CreateSummary("thread-queued", "Queued thread", "queued preview", @"C:\queue", createdAt, updatedAt),
            },
            NextCursor = "cursor-1",
        });

        runtime.QueueReadThreadSnapshot("thread-queued", new CodexThreadSnapshot
        {
            Id = "thread-queued",
            Name = "Queued snapshot",
            Preview = "snapshot preview",
            Status = CreateIdleStatus(),
            ModelProvider = "test",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Ephemeral = false,
            Path = @"C:\queue",
            Source = CreateSessionSource(),
            Turns = Array.Empty<CodexTurnRecord>(),
        });

        CodexThreadListResult list = await runtime.ListThreadsAsync(new CodexThreadListOptions(), CancellationToken.None);
        Assert.Equal("cursor-1", list.NextCursor);
        Assert.Single(list.Threads);
        Assert.Equal("thread-queued", list.Threads[0].Id);
        Assert.Equal("Queued thread", list.Threads[0].Name);

        CodexThreadSnapshot snapshot = await runtime.ReadThreadAsync("thread-queued", new CodexThreadReadOptions
        {
            IncludeTurns = true,
        }, CancellationToken.None);
        Assert.Equal("Queued snapshot", snapshot.Name);
        Assert.Equal("snapshot preview", snapshot.Preview);
        Assert.Equal(@"C:\queue", snapshot.Path);
    }

    [Fact]
    public async Task InitializeAndListModelsCanBeQueued()
    {
        ScriptedCodexRuntimeClient runtime = new();
        runtime.QueueInitializeResult(new CodexRuntimeMetadata
        {
            PlatformFamily = "test",
            PlatformOs = "windows",
            UserAgent = "scripted-runtime",
        });
        runtime.QueueListModelsResult(new CodexModelListResult
        {
            Models = Array.Empty<CodexModel>(),
            NextCursor = "models-cursor",
        });

        CodexRuntimeMetadata metadata = await runtime.InitializeAsync(CancellationToken.None);
        Assert.Equal("scripted-runtime", metadata.UserAgent);

        CodexModelListResult models = await runtime.ListModelsAsync(new CodexModelListOptions(), CancellationToken.None);
        Assert.Equal("models-cursor", models.NextCursor);
        Assert.Empty(models.Models);
    }

    private static CodexThreadSummary CreateSummary(
        string threadId,
        string name,
        string preview,
        string path,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
        => new()
        {
            Id = threadId,
            Name = name,
            Preview = preview,
            Status = CreateIdleStatus(),
            ModelProvider = "test",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Ephemeral = false,
            Path = path,
            Source = CreateSessionSource(),
        };

    private static CodexThreadStatus CreateIdleStatus()
        => new CodexIdleThreadStatus
        {
            Type = "idle",
        };

    private static CodexSessionSource CreateSessionSource()
        => new CodexSubAgentSessionSource(new CodexOtherSubAgentSource("test"));
}
