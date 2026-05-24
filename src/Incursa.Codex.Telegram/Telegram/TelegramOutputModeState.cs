using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Telegram;

internal interface ITelegramOutputModeState
{
    TelegramOutputPresentationMode CurrentMode { get; }

    bool HasRuntimeOverride { get; }

    void SetRuntimeMode(TelegramOutputPresentationMode mode);

    void ClearRuntimeMode();
}

internal sealed class TelegramOutputModeState : ITelegramOutputModeState
{
    private readonly object _gate = new();
    private readonly IOptionsMonitor<TelegramOutputOptions> _options;
    private TelegramOutputPresentationMode? _runtimeMode;

    public TelegramOutputModeState(IOptionsMonitor<TelegramOutputOptions> options)
    {
        _options = options;
    }

    public TelegramOutputPresentationMode CurrentMode
    {
        get
        {
            lock (_gate)
            {
                return _runtimeMode ?? _options.CurrentValue.PresentationMode;
            }
        }
    }

    public bool HasRuntimeOverride
    {
        get
        {
            lock (_gate)
            {
                return _runtimeMode.HasValue;
            }
        }
    }

    public void SetRuntimeMode(TelegramOutputPresentationMode mode)
    {
        lock (_gate)
        {
            _runtimeMode = mode;
        }
    }

    public void ClearRuntimeMode()
    {
        lock (_gate)
        {
            _runtimeMode = null;
        }
    }
}
