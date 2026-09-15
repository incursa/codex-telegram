using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Sends the local worker's bounded health snapshot to an operator-owned coordinator.
/// </summary>
internal sealed class CodexWorkerCoordinatorHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICodexWorkerRegistry _workerRegistry;
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly ILogger<CodexWorkerCoordinatorHostedService> _logger;

    public CodexWorkerCoordinatorHostedService(
        IHttpClientFactory httpClientFactory,
        ICodexWorkerRegistry workerRegistry,
        IOptions<CodexTelegramOptions> options,
        ILogger<CodexWorkerCoordinatorHostedService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _workerRegistry = workerRegistry;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CodexCoordinatorOptions coordinator = _options.Value.Coordinator;
        if (!coordinator.WorkerRegistrationEnabled)
        {
            return;
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(coordinator.HeartbeatIntervalSeconds));
        do
        {
            await SendHeartbeatAsync(coordinator, stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SendHeartbeatAsync(CodexCoordinatorOptions coordinator, CancellationToken cancellationToken)
    {
        try
        {
            CodexWorkerSnapshot worker = await _workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            using HttpClient client = _httpClientFactory.CreateClient(nameof(CodexWorkerCoordinatorHostedService));
            client.Timeout = TimeSpan.FromSeconds(coordinator.RequestTimeoutSeconds);
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                new Uri(new Uri(coordinator.Url!.TrimEnd('/') + "/", UriKind.Absolute), "api/coordinator/v1/workers/heartbeat"))
            {
                Content = JsonContent.Create(worker, options: JsonOptions),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", coordinator.AuthenticationToken);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Coordinator rejected worker heartbeat with HTTP status {StatusCode}.", (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not send the worker heartbeat to the configured coordinator.");
        }
    }
}
