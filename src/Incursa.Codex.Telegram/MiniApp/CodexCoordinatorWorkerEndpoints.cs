using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.MiniApp;

/// <summary>
/// Authenticated coordinator control-plane endpoint for worker heartbeats.
/// </summary>
internal static class CodexCoordinatorWorkerEndpoints
{
    private const string HeartbeatPath = "/api/coordinator/v1/workers/heartbeat";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Map(WebApplication app)
        => app.MapPost(HeartbeatPath, ReceiveHeartbeatAsync);

    private static async Task<IResult> ReceiveHeartbeatAsync(
        HttpContext context,
        IOptions<CodexTelegramOptions> options,
        ICodexCoordinatorWorkerStore workerStore,
        CancellationToken cancellationToken)
    {
        CodexCoordinatorOptions coordinator = options.Value.Coordinator;
        if (!coordinator.Enabled)
        {
            return Results.NotFound();
        }

        if (!TryAuthorize(context.Request, coordinator.AuthenticationToken))
        {
            return Results.Unauthorized();
        }

        if (context.Request.ContentLength is > 64 * 1024)
        {
            return Results.BadRequest(new { error = "Worker heartbeat is too large." });
        }

        CodexWorkerSnapshot? worker;
        try
        {
            worker = await JsonSerializer.DeserializeAsync<CodexWorkerSnapshot>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Worker heartbeat is malformed." });
        }

        if (worker is null || string.IsNullOrWhiteSpace(worker.WorkerId) || worker.WorkerId.Length > 120)
        {
            return Results.BadRequest(new { error = "Worker heartbeat identity is missing or invalid." });
        }

        if (coordinator.AllowedWorkerIds.Count > 0
            && !coordinator.AllowedWorkerIds.Contains(worker.WorkerId, StringComparer.Ordinal))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        bool accepted = await workerStore.UpsertAsync(worker, cancellationToken).ConfigureAwait(false);
        return accepted
            ? Results.Ok(new { accepted = true })
            : Results.StatusCode(StatusCodes.Status409Conflict);
    }

    internal static bool TryAuthorize(HttpRequest request, string? configuredToken)
    {
        if (string.IsNullOrWhiteSpace(configuredToken)
            || !AuthenticationHeaderValue.TryParse(request.Headers.Authorization.FirstOrDefault(), out AuthenticationHeaderValue? header)
            || !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(header.Parameter))
        {
            return false;
        }

        byte[] expected = Encoding.UTF8.GetBytes(configuredToken.Trim());
        byte[] supplied = Encoding.UTF8.GetBytes(header.Parameter.Trim());
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
