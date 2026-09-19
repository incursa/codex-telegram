using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Updater;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexTelegramUpdaterContractTests
{
    [Fact]
    public async Task UpdaterConfigurationDerivesTheApplicationHandoffPaths()
    {
        using TemporaryDirectory root = TemporaryDirectory.Create();
        string configPath = Path.Combine(root.Path, "updater.json");
        await File.WriteAllTextAsync(configPath, $$"""
        {
          "packageName": "codex-telegram",
          "serviceName": "codex-telegram.service",
          "dataRoot": "{{root.Path.Replace("\\", "\\\\")}}/data",
          "rollbackRoot": "{{root.Path.Replace("\\", "\\\\")}}/rollback",
          "healthCheckUrl": "",
          "healthCheckTimeoutSeconds": 30,
          "healthCheckIntervalSeconds": 1,
          "healthRequestTimeoutSeconds": 2,
          "aptTimeoutMinutes": 1,
          "startNotificationTimeoutSeconds": 3
        }
        """);

        UpdaterOptions options = await UpdaterOptions.LoadAsync(configPath, CancellationToken.None);

        Assert.Equal(Path.Combine(root.Path, "data", "codex-host-update-request.json"), options.RequestPath);
        Assert.Equal(Path.Combine(root.Path, "data", "codex-host-update-state.json"), options.StatePath);
        Assert.Equal(Path.Combine(root.Path, "rollback", "last-known-good.json"), options.RollbackManifestPath);
        Assert.Equal(3, options.StartNotificationTimeoutSeconds);
    }

    [Fact]
    public void UpdaterReadsTheApplicationJsonShapeWithoutAcceptingCommandsOrPathsFromIt()
    {
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        HostUpdateRequest request = JsonSerializer.Deserialize<HostUpdateRequest>("""
        {
          "schemaVersion": 1,
          "requestId": "abc123",
          "action": "Update",
          "requestedAtUtc": "2026-09-18T18:00:00Z",
          "requestedByUserId": 42,
          "conversationKey": "123:7",
          "currentVersion": "1.0.56",
          "targetVersion": "1.0.57",
          "expectedSha256": ""
        }
        """, jsonOptions)!;

        Assert.Equal(HostUpdateAction.Update, request.Action);
        Assert.Equal("1.0.57", request.TargetVersion);
        Assert.DoesNotContain("/", request.RequestId, StringComparison.Ordinal);
        Assert.DoesNotContain("/", request.TargetVersion, StringComparison.Ordinal);
    }
}
