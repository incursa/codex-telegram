using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Coordinates only the bounded update-control contract on an admitted worker.
/// Package transfer, service restart, and health completion remain installer-owned.
/// </summary>
internal sealed class CodexRemoteWorkerUpdateRelay
{
    private const string StatusPath = "api/worker/v1/update/status";
    private const string ControlPath = "api/worker/v1/update/control";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<CodexTelegramOptions> _options;

    public CodexRemoteWorkerUpdateRelay(
        IHttpClientFactory httpClientFactory,
        IOptions<CodexTelegramOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task<CodexWorkerUpdateSnapshot> GetStatusAsync(
        CodexWorkerSnapshot worker,
        CancellationToken cancellationToken)
    {
        Uri endpoint = GetEndpoint(worker, StatusPath);
        HttpClient client = _httpClientFactory.CreateClient(nameof(CodexRemoteWorkerUpdateRelay));
        using CancellationTokenSource timeout = CreateTimeout(cancellationToken);
        using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Coordinator.AuthenticationToken);
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        EnsureSuccess(response, "The remote worker rejected the update status request.");
        CodexWorkerUpdateSnapshot? result = await response.Content.ReadFromJsonAsync<CodexWorkerUpdateSnapshot>(JsonOptions, timeout.Token).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("The remote worker returned no update status.");
    }

    public async Task<CodexWorkerUpdateControlResponse> ExecuteAsync(
        CodexWorkerSnapshot worker,
        string rolloutId,
        string action,
        string? targetVersion,
        string? expectedSha256,
        IReadOnlyList<string> requiredCapabilities,
        CancellationToken cancellationToken)
    {
        if (action is not ("stage" or "rollback"))
        {
            throw new ArgumentException("The remote worker update action is invalid.", nameof(action));
        }

        Uri endpoint = GetEndpoint(worker, ControlPath);
        CodexWorkerUpdateControlRequest body = new(
            worker.WorkerId,
            rolloutId,
            action,
            true,
            targetVersion,
            expectedSha256,
            requiredCapabilities);
        HttpClient client = _httpClientFactory.CreateClient(nameof(CodexRemoteWorkerUpdateRelay));
        using CancellationTokenSource timeout = CreateTimeout(cancellationToken);
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Coordinator.AuthenticationToken);
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        CodexWorkerUpdateControlResponse? result = await response.Content
            .ReadFromJsonAsync<CodexWorkerUpdateControlResponse>(JsonOptions, timeout.Token)
            .ConfigureAwait(false);
        if (result is null)
        {
            throw new InvalidOperationException("The remote worker returned no update-control result.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The remote worker rejected the update-control request ({result.OutcomeCode}).");
        }

        return result;
    }

    private Uri GetEndpoint(CodexWorkerSnapshot worker, string path)
    {
        if (!worker.IsRemote
            || worker.State is not (CodexWorkerState.Online or CodexWorkerState.Draining)
            || string.Equals(worker.Readiness, "unavailable", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(worker.ControlPlaneUrl))
        {
            throw new InvalidOperationException("The assigned remote worker is unavailable for update coordination.");
        }

        return new Uri(new Uri(worker.ControlPlaneUrl.TrimEnd('/') + "/", UriKind.Absolute), path);
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.Value.Coordinator.RequestTimeoutSeconds, 1, 60)));
        return timeout;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string message)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(message);
        }
    }
}
