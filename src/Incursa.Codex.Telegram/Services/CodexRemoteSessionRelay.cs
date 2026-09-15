using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Sends an explicitly authorized text turn to the worker that owns a supervision task.
/// The worker remains responsible for Codex execution and forwards bounded events back.
/// </summary>
internal sealed class CodexRemoteSessionRelay
{
    private const string SendPath = "api/worker/v1/sessions/send";
    private const string EventPath = "api/coordinator/v1/worker-events";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICodexCoordinatorWorkerStore _workerStore;
    private readonly IOptions<CodexTelegramOptions> _options;

    public CodexRemoteSessionRelay(
        IHttpClientFactory httpClientFactory,
        ICodexCoordinatorWorkerStore workerStore,
        IOptions<CodexTelegramOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _workerStore = workerStore;
        _options = options;
    }

    public async Task<CodexThreadExecutionVm> SendAsync(
        CodexSupervisionTaskSnapshot task,
        long ownerUserId,
        long chatId,
        int? messageThreadId,
        string commandId,
        string input,
        bool planMode,
        CancellationToken cancellationToken)
    {
        if (!IsSafeToken(task.WorkerId, 120)
            || string.IsNullOrWhiteSpace(task.LeaseId))
        {
            throw new InvalidOperationException("The remote task has no valid worker ownership.");
        }

        string workerId = task.WorkerId!;

        CodexWorkerSnapshot? worker = (await _workerStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.IsRemote
                && string.Equals(candidate.WorkerId, workerId, StringComparison.Ordinal)
                && candidate.State == CodexWorkerState.Online
                && string.Equals(candidate.Readiness, "ready", StringComparison.OrdinalIgnoreCase));
        if (worker is null || string.IsNullOrWhiteSpace(worker.ControlPlaneUrl))
        {
            throw new InvalidOperationException("The assigned remote worker is unavailable.");
        }

        if (!Uri.TryCreate(_options.Value.Coordinator.Url, UriKind.Absolute, out Uri? coordinator)
            || coordinator.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("The coordinator callback URL is not configured.");
        }

        Uri callback = new(new Uri(coordinator.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute), EventPath);
        CodexRemoteSessionSendRequest requestBody = new(
            task.TaskId,
            ownerUserId,
            workerId,
            task.CodexThreadId,
            commandId,
            chatId,
            messageThreadId,
            input,
            planMode,
            callback.AbsoluteUri);
        HttpClient client = _httpClientFactory.CreateClient(nameof(CodexRemoteSessionRelay));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.Value.Coordinator.RequestTimeoutSeconds, 1, 60)));
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri(new Uri(worker.ControlPlaneUrl.TrimEnd('/') + "/", UriKind.Absolute), SendPath))
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Coordinator.AuthenticationToken);
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The remote worker rejected the session command; its outcome is recorded by the worker.");
        }

        CodexThreadExecutionVm? execution = await response.Content.ReadFromJsonAsync<CodexThreadExecutionVm>(JsonOptions, timeout.Token).ConfigureAwait(false);
        if (execution is null || !string.Equals(execution.ThreadId, task.CodexThreadId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(execution.TurnId))
        {
            throw new InvalidOperationException("The remote worker returned an invalid session execution result.");
        }

        return execution;
    }

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
