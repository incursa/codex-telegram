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

public sealed class CodexRemoteTaskWorkspaceTests
{
    [Fact]
    public async Task WorkerWorkspaceEndpointRequiresCoordinatorAuthentication()
    {
        IOptions<CodexTelegramOptions> options = CreateOptions();
        DefaultHttpContext context = CreateContext("Bearer wrong-token", new { TaskId = "task:1" });

        IResult result = await CodexRemoteTaskWorkspaceEndpoints.HandleAsync(
            context,
            options,
            workerRegistry: null!,
            supervisionLedger: null!,
            workspaceManager: null!,
            sessionManager: null!,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task WorkerWorkspaceEndpointReturnsBoundedStatusAndReleasesRemoteLease()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        FakeWorkerRegistry workerRegistry = new();
        FakeWorkspaceManager workspaceManager = new(new CodexTaskWorkspaceRecord(
            "workspace:1",
            "task:1",
            "C:\\private\\repository",
            "C:\\private\\worktree",
            "codex/task/task-1",
            "codex_task_1",
            45100,
            CodexTaskWorkspaceState.Provisioned,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
        ScriptedCodexSessionManager sessionManager = new();
        using CodexSupervisionLedger ledger = new(options, TimeProvider.System, dataRoot.Path);
        await ledger.RegisterTaskAsync(
            "task:1",
            "thread:1",
            "Remote task",
            new TelegramConversationScope(42, null),
            42,
            CancellationToken.None);
        await ledger.BindTaskWorkerAsync("task:1", 42, "worker:remote", "lease:1", "workspace:1", CancellationToken.None);

        CodexRemoteTaskWorkspaceRequest statusRequest = new(
            "task:1",
            42,
            "worker:remote",
            "lease:1",
            "thread:1",
            "status",
            false);
        DefaultHttpContext statusContext = CreateContext("Bearer coordinator-token-that-is-long-enough-1234", statusRequest);
        IResult statusResult = await CodexRemoteTaskWorkspaceEndpoints.HandleAsync(
            statusContext,
            options,
            workerRegistry,
            ledger,
            workspaceManager,
            sessionManager,
            CancellationToken.None);
        await statusResult.ExecuteAsync(statusContext);

        string statusBody = await ReadResponseAsync(statusContext);
        Assert.Equal(StatusCodes.Status200OK, statusContext.Response.StatusCode);
        Assert.Contains("codex/task/task-1", statusBody, StringComparison.Ordinal);
        Assert.DoesNotContain("private", statusBody, StringComparison.OrdinalIgnoreCase);

        CodexRemoteTaskWorkspaceRequest releaseRequest = statusRequest with { Action = "release" };
        DefaultHttpContext releaseContext = CreateContext("Bearer coordinator-token-that-is-long-enough-1234", releaseRequest);
        IResult releaseResult = await CodexRemoteTaskWorkspaceEndpoints.HandleAsync(
            releaseContext,
            options,
            workerRegistry,
            ledger,
            workspaceManager,
            sessionManager,
            CancellationToken.None);
        await releaseResult.ExecuteAsync(releaseContext);

        Assert.Equal(StatusCodes.Status200OK, releaseContext.Response.StatusCode);
        Assert.True(workerRegistry.LeaseReleased);
        Assert.Equal(CodexTaskWorkspaceState.Released, Assert.Single(workspaceManager.ReleaseRequests).State);
    }

    private static IOptions<CodexTelegramOptions> CreateOptions(string? dataRoot = null)
        => Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot ?? Path.GetTempPath() },
            Coordinator = new CodexCoordinatorOptions
            {
                Enabled = true,
                WorkerRegistrationEnabled = true,
                AuthenticationToken = "coordinator-token-that-is-long-enough-1234",
            },
        });

    private static DefaultHttpContext CreateContext(string authorization, object payload)
    {
        DefaultHttpContext context = new();
        context.RequestServices = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();
        context.Request.Headers.Authorization = authorization;
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using StreamReader reader = new(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class FakeWorkerRegistry : ICodexWorkerRegistry
    {
        public bool LeaseReleased { get; private set; }

        public Task<CodexWorkerSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(new CodexWorkerSnapshot(
                "worker:remote",
                "Remote worker",
                CodexWorkerState.Online,
                "ready",
                "1.0.43",
                1,
                4,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                ["codex-execution", "task-workspaces"],
                [],
                IsRemote: true,
                ControlPlaneUrl: "https://worker.example"));

        public Task<CodexWorkerLease?> TryAcquireLeaseAsync(string taskId, TimeSpan lifetime, CancellationToken cancellationToken)
            => Task.FromResult<CodexWorkerLease?>(null);

        public Task<bool> ReleaseLeaseAsync(string taskId, CancellationToken cancellationToken)
        {
            LeaseReleased = true;
            return Task.FromResult(true);
        }

        public Task<bool> SetDrainingAsync(bool draining, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    private sealed class FakeWorkspaceManager(CodexTaskWorkspaceRecord workspace) : ICodexTaskWorkspaceManager
    {
        public List<CodexTaskWorkspaceRecord> ReleaseRequests { get; } = [];

        public Task<CodexTaskWorkspaceRecord> CreateAsync(string taskId, string repositoryRoot, string? baseRef, CancellationToken cancellationToken)
            => Task.FromResult(workspace);

        public Task<CodexTaskWorkspaceRecord?> GetAsync(string taskId, CancellationToken cancellationToken)
            => Task.FromResult<CodexTaskWorkspaceRecord?>(workspace);

        public Task<IReadOnlyList<CodexTaskWorkspaceRecord>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CodexTaskWorkspaceRecord>>([workspace]);

        public Task<CodexTaskWorkspaceRecord?> ReleaseAsync(string taskId, bool discardChanges, CancellationToken cancellationToken)
        {
            CodexTaskWorkspaceRecord released = workspace with { State = CodexTaskWorkspaceState.Released };
            ReleaseRequests.Add(released);
            return Task.FromResult<CodexTaskWorkspaceRecord?>(released);
        }
    }
}
