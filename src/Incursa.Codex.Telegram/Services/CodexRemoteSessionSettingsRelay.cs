using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal sealed record CodexRemoteSessionSettingsRequest(
    string TaskId,
    long OwnerUserId,
    string WorkerId,
    string LeaseId,
    string CodexThreadId,
    string Action,
    string? Model,
    string? ReasoningEffort,
    string? Objective,
    long? TokenBudget,
    CodexThreadGoalStatus? GoalStatus);

internal sealed record CodexRemoteSessionSettingsResponse(
    bool Accepted,
    string Action,
    CodexSessionModelSettings? ModelSettings,
    CodexThreadGoalVm? Goal,
    bool Cleared,
    string OutcomeCode);

/// <summary>
/// Relays model and goal operations to the worker that owns a remote task.
/// These controls never execute against the coordinator's local Codex runtime.
/// </summary>
internal sealed class CodexRemoteSessionSettingsRelay
{
    private const string SettingsPath = "api/worker/v1/sessions/settings";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICodexCoordinatorWorkerStore _workerStore;
    private readonly IOptions<CodexTelegramOptions> _options;

    public CodexRemoteSessionSettingsRelay(
        IHttpClientFactory httpClientFactory,
        ICodexCoordinatorWorkerStore workerStore,
        IOptions<CodexTelegramOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _workerStore = workerStore;
        _options = options;
    }

    public async Task<CodexSessionModelSettings> GetModelSettingsAsync(CodexSupervisionTaskSnapshot task, long ownerUserId, CancellationToken cancellationToken)
        => (await SendAsync(task, ownerUserId, "get_model", null, null, null, null, null, cancellationToken).ConfigureAwait(false)).ModelSettings
            ?? throw new InvalidOperationException("The remote worker returned no model settings.");

    public async Task<CodexSessionModelSettings> UpdateModelSettingsAsync(
        CodexSupervisionTaskSnapshot task,
        long ownerUserId,
        string? model,
        string? reasoningEffort,
        CancellationToken cancellationToken)
        => (await SendAsync(task, ownerUserId, "update_model", model, reasoningEffort, null, null, null, cancellationToken).ConfigureAwait(false)).ModelSettings
            ?? throw new InvalidOperationException("The remote worker returned no model settings.");

    public async Task<CodexThreadGoalVm?> GetGoalAsync(CodexSupervisionTaskSnapshot task, long ownerUserId, CancellationToken cancellationToken)
        => (await SendAsync(task, ownerUserId, "get_goal", null, null, null, null, null, cancellationToken).ConfigureAwait(false)).Goal;

    public async Task<CodexThreadGoalVm> SetGoalAsync(
        CodexSupervisionTaskSnapshot task,
        long ownerUserId,
        string objective,
        long? tokenBudget,
        CancellationToken cancellationToken)
        => (await SendAsync(task, ownerUserId, "set_goal", null, null, objective, tokenBudget, null, cancellationToken).ConfigureAwait(false)).Goal
            ?? throw new InvalidOperationException("The remote worker returned no goal.");

    public async Task<CodexThreadGoalVm> SetGoalStatusAsync(
        CodexSupervisionTaskSnapshot task,
        long ownerUserId,
        CodexThreadGoalStatus status,
        CancellationToken cancellationToken)
        => (await SendAsync(task, ownerUserId, "set_goal_status", null, null, null, null, status, cancellationToken).ConfigureAwait(false)).Goal
            ?? throw new InvalidOperationException("The remote worker returned no goal.");

    public async Task<bool> ClearGoalAsync(CodexSupervisionTaskSnapshot task, long ownerUserId, CancellationToken cancellationToken)
        => (await SendAsync(task, ownerUserId, "clear_goal", null, null, null, null, null, cancellationToken).ConfigureAwait(false)).Cleared;

    private async Task<CodexRemoteSessionSettingsResponse> SendAsync(
        CodexSupervisionTaskSnapshot task,
        long ownerUserId,
        string action,
        string? model,
        string? reasoningEffort,
        string? objective,
        long? tokenBudget,
        CodexThreadGoalStatus? goalStatus,
        CancellationToken cancellationToken)
    {
        if (!IsSafeToken(task.TaskId, 160)
            || !IsSafeToken(task.WorkerId, 120)
            || !IsSafeToken(task.LeaseId, 160)
            || !IsSafeToken(task.CodexThreadId, 256)
            || ownerUserId == 0
            || action is not ("get_model" or "update_model" or "get_goal" or "set_goal" or "set_goal_status" or "clear_goal"))
        {
            throw new InvalidOperationException("The remote settings metadata is invalid.");
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

        CodexRemoteSessionSettingsRequest requestBody = new(
            task.TaskId,
            ownerUserId,
            workerId,
            task.LeaseId!,
            task.CodexThreadId,
            action,
            model,
            reasoningEffort,
            objective,
            tokenBudget,
            goalStatus);
        HttpClient client = _httpClientFactory.CreateClient(nameof(CodexRemoteSessionSettingsRelay));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.Value.Coordinator.RequestTimeoutSeconds, 1, 60)));
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri(new Uri(worker.ControlPlaneUrl.TrimEnd('/') + "/", UriKind.Absolute), SettingsPath))
        {
            Content = JsonContent.Create(requestBody, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Coordinator.AuthenticationToken);
        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The remote worker rejected the settings operation.");
        }

        CodexRemoteSessionSettingsResponse? result = await response.Content
            .ReadFromJsonAsync<CodexRemoteSessionSettingsResponse>(JsonOptions, timeout.Token)
            .ConfigureAwait(false);
        if (result is null || !result.Accepted || !string.Equals(result.Action, action, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The remote worker returned invalid settings evidence.");
        }

        return result;
    }

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
