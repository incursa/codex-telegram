using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Services;

internal interface ICodexThreadHandle
{
    string? Id { get; }

    Task<ICodexTurnHandle> StartTurnAsync(
        IReadOnlyList<CodexInputItem> input,
        CodexTurnOptions turnOptions,
        CancellationToken cancellationToken);

    Task SetNameAsync(string name, CancellationToken cancellationToken);

    Task CompactAsync(CancellationToken cancellationToken);

    Task<CodexThreadGoal?> GetGoalAsync(CancellationToken cancellationToken);

    Task<CodexThreadGoal> SetGoalAsync(string objective, long? tokenBudget, CancellationToken cancellationToken);

    Task<CodexThreadGoal> SetGoalStatusAsync(CodexThreadGoalStatus status, CancellationToken cancellationToken);

    Task<bool> ClearGoalAsync(CancellationToken cancellationToken);
}

internal interface ICodexTurnHandle
{
    string Id { get; }

    string? ThreadId { get; }

    IAsyncEnumerable<CodexThreadEvent> StreamAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<CodexTurnEvent> StreamNormalizedAsync(CancellationToken cancellationToken);

    Task<CodexTurnResult> RunToResultAsync(CancellationToken cancellationToken);

    Task SteerAsync(IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken);

    Task InterruptAsync(CancellationToken cancellationToken);
}

internal sealed class CodexThreadHandle : ICodexThreadHandle
{
    private readonly CodexThread _thread;

    public CodexThreadHandle(CodexThread thread)
    {
        _thread = thread;
    }

    public string? Id => _thread.Id;

    public async Task<ICodexTurnHandle> StartTurnAsync(
        IReadOnlyList<CodexInputItem> input,
        CodexTurnOptions turnOptions,
        CancellationToken cancellationToken)
        => new CodexTurnHandle(await _thread.StartTurnAsync(input, turnOptions, cancellationToken).ConfigureAwait(false));

    public Task SetNameAsync(string name, CancellationToken cancellationToken)
        => _thread.SetNameAsync(name, cancellationToken);

    public Task CompactAsync(CancellationToken cancellationToken)
        => _thread.CompactAsync(cancellationToken);

    public Task<CodexThreadGoal?> GetGoalAsync(CancellationToken cancellationToken)
        => _thread.GetGoalAsync(cancellationToken);

    public Task<CodexThreadGoal> SetGoalAsync(string objective, long? tokenBudget, CancellationToken cancellationToken)
        => _thread.SetGoalAsync(objective, tokenBudget, cancellationToken);

    public Task<CodexThreadGoal> SetGoalStatusAsync(CodexThreadGoalStatus status, CancellationToken cancellationToken)
        => _thread.SetGoalStatusAsync(status, cancellationToken);

    public Task<bool> ClearGoalAsync(CancellationToken cancellationToken)
        => _thread.ClearGoalAsync(cancellationToken);
}

internal sealed class CodexTurnHandle : ICodexTurnHandle
{
    private readonly CodexTurn _turn;

    public CodexTurnHandle(CodexTurn turn)
    {
        _turn = turn;
    }

    public string Id => _turn.Id;

    public string? ThreadId => _turn.ThreadId;

    public IAsyncEnumerable<CodexThreadEvent> StreamAsync(CancellationToken cancellationToken)
        => _turn.StreamAsync(cancellationToken);

    public IAsyncEnumerable<CodexTurnEvent> StreamNormalizedAsync(CancellationToken cancellationToken)
        => _turn.StreamNormalizedAsync(cancellationToken);

    public Task<CodexTurnResult> RunToResultAsync(CancellationToken cancellationToken)
        => _turn.RunToResultAsync(cancellationToken);

    public Task SteerAsync(IReadOnlyList<CodexInputItem> input, CancellationToken cancellationToken)
        => _turn.SteerAsync(input, cancellationToken);

    public Task InterruptAsync(CancellationToken cancellationToken)
        => _turn.InterruptAsync(cancellationToken);
}
