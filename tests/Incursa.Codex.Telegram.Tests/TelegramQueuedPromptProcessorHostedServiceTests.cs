using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramQueuedPromptProcessorHostedServiceTests
{
    [Fact]
    public async Task RunAsync_DoesNotProcessWhenTelegramBotIsDisabled()
    {
        FakeQueuedPromptProcessor processor = new();
        FakeProcessorDelayProvider delayProvider = new();
        TelegramQueuedPromptProcessorHostedService service = CreateService(processor, delayProvider, enabled: false);

        await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, processor.CallCount);
        Assert.Empty(delayProvider.Delays);
    }

    [Fact]
    public async Task RunAsync_UsesFastDelayAfterWorkWasProcessed()
    {
        FakeQueuedPromptProcessor processor = new();
        processor.Results.Enqueue(true);
        using CancellationTokenSource cancellation = new();
        FakeProcessorDelayProvider delayProvider = new()
        {
            Cancellation = cancellation,
            CancelAfterDelayCount = 1,
        };
        TelegramQueuedPromptProcessorHostedService service = CreateService(processor, delayProvider, enabled: true);

        await service.RunAsync(cancellation.Token);

        Assert.Equal(1, processor.CallCount);
        Assert.Equal([TimeSpan.FromMilliseconds(200)], delayProvider.Delays);
    }

    [Fact]
    public async Task RunAsync_UsesIdleDelayWhenNoWorkWasProcessed()
    {
        FakeQueuedPromptProcessor processor = new();
        processor.Results.Enqueue(false);
        using CancellationTokenSource cancellation = new();
        FakeProcessorDelayProvider delayProvider = new()
        {
            Cancellation = cancellation,
            CancelAfterDelayCount = 1,
        };
        TelegramQueuedPromptProcessorHostedService service = CreateService(processor, delayProvider, enabled: true);

        await service.RunAsync(cancellation.Token);

        Assert.Equal(1, processor.CallCount);
        Assert.Equal([TimeSpan.FromSeconds(1)], delayProvider.Delays);
    }

    [Fact]
    public async Task RunAsync_UsesRetryDelayAfterProcessorFailure()
    {
        FakeQueuedPromptProcessor processor = new();
        processor.Exceptions.Enqueue(new InvalidOperationException("store unavailable"));
        using CancellationTokenSource cancellation = new();
        FakeProcessorDelayProvider delayProvider = new()
        {
            Cancellation = cancellation,
            CancelAfterDelayCount = 1,
        };
        TelegramQueuedPromptProcessorHostedService service = CreateService(processor, delayProvider, enabled: true);

        await service.RunAsync(cancellation.Token);

        Assert.Equal(1, processor.CallCount);
        Assert.Equal([TimeSpan.FromSeconds(2)], delayProvider.Delays);
    }

    [Fact]
    public async Task RunAsync_StopsWithoutRetryDelayWhenProcessorObservesCancellation()
    {
        using CancellationTokenSource cancellation = new();
        FakeQueuedPromptProcessor processor = new()
        {
            OnProcess = token =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(token);
            },
        };
        FakeProcessorDelayProvider delayProvider = new();
        TelegramQueuedPromptProcessorHostedService service = CreateService(processor, delayProvider, enabled: true);

        await service.RunAsync(cancellation.Token);

        Assert.Equal(1, processor.CallCount);
        Assert.Empty(delayProvider.Delays);
    }

    private static TelegramQueuedPromptProcessorHostedService CreateService(
        FakeQueuedPromptProcessor processor,
        FakeProcessorDelayProvider delayProvider,
        bool enabled)
        => new(
            processor,
            Microsoft.Extensions.Options.Options.Create(new TelegramBotOptions { Enabled = enabled }),
            NullLogger<TelegramQueuedPromptProcessorHostedService>.Instance,
            delayProvider);

    private sealed class FakeQueuedPromptProcessor : ITelegramQueuedPromptProcessor
    {
        public Queue<bool> Results { get; } = [];

        public Queue<Exception> Exceptions { get; } = [];

        public Func<CancellationToken, bool>? OnProcess { get; set; }

        public int CallCount { get; private set; }

        public Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
        {
            CallCount++;

            if (OnProcess is not null)
            {
                return Task.FromResult(OnProcess(cancellationToken));
            }

            if (Exceptions.TryDequeue(out Exception? exception))
            {
                throw exception;
            }

            return Task.FromResult(Results.TryDequeue(out bool result) && result);
        }
    }

    private sealed class FakeProcessorDelayProvider : ITelegramProcessorDelayProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public CancellationTokenSource? Cancellation { get; init; }

        public int CancelAfterDelayCount { get; init; } = int.MaxValue;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            if (Delays.Count >= CancelAfterDelayCount)
            {
                Cancellation?.Cancel();
            }

            return Task.CompletedTask;
        }
    }
}
