using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Services;

namespace Incursa.Codex.Telegram.Telegram;

internal enum TelegramSessionUiState
{
    Idle,
    Starting,
    Working,
    Waiting,
    Draining,
    Completed,
    Interrupted,
    Failed,
    Unavailable,
    DeliveryDelayed,
    OutputCompacted,
}

internal sealed record TelegramSessionCardBehavior(
    TelegramSessionUiState State,
    string StateText,
    IReadOnlyList<IReadOnlyList<TelegramReplyButton>> Buttons)
{
    public static TelegramSessionCardBehavior Resolve(
        CodexSessionSummary session,
        string? activeTurnId,
        CodexActiveTurnStateVm? activeTurn,
        TelegramOutboundDestinationStatus? destination,
        TelegramTurnDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(diagnostics);

        TelegramSessionUiState state = ResolveState(session, activeTurnId, activeTurn, destination, diagnostics);
        return new TelegramSessionCardBehavior(state, FormatState(state), BuildButtons(session, state, activeTurnId));
    }

    private static TelegramSessionUiState ResolveState(
        CodexSessionSummary session,
        string? activeTurnId,
        CodexActiveTurnStateVm? activeTurn,
        TelegramOutboundDestinationStatus? destination,
        TelegramTurnDiagnostics diagnostics)
    {
        if (diagnostics.RateLimited || diagnostics.SendTimedOut || diagnostics.SendFailed)
        {
            return TelegramSessionUiState.DeliveryDelayed;
        }

        if (diagnostics.TerminalEventType?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true
            || session.Status is CodexSessionStatus.Failed)
        {
            return TelegramSessionUiState.Failed;
        }

        bool telegramDrainPending = (destination is not null && (destination.PendingMessageCount > 0 || destination.PendingChunkCount > 0))
            || diagnostics.PendingChunks > 0;
        if (telegramDrainPending)
        {
            return diagnostics.TerminalEventSeen
                ? TelegramSessionUiState.Draining
                : TelegramSessionUiState.DeliveryDelayed;
        }

        if (diagnostics.Compacted)
        {
            return TelegramSessionUiState.OutputCompacted;
        }

        if (diagnostics.TerminalEventSeen)
        {
            if (diagnostics.TerminalEventType?.Contains("interrupted", StringComparison.OrdinalIgnoreCase) == true)
            {
                return TelegramSessionUiState.Interrupted;
            }

            return TelegramSessionUiState.Completed;
        }

        if (!string.IsNullOrWhiteSpace(activeTurnId))
        {
            return IsWaitingForUser(activeTurn)
                ? TelegramSessionUiState.Waiting
                : TelegramSessionUiState.Working;
        }

        return session.Status switch
        {
            CodexSessionStatus.Starting => TelegramSessionUiState.Starting,
            CodexSessionStatus.Running => TelegramSessionUiState.Working,
            CodexSessionStatus.Failed => TelegramSessionUiState.Failed,
            CodexSessionStatus.Stopped => TelegramSessionUiState.Unavailable,
            _ => TelegramSessionUiState.Idle,
        };
    }

    private static IReadOnlyList<IReadOnlyList<TelegramReplyButton>> BuildButtons(
        CodexSessionSummary session,
        TelegramSessionUiState state,
        string? activeTurnId)
    {
        List<IReadOnlyList<TelegramReplyButton>> rows = [];
        switch (state)
        {
            case TelegramSessionUiState.Working:
            case TelegramSessionUiState.Waiting:
                rows.Add(
                    [
                        new TelegramReplyButton("Steer", $"input:{session.Id}|steer"),
                        new TelegramReplyButton("Queue next", $"input:{session.Id}|queue"),
                    ]);
                if (!string.IsNullOrWhiteSpace(activeTurnId))
                {
                    rows.Add([new TelegramReplyButton("Stop / Cancel", $"stop:{session.Id}")]);
                }

                break;
            case TelegramSessionUiState.Starting:
                rows.Add([new TelegramReplyButton("Queue next", $"input:{session.Id}|queue")]);
                rows.Add([new TelegramReplyButton("Stop / Cancel", $"stop:{session.Id}")]);
                break;
            case TelegramSessionUiState.Draining:
            case TelegramSessionUiState.DeliveryDelayed:
                rows.Add(
                    [
                        new TelegramReplyButton("Status / Refresh", $"status:{session.Id}"),
                        new TelegramReplyButton("Queue next", $"input:{session.Id}|queue"),
                    ]);
                break;
            case TelegramSessionUiState.Failed:
            case TelegramSessionUiState.Unavailable:
            case TelegramSessionUiState.OutputCompacted:
                rows.Add([new TelegramReplyButton("Status / Refresh", $"status:{session.Id}")]);
                break;
            default:
                rows.Add([new TelegramReplyButton("Send / Add Input", $"input:{session.Id}|send")]);
                break;
        }

        rows.Add(
            [
                new TelegramReplyButton("Tail / History", $"tail:{session.Id}"),
                new TelegramReplyButton("Debug / Trace", $"trace:{session.Id}"),
            ]);
        rows.Add(
            [
                new TelegramReplyButton("Output Mode", "output:mode"),
                new TelegramReplyButton("Show Updates", $"turn:updates|{session.Id}"),
            ]);
        rows.Add(
            [
                new TelegramReplyButton("Model", $"model:{session.Id}"),
                new TelegramReplyButton("Thinking", $"thinking:{session.Id}"),
            ]);
        return rows;
    }

    private static string FormatState(TelegramSessionUiState state)
        => state switch
        {
            TelegramSessionUiState.Idle => "Ready",
            TelegramSessionUiState.Starting => "Codex is starting",
            TelegramSessionUiState.Working => "Codex is working",
            TelegramSessionUiState.Waiting => "Codex is waiting for input",
            TelegramSessionUiState.Draining => "Codex finished; sending remaining Telegram output",
            TelegramSessionUiState.Completed => "Codex finished; Telegram delivery complete",
            TelegramSessionUiState.Interrupted => "Codex interrupted",
            TelegramSessionUiState.Failed => "Codex failed",
            TelegramSessionUiState.Unavailable => "Session unavailable",
            TelegramSessionUiState.DeliveryDelayed => "Telegram delivery delayed",
            TelegramSessionUiState.OutputCompacted => "Output compacted; open trace/history for full details",
            _ => state.ToString(),
        };

    private static bool IsWaitingForUser(CodexActiveTurnStateVm? activeTurn)
        => activeTurn?.LastEvent is { } lastEvent
            && (lastEvent.Type.Contains("approval", StringComparison.OrdinalIgnoreCase)
                || lastEvent.Type.Contains("input", StringComparison.OrdinalIgnoreCase)
                || lastEvent.Title.Contains("approval", StringComparison.OrdinalIgnoreCase)
                || lastEvent.Title.Contains("waiting", StringComparison.OrdinalIgnoreCase));
}

internal sealed record TelegramSessionStatusCard(
    string Text,
    IReadOnlyList<IReadOnlyList<TelegramReplyButton>> Buttons);
