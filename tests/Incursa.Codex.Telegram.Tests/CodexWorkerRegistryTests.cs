using System.Globalization;
using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexWorkerRegistryTests
{
    [Fact]
    public async Task WorkerIdentityPersistsAndLeaseIsIdempotent()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z", CultureInfo.InvariantCulture));
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path, maxConcurrentTasks: 2);

        using (CodexWorkerRegistry registry = CreateRegistry(options, clock, dataRoot.Path))
        {
            CodexWorkerLease? first = await registry.TryAcquireLeaseAsync("task-1", TimeSpan.FromMinutes(10), CancellationToken.None);
            CodexWorkerLease? second = await registry.TryAcquireLeaseAsync("task-1", TimeSpan.FromMinutes(10), CancellationToken.None);

            Assert.NotNull(first);
            Assert.Equal(first, second);
            Assert.True(File.Exists(Path.Combine(dataRoot.Path, "codex-worker-state.json")));
        }

        using (CodexWorkerRegistry reloaded = CreateRegistry(options, clock, dataRoot.Path))
        {
            CodexWorkerSnapshot snapshot = await reloaded.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal("worker:test", snapshot.WorkerId);
            Assert.Equal("Test worker", snapshot.DisplayName);
            Assert.Equal(1, snapshot.ActiveLeaseCount);
            Assert.Contains("task-workspaces", snapshot.Capabilities);
        }
    }

    [Fact]
    public async Task DrainingRejectsNewLeasesAndResumeAllowsThem()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z", CultureInfo.InvariantCulture));
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path, maxConcurrentTasks: 2);
        using CodexWorkerRegistry registry = CreateRegistry(options, clock, dataRoot.Path);

        Assert.True(await registry.SetDrainingAsync(true, CancellationToken.None));
        Assert.Null(await registry.TryAcquireLeaseAsync("task-1", TimeSpan.FromMinutes(10), CancellationToken.None));
        Assert.Equal(CodexWorkerState.Draining, (await registry.GetSnapshotAsync(CancellationToken.None)).State);

        Assert.True(await registry.SetDrainingAsync(false, CancellationToken.None));
        Assert.NotNull(await registry.TryAcquireLeaseAsync("task-1", TimeSpan.FromMinutes(10), CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredLeaseIsRemovedBeforeCapacityCheck()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z", CultureInfo.InvariantCulture));
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path, maxConcurrentTasks: 1);
        using CodexWorkerRegistry registry = CreateRegistry(options, clock, dataRoot.Path);

        Assert.NotNull(await registry.TryAcquireLeaseAsync("expired", TimeSpan.FromMinutes(1), CancellationToken.None));
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        CodexWorkerLease? replacement = await registry.TryAcquireLeaseAsync("replacement", TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.NotNull(replacement);
        Assert.Equal(1, (await registry.GetSnapshotAsync(CancellationToken.None)).ActiveLeaseCount);
    }

    private static CodexWorkerRegistry CreateRegistry(
        IOptions<CodexTelegramOptions> options,
        TimeProvider clock,
        string dataRoot)
        => new(options, new TestCodexGateway(), clock, dataRoot);

    private static IOptions<CodexTelegramOptions> CreateOptions(string dataRoot, int maxConcurrentTasks)
        => Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot },
            Worker = new CodexWorkerOptions
            {
                WorkerId = "worker:test",
                DisplayName = "Test worker",
                MaxConcurrentTasks = maxConcurrentTasks,
            },
        });

    private sealed class FixedTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class TestCodexGateway : ICodexGateway
    {
        public Task<CodexRuntimeStateVm> GetRuntimeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CodexRuntimeStateVm(
                true,
                "ready",
                "test",
                "1.0",
                "test",
                "test",
                "test",
                [],
                [],
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true));

        public Task<IReadOnlyList<CodexThreadListItemVm>> ListThreadsAsync(CodexThreadListQuery query, CancellationToken cancellationToken = default) => NotSupported<IReadOnlyList<CodexThreadListItemVm>>();
        public Task<CodexThreadDetailVm> GetThreadAsync(string threadId, string? workspaceQuery = null, CancellationToken cancellationToken = default) => NotSupported<CodexThreadDetailVm>();
        public Task<CodexThreadListItemVm> CreateThreadShellAsync(CodexThreadContextSubmission submission, CancellationToken cancellationToken = default) => NotSupported<CodexThreadListItemVm>();
        public Task<CodexThreadExecutionVm> CreateThreadAsync(CodexTurnSubmission submission, CancellationToken cancellationToken = default) => NotSupported<CodexThreadExecutionVm>();
        public Task<CodexThreadExecutionVm> SendTurnAsync(string threadId, CodexTurnSubmission submission, CancellationToken cancellationToken = default) => NotSupported<CodexThreadExecutionVm>();
        public Task<CodexThreadExecutionVm> SendTurnAsync(string threadId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken = default) => NotSupported<CodexThreadExecutionVm>();
        public Task<CodexThreadExecutionVm> ForkThreadAsync(string threadId, CodexTurnSubmission submission, CancellationToken cancellationToken = default) => NotSupported<CodexThreadExecutionVm>();
        public Task<CodexThreadDetailVm> RenameThreadAsync(string threadId, string name, CancellationToken cancellationToken = default) => NotSupported<CodexThreadDetailVm>();
        public Task<CodexThreadDetailVm> SetArchiveStateAsync(string threadId, bool archived, CancellationToken cancellationToken = default) => NotSupported<CodexThreadDetailVm>();
        public Task<CodexThreadDetailVm> CompactThreadAsync(string threadId, CancellationToken cancellationToken = default) => NotSupported<CodexThreadDetailVm>();
        public Task<CodexThreadGoalVm?> GetThreadGoalAsync(string threadId, CancellationToken cancellationToken = default) => NotSupported<CodexThreadGoalVm?>();
        public Task<CodexThreadGoalVm> SetThreadGoalAsync(string threadId, string objective, long? tokenBudget = null, CancellationToken cancellationToken = default) => NotSupported<CodexThreadGoalVm>();
        public Task<CodexThreadGoalVm> SetThreadGoalStatusAsync(string threadId, CodexThreadGoalStatus status, CancellationToken cancellationToken = default) => NotSupported<CodexThreadGoalVm>();
        public Task<bool> ClearThreadGoalAsync(string threadId, CancellationToken cancellationToken = default) => NotSupported<bool>();
        public Task<IReadOnlyList<CodexWorkspaceEntryVm>> SearchWorkspaceAsync(string? query = null, CancellationToken cancellationToken = default) => NotSupported<IReadOnlyList<CodexWorkspaceEntryVm>>();
        public Task<IReadOnlyList<CodexModelVm>> ListModelsAsync(CancellationToken cancellationToken = default) => NotSupported<IReadOnlyList<CodexModelVm>>();

        private static Task<T> NotSupported<T>() => Task.FromException<T>(new NotSupportedException());
    }
}
