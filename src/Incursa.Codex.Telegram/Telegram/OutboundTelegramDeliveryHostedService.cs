using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Subscribes to prepared outbound Telegram deliveries and sends each item when the queue emits it.
/// </summary>
internal sealed class OutboundTelegramDeliveryHostedService : BackgroundService
{
    private const int RestartDelaySeconds = 1;
    private readonly IOutboundTelegramDeliveryStream _deliveries;
    private readonly IOutboundTelegramMessageSender _sender;
    private readonly ILogger<OutboundTelegramDeliveryHostedService> _logger;

    public OutboundTelegramDeliveryHostedService(
        IOutboundTelegramDeliveryStream deliveries,
        IOutboundTelegramMessageSender sender,
        ILogger<OutboundTelegramDeliveryHostedService> logger)
    {
        _deliveries = deliveries;
        _sender = sender;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using DeliveryObserver observer = new(_sender, stoppingToken);
                using IDisposable subscription = _deliveries.ObserveDeliveries().Subscribe(observer);
                await observer.Completion.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Telegram outbound delivery subscriber failed; restarting.");
                await Task.Delay(TimeSpan.FromSeconds(RestartDelaySeconds), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class DeliveryObserver : IObserver<OutboundTelegramDelivery>, IDisposable
    {
        private readonly IOutboundTelegramMessageSender _sender;
        private readonly CancellationToken _stoppingToken;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public DeliveryObserver(IOutboundTelegramMessageSender sender, CancellationToken stoppingToken)
        {
            _sender = sender;
            _stoppingToken = stoppingToken;
        }

        public Task Completion => _completion.Task;

        public void OnNext(OutboundTelegramDelivery value)
        {
            if (_disposed != 0)
            {
                value.Fail(new OperationCanceledException(_stoppingToken));
                return;
            }

            _ = SendAndReportAsync(value);
        }

        public void OnError(Exception error)
            => _completion.TrySetException(error);

        public void OnCompleted()
            => _completion.TrySetResult();

        public void Dispose()
            => Interlocked.Exchange(ref _disposed, 1);

        private async Task SendAndReportAsync(OutboundTelegramDelivery delivery)
        {
            try
            {
                using CancellationTokenSource sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _stoppingToken,
                    delivery.CancellationToken);
                if (delivery.File is not null)
                {
                    await _sender.SendFileMessageAsync(
                        delivery.Conversation,
                        delivery.File,
                        sendCancellation.Token,
                        delivery.DebugContext).ConfigureAwait(false);
                }
                else
                {
                    await _sender.SendTextMessageAsync(
                        delivery.Conversation,
                        delivery.Text ?? string.Empty,
                        sendCancellation.Token,
                        delivery.DebugContext).ConfigureAwait(false);
                }

                delivery.Complete();
            }
            catch (Exception exception)
            {
                delivery.Fail(exception);
            }
        }
    }
}
