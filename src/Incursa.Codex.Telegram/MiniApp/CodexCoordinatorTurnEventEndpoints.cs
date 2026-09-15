using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Incursa.Codex.Telegram.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Receives authenticated, bounded turn events from workers for the coordinator's Telegram relay.
/// </summary>
internal static class CodexCoordinatorTurnEventEndpoints
{
    private const string EventPath = "/api/coordinator/v1/worker-events";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Map(WebApplication app)
        => app.MapPost(EventPath, ReceiveAsync);

    internal static async Task<IResult> ReceiveAsync(
        HttpContext context,
        IOptions<CodexTelegramOptions> options,
        ICodexCoordinatorWorkerStore workerStore,
        ITelegramTurnOutputRelay turnOutputRelay,
        ICodexSupervisionLedger supervisionLedger,
        CancellationToken cancellationToken)
    {
        CodexCoordinatorOptions coordinator = options.Value.Coordinator;
        if (!coordinator.Enabled)
        {
            return Results.NotFound();
        }

        if (!CodexCoordinatorWorkerEndpoints.TryAuthorize(context.Request, coordinator.AuthenticationToken))
        {
            return Results.Unauthorized();
        }

        if (context.Request.ContentLength is > 64 * 1024)
        {
            return Results.BadRequest(new { error = "Remote turn event is too large." });
        }

        CodexRemoteTurnEvent? remoteEvent;
        try
        {
            remoteEvent = await JsonSerializer.DeserializeAsync<CodexRemoteTurnEvent>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Remote turn event is malformed." });
        }

        if (remoteEvent is null || !IsValid(remoteEvent))
        {
            return Results.BadRequest(new { error = "Remote turn event metadata is invalid." });
        }

        CodexWorkerSnapshot? worker = (await workerStore.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.IsRemote
                && string.Equals(candidate.WorkerId, remoteEvent.WorkerId, StringComparison.Ordinal));
        if (worker is null)
        {
            return Results.Conflict(new { error = "Worker is not registered." });
        }

        await turnOutputRelay.PublishTurnEventAsync(remoteEvent.Entry, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(remoteEvent.Entry.TurnId))
        {
            (CodexSupervisionRunState? state, string? outcome) = ResolveSupervision(remoteEvent.Entry);
            if (state.HasValue)
            {
                await supervisionLedger.UpdateRunsForTurnAsync(
                    remoteEvent.Entry.TurnId,
                    state.Value,
                    remoteEvent.ThreadId,
                    outcome,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return Results.Ok(new { accepted = true });
    }

    private static bool IsValid(CodexRemoteTurnEvent remoteEvent)
        => IsSafeToken(remoteEvent.WorkerId, 120)
            && IsSafeToken(remoteEvent.ThreadId, 256)
            && remoteEvent.Entry is not null
            && string.Equals(remoteEvent.Entry.ThreadId, remoteEvent.ThreadId, StringComparison.Ordinal)
            && IsBoundedText(remoteEvent.Entry.Type, 160)
            && IsBoundedText(remoteEvent.Entry.Title, 240)
            && IsOptionalBoundedText(remoteEvent.Entry.Subtitle, 500)
            && IsOptionalBoundedText(remoteEvent.Entry.Body, 16_000)
            && IsBoundedText(remoteEvent.Entry.Severity, 40)
            && (string.IsNullOrWhiteSpace(remoteEvent.Entry.TurnId) || IsSafeToken(remoteEvent.Entry.TurnId, 256))
            && remoteEvent.Entry.Metadata is not null
            && remoteEvent.Entry.Metadata.Count <= 64;

    private static (CodexSupervisionRunState? State, string? Outcome) ResolveSupervision(CodexTimelineEntryVm entry)
        => string.Equals(entry.Type, "turn.completed", StringComparison.OrdinalIgnoreCase)
            ? (CodexSupervisionRunState.Completed, "turn_completed")
            : string.Equals(entry.Type, "turn.interrupted", StringComparison.OrdinalIgnoreCase)
                ? (CodexSupervisionRunState.Interrupted, "turn_interrupted")
                : string.Equals(entry.Type, "turn.failed", StringComparison.OrdinalIgnoreCase)
                    ? (CodexSupervisionRunState.Failed, "turn_failed")
                    : (CodexSupervisionRunState.Running, null);

    private static bool IsOptionalBoundedText(string? value, int maxLength)
        => value is null || IsBoundedText(value, maxLength);

    private static bool IsBoundedText(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl);

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
