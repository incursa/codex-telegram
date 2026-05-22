using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Services;
using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Tests.TestSupport;

internal sealed class ScriptedCodexSessionManager : ICodexSessionManager
{
    public List<CodexSessionSummary> Sessions { get; } = [];

    public List<CreateCodexSessionRequest> CreateRequests { get; } = [];

    public List<object> SendRequests { get; } = [];

    public List<string> SendSessionIds { get; } = [];

    public List<(string SessionId, string Text)> TextSends { get; } = [];

    public List<(string SessionId, string Text)> PlanSends { get; } = [];

    public List<(string SessionId, IReadOnlyList<CodexInputItem> Input)> AttachmentSends { get; } = [];

    public List<(string SessionId, string? Model, string? ReasoningEffort)> UpdateRequests { get; } = [];

    public Dictionary<string, CodexThreadGoalVm> Goals { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string SessionId, string Objective, long? TokenBudget)> SetGoalRequests { get; } = [];

    public List<(string SessionId, CodexThreadGoalStatus Status)> SetGoalStatusRequests { get; } = [];

    public List<string> ClearGoalRequests { get; } = [];

    public List<(string SessionId, int LineCount)> TailRequests { get; } = [];

    public List<string> StopRequests { get; } = [];

    public List<(string SessionId, object Input)> SteerRequests { get; } = [];

    public Queue<Exception> SendExceptions { get; } = [];

    public Queue<Exception> SteerExceptions { get; } = [];

    public Exception? SendException { get; set; }

    public TaskCompletionSource<CodexSessionModelSettings>? UpdateModelSettingsCompletion { get; set; }

    public TaskCompletionSource<CodexThreadExecutionVm>? PendingSend
    {
        get => _pendingSend;
        set => _pendingSend = value;
    }

    public TaskCompletionSource<CodexThreadExecutionVm>? PendingTextSend
    {
        get => _pendingSend;
        set => _pendingSend = value;
    }

    public TaskCompletionSource<bool> SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> TextSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? ExecutionThreadId { get; set; }

    private TaskCompletionSource<CodexThreadExecutionVm>? _pendingSend;

    public Task<IReadOnlyCollection<CodexSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyCollection<CodexSessionSummary>>(Sessions.ToArray());

    public Task<CodexSessionSummary> CreateSessionAsync(CreateCodexSessionRequest request, CancellationToken cancellationToken)
    {
        CreateRequests.Add(request);
        CodexSessionSummary session = CreateSession($"thread-{CreateRequests.Count}", request.Name, request.WorkingDirectory);
        Sessions.Add(session);
        return Task.FromResult(session);
    }

    public Task<CodexSessionSummary?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
        => Task.FromResult(Sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase)));

    public Task<CodexThreadExecutionVm> SendAsync(string sessionId, string input, CancellationToken cancellationToken)
    {
        ThrowNextSendExceptionIfPresent();
        SendSessionIds.Add(sessionId);
        SendRequests.Add(input);
        TextSends.Add((sessionId, input));
        TextSendStarted.TrySetResult(true);
        SendStarted.TrySetResult(true);

        if (_pendingSend is not null)
        {
            return _pendingSend.Task;
        }

        return Task.FromResult(new CodexThreadExecutionVm(ExecutionThreadId ?? sessionId, "turn-1", "running", null));
    }

    public Task<CodexThreadExecutionVm> SendAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
    {
        ThrowNextSendExceptionIfPresent();
        SendSessionIds.Add(sessionId);
        SendRequests.Add(input);
        AttachmentSends.Add((sessionId, input));
        TextSendStarted.TrySetResult(true);
        SendStarted.TrySetResult(true);

        if (_pendingSend is not null)
        {
            return _pendingSend.Task;
        }

        return Task.FromResult(new CodexThreadExecutionVm(ExecutionThreadId ?? sessionId, "turn-1", "running", null));
    }

    public Task<CodexThreadExecutionVm> SendPlanAsync(string sessionId, string input, CancellationToken cancellationToken)
    {
        ThrowNextSendExceptionIfPresent();
        SendSessionIds.Add(sessionId);
        SendRequests.Add(new PlanSendRequest(input));
        PlanSends.Add((sessionId, input));
        TextSendStarted.TrySetResult(true);
        SendStarted.TrySetResult(true);

        if (_pendingSend is not null)
        {
            return _pendingSend.Task;
        }

        return Task.FromResult(new CodexThreadExecutionVm(ExecutionThreadId ?? sessionId, "turn-1", "running", null));
    }

    public Task SteerAsync(string sessionId, string input, CancellationToken cancellationToken)
    {
        ThrowNextSteerExceptionIfPresent();
        SteerRequests.Add((sessionId, input));
        return Task.CompletedTask;
    }

    public Task SteerAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
    {
        ThrowNextSteerExceptionIfPresent();
        SteerRequests.Add((sessionId, input));
        return Task.CompletedTask;
    }

    public Task<CodexSessionModelSettings> GetModelSettingsAsync(string sessionId, CancellationToken cancellationToken)
    {
        CodexSessionSummary? session = Sessions.FirstOrDefault(candidate => string.Equals(candidate.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(CreateModelSettings(sessionId, session?.Name ?? sessionId, "gpt-5.4-mini", "high"));
    }

    public Task<CodexSessionModelSettings> UpdateModelSettingsAsync(
        string sessionId,
        string? model,
        string? reasoningEffort,
        CancellationToken cancellationToken)
    {
        UpdateRequests.Add((sessionId, model, reasoningEffort));
        if (UpdateModelSettingsCompletion is not null)
        {
            return UpdateModelSettingsCompletion.Task;
        }

        return Task.FromResult(CreateModelSettings(
            sessionId,
            Sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase))?.Name ?? sessionId,
            model ?? "gpt-5.4-mini",
            reasoningEffort ?? "high"));
    }

    public Task<CodexThreadGoalVm?> GetGoalAsync(string sessionId, CancellationToken cancellationToken)
        => Task.FromResult(Goals.GetValueOrDefault(sessionId));

    public Task<CodexThreadGoalVm> SetGoalAsync(
        string sessionId,
        string objective,
        long? tokenBudget,
        CancellationToken cancellationToken)
    {
        SetGoalRequests.Add((sessionId, objective, tokenBudget));
        CodexThreadGoalVm goal = CreateGoal(sessionId, objective, CodexThreadGoalStatus.Active, tokenBudget);
        Goals[sessionId] = goal;
        return Task.FromResult(goal);
    }

    public Task<CodexThreadGoalVm> SetGoalStatusAsync(
        string sessionId,
        CodexThreadGoalStatus status,
        CancellationToken cancellationToken)
    {
        SetGoalStatusRequests.Add((sessionId, status));
        CodexThreadGoalVm goal = Goals.GetValueOrDefault(sessionId) ?? CreateGoal(sessionId, "Existing goal");
        goal = goal with { Status = status };
        Goals[sessionId] = goal;
        return Task.FromResult(goal);
    }

    public Task<bool> ClearGoalAsync(string sessionId, CancellationToken cancellationToken)
    {
        ClearGoalRequests.Add(sessionId);
        return Task.FromResult(Goals.Remove(sessionId));
    }

    public Task<string> TailAsync(string sessionId, int lineCount, CancellationToken cancellationToken)
    {
        TailRequests.Add((sessionId, lineCount));
        return Task.FromResult("tail output");
    }

    public Task StopAsync(string sessionId, CancellationToken cancellationToken)
    {
        StopRequests.Add(sessionId);
        return Task.CompletedTask;
    }

    public Task KillAsync(string sessionId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task RenameAsync(string sessionId, string name, CancellationToken cancellationToken)
    {
        int index = Sessions.FindIndex(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            CodexSessionSummary current = Sessions[index];
            Sessions[index] = current with { Name = name };
        }

        return Task.CompletedTask;
    }

    public Task ForgetAsync(string sessionId, CancellationToken cancellationToken)
    {
        Sessions.RemoveAll(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }

    private void ThrowNextSendExceptionIfPresent()
    {
        if (SendException is not null)
        {
            throw SendException;
        }

        if (SendExceptions.TryDequeue(out Exception? exception))
        {
            throw exception;
        }
    }

    private void ThrowNextSteerExceptionIfPresent()
    {
        if (SteerExceptions.TryDequeue(out Exception? exception))
        {
            throw exception;
        }
    }

    private static CodexSessionSummary CreateSession(string id, string? name, string? workingDirectory)
        => new(
            id,
            name ?? id,
            CodexSessionStatus.Exited,
            workingDirectory,
            DateTimeOffset.Parse("2026-05-06T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-05-06T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            ExitCode: null,
            LastError: null);

    private static CodexSessionModelSettings CreateModelSettings(string sessionId, string sessionName, string model, string effort)
        => new(
            sessionId,
            sessionName,
            model,
            effort,
            [
                new CodexModelVm(
                    "gpt-5.4-mini",
                    "GPT-5.4 Mini",
                    "Fast model for Telegram tests.",
                    CodexReasoningEffort.High,
                    [CodexReasoningEffort.Low, CodexReasoningEffort.Medium, CodexReasoningEffort.High, CodexReasoningEffort.XHigh],
                    IsDefault: true,
                    Hidden: false,
                    SupportsPersonality: false,
                    AvailabilityMessage: null),
            ],
            [CodexReasoningEffort.Low, CodexReasoningEffort.Medium, CodexReasoningEffort.High, CodexReasoningEffort.XHigh]);

    private static CodexThreadGoalVm CreateGoal(
        string threadId,
        string objective,
        CodexThreadGoalStatus status = CodexThreadGoalStatus.Active,
        long? tokenBudget = null)
        => new(
            threadId,
            objective,
            status,
            tokenBudget,
            tokenBudget.HasValue ? 1000 : 0,
            95,
            DateTimeOffset.Parse("2026-05-06T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-05-06T12:05:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private sealed record PlanSendRequest(string Input);
}
