using Incursa.Codex.Telegram.Models;

namespace Incursa.Codex.Telegram.Services;

public interface ICodexRealtimeBroadcaster
{
    Task BroadcastRuntimeStateAsync(CodexRuntimeStateVm runtime, CancellationToken cancellationToken);

    Task BroadcastThreadEventAsync(string threadId, CodexTimelineEntryVm entry, CancellationToken cancellationToken);
}

internal sealed class NullCodexRealtimeBroadcaster : ICodexRealtimeBroadcaster
{
    public Task BroadcastRuntimeStateAsync(CodexRuntimeStateVm runtime, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task BroadcastThreadEventAsync(string threadId, CodexTimelineEntryVm entry, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
