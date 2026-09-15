using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal enum CodexCoordinatorLeaseState
{
    Issued,
    Accepted,
    Rejected,
}

internal sealed record CodexCoordinatorLeaseRequest(
    string TaskId,
    string? WorkspaceId,
    IReadOnlyList<string> RequiredCapabilities,
    string? RequestedWorkerId = null);

internal sealed record CodexWorkerLeaseGrant(
    string GrantId,
    string TaskId,
    string? WorkspaceId,
    string WorkerId,
    IReadOnlyList<string> RequiredCapabilities,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

internal sealed record CodexWorkerLeaseAcceptance(
    bool Accepted,
    string WorkerId,
    string? LeaseId,
    string OutcomeCode);

internal sealed record CodexCoordinatorLeaseSnapshot(
    string GrantId,
    string TaskId,
    string? WorkspaceId,
    string WorkerId,
    CodexCoordinatorLeaseState State,
    string? LeaseId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string OutcomeCode);

internal interface ICodexCoordinatorLeaseStore
{
    Task<CodexCoordinatorLeaseSnapshot?> GetActiveForTaskAsync(string taskId, CancellationToken cancellationToken);

    Task<CodexCoordinatorLeaseSnapshot> CreateIssuedAsync(
        CodexWorkerLeaseGrant grant,
        CancellationToken cancellationToken);

    Task<CodexCoordinatorLeaseSnapshot> CompleteAsync(
        string grantId,
        CodexCoordinatorLeaseState state,
        string? leaseId,
        string outcomeCode,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>
/// Persists bounded coordinator-issued lease grants. It stores identity and outcome metadata only.
/// </summary>
internal sealed class CodexCoordinatorLeaseStore : ICodexCoordinatorLeaseStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumRecords = 500;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly string _dataRoot;

    public CodexCoordinatorLeaseStore(IOptions<CodexTelegramOptions> options)
    {
        _options = options;
        _dataRoot = GetDataRoot();
    }

    internal CodexCoordinatorLeaseStore(IOptions<CodexTelegramOptions> options, string dataRootOverride)
    {
        _options = options;
        _dataRoot = Path.GetFullPath(dataRootOverride);
    }

    public async Task<CodexCoordinatorLeaseSnapshot?> GetActiveForTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LeaseState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return state.Records
                .Where(record => string.Equals(record.TaskId, taskId.Trim(), StringComparison.Ordinal))
                .OrderByDescending(record => record.UpdatedAtUtc)
                .Select(record => record.ToSnapshot())
                .FirstOrDefault(record => record.State is CodexCoordinatorLeaseState.Issued or CodexCoordinatorLeaseState.Accepted
                    && record.ExpiresAtUtc > now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexCoordinatorLeaseSnapshot> CreateIssuedAsync(
        CodexWorkerLeaseGrant grant,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LeaseState state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            LeaseRecord? existing = state.Records
                .Where(record => string.Equals(record.TaskId, grant.TaskId, StringComparison.Ordinal))
                .OrderByDescending(record => record.UpdatedAtUtc)
                .FirstOrDefault(record => record.State is CodexCoordinatorLeaseState.Issued or CodexCoordinatorLeaseState.Accepted
                    && record.ExpiresAtUtc > now);
            if (existing is not null)
            {
                return existing.ToSnapshot();
            }

            LeaseRecord record = new(
                grant.GrantId,
                grant.TaskId,
                grant.WorkspaceId,
                grant.WorkerId,
                CodexCoordinatorLeaseState.Issued,
                null,
                grant.IssuedAtUtc,
                grant.ExpiresAtUtc,
                grant.IssuedAtUtc,
                "lease_issued");
            state.Records.Add(record);
            state.Records = state.Records
                .OrderByDescending(candidate => candidate.UpdatedAtUtc)
                .Take(MaximumRecords)
                .ToList();
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return record.ToSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexCoordinatorLeaseSnapshot> CompleteAsync(
        string grantId,
        CodexCoordinatorLeaseState state,
        string? leaseId,
        string outcomeCode,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LeaseState persisted = await LoadAsync(cancellationToken).ConfigureAwait(false);
            LeaseRecord record = persisted.Records.FirstOrDefault(candidate => string.Equals(candidate.GrantId, grantId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The coordinator lease grant was not found.");
            record.State = state;
            record.LeaseId = leaseId;
            record.UpdatedAtUtc = updatedAtUtc;
            record.OutcomeCode = outcomeCode;
            await SaveAsync(persisted, cancellationToken).ConfigureAwait(false);
            return record.ToSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<LeaseState> LoadAsync(CancellationToken cancellationToken)
    {
        string path = GetStatePath();
        if (!File.Exists(path))
        {
            return new LeaseState { SchemaVersion = CurrentSchemaVersion };
        }

        await using FileStream stream = File.OpenRead(path);
        LeaseState? state = await JsonSerializer.DeserializeAsync<LeaseState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            throw new InvalidDataException("The coordinator lease state file was empty.");
        }

        state.Records ??= [];
        return state;
    }

    private async Task SaveAsync(LeaseState state, CancellationToken cancellationToken)
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

    private string GetStatePath() => Path.Combine(_dataRoot, "codex-coordinator-leases.json");

    private sealed class LeaseState
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<LeaseRecord> Records { get; set; } = [];
    }

    private sealed class LeaseRecord
    {
        public LeaseRecord() { }

        public LeaseRecord(
            string grantId,
            string taskId,
            string? workspaceId,
            string workerId,
            CodexCoordinatorLeaseState state,
            string? leaseId,
            DateTimeOffset issuedAtUtc,
            DateTimeOffset expiresAtUtc,
            DateTimeOffset updatedAtUtc,
            string outcomeCode)
        {
            GrantId = grantId;
            TaskId = taskId;
            WorkspaceId = workspaceId;
            WorkerId = workerId;
            State = state;
            LeaseId = leaseId;
            IssuedAtUtc = issuedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
            UpdatedAtUtc = updatedAtUtc;
            OutcomeCode = outcomeCode;
        }

        public string GrantId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string? WorkspaceId { get; set; }
        public string WorkerId { get; set; } = string.Empty;
        public CodexCoordinatorLeaseState State { get; set; }
        public string? LeaseId { get; set; }
        public DateTimeOffset IssuedAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public string OutcomeCode { get; set; } = string.Empty;

        public CodexCoordinatorLeaseSnapshot ToSnapshot()
            => new(GrantId, TaskId, WorkspaceId, WorkerId, State, LeaseId, IssuedAtUtc, ExpiresAtUtc, UpdatedAtUtc, OutcomeCode);
    }
}

/// <summary>
/// Selects an eligible remote worker and completes a coordinator-issued lease handoff.
/// </summary>
internal sealed class CodexCoordinatorLeaseHandoffService
{
    private const string AcceptPath = "api/coordinator/v1/worker-leases/accept";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICodexCoordinatorWorkerStore _workerStore;
    private readonly ICodexCoordinatorLeaseStore _leaseStore;
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly TimeProvider _timeProvider;

    public CodexCoordinatorLeaseHandoffService(
        IHttpClientFactory httpClientFactory,
        ICodexCoordinatorWorkerStore workerStore,
        ICodexCoordinatorLeaseStore leaseStore,
        IOptions<CodexTelegramOptions> options,
        TimeProvider timeProvider)
    {
        _httpClientFactory = httpClientFactory;
        _workerStore = workerStore;
        _leaseStore = leaseStore;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<CodexCoordinatorLeaseSnapshot> HandoffAsync(
        CodexCoordinatorLeaseRequest request,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        CodexCoordinatorOptions coordinator = _options.Value.Coordinator;
        if (!coordinator.Enabled)
        {
            return Rejected(request, now, "coordinator_disabled");
        }

        ValidateRequest(request);
        CodexCoordinatorLeaseSnapshot? existing = await _leaseStore.GetActiveForTaskAsync(request.TaskId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        IReadOnlyList<CodexWorkerSnapshot> workers = await _workerStore.ListAsync(cancellationToken).ConfigureAwait(false);
        CodexWorkerSnapshot? worker = workers
            .Where(candidate => candidate.IsRemote
                && candidate.State == CodexWorkerState.Online
                && string.Equals(candidate.Readiness, "ready", StringComparison.OrdinalIgnoreCase)
                && candidate.ActiveLeaseCount < candidate.MaximumConcurrentTasks
                && !string.IsNullOrWhiteSpace(candidate.ControlPlaneUrl)
                && request.RequiredCapabilities.All(required => candidate.Capabilities.Contains(required, StringComparer.Ordinal)))
            .Where(candidate => string.IsNullOrWhiteSpace(request.RequestedWorkerId)
                || string.Equals(candidate.WorkerId, request.RequestedWorkerId.Trim(), StringComparison.Ordinal))
            .OrderBy(candidate => candidate.ActiveLeaseCount)
            .ThenBy(candidate => candidate.LastHeartbeatUtc)
            .FirstOrDefault();
        if (worker is null)
        {
            return Rejected(request, now, "no_eligible_worker");
        }

        CodexWorkerLeaseGrant grant = new(
            $"grant:{Guid.NewGuid():N}",
            request.TaskId.Trim(),
            request.WorkspaceId,
            worker.WorkerId,
            request.RequiredCapabilities,
            now,
            now.AddMinutes(5));
        CodexCoordinatorLeaseSnapshot issued = await _leaseStore.CreateIssuedAsync(grant, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(issued.GrantId, grant.GrantId, StringComparison.Ordinal))
        {
            return issued;
        }

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(nameof(CodexCoordinatorLeaseHandoffService));
            using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(coordinator.RequestTimeoutSeconds));
            using HttpRequestMessage httpRequest = new(
                HttpMethod.Post,
                new Uri(new Uri(worker.ControlPlaneUrl!.TrimEnd('/') + "/", UriKind.Absolute), AcceptPath))
            {
                Content = JsonContent.Create(grant, options: JsonOptions),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", coordinator.AuthenticationToken);
            using HttpResponseMessage response = await client.SendAsync(httpRequest, requestTimeout.Token).ConfigureAwait(false);
            CodexWorkerLeaseAcceptance? acceptance = response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<CodexWorkerLeaseAcceptance>(JsonOptions, requestTimeout.Token).ConfigureAwait(false)
                : null;
            if (acceptance is { Accepted: true } && string.Equals(acceptance.WorkerId, worker.WorkerId, StringComparison.Ordinal))
            {
                return await _leaseStore.CompleteAsync(
                    grant.GrantId,
                    CodexCoordinatorLeaseState.Accepted,
                    acceptance.LeaseId,
                    "lease_accepted",
                    _timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
            }

            string outcome = acceptance?.OutcomeCode is { Length: > 0 } code
                ? $"worker_rejected_{code}"
                : $"worker_http_{(int)response.StatusCode}";
            return await _leaseStore.CompleteAsync(
                grant.GrantId,
                CodexCoordinatorLeaseState.Rejected,
                null,
                outcome,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException or JsonException)
        {
            return await _leaseStore.CompleteAsync(
                grant.GrantId,
                CodexCoordinatorLeaseState.Rejected,
                null,
                "worker_unreachable",
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateRequest(CodexCoordinatorLeaseRequest request)
    {
        if (!IsSafeToken(request.TaskId, 160)
            || !string.IsNullOrWhiteSpace(request.WorkspaceId) && !IsSafeToken(request.WorkspaceId, 160)
            || !string.IsNullOrWhiteSpace(request.RequestedWorkerId) && !IsSafeToken(request.RequestedWorkerId, 120)
            || request.RequiredCapabilities.Count > 24
            || request.RequiredCapabilities.Any(capability => !IsSafeToken(capability, 120)))
        {
            throw new ArgumentException("The coordinator lease request contains an invalid or unbounded identity.", nameof(request));
        }
    }

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');

    private static CodexCoordinatorLeaseSnapshot Rejected(CodexCoordinatorLeaseRequest request, DateTimeOffset now, string outcomeCode)
        => new(
            "grant:none",
            request.TaskId.Trim(),
            request.WorkspaceId,
            request.RequestedWorkerId?.Trim() ?? string.Empty,
            CodexCoordinatorLeaseState.Rejected,
            null,
            now,
            now,
            now,
            outcomeCode);
}
