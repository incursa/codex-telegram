using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal sealed record CodexRemoteTaskWorkspaceRequest(
    string TaskId,
    long OwnerUserId,
    string WorkerId,
    string LeaseId,
    string CodexThreadId,
    string Action,
    bool DiscardChanges);

/// <summary>
/// Bounded coordinator relay for status and explicit release of a worker-owned task workspace.
/// Filesystem paths remain on the worker.
/// </summary>
internal sealed class CodexRemoteTaskWorkspaceRelay
{
    private const string WorkspacePath = "api/worker/v1/tasks/workspace";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICodexCoordinatorWorkerStore _workerStore;
    private readonly IOptions<CodexTelegramOptions> _options;

    public CodexRemoteTaskWorkspaceRelay(
        IHttpClientFactory httpClientFactory,
        ICodexCoordinatorWorkerStore workerStore,
        IOptions<CodexTelegramOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _workerStore = workerStore;
        _options = options;
    }

    public Task<CodexRemoteTaskWorkspaceResponse> GetStatusAsync(
        CodexSupervisionTaskRecord task,
        CancellationToken cancellationToken)
        => SendAsync(task, "status", discardChanges: false, cancellationToken);

    public Task<CodexRemoteTaskWorkspaceResponse> ReleaseAsync(
        CodexSupervisionTaskRecord task,
        bool discardChanges,
        CancellationToken cancellationToken)
        => SendAsync(task, "release", discardChanges, cancellationToken);

    private async Task<CodexRemoteTaskWorkspaceResponse> SendAsync(
        CodexSupervisionTaskRecord task,
        string action,
        bool discardChanges,
        CancellationToken cancellationToken)
    {
        if (!IsSafeToken(task.WorkerId, 120)
            || string.IsNullOrWhiteSpace(task.LeaseId)
            || !IsSafeToken(task.TaskId, 160)
            || !IsSafeToken(task.CodexThreadId, 256)
            || action is not ("status" or "release"))
        {
            throw new InvalidOperationException("The remote workspace metadata is invalid.");
        }

        string workerId = task.WorkerId!;
        CodexWorkerSnapshot? worker = (await _workerStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.IsRemote
                && string.Equals(candidate.WorkerId, workerId, StringComparison.Ordinal)
                && (candidate.State is CodexWorkerState.Online or CodexWorkerState.Draining)
                && string.Equals(candidate.Readiness, "ready", StringComparison.OrdinalIgnoreCase));
        if (worker is null || string.IsNullOrWhiteSpace(worker.ControlPlaneUrl))
        {
            throw new InvalidOperationException("The assigned remote worker is unavailable.");
        }

        CodexRemoteTaskWorkspaceRequest requestBody = new(
            task.TaskId,
            task.OwnerUserId,
            workerId,
            task.LeaseId,
            task.CodexThreadId,
            action,
            discardChanges);
        HttpClient client = _httpClientFactory.CreateClient(nameof(CodexRemoteTaskWorkspaceRelay));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.Value.Coordinator.RequestTimeoutSeconds, 1, 60)));
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri(new Uri(worker.ControlPlaneUrl.TrimEnd('/') + "/", UriKind.Absolute), WorkspacePath))
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Coordinator.AuthenticationToken);
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The remote worker rejected the workspace operation.");
        }

        CodexRemoteTaskWorkspaceResponse? result = await response.Content.ReadFromJsonAsync<CodexRemoteTaskWorkspaceResponse>(JsonOptions, timeout.Token).ConfigureAwait(false);
        if (result is null || !string.Equals(result.TaskId, task.TaskId, StringComparison.Ordinal) || !string.Equals(result.WorkerId, workerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The remote worker returned invalid workspace evidence.");
        }

        return result;
    }

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}

internal sealed record CodexRemoteTaskWorkspaceResponse(
    string TaskId,
    string WorkerId,
    CodexTaskWorkspaceState State,
    string Branch,
    int DevelopmentPort,
    string DatabaseNamespace,
    string? OutcomeCode);
