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
    private const string LeaseAcceptPath = "/api/coordinator/v1/worker-leases/accept";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Map(WebApplication app)
    {
        app.MapPost(HeartbeatPath, ReceiveHeartbeatAsync);
        app.MapPost(LeaseAcceptPath, AcceptLeaseAsync);
    }

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

    internal static async Task<IResult> AcceptLeaseAsync(
        HttpContext context,
        IOptions<CodexTelegramOptions> options,
        ICodexWorkerRegistry workerRegistry,
        CancellationToken cancellationToken)
    {
        CodexCoordinatorOptions coordinator = options.Value.Coordinator;
        if (!coordinator.WorkerRegistrationEnabled)
        {
            return Results.NotFound();
        }

        if (!TryAuthorize(context.Request, coordinator.AuthenticationToken))
        {
            return Results.Unauthorized();
        }

        if (context.Request.ContentLength is > 64 * 1024)
        {
            return Results.BadRequest(new { error = "Worker lease grant is too large." });
        }

        CodexWorkerLeaseGrant? grant;
        try
        {
            grant = await JsonSerializer.DeserializeAsync<CodexWorkerLeaseGrant>(context.Request.Body, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Worker lease grant is malformed." });
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (grant is null
            || !IsSafeToken(grant.GrantId, 160)
            || !IsSafeToken(grant.TaskId, 160)
            || (!string.IsNullOrWhiteSpace(grant.WorkspaceId) && !IsSafeToken(grant.WorkspaceId, 160))
            || !IsSafeToken(grant.WorkerId, 120)
            || grant.RequiredCapabilities.Count > 24
            || grant.RequiredCapabilities.Any(capability => !IsSafeToken(capability, 120))
            || grant.ExpiresAtUtc <= now
            || grant.ExpiresAtUtc - grant.IssuedAtUtc > TimeSpan.FromMinutes(5))
        {
            return Results.BadRequest(new { error = "Worker lease grant identity or lifetime is invalid." });
        }

        CodexWorkerSnapshot worker = await workerRegistry.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(worker.WorkerId, grant.WorkerId, StringComparison.Ordinal))
        {
            return Results.Conflict(new CodexWorkerLeaseAcceptance(false, worker.WorkerId, null, "worker_identity_mismatch"));
        }

        if (worker.State != CodexWorkerState.Online || !string.Equals(worker.Readiness, "ready", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new CodexWorkerLeaseAcceptance(false, worker.WorkerId, null, "worker_not_ready"));
        }

        if (grant.RequiredCapabilities.Any(required => !worker.Capabilities.Contains(required, StringComparer.Ordinal)))
        {
            return Results.Conflict(new CodexWorkerLeaseAcceptance(false, worker.WorkerId, null, "worker_capability_mismatch"));
        }

        CodexWorkerLease? lease = await workerRegistry.TryAcquireLeaseAsync(
            grant.TaskId,
            grant.ExpiresAtUtc - now,
            cancellationToken).ConfigureAwait(false);
        return lease is null
            ? Results.Conflict(new CodexWorkerLeaseAcceptance(false, worker.WorkerId, null, "worker_capacity_or_draining"))
            : Results.Ok(new CodexWorkerLeaseAcceptance(true, worker.WorkerId, lease.LeaseId, "lease_accepted"));
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

    private static bool IsSafeToken(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length <= maxLength
            && value.All(character => char.IsLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
