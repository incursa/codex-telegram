using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal sealed record CodexRemoteTaskProvisionRequest(
    string TaskId,
    long OwnerUserId,
    long ChatId,
    int? MessageThreadId,
    string SessionName,
    string RepositoryRoot,
    string? BaseRef,
    CodexTaskRecipeSnapshot? Recipe,
    string RequestedWorkerId);

internal sealed record CodexRemoteTaskProvisionCommand(
    string TaskId,
    long OwnerUserId,
    long ChatId,
    int? MessageThreadId,
    string SessionName,
    string RepositoryRoot,
    string? BaseRef,
    CodexTaskRecipeSnapshot? Recipe,
    string WorkerId,
    string LeaseId);

internal sealed record CodexRemoteTaskProvisionResponse(
    bool Provisioned,
    string TaskId,
    string WorkerId,
    string LeaseId,
    string? WorkspaceId,
    string? CodexThreadId,
    string? SessionName,
    string? Branch,
    int? DevelopmentPort,
    string? DatabaseNamespace,
    string OutcomeCode);

internal sealed record CodexCoordinatorTaskSnapshot(
    string TaskId,
    long OwnerUserId,
    string WorkerId,
    string LeaseId,
    string WorkspaceId,
    string CodexThreadId,
    string SessionName,
    string Branch,
    int DevelopmentPort,
    string DatabaseNamespace,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string OutcomeCode);

internal interface ICodexCoordinatorTaskStore
{
    Task<CodexCoordinatorTaskSnapshot?> GetAsync(
        long ownerUserId,
        string taskId,
        CancellationToken cancellationToken);

    Task UpsertAsync(CodexCoordinatorTaskSnapshot task, CancellationToken cancellationToken);
}

/// <summary>
/// Persists the coordinator's bounded projection of remote task ownership.
/// Paths, prompt bodies, credentials, and Codex transcripts never cross this boundary.
/// </summary>
internal sealed class CodexCoordinatorTaskStore : ICodexCoordinatorTaskStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumRecords = 500;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly string _dataRoot;

    public CodexCoordinatorTaskStore(IOptions<CodexTelegramOptions> options)
    {
        _options = options;
        _dataRoot = GetDataRoot();
    }

    internal CodexCoordinatorTaskStore(IOptions<CodexTelegramOptions> options, string dataRootOverride)
    {
        _options = options;
        _dataRoot = Path.GetFullPath(dataRootOverride);
    }

    public async Task<CodexCoordinatorTaskSnapshot?> GetAsync(
        long ownerUserId,
        string taskId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CoordinatorTaskState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return state.Tasks
                .Where(task => task.OwnerUserId == ownerUserId)
                .FirstOrDefault(task => string.Equals(task.TaskId, taskId.Trim(), StringComparison.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(CodexCoordinatorTaskSnapshot task, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CoordinatorTaskState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            CodexCoordinatorTaskSnapshot? existing = state.Tasks.FirstOrDefault(candidate =>
                candidate.OwnerUserId == task.OwnerUserId
                && string.Equals(candidate.TaskId, task.TaskId, StringComparison.Ordinal));
            if (existing is null)
            {
                state.Tasks.Add(task);
            }
            else
            {
                state.Tasks[state.Tasks.IndexOf(existing)] = task;
            }

            state.Tasks = state.Tasks
                .OrderByDescending(candidate => candidate.UpdatedAtUtc)
                .Take(MaximumRecords)
                .ToList();
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<CoordinatorTaskState> LoadAsync(CancellationToken cancellationToken)
    {
        string path = GetStatePath();
        if (!File.Exists(path))
        {
            return new CoordinatorTaskState { SchemaVersion = CurrentSchemaVersion };
        }

        await using FileStream stream = File.OpenRead(path);
        CoordinatorTaskState? state = await JsonSerializer.DeserializeAsync<CoordinatorTaskState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            throw new InvalidDataException("The coordinator task state file was empty.");
        }

        state.Tasks ??= [];
        return state;
    }

    private async Task SaveAsync(CoordinatorTaskState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataRoot);
        string path = GetStatePath();
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private string GetDataRoot()
    {
        string? configuredRoot = _options.Value.Workspace.DataRoot;
        return string.IsNullOrWhiteSpace(configuredRoot)
            ? CodexTelegramDataRoot.GetDefaultDataRoot(_options.Value.InstanceId)
            : Path.GetFullPath(configuredRoot);
    }

    private string GetStatePath() => Path.Combine(_dataRoot, "codex-coordinator-tasks.json");

    private sealed class CoordinatorTaskState
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<CodexCoordinatorTaskSnapshot> Tasks { get; set; } = [];
    }
}

/// <summary>
/// Routes an explicitly requested task to one eligible worker and records the result.
/// The coordinator owns Telegram identity and routing; the worker owns workspace and Codex execution.
/// </summary>
internal sealed class CodexRemoteTaskProvisioningService
{
    private const string ProvisionPath = "api/worker/v1/tasks/provision";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICodexCoordinatorWorkerStore _workerStore;
    private readonly CodexCoordinatorLeaseHandoffService _leaseHandoff;
    private readonly ICodexCoordinatorTaskStore _taskStore;
    private readonly ICodexSupervisionLedger _supervisionLedger;
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly TimeProvider _timeProvider;

    public CodexRemoteTaskProvisioningService(
        IHttpClientFactory httpClientFactory,
        ICodexCoordinatorWorkerStore workerStore,
        CodexCoordinatorLeaseHandoffService leaseHandoff,
        ICodexCoordinatorTaskStore taskStore,
        ICodexSupervisionLedger supervisionLedger,
        IOptions<CodexTelegramOptions> options,
        TimeProvider timeProvider)
    {
        _httpClientFactory = httpClientFactory;
        _workerStore = workerStore;
        _leaseHandoff = leaseHandoff;
        _taskStore = taskStore;
        _supervisionLedger = supervisionLedger;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<CodexRemoteTaskProvisionResponse> ProvisionAsync(
        CodexRemoteTaskProvisionRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        if (!_options.Value.Coordinator.Enabled)
        {
            return Rejected(request, "coordinator_disabled");
        }

        CodexCoordinatorTaskSnapshot? existing = await _taskStore.GetAsync(request.OwnerUserId, request.TaskId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.WorkerId, request.RequestedWorkerId.Trim(), StringComparison.Ordinal))
            {
                return Rejected(request, "task_owned_by_other_worker");
            }

            return new CodexRemoteTaskProvisionResponse(
                true,
                existing.TaskId,
                existing.WorkerId,
                existing.LeaseId,
                existing.WorkspaceId,
                existing.CodexThreadId,
                existing.SessionName,
                existing.Branch,
                existing.DevelopmentPort,
                existing.DatabaseNamespace,
                existing.OutcomeCode);
        }

        CodexCoordinatorLeaseSnapshot lease = await _leaseHandoff.HandoffAsync(
            new CodexCoordinatorLeaseRequest(
                request.TaskId,
                null,
                request.Recipe?.RequiredCapabilities is { Count: > 0 } capabilities
                    ? capabilities
                    : ["codex-execution", "task-workspaces"],
                request.RequestedWorkerId),
            cancellationToken).ConfigureAwait(false);
        if (lease.State != CodexCoordinatorLeaseState.Accepted || string.IsNullOrWhiteSpace(lease.LeaseId))
        {
            return Rejected(request, lease.OutcomeCode);
        }

        CodexWorkerSnapshot? worker = (await _workerStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => string.Equals(candidate.WorkerId, lease.WorkerId, StringComparison.Ordinal));
        if (worker is null || string.IsNullOrWhiteSpace(worker.ControlPlaneUrl))
        {
            return Rejected(request, "worker_endpoint_missing");
        }

        CodexRemoteTaskProvisionCommand command = new(
            request.TaskId.Trim(),
            request.OwnerUserId,
            request.ChatId,
            request.MessageThreadId,
            request.SessionName.Trim(),
            request.RepositoryRoot.Trim(),
            string.IsNullOrWhiteSpace(request.BaseRef) ? null : request.BaseRef.Trim(),
            request.Recipe,
            lease.WorkerId,
            lease.LeaseId);
        try
        {
            HttpClient client = _httpClientFactory.CreateClient(nameof(CodexRemoteTaskProvisioningService));
            using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(_options.Value.Coordinator.RequestTimeoutSeconds));
            using HttpRequestMessage httpRequest = new(
                HttpMethod.Post,
                new Uri(new Uri(worker.ControlPlaneUrl.TrimEnd('/') + "/", UriKind.Absolute), ProvisionPath))
            {
                Content = JsonContent.Create(command, options: JsonOptions),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Coordinator.AuthenticationToken);
            using HttpResponseMessage response = await client.SendAsync(httpRequest, requestTimeout.Token).ConfigureAwait(false);
            CodexRemoteTaskProvisionResponse? provisioned = response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<CodexRemoteTaskProvisionResponse>(JsonOptions, requestTimeout.Token).ConfigureAwait(false)
                : null;
            if (provisioned is null
                || !provisioned.Provisioned
                || !string.Equals(provisioned.TaskId, command.TaskId, StringComparison.Ordinal)
                || !string.Equals(provisioned.WorkerId, command.WorkerId, StringComparison.Ordinal)
                || !string.Equals(provisioned.LeaseId, command.LeaseId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(provisioned.CodexThreadId)
                || string.IsNullOrWhiteSpace(provisioned.WorkspaceId))
            {
                return Rejected(request, provisioned?.OutcomeCode ?? $"worker_http_{(int)response.StatusCode}");
            }

            TelegramConversationScope conversation = new(command.ChatId, command.MessageThreadId);
            CodexSupervisionTaskRecord? registered = await _supervisionLedger.RegisterTaskAsync(
                command.TaskId,
                provisioned.CodexThreadId,
                provisioned.SessionName ?? command.SessionName,
                conversation,
                command.OwnerUserId,
                cancellationToken,
                command.Recipe).ConfigureAwait(false);
            if (registered is null)
            {
                return Rejected(request, "task_projection_conflict");
            }

            CodexSupervisionTaskRecord? bound = await _supervisionLedger.BindTaskWorkerAsync(
                command.TaskId,
                command.OwnerUserId,
                command.WorkerId,
                command.LeaseId,
                provisioned.WorkspaceId,
                cancellationToken).ConfigureAwait(false);
            if (bound is null)
            {
                return Rejected(request, "task_worker_binding_failed");
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            await _taskStore.UpsertAsync(
                new CodexCoordinatorTaskSnapshot(
                    command.TaskId,
                    command.OwnerUserId,
                    command.WorkerId,
                    command.LeaseId,
                    provisioned.WorkspaceId,
                    provisioned.CodexThreadId,
                    provisioned.SessionName ?? command.SessionName,
                    provisioned.Branch ?? "",
                    provisioned.DevelopmentPort ?? 0,
                    provisioned.DatabaseNamespace ?? "",
                    now,
                    now,
                    provisioned.OutcomeCode),
                cancellationToken).ConfigureAwait(false);
            return provisioned;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException or JsonException)
        {
            return Rejected(request, "worker_unreachable");
        }
    }

    private static void ValidateRequest(CodexRemoteTaskProvisionRequest request)
    {
        if (!IsSafeToken(request.TaskId, 160)
            || request.OwnerUserId == 0
            || request.ChatId == 0
            || !IsBoundedText(request.SessionName, 200)
            || string.IsNullOrWhiteSpace(request.RepositoryRoot)
            || request.RepositoryRoot.Trim().Length > 2_000
            || request.RepositoryRoot.Any(char.IsControl)
            || !string.IsNullOrWhiteSpace(request.BaseRef) && (request.BaseRef.Trim().Length > 200 || request.BaseRef.Any(char.IsControl))
            || !IsSafeToken(request.RequestedWorkerId, 120))
        {
            throw new ArgumentException("The remote task request contains invalid or unbounded metadata.", nameof(request));
        }
    }

    private static bool IsBoundedText(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength && !value.Any(char.IsControl);

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');

    private static CodexRemoteTaskProvisionResponse Rejected(
        CodexRemoteTaskProvisionRequest request,
        string outcomeCode)
        => new(false, request.TaskId.Trim(), request.RequestedWorkerId.Trim(), "", null, null, null, null, null, null, outcomeCode);
}
