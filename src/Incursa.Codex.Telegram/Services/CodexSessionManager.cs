using Incursa.OpenAI.Codex;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Services;

internal interface ICodexSessionManager
{
    Task<IReadOnlyCollection<CodexSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken);

    Task<CodexSessionSummary> CreateSessionAsync(CreateCodexSessionRequest request, CancellationToken cancellationToken);

    Task<CodexSessionSummary?> GetSessionAsync(string sessionId, CancellationToken cancellationToken);

    Task<CodexThreadExecutionVm> SendAsync(string sessionId, string input, CancellationToken cancellationToken);

    Task<CodexThreadExecutionVm> SendAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken);

    Task SteerAsync(string sessionId, string input, CancellationToken cancellationToken);

    Task SteerAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken);

    Task<CodexSessionModelSettings> GetModelSettingsAsync(string sessionId, CancellationToken cancellationToken);

    Task<CodexSessionModelSettings> UpdateModelSettingsAsync(
        string sessionId,
        string? model,
        string? reasoningEffort,
        CancellationToken cancellationToken);

    Task<CodexThreadGoalVm?> GetGoalAsync(string sessionId, CancellationToken cancellationToken);

    Task<CodexThreadGoalVm> SetGoalAsync(
        string sessionId,
        string objective,
        long? tokenBudget,
        CancellationToken cancellationToken);

    Task<CodexThreadGoalVm> SetGoalStatusAsync(
        string sessionId,
        CodexThreadGoalStatus status,
        CancellationToken cancellationToken);

    Task<bool> ClearGoalAsync(string sessionId, CancellationToken cancellationToken);

    Task<string> TailAsync(string sessionId, int lineCount, CancellationToken cancellationToken);

    Task StopAsync(string sessionId, CancellationToken cancellationToken);

    Task KillAsync(string sessionId, CancellationToken cancellationToken);

    Task RenameAsync(string sessionId, string name, CancellationToken cancellationToken);

    Task ForgetAsync(string sessionId, CancellationToken cancellationToken);
}

internal sealed record CreateCodexSessionRequest(string Name, string? WorkingDirectory);

internal sealed record CodexSessionModelSettings(
    string SessionId,
    string SessionName,
    string? Model,
    string? ReasoningEffort,
    IReadOnlyList<CodexModelVm> AvailableModels,
    IReadOnlyList<CodexReasoningEffort> AvailableReasoningEfforts);

internal sealed record CodexSessionSummary(
    string Id,
    string Name,
    CodexSessionStatus Status,
    string? WorkingDirectory,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastActivityUtc,
    int? ExitCode,
    string? LastError);

internal enum CodexSessionStatus
{
    Starting,
    Running,
    Exited,
    Failed,
    Stopped,
}

internal sealed class CodexGatewaySessionManager : ICodexSessionManager
{
    private const int SessionListLimit = 200;

    private readonly ICodexGateway _gateway;
    private readonly ICodexTurnExecutionCoordinator _turnCoordinator;
    private readonly ICodexThreadManifestStore _manifestStore;
    private readonly ITelegramBotStateStore _stateStore;
    private readonly TelegramBotOptions _options;
    private readonly CodexTelegramOptions _codexOptions;

    public CodexGatewaySessionManager(
        ICodexGateway gateway,
        ICodexTurnExecutionCoordinator turnCoordinator,
        ICodexThreadManifestStore manifestStore,
        ITelegramBotStateStore stateStore,
        IOptions<TelegramBotOptions> options,
        IOptions<CodexTelegramOptions> codexOptions)
    {
        _gateway = gateway;
        _turnCoordinator = turnCoordinator;
        _manifestStore = manifestStore;
        _stateStore = stateStore;
        _options = options.Value;
        _codexOptions = codexOptions.Value;
    }

    public async Task<IReadOnlyCollection<CodexSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> forgottenSessionIds = await _stateStore.GetForgottenSessionIdsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyCollection<string> trackedSessionIds = await _stateStore.GetTrackedSessionIdsAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> forgotten = new(forgottenSessionIds, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CodexSessionSummary> sessions = new(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<CodexThreadListItemVm> threads = await _gateway.ListThreadsAsync(
            new CodexThreadListQuery(null, "all", "updatedAt", true, null, SessionListLimit),
            cancellationToken).ConfigureAwait(false);

        foreach (CodexThreadListItemVm thread in threads.Where(thread => !forgotten.Contains(thread.Id)))
        {
            sessions[thread.Id] = ToSummary(thread, _turnCoordinator.TryGetActiveTurnState(thread.Id));
        }

        foreach (string trackedSessionId in trackedSessionIds)
        {
            if (forgotten.Contains(trackedSessionId) || sessions.ContainsKey(trackedSessionId))
            {
                continue;
            }

            CodexThreadManifestRecord? manifest = await _manifestStore.ReadAsync(trackedSessionId, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                continue;
            }

            sessions[manifest.ThreadId] = ToSummary(manifest, _turnCoordinator.TryGetActiveTurnState(manifest.ThreadId));
        }

        return sessions.Values
            .OrderByDescending(session => session.LastActivityUtc)
            .ToArray();
    }

    public async Task<CodexSessionSummary> CreateSessionAsync(CreateCodexSessionRequest request, CancellationToken cancellationToken)
    {
        string name = CodexTextFormatting.ResolveDisplayName(request.Name, "Telegram session");
        CodexThreadContextSubmission submission = new()
        {
            ThreadName = name,
            WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? _options.DefaultWorkingDirectory
                : request.WorkingDirectory,
        };

        CodexThreadListItemVm thread = await _gateway.CreateThreadShellAsync(submission, cancellationToken).ConfigureAwait(false);
        await _stateStore.TrackSessionAsync(thread.Id, cancellationToken).ConfigureAwait(false);
        return ToSummary(thread, _turnCoordinator.TryGetActiveTurnState(thread.Id));
    }

    public async Task<CodexSessionSummary?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        if (await _stateStore.IsSessionForgottenAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        IReadOnlyCollection<CodexSessionSummary> sessions = await ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        return sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<CodexThreadExecutionVm> SendAsync(string sessionId, string input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Input cannot be empty.", nameof(input));
        }

        return await SendAsync(sessionId, [new CodexTextInput { Text = input.Trim() }], cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexThreadExecutionVm> SendAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
    {
        if (input.Count == 0)
        {
            throw new ArgumentException("Input cannot be empty.", nameof(input));
        }

        await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        CodexThreadExecutionVm execution = await _gateway.SendTurnAsync(sessionId, input, cancellationToken).ConfigureAwait(false);
        await _stateStore.TrackSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return execution;
    }

    public async Task SteerAsync(string sessionId, string input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Input cannot be empty.", nameof(input));
        }

        await SteerAsync(sessionId, [new CodexTextInput { Text = input.Trim() }], cancellationToken).ConfigureAwait(false);
    }

    public async Task SteerAsync(string sessionId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
    {
        if (input.Count == 0)
        {
            throw new ArgumentException("Input cannot be empty.", nameof(input));
        }

        await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        CodexActiveTurnStateVm? activeTurn = _turnCoordinator.TryGetActiveTurnState(sessionId);
        if (activeTurn is null)
        {
            throw new InvalidOperationException("No active turn is currently running for this session.");
        }

        await _turnCoordinator.SteerAsync(
            activeTurn.ThreadId,
            activeTurn.TurnId,
            input,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexSessionModelSettings> GetModelSettingsAsync(string sessionId, CancellationToken cancellationToken)
    {
        CodexSessionSummary session = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        CodexThreadManifestRecord manifest = await _manifestStore.GetOrCreateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CodexModelVm> models = await _gateway.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return BuildModelSettings(session, manifest, models);
    }

    public async Task<CodexSessionModelSettings> UpdateModelSettingsAsync(
        string sessionId,
        string? model,
        string? reasoningEffort,
        CancellationToken cancellationToken)
    {
        CodexSessionSummary session = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        CodexThreadManifestRecord manifest = await _manifestStore.GetOrCreateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CodexModelVm> models = await _gateway.ListModelsAsync(cancellationToken).ConfigureAwait(false);

        CodexModelVm? selectedModel = string.IsNullOrWhiteSpace(model)
            ? ResolveModel(models, ResolveConfiguredModel(manifest))
            : ResolveModelOrThrow(models, model);
        string? selectedModelId = string.IsNullOrWhiteSpace(model)
            ? null
            : selectedModel?.Id ?? model.Trim();

        CodexReasoningEffort? selectedEffort = string.IsNullOrWhiteSpace(reasoningEffort)
            ? ResolveReasoningEffort(manifest.ReasoningEffort ?? _codexOptions.Context.ReasoningEffort)
            : ResolveReasoningEffortOrThrow(reasoningEffort);

        IReadOnlyList<CodexReasoningEffort> availableEfforts = ResolveAvailableEfforts(selectedModel);
        if (selectedEffort.HasValue && availableEfforts.Count > 0 && !availableEfforts.Contains(selectedEffort.Value))
        {
            if (string.IsNullOrWhiteSpace(reasoningEffort))
            {
                selectedEffort = selectedModel?.DefaultReasoningEffort is { } defaultEffort && defaultEffort is not CodexReasoningEffort.None
                    ? defaultEffort
                    : availableEfforts[0];
            }
            else
            {
                throw new ArgumentException($"Thinking effort '{reasoningEffort}' is not available for model '{selectedModel?.Id ?? selectedModelId ?? ResolveConfiguredModel(manifest) ?? "current"}'. Available: {string.Join(", ", availableEfforts)}.");
            }
        }

        CodexThreadManifestRecord updatedManifest = await _manifestStore.SetContextAsync(
            sessionId,
            new CodexThreadContextSubmission
            {
                Model = selectedModelId,
                ReasoningEffort = selectedEffort?.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return BuildModelSettings(session, updatedManifest, models);
    }

    public async Task<CodexThreadGoalVm?> GetGoalAsync(string sessionId, CancellationToken cancellationToken)
    {
        await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await _gateway.GetThreadGoalAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexThreadGoalVm> SetGoalAsync(
        string sessionId,
        string objective,
        long? tokenBudget,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(objective))
        {
            throw new ArgumentException("Goal objective cannot be empty.", nameof(objective));
        }

        await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await _gateway.SetThreadGoalAsync(sessionId, objective.Trim(), tokenBudget, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexThreadGoalVm> SetGoalStatusAsync(
        string sessionId,
        CodexThreadGoalStatus status,
        CancellationToken cancellationToken)
    {
        await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await _gateway.SetThreadGoalStatusAsync(sessionId, status, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ClearGoalAsync(string sessionId, CancellationToken cancellationToken)
    {
        await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await _gateway.ClearThreadGoalAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> TailAsync(string sessionId, int lineCount, CancellationToken cancellationToken)
    {
        await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        lineCount = Math.Clamp(lineCount, 1, 200);

        CodexThreadManifestRecord? manifest = await _manifestStore.ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (manifest is not null && string.IsNullOrWhiteSpace(manifest.LastTurnId))
        {
            return "No transcript output is available for this session yet.";
        }

        CodexThreadDetailVm detail = await _gateway.GetThreadAsync(sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
        List<string> lines = [];

        foreach (CodexTurnVm turn in detail.Turns)
        {
            lines.Add($"turn {turn.Id} {turn.Status}");
            foreach (CodexTimelineEntryVm item in turn.Items.Where(item => !item.IsInternal))
            {
                string body = item.Body ?? item.Subtitle ?? string.Empty;
                lines.Add(string.IsNullOrWhiteSpace(body)
                    ? $"{item.Timestamp:u} {item.Title}"
                    : $"{item.Timestamp:u} {item.Title}: {body}");
            }

            if (!string.IsNullOrWhiteSpace(turn.FinalResponse))
            {
                AddWrappedLines(lines, "final: ", turn.FinalResponse);
            }

            if (!string.IsNullOrWhiteSpace(turn.ErrorMessage))
            {
                AddWrappedLines(lines, "error: ", turn.ErrorMessage);
            }
        }

        if (lines.Count == 0)
        {
            return "No transcript output is available for this session yet.";
        }

        return string.Join(Environment.NewLine, lines.TakeLast(lineCount));
    }

    public async Task StopAsync(string sessionId, CancellationToken cancellationToken)
    {
        await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await InterruptActiveTurnIfPresentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await _gateway.SetArchiveStateAsync(sessionId, archived: true, cancellationToken).ConfigureAwait(false);
        await _stateStore.RemoveQueuedPromptsForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task KillAsync(string sessionId, CancellationToken cancellationToken)
    {
        await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await InterruptActiveTurnIfPresentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await _gateway.SetArchiveStateAsync(sessionId, archived: true, cancellationToken).ConfigureAwait(false);
        await _stateStore.RemoveQueuedPromptsForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameAsync(string sessionId, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Session name cannot be empty.", nameof(name));
        }

        await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await _gateway.RenameThreadAsync(sessionId, name.Trim(), cancellationToken).ConfigureAwait(false);
    }

    public async Task ForgetAsync(string sessionId, CancellationToken cancellationToken)
    {
        CodexSessionSummary session = await RequireSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session.Status is CodexSessionStatus.Running or CodexSessionStatus.Starting)
        {
            throw new InvalidOperationException("Only stopped, exited, or failed sessions can be forgotten.");
        }

        await _stateStore.ForgetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await _stateStore.ClearActiveSessionForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CodexSessionSummary> RequireSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        CodexSessionSummary? session = await GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return session ?? throw new KeyNotFoundException($"Codex session '{sessionId}' was not found.");
    }

    private async Task InterruptActiveTurnIfPresentAsync(string sessionId, CancellationToken cancellationToken)
    {
        CodexActiveTurnStateVm? activeTurn = _turnCoordinator.TryGetActiveTurnState(sessionId);
        if (activeTurn is null)
        {
            return;
        }

        await _turnCoordinator.InterruptAsync(activeTurn.ThreadId, activeTurn.TurnId, cancellationToken).ConfigureAwait(false);
    }

    private static CodexSessionSummary ToSummary(CodexThreadListItemVm thread, CodexActiveTurnStateVm? activeTurn)
        => new(
            thread.Id,
            CodexTextFormatting.ResolveDisplayName(thread.Name, thread.Id),
            ResolveStatus(thread, activeTurn),
            thread.WorkingDirectory,
            thread.CreatedAt,
            activeTurn?.UpdatedAt ?? thread.UpdatedAt,
            null,
            null);

    private static CodexSessionSummary ToSummary(CodexThreadManifestRecord manifest, CodexActiveTurnStateVm? activeTurn)
    {
        DateTimeOffset createdAt = manifest.CreatedAt == default
            ? manifest.UpdatedAt
            : manifest.CreatedAt;
        DateTimeOffset updatedAt = manifest.UpdatedAt == default
            ? createdAt
            : manifest.UpdatedAt;

        return new(
            manifest.ThreadId,
            CodexTextFormatting.ResolveDisplayName(manifest.ThreadName, manifest.ThreadId),
            ResolveStatus(manifest, activeTurn),
            manifest.WorkingDirectory,
            createdAt,
            activeTurn?.UpdatedAt ?? updatedAt,
            null,
            null);
    }

    private static CodexSessionStatus ResolveStatus(CodexThreadListItemVm thread, CodexActiveTurnStateVm? activeTurn)
    {
        if (activeTurn is not null)
        {
            return CodexSessionStatus.Running;
        }

        if (thread.Archived)
        {
            return CodexSessionStatus.Stopped;
        }

        return thread.Status.ToLowerInvariant() switch
        {
            "starting" => CodexSessionStatus.Starting,
            "running" => CodexSessionStatus.Running,
            "failed" => CodexSessionStatus.Failed,
            "error" => CodexSessionStatus.Failed,
            "stopped" => CodexSessionStatus.Stopped,
            _ => CodexSessionStatus.Exited,
        };
    }

    private static CodexSessionStatus ResolveStatus(CodexThreadManifestRecord manifest, CodexActiveTurnStateVm? activeTurn)
    {
        if (activeTurn is not null)
        {
            return CodexSessionStatus.Running;
        }

        return manifest.IsArchived ? CodexSessionStatus.Stopped : CodexSessionStatus.Exited;
    }

    private static void AddWrappedLines(List<string> lines, string prefix, string value)
    {
        foreach (string line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            lines.Add(prefix + line);
            prefix = string.Empty;
        }
    }

    private string? ResolveConfiguredModel(CodexThreadManifestRecord manifest)
        => string.IsNullOrWhiteSpace(manifest.Model) ? _codexOptions.Context.Model : manifest.Model;

    private string? ResolveConfiguredEffort(CodexThreadManifestRecord manifest, CodexModelVm? selectedModel)
    {
        string? configured = string.IsNullOrWhiteSpace(manifest.ReasoningEffort)
            ? _codexOptions.Context.ReasoningEffort
            : manifest.ReasoningEffort;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return ResolveReasoningEffort(configured)?.ToString() ?? configured;
        }

        return selectedModel?.DefaultReasoningEffort is { } effort && effort is not CodexReasoningEffort.None
            ? effort.ToString()
            : null;
    }

    private static IReadOnlyList<CodexReasoningEffort> ResolveAvailableEfforts(CodexModelVm? model)
        => model?.SupportedEfforts.Count > 0
            ? model.SupportedEfforts.Where(effort => effort is not CodexReasoningEffort.None).Distinct().ToArray()
            : Enum.GetValues<CodexReasoningEffort>().Where(effort => effort is not CodexReasoningEffort.None).ToArray();

    private static CodexModelVm? ResolveModel(IReadOnlyList<CodexModelVm> models, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return models.FirstOrDefault(candidate => candidate.IsDefault) ?? models.FirstOrDefault();
        }

        string normalized = NormalizeModelToken(model);
        return models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, model, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.DisplayName, model, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeModelToken(candidate.Id), normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeModelToken(candidate.DisplayName), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static CodexModelVm? ResolveModelOrThrow(IReadOnlyList<CodexModelVm> models, string model)
    {
        CodexModelVm? resolved = ResolveModel(models, model);
        if (resolved is not null || models.Count == 0)
        {
            return resolved;
        }

        string choices = string.Join(", ", models.Select(candidate => candidate.Id).Take(12));
        throw new ArgumentException($"Model '{model}' was not found. Available examples: {choices}.");
    }

    private static string NormalizeModelToken(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int index = 0;
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[index++] = char.ToLowerInvariant(ch);
            }
        }

        return new string(buffer[..index]);
    }

    private static CodexReasoningEffort? ResolveReasoningEffort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = NormalizeModelToken(value);
        return normalized switch
        {
            "none" => CodexReasoningEffort.None,
            "minimal" or "min" => CodexReasoningEffort.Minimal,
            "low" => CodexReasoningEffort.Low,
            "medium" or "med" => CodexReasoningEffort.Medium,
            "high" => CodexReasoningEffort.High,
            "xhigh" or "extra" or "extrahigh" or "veryhigh" or "max" or "maximum" => CodexReasoningEffort.XHigh,
            _ => Enum.TryParse(value, ignoreCase: true, out CodexReasoningEffort parsed) ? parsed : null,
        };
    }

    private static CodexReasoningEffort ResolveReasoningEffortOrThrow(string value)
        => ResolveReasoningEffort(value)
            ?? throw new ArgumentException($"Thinking effort '{value}' was not recognized. Use minimal, low, medium, high, or xhigh.");

    private CodexSessionModelSettings BuildModelSettings(
        CodexSessionSummary session,
        CodexThreadManifestRecord manifest,
        IReadOnlyList<CodexModelVm> models)
    {
        string? model = ResolveConfiguredModel(manifest);
        CodexModelVm? selectedModel = ResolveModel(models, model);
        string? effort = ResolveConfiguredEffort(manifest, selectedModel);

        return new CodexSessionModelSettings(
            session.Id,
            session.Name,
            model,
            effort,
            models,
            ResolveAvailableEfforts(selectedModel));
    }
}
