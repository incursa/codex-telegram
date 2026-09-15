using System.Text.Json;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Incursa.Codex.Telegram.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexRemoteTaskEndpointTests
{
    [Fact]
    public async Task ProvisionEndpointRequiresCoordinatorAuthentication()
    {
        IOptions<CodexTelegramOptions> options = Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Coordinator = new CodexCoordinatorOptions
            {
                Enabled = true,
                WorkerRegistrationEnabled = true,
                AuthenticationToken = "coordinator-token-that-is-long-enough-1234",
            },
        });
        DefaultHttpContext context = new();
        context.RequestServices = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();
        context.Request.Headers.Authorization = "Bearer wrong-token";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { TaskId = "task:1" }));
        context.Response.Body = new MemoryStream();

        IResult result = await CodexRemoteTaskEndpoints.ProvisionAsync(
            context,
            options,
            workerRegistry: null!,
            workspaceManager: null!,
            sessionManager: null!,
            supervisionLedger: null!,
            recipeCatalog: null!,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task ProvisionEndpointCreatesWorkerLocalTaskAndBindsLease()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        string repositoryRoot = dataRoot.CreateDirectory("repository");
        string worktreePath = dataRoot.CreateDirectory("worktree");
        IOptions<CodexTelegramOptions> options = Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Mode = CodexTelegramMode.Repository,
            RepositoryRoot = repositoryRoot,
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot.Path },
            Coordinator = new CodexCoordinatorOptions
            {
                Enabled = true,
                WorkerRegistrationEnabled = true,
                AuthenticationToken = "coordinator-token-that-is-long-enough-1234",
            },
        });
        FakeWorkerRegistry workerRegistry = new();
        FakeWorkspaceManager workspaceManager = new(new CodexTaskWorkspaceRecord(
            "workspace:1",
            "task:1",
            repositoryRoot,
            worktreePath,
            "codex/task/task-1",
            "codex_task_1",
            45100,
            CodexTaskWorkspaceState.Provisioned,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
        ScriptedCodexSessionManager sessionManager = new();
        using CodexSupervisionLedger ledger = new(options, TimeProvider.System, dataRoot.Path);
        CodexTaskRecipeCatalog recipeCatalog = new(options);
        DefaultHttpContext context = CreateContext(new CodexRemoteTaskProvisionCommand(
            "task:1",
            42,
            42,
            null,
            "Remote task",
            repositoryRoot,
            "main",
            null,
            "worker:local",
            "lease:1"));

        IResult result = await CodexRemoteTaskEndpoints.ProvisionAsync(
            context,
            options,
            workerRegistry,
            workspaceManager,
            sessionManager,
            ledger,
            recipeCatalog,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Single(sessionManager.CreateRequests);
        CodexSupervisionTaskSnapshot task = Assert.Single(await ledger.ListTasksAsync(42, CancellationToken.None));
        Assert.Equal("worker:local", task.WorkerId);
        Assert.Equal("lease:1", task.LeaseId);
        Assert.Equal("workspace:1", task.WorkspaceId);
    }

    private static DefaultHttpContext CreateContext(CodexRemoteTaskProvisionCommand command)
    {
        DefaultHttpContext context = new();
        context.RequestServices = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();
        context.Request.Headers.Authorization = "Bearer coordinator-token-that-is-long-enough-1234";
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(command));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FakeWorkerRegistry : ICodexWorkerRegistry
    {
        public Task<CodexWorkerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(new CodexWorkerSnapshot(
                "worker:local",
                "Local worker",
                CodexWorkerState.Online,
                "ready",
                "1.0.40",
                1,
                4,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                ["codex-execution", "task-workspaces"],
                []));

        public Task<CodexWorkerLease?> TryAcquireLeaseAsync(string taskId, TimeSpan lifetime, CancellationToken cancellationToken)
            => Task.FromResult<CodexWorkerLease?>(new CodexWorkerLease("lease:1", taskId, "worker:local", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)));

        public Task<bool> ReleaseLeaseAsync(string taskId, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> SetDrainingAsync(bool draining, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    private sealed class FakeWorkspaceManager(CodexTaskWorkspaceRecord workspace) : ICodexTaskWorkspaceManager
    {
        public Task<CodexTaskWorkspaceRecord> CreateAsync(string taskId, string repositoryRoot, string? baseRef, CancellationToken cancellationToken)
            => Task.FromResult(workspace);

        public Task<CodexTaskWorkspaceRecord?> GetAsync(string taskId, CancellationToken cancellationToken)
            => Task.FromResult<CodexTaskWorkspaceRecord?>(workspace);

        public Task<IReadOnlyList<CodexTaskWorkspaceRecord>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CodexTaskWorkspaceRecord>>([workspace]);

        public Task<CodexTaskWorkspaceRecord?> ReleaseAsync(string taskId, bool discardChanges, CancellationToken cancellationToken)
            => Task.FromResult<CodexTaskWorkspaceRecord?>(workspace with { State = CodexTaskWorkspaceState.Released });
    }
}
