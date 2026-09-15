using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal sealed record CodexRemoteWorkerControlRequest(
    string WorkerId,
    string Action,
    bool Confirm);

internal sealed record CodexRemoteWorkerControlResponse(
    bool Accepted,
    CodexWorkerSnapshot Worker,
    string OutcomeCode);

/// <summary>
/// Relays explicit drain/resume operations to an admitted worker. The coordinator
/// never changes remote worker state locally and never sends task or Codex data.
/// </summary>
internal sealed class CodexRemoteWorkerControlRelay
{
    private const string ControlPath = "api/worker/v1/worker/control";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICodexCoordinatorWorkerStore _workerStore;
    private readonly IOptions<CodexTelegramOptions> _options;

    public CodexRemoteWorkerControlRelay(
        IHttpClientFactory httpClientFactory,
        ICodexCoordinatorWorkerStore workerStore,
        IOptions<CodexTelegramOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _workerStore = workerStore;
        _options = options;
    }

    public async Task<CodexWorkerSnapshot> SetDrainingAsync(
        CodexWorkerSnapshot worker,
        bool draining,
        CancellationToken cancellationToken)
    {
        if (!worker.IsRemote
            || !IsSafeToken(worker.WorkerId, 120)
            || string.IsNullOrWhiteSpace(worker.ControlPlaneUrl)
            || worker.State is not (CodexWorkerState.Online or CodexWorkerState.Draining)
            || string.Equals(worker.Readiness, "unavailable", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The remote worker is not available for control.");
        }

        CodexWorkerSnapshot? admittedWorker = (await _workerStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.IsRemote
                && string.Equals(candidate.WorkerId, worker.WorkerId, StringComparison.Ordinal)
                && candidate.State is CodexWorkerState.Online or CodexWorkerState.Draining
                && !string.Equals(candidate.Readiness, "unavailable", StringComparison.OrdinalIgnoreCase));
        if (admittedWorker is null || string.IsNullOrWhiteSpace(admittedWorker.ControlPlaneUrl))
        {
            throw new InvalidOperationException("The remote worker is no longer admitted or available.");
        }

        CodexRemoteWorkerControlRequest requestBody = new(
            admittedWorker.WorkerId,
            draining ? "drain" : "resume",
            true);
        HttpClient client = _httpClientFactory.CreateClient(nameof(CodexRemoteWorkerControlRelay));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.Value.Coordinator.RequestTimeoutSeconds, 1, 60)));
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri(new Uri(admittedWorker.ControlPlaneUrl.TrimEnd('/') + "/", UriKind.Absolute), ControlPath))
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Coordinator.AuthenticationToken);
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The remote worker rejected the worker control request.");
        }

        CodexRemoteWorkerControlResponse? result = await response.Content
            .ReadFromJsonAsync<CodexRemoteWorkerControlResponse>(JsonOptions, timeout.Token)
            .ConfigureAwait(false);
        if (result is null
            || !result.Accepted
            || !string.Equals(result.Worker.WorkerId, admittedWorker.WorkerId, StringComparison.Ordinal)
            || (draining && result.Worker.State != CodexWorkerState.Draining)
            || (!draining && result.Worker.State != CodexWorkerState.Online))
        {
            throw new InvalidOperationException("The remote worker returned invalid worker control evidence.");
        }

        return result.Worker with { IsRemote = true };
    }

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
