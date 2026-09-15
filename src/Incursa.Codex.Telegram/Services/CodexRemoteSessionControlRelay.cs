using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal sealed record CodexRemoteSessionControlRequest(
    string TaskId,
    long OwnerUserId,
    string WorkerId,
    string CodexThreadId,
    string CommandId,
    long ChatId,
    int? MessageThreadId,
    string Action,
    string? Input);

internal sealed record CodexRemoteSessionControlResponse(
    bool Accepted,
    string Message);

/// <summary>
/// Sends an explicitly authorized control operation to the worker that owns a supervision task.
/// Only steering and lifecycle interruption are included in this contract; no file or credential
/// data crosses the coordinator boundary.
/// </summary>
internal sealed class CodexRemoteSessionControlRelay
{
    private const string ControlPath = "api/worker/v1/sessions/control";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICodexCoordinatorWorkerStore _workerStore;
    private readonly IOptions<CodexTelegramOptions> _options;

    public CodexRemoteSessionControlRelay(
        IHttpClientFactory httpClientFactory,
        ICodexCoordinatorWorkerStore workerStore,
        IOptions<CodexTelegramOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _workerStore = workerStore;
        _options = options;
    }

    public async Task<CodexRemoteSessionControlResponse> SendAsync(
        CodexSupervisionTaskSnapshot task,
        long ownerUserId,
        long chatId,
        int? messageThreadId,
        string commandId,
        string action,
        string? input,
        CancellationToken cancellationToken)
    {
        if (!IsSafeToken(task.WorkerId, 120)
            || string.IsNullOrWhiteSpace(task.LeaseId)
            || !IsSafeToken(commandId, 256)
            || action is not ("steer" or "stop" or "kill"))
        {
            throw new InvalidOperationException("The remote session control metadata is invalid.");
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

        CodexRemoteSessionControlRequest requestBody = new(
            task.TaskId,
            ownerUserId,
            workerId,
            task.CodexThreadId,
            commandId,
            chatId,
            messageThreadId,
            action,
            input);
        HttpClient client = _httpClientFactory.CreateClient(nameof(CodexRemoteSessionControlRelay));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.Value.Coordinator.RequestTimeoutSeconds, 1, 60)));
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri(new Uri(worker.ControlPlaneUrl.TrimEnd('/') + "/", UriKind.Absolute), ControlPath))
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Coordinator.AuthenticationToken);
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The remote worker rejected the session control command; its outcome is recorded by the worker.");
        }

        CodexRemoteSessionControlResponse? result = await response.Content.ReadFromJsonAsync<CodexRemoteSessionControlResponse>(JsonOptions, timeout.Token).ConfigureAwait(false);
        if (result is null || string.IsNullOrWhiteSpace(result.Message) || result.Message.Length > 500)
        {
            throw new InvalidOperationException("The remote worker returned an invalid session control result.");
        }

        return result;
    }

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
