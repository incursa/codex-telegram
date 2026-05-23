using Incursa.Codex.Telegram.Models;

namespace Incursa.Codex.Telegram.Services;

internal interface ICodexSessionEventLog
{
    void Record(CodexSessionEventRecord record);

    void RecordVisibleAssistantOutput(string threadId, string? turnId, DateTimeOffset timestamp, string text);

    bool HasVisibleAssistantOutput(string threadId, string? turnId);

    bool TryMarkCloseoutWarningPublished(string threadId, string? turnId);

    IReadOnlyList<CodexSessionEventRecord> GetRecent(string threadId, int count);

    CodexTurnCloseoutSummary? GetLastCloseout(string threadId);
}

internal sealed class CodexSessionEventLog : ICodexSessionEventLog
{
    private const int MaxEvents = 2_000;
    private readonly object _gate = new();
    private readonly Queue<CodexSessionEventRecord> _events = [];
    private readonly Dictionary<string, CodexTurnCloseoutSummary> _lastCloseouts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visibleAssistantOutputKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _publishedCloseoutWarningKeys = new(StringComparer.Ordinal);

    public void Record(CodexSessionEventRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ThreadId))
        {
            return;
        }

        lock (_gate)
        {
            _events.Enqueue(record);
            while (_events.Count > MaxEvents)
            {
                _events.Dequeue();
            }

            if (record.Closeout is not null)
            {
                _lastCloseouts[record.ThreadId] = record.Closeout;
            }
        }
    }

    public IReadOnlyList<CodexSessionEventRecord> GetRecent(string threadId, int count)
    {
        if (string.IsNullOrWhiteSpace(threadId) || count <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            return _events
                .Where(evt => string.Equals(evt.ThreadId, threadId, StringComparison.Ordinal))
                .TakeLast(count)
                .ToArray();
        }
    }

    public CodexTurnCloseoutSummary? GetLastCloseout(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        lock (_gate)
        {
            return _lastCloseouts.GetValueOrDefault(threadId);
        }
    }

    public void RecordVisibleAssistantOutput(string threadId, string? turnId, DateTimeOffset timestamp, string text)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            _visibleAssistantOutputKeys.Add(CreateTurnKey(threadId, turnId));
        }

        Record(new CodexSessionEventRecord(
            threadId,
            turnId,
            timestamp,
            "telegram.assistant.visible",
            CodexSessionEventKind.AssistantOutput,
            CodexSessionEventLane.Timeline,
            "Assistant output sent to Telegram",
            text));
    }

    public bool HasVisibleAssistantOutput(string threadId, string? turnId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        lock (_gate)
        {
            return _visibleAssistantOutputKeys.Contains(CreateTurnKey(threadId, turnId));
        }
    }

    public bool TryMarkCloseoutWarningPublished(string threadId, string? turnId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        lock (_gate)
        {
            return _publishedCloseoutWarningKeys.Add(CreateTurnKey(threadId, turnId));
        }
    }

    private static string CreateTurnKey(string threadId, string? turnId)
        => string.IsNullOrWhiteSpace(turnId)
            ? threadId
            : $"{threadId}\u001f{turnId}";
}

internal sealed class NullCodexSessionEventLog : ICodexSessionEventLog
{
    public static NullCodexSessionEventLog Instance { get; } = new();

    private NullCodexSessionEventLog()
    {
    }

    public void Record(CodexSessionEventRecord record)
    {
    }

    public void RecordVisibleAssistantOutput(string threadId, string? turnId, DateTimeOffset timestamp, string text)
    {
    }

    public bool HasVisibleAssistantOutput(string threadId, string? turnId)
        => false;

    public bool TryMarkCloseoutWarningPublished(string threadId, string? turnId)
        => true;

    public IReadOnlyList<CodexSessionEventRecord> GetRecent(string threadId, int count)
        => [];

    public CodexTurnCloseoutSummary? GetLastCloseout(string threadId)
        => null;
}

internal sealed record CodexSessionEventRecord(
    string ThreadId,
    string? TurnId,
    DateTimeOffset Timestamp,
    string Type,
    CodexSessionEventKind Kind,
    CodexSessionEventLane Lane,
    string Title,
    string? Summary,
    CodexTurnCloseoutSummary? Closeout = null)
{
    public static CodexSessionEventRecord FromTimelineEntry(
        CodexTimelineEntryVm entry,
        CodexTurnCloseoutSummary? closeout = null)
        => new(
            entry.ThreadId ?? string.Empty,
            entry.TurnId,
            entry.Timestamp,
            entry.Type,
            CodexSessionEventClassifier.ClassifyKind(entry),
            CodexSessionEventClassifier.ClassifyLane(entry),
            entry.Title,
            ResolveSummary(entry),
            closeout);

    private static string? ResolveSummary(CodexTimelineEntryVm entry)
        => string.IsNullOrWhiteSpace(entry.Body)
            ? entry.Subtitle
            : entry.Body;
}

internal enum CodexSessionEventKind
{
    AssistantOutput,
    ToolProgress,
    TerminalSuccess,
    TerminalFailure,
    RetryScheduled,
    CloseoutWarning,
    InternalNoise,
    Other,
}

internal enum CodexSessionEventLane
{
    State,
    Timeline,
    Log,
}

internal static class CodexSessionEventClassifier
{
    public static CodexSessionEventKind ClassifyKind(CodexTimelineEntryVm entry)
    {
        if (string.Equals(entry.Type, "item.agentMessage.delta", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Type, "turn.assistant.recovered", StringComparison.OrdinalIgnoreCase))
        {
            return CodexSessionEventKind.AssistantOutput;
        }

        if (string.Equals(entry.Type, "turn.completed", StringComparison.OrdinalIgnoreCase))
        {
            return CodexSessionEventKind.TerminalSuccess;
        }

        if (string.Equals(entry.Type, "turn.failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Severity, "danger", StringComparison.OrdinalIgnoreCase))
        {
            return CodexSessionEventKind.TerminalFailure;
        }

        if (string.Equals(entry.Type, "turn.retry", StringComparison.OrdinalIgnoreCase))
        {
            return CodexSessionEventKind.RetryScheduled;
        }

        if (string.Equals(entry.Type, "turn.closeout.warning", StringComparison.OrdinalIgnoreCase))
        {
            return CodexSessionEventKind.CloseoutWarning;
        }

        if (entry.Type.StartsWith("item.command", StringComparison.OrdinalIgnoreCase)
            || entry.Type.StartsWith("item.tool", StringComparison.OrdinalIgnoreCase))
        {
            return CodexSessionEventKind.ToolProgress;
        }

        return entry.IsInternal ? CodexSessionEventKind.InternalNoise : CodexSessionEventKind.Other;
    }

    public static CodexSessionEventLane ClassifyLane(CodexTimelineEntryVm entry)
    {
        CodexSessionEventKind kind = ClassifyKind(entry);
        return kind switch
        {
            CodexSessionEventKind.AssistantOutput => CodexSessionEventLane.Timeline,
            CodexSessionEventKind.TerminalFailure => CodexSessionEventLane.Timeline,
            CodexSessionEventKind.CloseoutWarning => CodexSessionEventLane.Timeline,
            CodexSessionEventKind.TerminalSuccess => CodexSessionEventLane.State,
            CodexSessionEventKind.RetryScheduled => CodexSessionEventLane.State,
            _ => CodexSessionEventLane.Log,
        };
    }
}
