using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Services;
using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Tests.TestSupport;

internal sealed class ScriptedTurnExecutionCoordinator : ICodexTurnExecutionCoordinator
{
    public HashSet<string> ActiveThreadIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasActiveTurn => ActiveThreadIds.Count > 0;

    public IReadOnlyCollection<string> GetActiveThreadIds() => ActiveThreadIds.ToArray();

    public bool HasActiveTurnForThread(string threadId) => ActiveThreadIds.Contains(threadId);

    public string? GetActiveTurnId(string threadId) => HasActiveTurnForThread(threadId) ? "turn-1" : null;

    public CodexActiveTurnStateVm? TryGetActiveTurnState(string threadId) => null;

    public void RegisterActiveTurn(string threadId, string turnId, ICodexTurnHandle? turn = null, CodexTimelineEntryVm? lastEvent = null)
        => ActiveThreadIds.Add(threadId);

    public void UpdateActiveTurnState(string threadId, string turnId, CodexTimelineEntryVm? lastEvent = null)
    {
    }

    public bool TryClearActiveTurn(string threadId, string turnId)
        => ActiveThreadIds.Remove(threadId);

    public Task SteerAsync(string threadId, string turnId, IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task InterruptAsync(string threadId, string turnId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
