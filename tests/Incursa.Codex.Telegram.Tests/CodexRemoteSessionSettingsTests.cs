using System.Net;
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

public sealed class CodexRemoteSessionSettingsTests
{
    [Fact]
    public async Task WorkerSettingsEndpointRequiresCoordinatorAuthentication()
    {
        IOptions<CodexTelegramOptions> options = CreateOptions();
        DefaultHttpContext context = CreateContext("Bearer wrong-token", new { TaskId = "task:1" });

        IResult result = await CodexRemoteSessionSettingsEndpoints.HandleAsync(
            context,
            options,
            workerRegistry: null!,
            supervisionLedger: null!,
            sessionManager: null!,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task WorkerSettingsEndpointValidatesOwnershipAndAppliesGoalStatus()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        using CodexSupervisionLedger ledger = new(options, TimeProvider.System, dataRoot.Path);
        await ledger.RegisterTaskAsync(
            "task:1",
            "thread:1",
            "Remote task",
            new TelegramConversationScope(42, null),
            42,
            CancellationToken.None);
        await ledger.BindTaskWorkerAsync("task:1", 42, "worker:remote", "lease:1", "workspace:1", CancellationToken.None);

        CodexRemoteSessionSettingsRequest request = new(
            "task:1",
            42,
            "worker:remote",
            "lease:1",
            "thread:1",
            "set_goal_status",
            null,
            null,
            null,
            null,
            CodexThreadGoalStatus.Paused);
        RecordingSessionManager sessionManager = new();
        DefaultHttpContext context = CreateContext("Bearer coordinator-token-that-is-long-enough-1234", request);
        IResult result = await CodexRemoteSessionSettingsEndpoints.HandleAsync(
            context,
            options,
            new FakeWorkerRegistry(),
            ledger,
            sessionManager,
            CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(CodexThreadGoalStatus.Paused, sessionManager.LastGoalStatus);
        Assert.Contains("goal_status_updated", await ReadResponseAsync(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoordinatorSettingsRelayCarriesOwnerAndGoalOperation()
    {
        CodexRemoteSessionSettingsResponse response = new(
            true,
            "set_goal",
            null,
            new CodexThreadGoalVm(
                "thread:1",
                "Ship the feature",
                CodexThreadGoalStatus.Active,
                10_000,
                0,
                0,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
            false,
            "goal_updated");
        CapturingHandler handler = new(response);
        using HttpClient client = new(handler);
        CodexRemoteSessionSettingsRelay relay = new(
            new StaticHttpClientFactory(client),
            new FakeCoordinatorWorkerStore(),
            CreateOptions());
        CodexSupervisionTaskSnapshot task = new(
            "task:1",
            "thread:1",
            "Remote task",
            new TelegramConversationScope(900, 7),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            WorkerId: "worker:remote",
            LeaseId: "lease:1",
            WorkspaceId: "workspace:1");

        CodexThreadGoalVm goal = await relay.SetGoalAsync(task, 42, "Ship the feature", 10_000, CancellationToken.None);

        Assert.Equal("thread:1", goal.ThreadId);
        CodexRemoteSessionSettingsRequest sent = JsonSerializer.Deserialize<CodexRemoteSessionSettingsRequest>(handler.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(42, sent.OwnerUserId);
        Assert.Equal("set_goal", sent.Action);
        Assert.Equal("Ship the feature", sent.Objective);
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
                "1.0.44",
                1,
                4,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                ["codex-execution"],
                [],
                IsRemote: true));

        public Task<CodexWorkerLease?> TryAcquireLeaseAsync(string taskId, TimeSpan lifetime, CancellationToken cancellationToken) => Task.FromResult<CodexWorkerLease?>(null);
        public Task<bool> ReleaseLeaseAsync(string taskId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> SetDrainingAsync(bool draining, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class RecordingSessionManager : ICodexSessionManager
    {
        public CodexThreadGoalStatus? LastGoalStatus { get; private set; }

        public Task<IReadOnlyCollection<CodexSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken) => NotSupported<IReadOnlyCollection<CodexSessionSummary>>();
        public Task<CodexSessionSummary> CreateSessionAsync(CreateCodexSessionRequest request, CancellationToken cancellationToken) => NotSupported<CodexSessionSummary>();
        public Task<CodexSessionSummary?> GetSessionAsync(string sessionId, CancellationToken cancellationToken) => NotSupported<CodexSessionSummary?>();
        public Task<CodexThreadExecutionVm> SendAsync(string sessionId, string input, CancellationToken cancellationToken) => NotSupported<CodexThreadExecutionVm>();
        public Task<CodexThreadExecutionVm> SendAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken) => NotSupported<CodexThreadExecutionVm>();
        public Task<CodexThreadExecutionVm> SendPlanAsync(string sessionId, string input, CancellationToken cancellationToken) => NotSupported<CodexThreadExecutionVm>();
        public Task SteerAsync(string sessionId, string input, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SteerAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CodexSessionModelSettings> GetModelSettingsAsync(string sessionId, CancellationToken cancellationToken) => NotSupported<CodexSessionModelSettings>();
        public Task<CodexSessionModelSettings> UpdateModelSettingsAsync(string sessionId, string? model, string? reasoningEffort, CancellationToken cancellationToken) => NotSupported<CodexSessionModelSettings>();
        public Task<CodexThreadGoalVm?> GetGoalAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult<CodexThreadGoalVm?>(null);
        public Task<CodexThreadGoalVm> SetGoalAsync(string sessionId, string objective, long? tokenBudget, CancellationToken cancellationToken) => Task.FromResult(CreateGoal(sessionId, objective, CodexThreadGoalStatus.Active));
        public Task<CodexThreadGoalVm> SetGoalStatusAsync(string sessionId, CodexThreadGoalStatus status, CancellationToken cancellationToken)
        {
            LastGoalStatus = status;
            return Task.FromResult(CreateGoal(sessionId, "Ship the feature", status));
        }
        public Task<bool> ClearGoalAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<string> TailAsync(string sessionId, int lineCount, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
        public Task StopAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task KillAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RenameAsync(string sessionId, string name, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ForgetAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        private static CodexThreadGoalVm CreateGoal(string threadId, string objective, CodexThreadGoalStatus status)
            => new(threadId, objective, status, null, 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        private static Task<T> NotSupported<T>() => Task.FromException<T>(new NotSupportedException());
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
                    "1.0.44",
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

    private sealed class CapturingHandler(CodexRemoteSessionSettingsResponse response) : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(response),
            };
        }
    }
}
