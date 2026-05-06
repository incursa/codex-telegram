using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

internal interface ITelegramBotStateStore
{
    Task<string?> GetActiveSessionIdAsync(TelegramConversationScope conversation, CancellationToken cancellationToken);

    Task SetActiveSessionIdAsync(TelegramConversationScope conversation, string sessionId, CancellationToken cancellationToken);

    Task ClearActiveSessionAsync(TelegramConversationScope conversation, CancellationToken cancellationToken);

    Task<string?> GetActiveProjectWorkingDirectoryAsync(TelegramConversationScope conversation, CancellationToken cancellationToken);

    Task SetActiveProjectWorkingDirectoryAsync(TelegramConversationScope conversation, string workingDirectory, CancellationToken cancellationToken);

    Task ClearActiveProjectAsync(TelegramConversationScope conversation, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<TelegramConversationState>> ListConversationStatesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyCollection<TelegramConversationState>> ListConversationStatesForChatAsync(long chatId, CancellationToken cancellationToken);

    Task ClearActiveSessionForSessionAsync(string sessionId, CancellationToken cancellationToken);

    Task TrackSessionAsync(string sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<string>> GetTrackedSessionIdsAsync(CancellationToken cancellationToken);

    Task ForgetSessionAsync(string sessionId, CancellationToken cancellationToken);

    Task<bool> IsSessionForgottenAsync(string sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<string>> GetForgottenSessionIdsAsync(CancellationToken cancellationToken);

    Task EnqueueQueuedPromptAsync(TelegramQueuedPrompt prompt, CancellationToken cancellationToken);

    Task<IReadOnlyList<TelegramQueuedPrompt>> ListQueuedPromptsAsync(
        long? userId,
        TelegramConversationScope? conversation,
        CancellationToken cancellationToken);

    Task<TelegramQueuedPrompt?> TryGetQueuedPromptAsync(string promptId, CancellationToken cancellationToken);

    Task<TelegramQueuedPrompt?> TryRemoveQueuedPromptAsync(string promptId, long? ownerUserId, CancellationToken cancellationToken);

    Task<TelegramQueuedPrompt?> TryUpdateQueuedPromptTextAsync(string promptId, long? ownerUserId, string text, CancellationToken cancellationToken);

    Task<TelegramQueuedPrompt?> DequeueQueuedPromptAsync(CancellationToken cancellationToken);

    Task<TelegramQueuedPrompt?> DequeueNextQueuedPromptAsync(IReadOnlyCollection<string> unavailableSessionIds, CancellationToken cancellationToken);

    Task<TelegramQueuedPrompt?> DequeueNextQueuedPromptAsync(
        IReadOnlyCollection<string> unavailableSessionIds,
        IReadOnlyCollection<TelegramConversationScope> unavailableConversations,
        CancellationToken cancellationToken);

    Task RemoveQueuedPromptsForSessionAsync(string sessionId, CancellationToken cancellationToken);
}

internal sealed record TelegramConversationState(
    TelegramConversationScope Scope,
    string? ActiveSessionId,
    string? ActiveProjectWorkingDirectory,
    int QueuedPromptCount,
    DateTimeOffset? OldestQueuedPromptAt);

internal sealed class TelegramBotStateStore : ITelegramBotStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly IOptions<CodexTelegramOptions> _options;

    public TelegramBotStateStore(IOptions<CodexTelegramOptions> options)
    {
        _options = options;
    }

    public async Task<string?> GetActiveSessionIdAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
    {
        TelegramBotState state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.ActiveSessionsByScope.TryGetValue(conversation.ToStorageKey(), out string? sessionId)
            ? sessionId
            : null;
    }

    public Task SetActiveSessionIdAsync(TelegramConversationScope conversation, string sessionId, CancellationToken cancellationToken)
        => MutateAsync(state =>
        {
            state.ActiveSessionsByScope[conversation.ToStorageKey()] = sessionId;
            AddTrackedSession(state, sessionId);
            return state;
        }, cancellationToken);

    public Task ClearActiveSessionAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
        => MutateAsync(state =>
        {
            state.ActiveSessionsByScope.Remove(conversation.ToStorageKey());
            return state;
        }, cancellationToken);

    public async Task<string?> GetActiveProjectWorkingDirectoryAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
    {
        TelegramBotState state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.ActiveProjectsByScope.TryGetValue(conversation.ToStorageKey(), out string? workingDirectory)
            ? workingDirectory
            : null;
    }

    public Task SetActiveProjectWorkingDirectoryAsync(TelegramConversationScope conversation, string workingDirectory, CancellationToken cancellationToken)
        => MutateAsync(state =>
        {
            state.ActiveProjectsByScope[conversation.ToStorageKey()] = Path.GetFullPath(workingDirectory);
            return state;
        }, cancellationToken);

    public Task ClearActiveProjectAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
        => MutateAsync(state =>
        {
            state.ActiveProjectsByScope.Remove(conversation.ToStorageKey());
            return state;
        }, cancellationToken);

    public async Task<IReadOnlyCollection<TelegramConversationState>> ListConversationStatesAsync(CancellationToken cancellationToken)
    {
        TelegramBotState state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return BuildConversationStates(state);
    }

    public async Task<IReadOnlyCollection<TelegramConversationState>> ListConversationStatesForChatAsync(long chatId, CancellationToken cancellationToken)
    {
        TelegramBotState state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return BuildConversationStates(state, chatId);
    }

    public Task ClearActiveSessionForSessionAsync(string sessionId, CancellationToken cancellationToken)
        => MutateAsync(state =>
        {
            List<string> usersToClear = state.ActiveSessionsByScope
                .Where(pair => string.Equals(pair.Value, sessionId, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();

            foreach (string userId in usersToClear)
            {
                state.ActiveSessionsByScope.Remove(userId);
            }

            return state;
        }, cancellationToken);

    public Task TrackSessionAsync(string sessionId, CancellationToken cancellationToken)
        => MutateAsync(state =>
        {
            AddTrackedSession(state, sessionId);
            return state;
        }, cancellationToken);

    public async Task<IReadOnlyCollection<string>> GetTrackedSessionIdsAsync(CancellationToken cancellationToken)
    {
        TelegramBotState state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.TrackedSessionIds.ToArray();
    }

    public Task ForgetSessionAsync(string sessionId, CancellationToken cancellationToken)
        => MutateAsync(state =>
        {
            if (!state.ForgottenSessionIds.Contains(sessionId, StringComparer.OrdinalIgnoreCase))
            {
                state.ForgottenSessionIds.Add(sessionId);
            }

            state.TrackedSessionIds.RemoveAll(id => string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase));
            return state;
        }, cancellationToken);

    public async Task<bool> IsSessionForgottenAsync(string sessionId, CancellationToken cancellationToken)
    {
        TelegramBotState state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.ForgottenSessionIds.Contains(sessionId, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyCollection<string>> GetForgottenSessionIdsAsync(CancellationToken cancellationToken)
    {
        TelegramBotState state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.ForgottenSessionIds.ToArray();
    }

    public Task EnqueueQueuedPromptAsync(TelegramQueuedPrompt prompt, CancellationToken cancellationToken)
        => MutateAsync(state =>
        {
            state.QueuedPrompts.Add(prompt);
            return state;
        }, cancellationToken);

    public async Task<IReadOnlyList<TelegramQueuedPrompt>> ListQueuedPromptsAsync(
        long? userId,
        TelegramConversationScope? conversation,
        CancellationToken cancellationToken)
    {
        TelegramBotState state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.QueuedPrompts
            .Where(prompt => IsQueuedPromptMatch(prompt, userId, conversation))
            .OrderBy(prompt => prompt.EnqueuedAt)
            .ThenBy(prompt => prompt.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<TelegramQueuedPrompt?> TryGetQueuedPromptAsync(string promptId, CancellationToken cancellationToken)
    {
        TelegramBotState state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.QueuedPrompts.FirstOrDefault(prompt => IsPromptIdMatch(prompt, promptId));
    }

    public async Task<TelegramQueuedPrompt?> TryRemoveQueuedPromptAsync(string promptId, long? ownerUserId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TelegramBotState state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            TelegramQueuedPrompt? prompt = state.QueuedPrompts.FirstOrDefault(item => IsOwnedPromptIdMatch(item, promptId, ownerUserId));
            if (prompt is null)
            {
                return null;
            }

            state.QueuedPrompts.RemoveAll(item => IsPromptIdMatch(item, promptId));
            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return prompt;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TelegramQueuedPrompt?> TryUpdateQueuedPromptTextAsync(
        string promptId,
        long? ownerUserId,
        string text,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TelegramBotState state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            int index = state.QueuedPrompts.FindIndex(prompt => IsOwnedPromptIdMatch(prompt, promptId, ownerUserId));
            if (index < 0)
            {
                return null;
            }

            TelegramQueuedPrompt updated = state.QueuedPrompts[index] with { Text = text };
            state.QueuedPrompts[index] = updated;
            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TelegramQueuedPrompt?> DequeueQueuedPromptAsync(CancellationToken cancellationToken)
        => await DequeueNextQueuedPromptAsync([], cancellationToken).ConfigureAwait(false);

    public async Task<TelegramQueuedPrompt?> DequeueNextQueuedPromptAsync(
        IReadOnlyCollection<string> unavailableSessionIds,
        CancellationToken cancellationToken)
        => await DequeueNextQueuedPromptAsync(unavailableSessionIds, [], cancellationToken).ConfigureAwait(false);

    public async Task<TelegramQueuedPrompt?> DequeueNextQueuedPromptAsync(
        IReadOnlyCollection<string> unavailableSessionIds,
        IReadOnlyCollection<TelegramConversationScope> unavailableConversations,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HashSet<string> unavailable = new(unavailableSessionIds, StringComparer.OrdinalIgnoreCase);
            HashSet<string> unavailableConversationKeys = new(
                unavailableConversations.Select(conversation => conversation.ToStorageKey()),
                StringComparer.OrdinalIgnoreCase);
            TelegramBotState state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            TelegramQueuedPrompt? prompt = state.QueuedPrompts
                .OrderBy(item => item.EnqueuedAt)
                .Where(item => !unavailable.Contains(item.SessionId))
                .Where(item => !unavailableConversationKeys.Contains(item.ConversationScope.ToStorageKey()))
                .FirstOrDefault();

            if (prompt is null)
            {
                return null;
            }

            state.QueuedPrompts.RemoveAll(item => string.Equals(item.Id, prompt.Id, StringComparison.OrdinalIgnoreCase));
            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return prompt;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RemoveQueuedPromptsForSessionAsync(string sessionId, CancellationToken cancellationToken)
        => MutateAsync(state =>
        {
            state.QueuedPrompts.RemoveAll(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            return state;
        }, cancellationToken);

    private async Task MutateAsync(Func<TelegramBotState, TelegramBotState> updater, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TelegramBotState state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            state = updater(state);
            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TelegramBotState> LoadStateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TelegramBotState> LoadStateCoreAsync(CancellationToken cancellationToken)
    {
        string statePath = GetStatePath();
        if (!File.Exists(statePath))
        {
            return new TelegramBotState();
        }

        await using FileStream stream = File.OpenRead(statePath);
        TelegramBotState? state = await JsonSerializer.DeserializeAsync<TelegramBotState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        return state ?? new TelegramBotState();
    }

    private async Task SaveStateCoreAsync(TelegramBotState state, CancellationToken cancellationToken)
    {
        string statePath = GetStatePath();
        string? directory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = Path.Combine(directory ?? Path.GetTempPath(), $"{Guid.NewGuid():N}.json.tmp");
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, statePath, overwrite: true);
    }

    private string GetStatePath()
        => Path.Combine(GetDataRoot(), "telegram-state.json");

    private IReadOnlyCollection<TelegramConversationState> BuildConversationStates(TelegramBotState state, long? chatId = null)
    {
        Dictionary<TelegramConversationScope, ConversationStateBuilder> builders = new();

        foreach (KeyValuePair<string, string> pair in state.ActiveSessionsByScope)
        {
            if (TryParseScopeForChat(pair.Key, chatId, out TelegramConversationScope scope))
            {
                GetBuilder(scope).ActiveSessionId = pair.Value;
            }
        }

        foreach (KeyValuePair<string, string> pair in state.ActiveProjectsByScope)
        {
            if (TryParseScopeForChat(pair.Key, chatId, out TelegramConversationScope scope))
            {
                GetBuilder(scope).ActiveProjectWorkingDirectory = pair.Value;
            }
        }

        IEnumerable<TelegramQueuedPrompt> queuedPrompts = chatId.HasValue
            ? state.QueuedPrompts.Where(prompt => prompt.ChatId == chatId.Value)
            : state.QueuedPrompts;

        foreach (IGrouping<TelegramConversationScope, TelegramQueuedPrompt> group in queuedPrompts.GroupBy(prompt => prompt.ConversationScope))
        {
            ConversationStateBuilder builder = GetBuilder(group.Key);
            TelegramQueuedPrompt[] prompts = group.ToArray();
            builder.QueuedPromptCount = prompts.Length;
            builder.OldestQueuedPromptAt = prompts.Min(prompt => prompt.EnqueuedAt);
        }

        return builders.Values
            .Select(builder => builder.ToState())
            .OrderBy(conversationState => conversationState.Scope.MessageThreadId.HasValue ? 1 : 0)
            .ThenBy(conversationState => conversationState.Scope.MessageThreadId ?? 0)
            .ToArray();

        ConversationStateBuilder GetBuilder(TelegramConversationScope scope)
        {
            if (!builders.TryGetValue(scope, out ConversationStateBuilder? builder))
            {
                builder = new ConversationStateBuilder(scope);
                builders[scope] = builder;
            }

            return builder;
        }
    }

    private string GetDataRoot()
    {
        string? configuredRoot = _options.Value.Workspace.DataRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        return Path.Combine(AppContext.BaseDirectory, "App_Data", "codex-telegram");
    }

    private static void AddTrackedSession(TelegramBotState state, string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && !state.TrackedSessionIds.Contains(sessionId, StringComparer.OrdinalIgnoreCase))
        {
            state.TrackedSessionIds.Add(sessionId);
        }
    }

    private static bool IsQueuedPromptMatch(TelegramQueuedPrompt prompt, long? userId, TelegramConversationScope? conversation)
        => (!userId.HasValue || prompt.UserId == userId.Value)
            && (!conversation.HasValue || prompt.ConversationScope == conversation.Value);

    private static bool IsOwnedPromptIdMatch(TelegramQueuedPrompt prompt, string promptId, long? ownerUserId)
        => IsPromptIdMatch(prompt, promptId)
            && (!ownerUserId.HasValue || prompt.UserId == ownerUserId.Value);

    private static bool IsPromptIdMatch(TelegramQueuedPrompt prompt, string promptId)
        => prompt.Id.Equals(promptId, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseScopeForChat(string key, long? chatId, out TelegramConversationScope scope)
    {
        if (!TelegramConversationScope.TryParseStorageKey(key, out scope))
        {
            return false;
        }

        return !chatId.HasValue || scope.ChatId == chatId.Value;
    }

    private sealed class ConversationStateBuilder(TelegramConversationScope scope)
    {
        public TelegramConversationScope Scope { get; } = scope;

        public string? ActiveSessionId { get; set; }

        public string? ActiveProjectWorkingDirectory { get; set; }

        public int QueuedPromptCount { get; set; }

        public DateTimeOffset? OldestQueuedPromptAt { get; set; }

        public TelegramConversationState ToState()
            => new(Scope, ActiveSessionId, ActiveProjectWorkingDirectory, QueuedPromptCount, OldestQueuedPromptAt);
    }

    private sealed class TelegramBotState
    {
        [JsonPropertyName("ActiveSessionsByUserId")]
        public Dictionary<string, string> ActiveSessionsByScope { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("ActiveProjectsByUserId")]
        public Dictionary<string, string> ActiveProjectsByScope { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> TrackedSessionIds { get; set; } = [];

        public List<string> ForgottenSessionIds { get; set; } = [];

        public List<TelegramQueuedPrompt> QueuedPrompts { get; set; } = [];
    }
}
