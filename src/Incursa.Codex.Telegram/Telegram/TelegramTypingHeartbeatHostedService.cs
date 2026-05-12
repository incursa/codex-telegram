using System.Collections.Concurrent;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

internal interface ITelegramTypingIndicatorRegistry
{
    long ChangeVersion { get; }

    IDisposable Track(TelegramConversationScope conversation);

    IReadOnlyCollection<TelegramConversationScope> GetTargets();

    Task WaitForChangeAsync(TimeSpan timeout, long observedVersion, CancellationToken cancellationToken);
}

internal sealed class TelegramTypingIndicatorRegistry : ITelegramTypingIndicatorRegistry
{
    private readonly ConcurrentDictionary<TelegramConversationScope, int> _targetCounts = new();
    private readonly object _wakeLock = new();
    private long _changeVersion;
    private TaskCompletionSource<bool> _wakeSignal = CreateWakeSignal();

    public long ChangeVersion => Volatile.Read(ref _changeVersion);

    public IDisposable Track(TelegramConversationScope conversation)
    {
        _targetCounts.AddOrUpdate(conversation, 1, static (_, count) => count + 1);
        SignalChanged();
        return new Registration(this, conversation);
    }

    public IReadOnlyCollection<TelegramConversationScope> GetTargets()
        => _targetCounts.Keys.ToArray();

    public async Task WaitForChangeAsync(TimeSpan timeout, long observedVersion, CancellationToken cancellationToken)
    {
        Task wakeTask;
        lock (_wakeLock)
        {
            if (_changeVersion != observedVersion)
            {
                return;
            }

            wakeTask = _wakeSignal.Task;
        }

        Task delayTask = Task.Delay(timeout, cancellationToken);
        Task completed = await Task.WhenAny(wakeTask, delayTask).ConfigureAwait(false);
        if (ReferenceEquals(completed, delayTask))
        {
            await delayTask.ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource<bool> CreateWakeSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Release(TelegramConversationScope conversation)
    {
        _targetCounts.AddOrUpdate(
            conversation,
            0,
            static (_, count) => Math.Max(0, count - 1));
        if (_targetCounts.TryGetValue(conversation, out int count) && count == 0)
        {
            _targetCounts.TryRemove(conversation, out _);
        }

        SignalChanged();
    }

    private void SignalChanged()
    {
        TaskCompletionSource<bool> signal;
        lock (_wakeLock)
        {
            _changeVersion++;
            signal = _wakeSignal;
            _wakeSignal = CreateWakeSignal();
        }

        signal.TrySetResult(true);
    }

    private sealed class Registration : IDisposable
    {
        private TelegramTypingIndicatorRegistry? _owner;
        private readonly TelegramConversationScope _conversation;

        public Registration(TelegramTypingIndicatorRegistry owner, TelegramConversationScope conversation)
        {
            _owner = owner;
            _conversation = conversation;
        }

        public void Dispose()
        {
            TelegramTypingIndicatorRegistry? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_conversation);
        }
    }
}

/// <summary>
/// Keeps Telegram's native typing indicator alive while followed Codex turns are running.
/// </summary>
internal sealed class TelegramTypingHeartbeatHostedService : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(3);

    private readonly TelegramBotOptions _options;
    private readonly ICodexTurnExecutionCoordinator _turnCoordinator;
    private readonly ITelegramThreadFollowRegistry _followRegistry;
    private readonly ITelegramTypingIndicatorRegistry _typingIndicatorRegistry;
    private readonly ITelegramBotMessageSender _sender;
    private readonly ILogger<TelegramTypingHeartbeatHostedService> _logger;

    public TelegramTypingHeartbeatHostedService(
        IOptions<TelegramBotOptions> options,
        ICodexTurnExecutionCoordinator turnCoordinator,
        ITelegramThreadFollowRegistry followRegistry,
        ITelegramTypingIndicatorRegistry typingIndicatorRegistry,
        ITelegramBotMessageSender sender,
        ILogger<TelegramTypingHeartbeatHostedService> logger)
    {
        _options = options.Value;
        _turnCoordinator = turnCoordinator;
        _followRegistry = followRegistry;
        _typingIndicatorRegistry = typingIndicatorRegistry;
        _sender = sender;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => RunAsync(stoppingToken);

    internal async Task RunAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Telegram typing heartbeat is disabled.");
            return;
        }

        _logger.LogInformation("Telegram typing heartbeat started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                long observedVersion = _typingIndicatorRegistry.ChangeVersion;
                await SendHeartbeatAsync(stoppingToken).ConfigureAwait(false);
                await _typingIndicatorRegistry.WaitForChangeAsync(
                    HeartbeatInterval,
                    observedVersion,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Telegram typing heartbeat failed; retrying.");
                await Task.Delay(HeartbeatInterval, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Telegram typing heartbeat stopped.");
    }

    internal async Task<int> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        HashSet<TelegramConversationScope> targets = [];
        foreach (TelegramConversationScope target in _typingIndicatorRegistry.GetTargets())
        {
            targets.Add(target);
        }

        foreach (string threadId in _turnCoordinator.GetActiveThreadIds())
        {
            foreach (TelegramConversationScope target in _followRegistry.GetTargets(threadId))
            {
                targets.Add(target);
            }
        }

        foreach (TelegramConversationScope target in targets)
        {
            await _sender.SendTypingActionAsync(target, cancellationToken).ConfigureAwait(false);
        }

        return targets.Count;
    }
}
