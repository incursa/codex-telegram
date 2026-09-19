using System.Text.Json;
using System.Text.Json.Serialization;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexHostUpdateManagerTests
{
    [Fact]
    public async Task RequestWritesAtomicExternalUpdaterContract()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        using CodexHostUpdateManager manager = new(options, TimeProvider.System, dataRoot.Path);

        CodexHostUpdateSnapshot result = await manager.RequestAsync(
            CodexHostUpdateAction.Update,
            new TelegramConversationScope(1234, 7),
            42,
            CancellationToken.None);

        Assert.Equal(CodexHostUpdateState.Requested, result.State);
        Assert.Equal("update_request_written", result.OutcomeCode);
        Assert.NotNull(result.RequestId);

        string statePath = Path.Combine(dataRoot.Path, "codex-host-update-state.json");
        string requestPath = Path.Combine(dataRoot.Path, "codex-host-update-request.json");
        Assert.True(File.Exists(statePath));
        Assert.True(File.Exists(requestPath));
        string request = await File.ReadAllTextAsync(requestPath);
        Assert.Contains(result.RequestId, request, StringComparison.Ordinal);
        Assert.Contains("1234:7", request, StringComparison.Ordinal);
        Assert.DoesNotContain("codex-host-update-request.json", await File.ReadAllTextAsync(statePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestRejectsConcurrentUpdateWithoutReplacingRequestIdentity()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        using CodexHostUpdateManager manager = new(options, TimeProvider.System, dataRoot.Path);
        TelegramConversationScope conversation = new(1234, null);

        CodexHostUpdateSnapshot first = await manager.RequestAsync(CodexHostUpdateAction.Update, conversation, 42, CancellationToken.None);
        CodexHostUpdateSnapshot second = await manager.RequestAsync(CodexHostUpdateAction.Update, conversation, 42, CancellationToken.None);

        Assert.Equal(CodexHostUpdateState.Requested, first.State);
        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal("update_already_in_progress", second.OutcomeCode);
    }

    [Fact]
    public async Task TerminalExternalStateProducesOneCompletionNotification()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        using CodexHostUpdateManager manager = new(options, TimeProvider.System, dataRoot.Path);
        TelegramConversationScope conversation = new(1234, null);
        CodexHostUpdateSnapshot requested = await manager.RequestAsync(CodexHostUpdateAction.Update, conversation, 42, CancellationToken.None);

        string statePath = Path.Combine(dataRoot.Path, "codex-host-update-state.json");
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };
        CodexHostUpdateManager.HostUpdateState active = new(
            1,
            CodexHostUpdateState.Active,
            CodexHostUpdateAction.Update,
            requested.RequestId,
            42,
            conversation.ToStorageKey(),
            requested.CurrentVersion,
            requested.TargetVersion,
            requested.RequestedAtUtc,
            DateTimeOffset.UtcNow,
            "health_verified",
            true);
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(active, jsonOptions));

        CodexHostUpdateSnapshot? notification = await manager.TryGetPendingNotificationAsync(CancellationToken.None);
        Assert.NotNull(notification);
        Assert.Equal(CodexHostUpdateState.Active, notification.State);
        Assert.Equal(conversation.ToStorageKey(), await manager.GetConversationKeyAsync(requested.RequestId!, CancellationToken.None));
        Assert.True(await manager.MarkNotificationSentAsync(requested.RequestId!, CancellationToken.None));
        Assert.False(await manager.MarkNotificationSentAsync(requested.RequestId!, CancellationToken.None));
        Assert.Null(await manager.TryGetPendingNotificationAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ApplyingExternalStateProducesOneStartNotification()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        using CodexHostUpdateManager manager = new(options, TimeProvider.System, dataRoot.Path);
        TelegramConversationScope conversation = new(1234, null);
        CodexHostUpdateSnapshot requested = await manager.RequestAsync(CodexHostUpdateAction.Update, conversation, 42, CancellationToken.None);

        string statePath = Path.Combine(dataRoot.Path, "codex-host-update-state.json");
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };
        CodexHostUpdateManager.HostUpdateState applying = new(
            1,
            CodexHostUpdateState.Applying,
            CodexHostUpdateAction.Update,
            requested.RequestId,
            42,
            conversation.ToStorageKey(),
            requested.CurrentVersion,
            "1.0.59",
            requested.RequestedAtUtc,
            DateTimeOffset.UtcNow,
            null,
            true);
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(applying, jsonOptions));

        CodexHostUpdateSnapshot? notification = await manager.TryGetPendingNotificationAsync(CancellationToken.None);
        Assert.NotNull(notification);
        Assert.Equal(CodexHostUpdateState.Applying, notification.State);
        Assert.True(await manager.MarkNotificationSentAsync(requested.RequestId!, CancellationToken.None));
        Assert.Null(await manager.TryGetPendingNotificationAsync(CancellationToken.None));
    }

    private static IOptions<CodexTelegramOptions> CreateOptions(string dataRoot)
        => Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot },
            HostUpdate = new CodexHostUpdateOptions
            {
                Enabled = true,
                TargetVersion = "1.0.57",
                ExpectedSha256 = new string('a', 64),
            },
        });
}
