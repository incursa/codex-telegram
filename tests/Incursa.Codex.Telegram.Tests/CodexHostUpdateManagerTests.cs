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

    [Fact]
    public async Task ManualVersionChangeReconcilesQueuedRequestAndRemovesReplayFile()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        using CodexHostUpdateManager manager = new(options, TimeProvider.System, dataRoot.Path);
        CodexHostUpdateSnapshot requested = await manager.RequestAsync(
            CodexHostUpdateAction.Update,
            new TelegramConversationScope(1234, null),
            42,
            CancellationToken.None);

        string statePath = Path.Combine(dataRoot.Path, "codex-host-update-state.json");
        JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };
        CodexHostUpdateManager.HostUpdateState queuedBeforeManualInstall = new(
            1,
            CodexHostUpdateState.Requested,
            CodexHostUpdateAction.Update,
            requested.RequestId,
            42,
            "1234",
            "1.0.59.0",
            "1.0.60",
            requested.RequestedAtUtc,
            requested.UpdatedAtUtc,
            null,
            false);
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(queuedBeforeManualInstall, jsonOptions));

        CodexHostUpdateSnapshot status = await manager.GetStatusAsync(CancellationToken.None);

        Assert.Equal(CodexHostUpdateState.Active, status.State);
        Assert.Equal("manual_update_detected", status.OutcomeCode);
        Assert.True(status.NotificationPending);
        Assert.False(File.Exists(Path.Combine(dataRoot.Path, "codex-host-update-request.json")));
    }

    [Fact]
    public async Task CancelPendingRemovesQueuedRequestWithoutReachingUpdater()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        using CodexHostUpdateManager manager = new(options, TimeProvider.System, dataRoot.Path);
        await manager.RequestAsync(CodexHostUpdateAction.Update, new TelegramConversationScope(1234, null), 42, CancellationToken.None);

        CodexHostUpdateSnapshot cancelled = await manager.CancelPendingAsync(CancellationToken.None);

        Assert.Equal(CodexHostUpdateState.None, cancelled.State);
        Assert.Equal("update_request_cancelled", cancelled.OutcomeCode);
        Assert.False(File.Exists(Path.Combine(dataRoot.Path, "codex-host-update-request.json")));
        Assert.Equal(CodexHostUpdateState.None, (await manager.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task CancelPendingRefusesWhenUpdaterOwnsTheSharedLock()
    {
        using TemporaryDirectory dataRoot = TemporaryDirectory.Create();
        IOptions<CodexTelegramOptions> options = CreateOptions(dataRoot.Path);
        using CodexHostUpdateManager manager = new(options, TimeProvider.System, dataRoot.Path);
        await manager.RequestAsync(CodexHostUpdateAction.Update, new TelegramConversationScope(1234, null), 42, CancellationToken.None);

        using FileStream updaterLock = new(
            Path.Combine(dataRoot.Path, "codex-host-update.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        CodexHostUpdateSnapshot result = await manager.CancelPendingAsync(CancellationToken.None);

        Assert.Equal(CodexHostUpdateState.Requested, result.State);
        Assert.Equal("update_in_progress_cannot_cancel", result.OutcomeCode);
        Assert.True(File.Exists(Path.Combine(dataRoot.Path, "codex-host-update-request.json")));
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
