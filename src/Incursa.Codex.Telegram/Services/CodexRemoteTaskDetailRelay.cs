using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal sealed record CodexRemoteTaskDetailRequest(
    string TaskId,
    long OwnerUserId,
    string WorkerId,
    string LeaseId,
    string CodexThreadId);

/// <summary>
/// Retrieves a redacted Mini App task detail projection from the worker that owns
/// the Codex thread. Raw thread details and worker filesystem paths stay local.
/// </summary>
internal sealed class CodexRemoteTaskDetailRelay
{
    private const string DetailPath = "api/worker/v1/tasks/detail";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICodexCoordinatorWorkerStore _workerStore;
    private readonly IOptions<CodexTelegramOptions> _options;

    public CodexRemoteTaskDetailRelay(
        IHttpClientFactory httpClientFactory,
        ICodexCoordinatorWorkerStore workerStore,
        IOptions<CodexTelegramOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _workerStore = workerStore;
        _options = options;
    }

    public async Task<TelegramMiniAppThreadDetailVm> GetAsync(
        CodexSupervisionTaskRecord task,
        CancellationToken cancellationToken)
    {
        if (!IsSafeToken(task.TaskId, 160)
            || task.OwnerUserId == 0
            || !IsSafeToken(task.WorkerId, 120)
            || !IsSafeToken(task.LeaseId, 160)
            || !IsSafeToken(task.CodexThreadId, 256))
        {
            throw new InvalidOperationException("The remote task detail metadata is invalid.");
        }

        string workerId = task.WorkerId!;
        CodexWorkerSnapshot? worker = (await _workerStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.IsRemote
                && string.Equals(candidate.WorkerId, workerId, StringComparison.Ordinal)
                && candidate.State is CodexWorkerState.Online or CodexWorkerState.Draining
                && string.Equals(candidate.Readiness, "ready", StringComparison.OrdinalIgnoreCase));
        if (worker is null || string.IsNullOrWhiteSpace(worker.ControlPlaneUrl))
        {
            throw new InvalidOperationException("The assigned remote worker is unavailable.");
        }

        CodexRemoteTaskDetailRequest requestBody = new(
            task.TaskId,
            task.OwnerUserId,
            workerId,
            task.LeaseId!,
            task.CodexThreadId);
        HttpClient client = _httpClientFactory.CreateClient(nameof(CodexRemoteTaskDetailRelay));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.Value.Coordinator.RequestTimeoutSeconds, 1, 60)));
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri(new Uri(worker.ControlPlaneUrl.TrimEnd('/') + "/", UriKind.Absolute), DetailPath))
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Coordinator.AuthenticationToken);
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The remote worker rejected the task detail request.");
        }

        TelegramMiniAppThreadDetailVm? result = await response.Content
            .ReadFromJsonAsync<TelegramMiniAppThreadDetailVm>(JsonOptions, timeout.Token)
            .ConfigureAwait(false);
        if (result is null || !string.Equals(result.Thread.Id, task.CodexThreadId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The remote worker returned invalid task detail evidence.");
        }

        return result;
    }

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
