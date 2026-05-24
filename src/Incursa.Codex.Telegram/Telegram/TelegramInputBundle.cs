using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Desired dispatch path for a captured Telegram input bundle.
/// </summary>
internal enum TelegramInputBundleIntent
{
    SendNow,
    QueueNext,
    SteerCurrentTurn,
    Prompt = SendNow,
    Queue = QueueNext,
    Steer = SteerCurrentTurn,
    PlanAnswer,
}

/// <summary>
/// Durable lifecycle state for a Telegram input bundle.
/// </summary>
internal enum TelegramInputBundleStatus
{
    Capturing,
    Submitted,
    Queued,
    Steered,
    Sent,
    Cancelled,
    Expired,
}

/// <summary>
/// Text captured from one Telegram source message.
/// </summary>
internal sealed record TelegramInputTextPart(
    string Text,
    string Source,
    int? SourceMessageId,
    DateTimeOffset CapturedAt);

/// <summary>
/// Captured Telegram input waiting for an explicit dispatch action.
/// </summary>
internal sealed record TelegramInputBundle
{
    public string Id { get; init; } = string.Empty;

    public long UserId { get; init; }

    public TelegramConversationScope Conversation { get; init; }

    public TelegramConversationScope ConversationScope => Conversation;

    public string SessionId { get; init; } = string.Empty;

    public string SessionName { get; init; } = string.Empty;

    public TelegramInputBundleIntent Intent { get; init; } = TelegramInputBundleIntent.SendNow;

    public TelegramInputBundleStatus Status { get; init; } = TelegramInputBundleStatus.Capturing;

    public List<TelegramInputTextPart> TextParts { get; init; } = [];

    public List<TelegramAttachmentDescriptor> Attachments { get; init; } = [];

    public List<int> SourceMessageIds { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public int? StatusMessageId { get; init; }

    [JsonIgnore]
    public int? StatusCardMessageId => StatusMessageId;

    public string TraceId { get; init; } = string.Empty;

    [JsonIgnore]
    public bool HasContent
        => TextParts.Any(part => !string.IsNullOrWhiteSpace(part.Text)) || Attachments.Count > 0;

    [JsonIgnore]
    public string CombinedText
        => string.Join(
            Environment.NewLine + Environment.NewLine,
            TextParts
                .Select(part => part.Text.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
}

internal interface ITelegramInputBundleStore
{
    Task<TelegramInputBundle> CreateAsync(
        TelegramConversationScope conversation,
        long userId,
        CancellationToken cancellationToken);

    Task<TelegramInputBundle> GetOrCreateActiveAsync(
        TelegramConversationScope conversation,
        long userId,
        CancellationToken cancellationToken);

    Task<TelegramInputBundle?> TryGetOpenBundleAsync(
        TelegramConversationScope conversation,
        long userId,
        CancellationToken cancellationToken);

    Task<TelegramInputBundle?> TryGetAsync(string bundleId, CancellationToken cancellationToken);

    Task<TelegramInputBundle?> TryGetBundleAsync(
        string bundleId,
        long? ownerUserId,
        CancellationToken cancellationToken);

    Task<TelegramInputBundle> AppendAsync(
        TelegramInputBundleAppendRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TelegramInputBundle>> ListAsync(
        TelegramConversationScope? conversation,
        CancellationToken cancellationToken);

    Task<TelegramInputBundle?> TryAppendAsync(
        string bundleId,
        long ownerUserId,
        string? text,
        IReadOnlyList<TelegramAttachmentDescriptor>? attachments,
        IReadOnlyList<int>? sourceMessageIds,
        CancellationToken cancellationToken);

    Task<TelegramInputBundle?> TryUpdateBundleAsync(
        string bundleId,
        long ownerUserId,
        Func<TelegramInputBundle, TelegramInputBundle> updater,
        CancellationToken cancellationToken);

    Task<TelegramInputBundle?> TryUpdateBundleAsync(
        string bundleId,
        long ownerUserId,
        TelegramInputBundleUpdate update,
        CancellationToken cancellationToken);

    Task<TelegramInputBundle?> TryCompleteBundleAsync(
        string bundleId,
        long ownerUserId,
        TelegramInputBundleStatus status,
        bool deleteAttachments,
        CancellationToken cancellationToken);

    Task<TelegramInputBundle?> TryCompleteBundleAsync(
        string bundleId,
        long ownerUserId,
        TelegramInputBundleIntent intent,
        TelegramInputBundleStatus status,
        bool deleteAttachments,
        CancellationToken cancellationToken);

    Task<TelegramInputBundle?> TrySetIntentAsync(
        string bundleId,
        long ownerUserId,
        TelegramInputBundleIntent intent,
        CancellationToken cancellationToken);

    Task<TelegramInputBundle?> TrySetStatusCardMessageIdAsync(
        string bundleId,
        long ownerUserId,
        int? messageId,
        CancellationToken cancellationToken);

    Task<TelegramInputBundle?> TryClearAsync(
        string bundleId,
        long ownerUserId,
        CancellationToken cancellationToken);

    Task<TelegramInputBundle?> TryCancelAsync(
        string bundleId,
        long ownerUserId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TelegramInputBundle>> ExpireAsync(CancellationToken cancellationToken);
}

internal sealed record TelegramInputBundleAppendRequest(
    TelegramConversationScope ConversationScope,
    long UserId,
    string SessionId,
    string SessionName,
    TelegramInputBundleIntent Intent,
    string? Text,
    string TextSource,
    IReadOnlyList<TelegramAttachmentDescriptor>? Attachments,
    int? SourceMessageId,
    string TraceId,
    IReadOnlyList<int>? SourceMessageIds = null);

internal sealed record TelegramInputBundleUpdate(
    string? SessionId = null,
    string? SessionName = null,
    TelegramInputBundleIntent? Intent = null,
    int? StatusMessageId = null);

internal sealed class TelegramInputBundleStore : ITelegramInputBundleStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly IOptions<CodexTelegramOptions> _options;
    private readonly IOptions<TelegramInputOptions> _inputOptions;
    private readonly TimeProvider _timeProvider;

    public TelegramInputBundleStore(
        IOptions<CodexTelegramOptions> options,
        IOptions<TelegramInputOptions> inputOptions,
        TimeProvider timeProvider)
    {
        _options = options;
        _inputOptions = inputOptions;
        _timeProvider = timeProvider;
    }

    public async Task<TelegramInputBundle> CreateAsync(
        TelegramConversationScope conversation,
        long userId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TelegramInputBundleState state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            ExpireDueBundles(state, now);

            TelegramInputBundle bundle = CreateBundle(conversation, userId, now);
            state.Bundles.Add(bundle);
            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return bundle;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TelegramInputBundle> GetOrCreateActiveAsync(
        TelegramConversationScope conversation,
        long userId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TelegramInputBundleState state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            bool expired = ExpireDueBundles(state, now).Count > 0;

            TelegramInputBundle? active = state.Bundles.LastOrDefault(bundle =>
                bundle.UserId == userId
                && bundle.Conversation == conversation
                && IsActive(bundle.Status));

            if (active is not null)
            {
                if (expired)
                {
                    await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
                }

                return active;
            }

            TelegramInputBundle bundle = CreateBundle(conversation, userId, now);
            state.Bundles.Add(bundle);
            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return bundle;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TelegramInputBundle?> TryGetOpenBundleAsync(
        TelegramConversationScope conversation,
        long userId,
        CancellationToken cancellationToken)
    {
        TelegramInputBundleState state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.Bundles.LastOrDefault(bundle =>
            bundle.UserId == userId
            && bundle.Conversation == conversation
            && IsActive(bundle.Status)
            && (bundle.ExpiresAt is null || bundle.ExpiresAt > _timeProvider.GetUtcNow()));
    }

    public async Task<TelegramInputBundle?> TryGetAsync(string bundleId, CancellationToken cancellationToken)
    {
        TelegramInputBundleState state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.Bundles.FirstOrDefault(bundle => IsBundleIdMatch(bundle, bundleId));
    }

    public async Task<TelegramInputBundle?> TryGetBundleAsync(
        string bundleId,
        long? ownerUserId,
        CancellationToken cancellationToken)
    {
        TelegramInputBundleState state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.Bundles.FirstOrDefault(bundle =>
            IsBundleIdMatch(bundle, bundleId)
            && (!ownerUserId.HasValue || bundle.UserId == ownerUserId.Value));
    }

    public async Task<IReadOnlyList<TelegramInputBundle>> ListAsync(
        TelegramConversationScope? conversation,
        CancellationToken cancellationToken)
    {
        TelegramInputBundleState state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.Bundles
            .Where(bundle => !conversation.HasValue || bundle.Conversation == conversation.Value)
            .OrderByDescending(bundle => bundle.UpdatedAt)
            .ThenBy(bundle => bundle.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<TelegramInputBundle?> TryAppendAsync(
        string bundleId,
        long ownerUserId,
        string? text,
        IReadOnlyList<TelegramAttachmentDescriptor>? attachments,
        IReadOnlyList<int>? sourceMessageIds,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TelegramInputBundleState state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            ExpireDueBundles(state, now);

            int index = FindOwnedBundleIndex(state, bundleId, ownerUserId);
            if (index < 0 || !IsActive(state.Bundles[index].Status))
            {
                await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
                return null;
            }

            TelegramInputBundle bundle = state.Bundles[index];
            List<int> sourceIds = AddSourceMessageIds(bundle.SourceMessageIds, sourceMessageIds);
            List<TelegramInputTextPart> textParts = [.. bundle.TextParts];
            int? textSourceMessageId = sourceMessageIds?.FirstOrDefault(id => id > 0);
            if (!string.IsNullOrWhiteSpace(text))
            {
                textParts.Add(new TelegramInputTextPart(text.Trim(), "text", textSourceMessageId, now));
            }

            List<TelegramAttachmentDescriptor> updatedAttachments = [.. bundle.Attachments];
            if (attachments is { Count: > 0 })
            {
                updatedAttachments.AddRange(attachments);
            }

            TelegramInputBundle updated = bundle with
            {
                TextParts = textParts,
                Attachments = updatedAttachments,
                SourceMessageIds = sourceIds,
                UpdatedAt = now,
                ExpiresAt = now.Add(GetBundleExpiration()),
            };

            state.Bundles[index] = updated;
            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TelegramInputBundle> AppendAsync(
        TelegramInputBundleAppendRequest request,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TelegramInputBundleState state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            ExpireDueBundles(state, now);

            int index = state.Bundles.FindIndex(bundle =>
                bundle.UserId == request.UserId
                && bundle.Conversation == request.ConversationScope
                && IsActive(bundle.Status));
            TelegramInputBundle bundle = index >= 0
                ? state.Bundles[index]
                : CreateBundle(request.ConversationScope, request.UserId, now);

            List<int> sourceIds = AddSourceMessageIds(
                bundle.SourceMessageIds,
                request.SourceMessageIds ?? (request.SourceMessageId.HasValue ? [request.SourceMessageId.Value] : null));
            List<TelegramInputTextPart> textParts = [.. bundle.TextParts];
            if (!string.IsNullOrWhiteSpace(request.Text))
            {
                textParts.Add(new TelegramInputTextPart(request.Text.Trim(), request.TextSource, request.SourceMessageId, now));
            }

            List<TelegramAttachmentDescriptor> attachments = [.. bundle.Attachments];
            if (request.Attachments is { Count: > 0 })
            {
                attachments.AddRange(request.Attachments);
            }

            bool initializeTarget = index < 0 || string.IsNullOrWhiteSpace(bundle.SessionId);
            TelegramInputBundle updated = bundle with
            {
                SessionId = initializeTarget ? request.SessionId : bundle.SessionId,
                SessionName = initializeTarget || string.IsNullOrWhiteSpace(bundle.SessionName) ? request.SessionName : bundle.SessionName,
                Intent = initializeTarget ? request.Intent : bundle.Intent,
                TextParts = textParts,
                Attachments = attachments,
                SourceMessageIds = sourceIds,
                UpdatedAt = now,
                ExpiresAt = now.Add(GetBundleExpiration()),
                TraceId = string.IsNullOrWhiteSpace(bundle.TraceId) ? request.TraceId : bundle.TraceId,
            };

            if (index >= 0)
            {
                state.Bundles[index] = updated;
            }
            else
            {
                state.Bundles.Add(updated);
            }

            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<TelegramInputBundle?> TryUpdateBundleAsync(
        string bundleId,
        long ownerUserId,
        Func<TelegramInputBundle, TelegramInputBundle> updater,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        return UpdateOwnedAsync(bundleId, ownerUserId, updater, cancellationToken);
    }

    public Task<TelegramInputBundle?> TryUpdateBundleAsync(
        string bundleId,
        long ownerUserId,
        TelegramInputBundleUpdate update,
        CancellationToken cancellationToken)
        => UpdateOwnedAsync(bundleId, ownerUserId, bundle => bundle with
        {
            SessionId = string.IsNullOrWhiteSpace(update.SessionId) ? bundle.SessionId : update.SessionId,
            SessionName = string.IsNullOrWhiteSpace(update.SessionName) ? bundle.SessionName : update.SessionName,
            Intent = update.Intent ?? bundle.Intent,
            StatusMessageId = update.StatusMessageId ?? bundle.StatusMessageId,
            UpdatedAt = _timeProvider.GetUtcNow(),
        }, cancellationToken);

    public Task<TelegramInputBundle?> TryCompleteBundleAsync(
        string bundleId,
        long ownerUserId,
        TelegramInputBundleStatus status,
        bool deleteAttachments,
        CancellationToken cancellationToken)
        => UpdateOwnedAsync(bundleId, ownerUserId, bundle => CompleteBundle(bundle, bundle.Intent, status, deleteAttachments), cancellationToken);

    public Task<TelegramInputBundle?> TryCompleteBundleAsync(
        string bundleId,
        long ownerUserId,
        TelegramInputBundleIntent intent,
        TelegramInputBundleStatus status,
        bool deleteAttachments,
        CancellationToken cancellationToken)
        => UpdateOwnedAsync(bundleId, ownerUserId, bundle => CompleteBundle(bundle, intent, status, deleteAttachments), cancellationToken);

    public Task<TelegramInputBundle?> TrySetIntentAsync(
        string bundleId,
        long ownerUserId,
        TelegramInputBundleIntent intent,
        CancellationToken cancellationToken)
        => UpdateOwnedAsync(bundleId, ownerUserId, bundle => bundle with
        {
            Intent = intent,
            UpdatedAt = _timeProvider.GetUtcNow(),
        }, cancellationToken);

    public Task<TelegramInputBundle?> TrySetStatusCardMessageIdAsync(
        string bundleId,
        long ownerUserId,
        int? messageId,
        CancellationToken cancellationToken)
        => UpdateOwnedAsync(bundleId, ownerUserId, bundle => bundle with
        {
            StatusMessageId = messageId,
        }, cancellationToken);

    public Task<TelegramInputBundle?> TryClearAsync(
        string bundleId,
        long ownerUserId,
        CancellationToken cancellationToken)
        => UpdateOwnedAsync(bundleId, ownerUserId, bundle =>
        {
            DeleteAttachments(bundle.Attachments);
            return bundle with
            {
                TextParts = [],
                Attachments = [],
                SourceMessageIds = [],
                UpdatedAt = _timeProvider.GetUtcNow(),
            };
        }, cancellationToken);

    public Task<TelegramInputBundle?> TryCancelAsync(
        string bundleId,
        long ownerUserId,
        CancellationToken cancellationToken)
        => UpdateOwnedAsync(bundleId, ownerUserId, bundle =>
        {
            DeleteAttachments(bundle.Attachments);
            return bundle with
            {
                Status = TelegramInputBundleStatus.Cancelled,
                Attachments = [],
                UpdatedAt = _timeProvider.GetUtcNow(),
            };
        }, cancellationToken);

    public async Task<IReadOnlyList<TelegramInputBundle>> ExpireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TelegramInputBundleState state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<TelegramInputBundle> expired = ExpireDueBundles(state, _timeProvider.GetUtcNow());
            if (expired.Count > 0)
            {
                await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            }

            return expired;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TelegramInputBundle?> UpdateOwnedAsync(
        string bundleId,
        long ownerUserId,
        Func<TelegramInputBundle, TelegramInputBundle> updater,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TelegramInputBundleState state = await LoadStateCoreAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            bool expired = ExpireDueBundles(state, now).Count > 0;

            int index = FindOwnedBundleIndex(state, bundleId, ownerUserId);
            if (index < 0)
            {
                if (expired)
                {
                    await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
                }

                return null;
            }

            TelegramInputBundle bundle = state.Bundles[index];
            if (!IsActive(bundle.Status))
            {
                if (expired)
                {
                    await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
                }

                return null;
            }

            TelegramInputBundle updated = updater(bundle);
            state.Bundles[index] = updated;
            await SaveStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TelegramInputBundleState> LoadStateAsync(CancellationToken cancellationToken)
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

    private async Task<TelegramInputBundleState> LoadStateCoreAsync(CancellationToken cancellationToken)
    {
        string statePath = GetStatePath();
        if (!File.Exists(statePath))
        {
            return new TelegramInputBundleState();
        }

        await using FileStream stream = File.OpenRead(statePath);
        TelegramInputBundleState? state = await JsonSerializer.DeserializeAsync<TelegramInputBundleState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        return state ?? new TelegramInputBundleState();
    }

    private async Task SaveStateCoreAsync(TelegramInputBundleState state, CancellationToken cancellationToken)
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
        => Path.Combine(GetDataRoot(), "input-bundles.json");

    private string GetDataRoot()
    {
        string? configuredRoot = _options.Value.Workspace.DataRoot;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        return Path.Combine(AppContext.BaseDirectory, "App_Data", "codex-telegram");
    }

    private TelegramInputBundle CreateBundle(
        TelegramConversationScope conversation,
        long userId,
        DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Conversation = conversation,
            Intent = TelegramInputBundleIntent.Prompt,
            Status = TelegramInputBundleStatus.Capturing,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.Add(GetBundleExpiration()),
        };

    private TimeSpan GetBundleExpiration()
    {
        int minutes = Math.Clamp(
            _inputOptions.Value.BundleExpirationMinutes,
            TelegramInputLimits.MinBundleExpirationMinutes,
            TelegramInputLimits.MaxBundleExpirationMinutes);
        return TimeSpan.FromMinutes(minutes);
    }

    private static IReadOnlyList<TelegramInputBundle> ExpireDueBundles(TelegramInputBundleState state, DateTimeOffset now)
    {
        List<TelegramInputBundle> expired = [];
        for (int index = 0; index < state.Bundles.Count; index++)
        {
            TelegramInputBundle bundle = state.Bundles[index];
            if (!IsActive(bundle.Status) || bundle.ExpiresAt is null || bundle.ExpiresAt > now)
            {
                continue;
            }

            DeleteAttachments(bundle.Attachments);
            TelegramInputBundle updated = bundle with
            {
                Status = TelegramInputBundleStatus.Expired,
                Attachments = [],
                UpdatedAt = now,
            };

            state.Bundles[index] = updated;
            expired.Add(updated);
        }

        return expired;
    }

    private static int FindOwnedBundleIndex(TelegramInputBundleState state, string bundleId, long ownerUserId)
        => state.Bundles.FindIndex(bundle => IsBundleIdMatch(bundle, bundleId) && bundle.UserId == ownerUserId);

    private static bool IsBundleIdMatch(TelegramInputBundle bundle, string bundleId)
        => bundle.Id.Equals(bundleId, StringComparison.OrdinalIgnoreCase);

    private static bool IsActive(TelegramInputBundleStatus status)
        => status is TelegramInputBundleStatus.Capturing;

    private static bool ShouldAlwaysDeleteAttachments(TelegramInputBundleStatus status)
        => status is TelegramInputBundleStatus.Cancelled or TelegramInputBundleStatus.Expired;

    private TelegramInputBundle CompleteBundle(
        TelegramInputBundle bundle,
        TelegramInputBundleIntent intent,
        TelegramInputBundleStatus status,
        bool deleteAttachments)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<TelegramAttachmentDescriptor> attachments = bundle.Attachments;
        if (deleteAttachments || ShouldAlwaysDeleteAttachments(status))
        {
            DeleteAttachments(attachments);
            attachments = [];
        }

        return bundle with
        {
            Intent = intent,
            Status = status,
            Attachments = attachments,
            UpdatedAt = now,
        };
    }

    private static List<int> AddSourceMessageIds(
        IReadOnlyCollection<int> existing,
        IReadOnlyCollection<int>? additional)
    {
        List<int> sourceIds = existing.ToList();
        foreach (int sourceMessageId in additional ?? [])
        {
            if (sourceMessageId > 0 && !sourceIds.Contains(sourceMessageId))
            {
                sourceIds.Add(sourceMessageId);
            }
        }

        return sourceIds;
    }

    private static void DeleteAttachments(IReadOnlyList<TelegramAttachmentDescriptor> attachments)
    {
        foreach (TelegramAttachmentDescriptor attachment in attachments)
        {
            try
            {
                File.Delete(attachment.FilePath);
            }
            catch
            {
            }
        }
    }

    private sealed class TelegramInputBundleState
    {
        public List<TelegramInputBundle> Bundles { get; set; } = [];
    }
}
