using Incursa.Codex.Telegram.Models;

namespace Incursa.Codex.Telegram.Services;

/// <summary>
/// Broadcasts Codex runtime and thread events to optional realtime surfaces.
/// </summary>
internal interface ICodexRealtimeBroadcaster
{
    /// <summary>
    /// Broadcasts the latest Codex runtime state.
    /// </summary>
    /// <param name="runtime">Runtime state to broadcast.</param>
    /// <param name="cancellationToken">Cancellation token for request aborts.</param>
    /// <returns>A task that completes after broadcasting.</returns>
    Task BroadcastRuntimeStateAsync(CodexRuntimeStateVm runtime, CancellationToken cancellationToken);

    /// <summary>
    /// Broadcasts a thread timeline entry.
    /// </summary>
    /// <param name="threadId">Codex thread ID.</param>
    /// <param name="entry">Timeline entry to broadcast.</param>
    /// <param name="cancellationToken">Cancellation token for request aborts.</param>
    /// <returns>A task that completes after broadcasting.</returns>
    Task BroadcastThreadEventAsync(string threadId, CodexTimelineEntryVm entry, CancellationToken cancellationToken);
}

/// <summary>
/// No-op realtime broadcaster used by the standalone Telegram app.
/// </summary>
internal sealed class NullCodexRealtimeBroadcaster : ICodexRealtimeBroadcaster
{
    /// <inheritdoc />
    public Task BroadcastRuntimeStateAsync(CodexRuntimeStateVm runtime, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task BroadcastThreadEventAsync(string threadId, CodexTimelineEntryVm entry, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
