using System.Globalization;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramMiniAppBrowserPairingStoreTests
{
    [Fact]
    public async Task PairingApprovalCreatesReadOnlySessionAndPersistsOnlyHashes()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z", CultureInfo.InvariantCulture));
        using TelegramMiniAppBrowserPairingStore store = CreateStore(temp.Path, clock);

        TelegramMiniAppBrowserPairingStart start = await store.StartAsync(CancellationToken.None);
        TelegramMiniAppBrowserPairingStatus pending = await store.GetPairingStatusAsync(start.PairingToken, CancellationToken.None);

        Assert.Equal("pending", pending.State);
        Assert.Null(pending.SessionToken);
        Assert.False(await store.ApproveAsync("WRONG-CODE", 1234, CancellationToken.None));
        Assert.True(await store.ApproveAsync(start.PairingCode, 1234, CancellationToken.None));

        TelegramMiniAppBrowserPairingStatus approved = await store.GetPairingStatusAsync(start.PairingToken, CancellationToken.None);
        Assert.Equal("approved", approved.State);
        Assert.Equal(start.PairingToken, approved.SessionToken);
        Assert.Equal(1234, await store.AuthenticateSessionAsync(start.PairingToken, CancellationToken.None));
        Assert.Single(await store.ListSessionsAsync(1234, CancellationToken.None));

        string persisted = await File.ReadAllTextAsync(Path.Combine(temp.Path, "telegram-mini-app-browser-pairing.json"));
        Assert.DoesNotContain(start.PairingCode, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(start.PairingToken, persisted, StringComparison.Ordinal);
        Assert.Contains("codeHash", persisted, StringComparison.Ordinal);
        Assert.Contains("tokenHash", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairingAndSessionExpireAndRevokeFailClosed()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z", CultureInfo.InvariantCulture));
        using TelegramMiniAppBrowserPairingStore store = CreateStore(temp.Path, clock);

        TelegramMiniAppBrowserPairingStart start = await store.StartAsync(CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddMinutes(11);
        Assert.Equal("expired", (await store.GetPairingStatusAsync(start.PairingToken, CancellationToken.None)).State);
        Assert.False(await store.ApproveAsync(start.PairingCode, 1234, CancellationToken.None));

        TelegramMiniAppBrowserPairingStart approvedStart = await store.StartAsync(CancellationToken.None);
        Assert.True(await store.ApproveAsync(approvedStart.PairingCode, 1234, CancellationToken.None));
        Assert.Equal(1, await store.RevokeAsync(1234, CancellationToken.None));
        Assert.Null(await store.AuthenticateSessionAsync(approvedStart.PairingToken, CancellationToken.None));
        Assert.Equal("expired", (await store.GetPairingStatusAsync(approvedStart.PairingToken, CancellationToken.None)).State);
    }

    private static TelegramMiniAppBrowserPairingStore CreateStore(string dataRoot, FixedTimeProvider clock)
        => new(
            Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
            {
                Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot },
            }),
            Microsoft.Extensions.Options.Options.Create(new TelegramMiniAppOptions
            {
                Enabled = true,
                BrowserPairingEnabled = true,
                BrowserPairingLifetimeMinutes = 10,
                BrowserSessionLifetimeHours = 12,
            }),
            clock,
            dataRoot);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
