using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
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
        CancellationToken cancellationToken,
        IReadOnlyList<TelegramAttachmentDescriptor>? attachments = null)
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

        if (planMode && attachments is { Count: > 0 })
        {
            throw new InvalidOperationException("Remote Plan mode does not support attachments.");
        }

        IReadOnlyList<CodexRemoteAttachmentPayload>? remoteAttachments = await ReadAttachmentsAsync(attachments, cancellationToken).ConfigureAwait(false);

        Uri callback = new(new Uri(coordinator.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute), EventPath);
        CodexRemoteSessionSendRequest requestBody = new(
            task.TaskId,
            ownerUserId,
            workerId,
            task.LeaseId,
            task.CodexThreadId,
            commandId,
            chatId,
            messageThreadId,
            input,
            planMode,
            callback.AbsoluteUri,
            remoteAttachments);
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

    private static async Task<IReadOnlyList<CodexRemoteAttachmentPayload>?> ReadAttachmentsAsync(
        IReadOnlyList<TelegramAttachmentDescriptor>? attachments,
        CancellationToken cancellationToken)
    {
        if (attachments is null or { Count: 0 })
        {
            return null;
        }

        if (attachments.Count > CodexRemoteAttachmentTransfer.MaximumAttachmentCount)
        {
            throw new InvalidOperationException("Remote task attachment count exceeds the bounded transfer limit.");
        }

        List<CodexRemoteAttachmentPayload> payloads = new(attachments.Count);
        long totalBytes = 0;
        foreach (TelegramAttachmentDescriptor attachment in attachments)
        {
            if (!CodexRemoteAttachmentTransfer.IsValidMetadata(new CodexRemoteAttachmentPayload(
                    string.IsNullOrWhiteSpace(attachment.FileName) ? Path.GetFileName(attachment.FilePath) : attachment.FileName,
                    attachment.ContentType,
                    attachment.IsImage,
                    "placeholder"))
                || !File.Exists(attachment.FilePath))
            {
                throw new InvalidOperationException("A remote task attachment is missing or has invalid metadata.");
            }

            FileInfo file = new(attachment.FilePath);
            if (file.Length > CodexRemoteAttachmentTransfer.MaximumAttachmentBytes
                || totalBytes > CodexRemoteAttachmentTransfer.MaximumTotalBytes - file.Length)
            {
                throw new InvalidOperationException("Remote task attachment size exceeds the bounded transfer limit.");
            }

            byte[] content = await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
            if (content.LongLength > CodexRemoteAttachmentTransfer.MaximumAttachmentBytes
                || totalBytes > CodexRemoteAttachmentTransfer.MaximumTotalBytes - content.LongLength)
            {
                throw new InvalidOperationException("Remote task attachment size exceeds the bounded transfer limit.");
            }

            totalBytes += content.LongLength;
            string fileName = string.IsNullOrWhiteSpace(attachment.FileName) ? Path.GetFileName(file.FullName) : attachment.FileName;
            payloads.Add(new CodexRemoteAttachmentPayload(
                fileName,
                attachment.ContentType,
                attachment.IsImage,
                Convert.ToBase64String(content)));
        }

        return payloads;
    }
}
