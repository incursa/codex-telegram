using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal sealed record CodexRemoteSessionSendRequest(
    string TaskId,
    long OwnerUserId,
    string WorkerId,
    string LeaseId,
    string CodexThreadId,
    string CommandId,
    long ChatId,
    int? MessageThreadId,
    string Input,
    bool PlanMode,
    string CallbackUrl,
    IReadOnlyList<CodexRemoteAttachmentPayload>? Attachments = null);

internal sealed record CodexRemoteTurnEvent(
    string WorkerId,
    string ThreadId,
    CodexTimelineEntryVm Entry);

internal sealed record CodexRemoteTurnCallback(
    string WorkerId,
    string CallbackUrl);

internal interface ICodexRemoteTurnEventForwarder
{
    void RegisterPending(string threadId, string workerId, string callbackUrl);

    Task ForwardAsync(string threadId, CodexTimelineEntryVm entry, CancellationToken cancellationToken);
}

/// <summary>
/// Forwards worker-owned turn events to the coordinator after an explicit remote send.
/// Callback registrations are intentionally in-memory; a restarted worker must receive a new send
/// registration before it can forward a subsequent turn.
/// </summary>
internal sealed class CodexRemoteTurnEventForwarder : ICodexRemoteTurnEventForwarder
{
    private const int MaximumRegistrations = 500;
    private readonly ConcurrentDictionary<string, CodexRemoteTurnCallback> _callbacks = new(StringComparer.Ordinal);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly ILogger<CodexRemoteTurnEventForwarder> _logger;

    public CodexRemoteTurnEventForwarder(
        IHttpClientFactory httpClientFactory,
        IOptions<CodexTelegramOptions> options,
        ILogger<CodexRemoteTurnEventForwarder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public void RegisterPending(string threadId, string workerId, string callbackUrl)
    {
        if (!IsSafeToken(threadId, 256) || !IsSafeToken(workerId, 120) || !Uri.TryCreate(callbackUrl, UriKind.Absolute, out Uri? callback)
            || callback.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The remote turn callback is invalid.");
        }

        _callbacks[threadId] = new CodexRemoteTurnCallback(workerId, callback.AbsoluteUri);
        while (_callbacks.Count > MaximumRegistrations && _callbacks.Keys.FirstOrDefault() is { } oldest)
        {
            _callbacks.TryRemove(oldest, out _);
        }
    }

    public async Task ForwardAsync(string threadId, CodexTimelineEntryVm entry, CancellationToken cancellationToken)
    {
        if (!_callbacks.TryGetValue(threadId, out CodexRemoteTurnCallback? callback))
        {
            return;
        }

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(nameof(CodexRemoteTurnEventForwarder));
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.Value.Coordinator.RequestTimeoutSeconds, 1, 60)));
            using HttpRequestMessage request = new(HttpMethod.Post, callback.CallbackUrl)
            {
                Content = JsonContent.Create(new CodexRemoteTurnEvent(callback.WorkerId, threadId, entry), options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Coordinator.AuthenticationToken);
            using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Coordinator rejected remote turn event for worker {WorkerId}, thread {ThreadId}, status {StatusCode}.", callback.WorkerId, threadId, (int)response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            _logger.LogWarning(exception, "Could not forward remote turn event for thread {ThreadId} to the coordinator.", threadId);
        }

        if (IsTerminal(entry))
        {
            _callbacks.TryRemove(threadId, out _);
        }
    }

    private static bool IsTerminal(CodexTimelineEntryVm entry)
        => entry.Type.Contains("turn.completed", StringComparison.OrdinalIgnoreCase)
            || entry.Type.Contains("turn.failed", StringComparison.OrdinalIgnoreCase)
            || entry.Type.Contains("turn.interrupted", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}

/// <summary>
/// Adapts worker turn events to the optional coordinator callback path while retaining the
/// existing realtime broadcaster seam for tests and future projections.
/// </summary>
internal sealed class CodexRemoteTurnEventBroadcaster(ICodexRemoteTurnEventForwarder forwarder) : ICodexRealtimeBroadcaster
{
    public Task BroadcastRuntimeStateAsync(CodexRuntimeStateVm runtime, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task BroadcastThreadEventAsync(string threadId, CodexTimelineEntryVm entry, CancellationToken cancellationToken)
        => forwarder.ForwardAsync(threadId, entry, cancellationToken);
}
