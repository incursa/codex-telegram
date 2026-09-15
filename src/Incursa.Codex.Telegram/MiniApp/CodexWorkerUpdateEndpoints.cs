using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Authenticated post-install acknowledgement endpoint for an external worker installer.
/// </summary>
internal static class CodexWorkerUpdateEndpoints
{
    private const string CompletionPath = "/api/worker/v1/update/complete";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Map(WebApplication app)
        => app.MapPost(CompletionPath, CompleteAsync);

    internal static async Task<IResult> CompleteAsync(
        HttpContext context,
        IOptions<CodexTelegramOptions> options,
        ICodexWorkerUpdateManager updateManager,
        CancellationToken cancellationToken)
    {
        CodexWorkerUpdateOptions updates = options.Value.Updates;
        if (!updates.Enabled)
        {
            return Results.NotFound();
        }

        if (!CodexCoordinatorWorkerEndpoints.TryAuthorize(context.Request, updates.InstallerAuthenticationToken))
        {
            return Results.Unauthorized();
        }

        if (context.Request.ContentLength is > 16 * 1024)
        {
            return Results.BadRequest(new { error = "Worker update completion is too large." });
        }

        CodexWorkerUpdateCompletion? completion;
        try
        {
            completion = await JsonSerializer.DeserializeAsync<CodexWorkerUpdateCompletion>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Worker update completion is malformed." });
        }

        if (completion is null
            || !IsSafeToken(completion.Version, 40)
            || string.IsNullOrWhiteSpace(completion.Sha256)
            || completion.Sha256.Trim().Length != 64
            || !completion.Sha256.Trim().All(char.IsAsciiHexDigit))
        {
            return Results.BadRequest(new { error = "Worker update completion version or SHA-256 is invalid." });
        }

        CodexWorkerUpdateSnapshot result = await updateManager.CompleteAsync(completion, cancellationToken).ConfigureAwait(false);
        return result.State is CodexWorkerUpdateState.Active or CodexWorkerUpdateState.RollbackActive
            ? Results.Ok(result)
            : Results.Conflict(result);
    }

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');
}
