using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Incursa.Codex.Telegram.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexRemoteTaskDetailTests
{
    [Fact]
    public async Task WorkerDetailEndpointRequiresCoordinatorAuthentication()
    {
        IOptions<CodexTelegramOptions> options = CreateOptions();
        DefaultHttpContext context = CreateContext("Bearer wrong-token", new { TaskId = "task:1" });

        IResult result = await CodexRemoteTaskDetailEndpoints.HandleAsync(
            context,
            options,
            workerRegistry: null!,
            supervisionLedger: null!,
            gateway: null!,
            turnCoordinator: null!,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task WorkerDetailEndpointReturnsRedactedProjectionForOwnedTask()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        FakeWorkerRegistry workerRegistry = new();
        FakeGateway gateway = new(CreateDetail());
        using CodexSupervisionLedger ledger = new(options, TimeProvider.System, dataRoot.Path);
        await ledger.RegisterTaskAsync(
            "task:1",
            "thread:1",
            "Remote task",
            new TelegramConversationScope(42, null),
            42,
            CancellationToken.None);
        await ledger.BindTaskWorkerAsync("task:1", 42, "worker:remote", "lease:1", "workspace:1", CancellationToken.None);

        CodexRemoteTaskDetailRequest request = new("task:1", 42, "worker:remote", "lease:1", "thread:1");
        DefaultHttpContext context = CreateContext("Bearer coordinator-token-that-is-long-enough-1234", request);
        IResult result = await CodexRemoteTaskDetailEndpoints.HandleAsync(
            context,
            options,
            workerRegistry,
            ledger,
            gateway,
            new FakeTurnCoordinator(),
            CancellationToken.None);
        await result.ExecuteAsync(context);

        string body = await ReadResponseAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("thread:1", body, StringComparison.Ordinal);
        Assert.Contains("private-repo", body, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\Samuel\\private-repo", body, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\private\\secret.txt", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoordinatorRelayUsesAssignedWorkerAndBoundedDetailContract()
    {
        TelegramMiniAppThreadDetailVm projection = TelegramMiniAppProjection.ToThreadDetailViewModel(
            CreateDetail(),
            null,
            DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        CapturingHandler handler = new(projection);
        using HttpClient client = new(handler);
        CodexRemoteTaskDetailRelay relay = new(
            new StaticHttpClientFactory(client),
            new FakeCoordinatorWorkerStore(),
            CreateOptions());
        CodexSupervisionTaskRecord task = new(
            "task:1",
            "thread:1",
            "Remote task",
            new TelegramConversationScope(42, null),
            42,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            WorkerId: "worker:remote",
            LeaseId: "lease:1",
            WorkspaceId: "workspace:1");

        TelegramMiniAppThreadDetailVm result = await relay.GetAsync(task, CancellationToken.None);

        Assert.Equal("thread:1", result.Thread.Id);
        Assert.NotNull(handler.Request);
        Assert.Equal("Bearer coordinator-token-that-is-long-enough-1234", handler.Request!.Headers.Authorization!.ToString());
        Assert.EndsWith("/api/worker/v1/tasks/detail", handler.Request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        CodexRemoteTaskDetailRequest body = JsonSerializer.Deserialize<CodexRemoteTaskDetailRequest>(handler.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("task:1", body.TaskId);
        Assert.Equal("lease:1", body.LeaseId);
    }

    private static CodexThreadDetailVm CreateDetail()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-09-15T12:00:00Z");
        CodexThreadListItemVm summary = new(
            "thread:1",
            "Remote task",
            "Remote preview",
            "completed",
            "OpenAI",
            now.AddMinutes(-5),
            now,
            false,
            null,
            null,
            null,
            null,
            null,
            false,
            @"C:\Users\Samuel\private-repo");
        CodexTurnVm turn = new("turn:1", "Completed", null, "Done", null, [])
        {
            Changes = [new CodexFileChangePreviewVm(@"C:\private\secret.txt", "Update", "diff")],
        };
        return new CodexThreadDetailVm(
            summary,
            [turn],
            [],
            [],
            [],
            CreateRuntime(),
            null,
            "gpt-test",
            @"C:\Users\Samuel\private-repo",
            null,
            null,
            []);
    }

    private static CodexRuntimeStateVm CreateRuntime()
        => new(
            true,
            "ready",
            "Codex",
            "1.0",
            "linux",
            "linux",
            null,
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
            true);

    private static IOptions<CodexTelegramOptions> CreateOptions(string? dataRoot = null)
        => Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot ?? Path.GetTempPath() },
            Coordinator = new CodexCoordinatorOptions
            {
                Enabled = true,
                WorkerRegistrationEnabled = true,
                AuthenticationToken = "coordinator-token-that-is-long-enough-1234",
                RequestTimeoutSeconds = 10,
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
                ["codex-execution"],
                [],
                IsRemote: true));

        public Task<CodexWorkerLease?> TryAcquireLeaseAsync(string taskId, TimeSpan lifetime, CancellationToken cancellationToken)
            => Task.FromResult<CodexWorkerLease?>(null);

        public Task<bool> ReleaseLeaseAsync(string taskId, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> SetDrainingAsync(bool draining, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }

    private sealed class FakeTurnCoordinator : ICodexTurnExecutionCoordinator
    {
        public bool HasActiveTurn => false;

        public IReadOnlyCollection<string> GetActiveThreadIds() => [];

        public bool HasActiveTurnForThread(string threadId) => false;

        public string? GetActiveTurnId(string threadId) => null;

        public CodexActiveTurnStateVm? TryGetActiveTurnState(string threadId) => null;

        public void RegisterActiveTurn(string threadId, string turnId, ICodexTurnHandle? turn = null, CodexTimelineEntryVm? lastEvent = null) { }

        public void UpdateActiveTurnState(string threadId, string turnId, CodexTimelineEntryVm? lastEvent = null) { }

        public bool TryClearActiveTurn(string threadId, string turnId) => false;

        public Task SteerAsync(string threadId, string turnId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task InterruptAsync(string threadId, string turnId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeGateway(CodexThreadDetailVm detail) : ICodexGateway
    {
        public Task<CodexRuntimeStateVm> GetRuntimeAsync(CancellationToken cancellationToken = default) => Task.FromResult(detail.Runtime);
        public Task<IReadOnlyList<CodexThreadListItemVm>> ListThreadsAsync(CodexThreadListQuery query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CodexThreadListItemVm>>([detail.Summary]);
        public Task<CodexThreadDetailVm> GetThreadAsync(string threadId, string? workspaceQuery = null, CancellationToken cancellationToken = default) => Task.FromResult(detail);
        public Task<CodexThreadListItemVm> CreateThreadShellAsync(CodexThreadContextSubmission submission, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodexThreadExecutionVm> CreateThreadAsync(CodexTurnSubmission submission, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodexThreadExecutionVm> SendTurnAsync(string threadId, CodexTurnSubmission submission, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodexThreadExecutionVm> SendTurnAsync(string threadId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodexThreadExecutionVm> ForkThreadAsync(string threadId, CodexTurnSubmission submission, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodexThreadDetailVm> RenameThreadAsync(string threadId, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodexThreadDetailVm> SetArchiveStateAsync(string threadId, bool archived, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodexThreadDetailVm> CompactThreadAsync(string threadId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodexThreadGoalVm?> GetThreadGoalAsync(string threadId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodexThreadGoalVm> SetThreadGoalAsync(string threadId, string objective, long? tokenBudget = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodexThreadGoalVm> SetThreadGoalStatusAsync(string threadId, CodexThreadGoalStatus status, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ClearThreadGoalAsync(string threadId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodexWorkspaceEntryVm>> SearchWorkspaceAsync(string? query = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CodexModelVm>> ListModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeCoordinatorWorkerStore : ICodexCoordinatorWorkerStore
    {
        public Task<bool> UpsertAsync(CodexWorkerSnapshot worker, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<IReadOnlyList<CodexWorkerSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CodexWorkerSnapshot>>([
                new CodexWorkerSnapshot(
                    "worker:remote",
                    "Remote worker",
                    CodexWorkerState.Online,
                    "ready",
                    "1.0.43",
                    0,
                    4,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    ["codex-execution"],
                    [],
                    IsRemote: true,
                    ControlPlaneUrl: "https://worker.example"),
            ]);
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler(TelegramMiniAppThreadDetailVm projection) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(projection),
            };
        }
    }
}
