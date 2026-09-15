using System.Text.Json;
using Incursa.Codex.Telegram.MiniApp;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramMiniAppTaskActionTests
{
    [Fact]
    public async Task AcknowledgementIsDurableIdempotentAndUserScoped()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        using TelegramMiniAppAcknowledgementStore store = CreateStore(temp.Path, clock);

        TelegramMiniAppAcknowledgementSnapshot? first = await store.AcknowledgeAsync(
            42,
            "task:1",
            "run:1",
            "review:1",
            CancellationToken.None);
        TelegramMiniAppAcknowledgementSnapshot? replay = await store.AcknowledgeAsync(
            42,
            "task:1",
            "run:1",
            "review:1",
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first, replay);
        Assert.Single(await store.ListAsync(42, CancellationToken.None));
        Assert.Empty(await store.ListAsync(99, CancellationToken.None));
        Assert.Contains("task:1", await File.ReadAllTextAsync(Path.Combine(temp.Path, "telegram-mini-app-acknowledgements.json")), StringComparison.Ordinal);

        using TelegramMiniAppAcknowledgementStore reloaded = CreateStore(temp.Path, clock);
        TelegramMiniAppAcknowledgementSnapshot persisted = Assert.Single(await reloaded.ListAsync(42, CancellationToken.None));
        Assert.Equal(first, persisted);
    }

    [Fact]
    public async Task TaskActionRequiresAuthenticationAndRejectsStaleAcknowledgement()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        IOptions<CodexTelegramOptions> options = CreateHostOptions(temp.Path);
        IOptions<TelegramMiniAppOptions> miniAppOptions = CreateMiniAppOptions();
        using CodexSupervisionLedger ledger = new(options, clock, temp.Path);
        CodexSupervisionCommandStart started = await ledger.StartCommandAsync(
            "command:1",
            "thread:1",
            "Task one",
            new TelegramConversationScope(42, null),
            42,
            "prompt",
            CancellationToken.None);
        Assert.NotNull(started.Task);
        Assert.NotNull(started.Run);

        using TelegramMiniAppBrowserPairingStore pairingStore = CreatePairingStore(temp.Path, clock);
        TelegramMiniAppBrowserPairingStart pairing = await pairingStore.StartAsync(CancellationToken.None);
        Assert.True(await pairingStore.ApproveAsync(pairing.PairingCode, 42, CancellationToken.None));
        using TelegramMiniAppAcknowledgementStore acknowledgementStore = CreateStore(temp.Path, clock);
        TelegramMiniAppAuth auth = CreateAuth(clock);

        DefaultHttpContext unauthorized = CreateContext(new { Action = "acknowledge", RunId = started.Run!.RunId, PacketId = "review:1" });
        IResult unauthorizedResult = await TelegramMiniAppTaskActionEndpoints.HandleAsync(
            started.Task!.TaskId,
            unauthorized,
            auth,
            miniAppOptions,
            ledger,
            pairingStore,
            acknowledgementStore,
            CancellationToken.None);
        await unauthorizedResult.ExecuteAsync(unauthorized);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorized.Response.StatusCode);

        DefaultHttpContext stale = CreateContext(
            new { Action = "acknowledge", RunId = "run:stale", PacketId = "review:1" },
            pairing.PairingToken);
        IResult staleResult = await TelegramMiniAppTaskActionEndpoints.HandleAsync(
            started.Task.TaskId,
            stale,
            auth,
            miniAppOptions,
            ledger,
            pairingStore,
            acknowledgementStore,
            CancellationToken.None);
        await staleResult.ExecuteAsync(stale);
        Assert.Equal(StatusCodes.Status409Conflict, stale.Response.StatusCode);
        Assert.Empty(await acknowledgementStore.ListAsync(42, CancellationToken.None));
    }

    [Fact]
    public async Task TaskActionAcknowledgesExactRunAndPreparesTelegramHandoff()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        FixedTimeProvider clock = new(DateTimeOffset.Parse("2026-09-15T12:00:00Z"));
        IOptions<CodexTelegramOptions> options = CreateHostOptions(temp.Path);
        IOptions<TelegramMiniAppOptions> miniAppOptions = CreateMiniAppOptions();
        using CodexSupervisionLedger ledger = new(options, clock, temp.Path);
        CodexSupervisionCommandStart started = await ledger.StartCommandAsync(
            "command:1",
            "thread:1",
            "Task one",
            new TelegramConversationScope(42, null),
            42,
            "prompt",
            CancellationToken.None);
        CodexSupervisionTaskRecord task = Assert.IsType<CodexSupervisionTaskRecord>(started.Task);
        CodexSupervisionRunRecord run = Assert.IsType<CodexSupervisionRunRecord>(started.Run);

        using TelegramMiniAppBrowserPairingStore pairingStore = CreatePairingStore(temp.Path, clock);
        TelegramMiniAppBrowserPairingStart pairing = await pairingStore.StartAsync(CancellationToken.None);
        Assert.True(await pairingStore.ApproveAsync(pairing.PairingCode, 42, CancellationToken.None));
        using TelegramMiniAppAcknowledgementStore acknowledgementStore = CreateStore(temp.Path, clock);
        TelegramMiniAppAuth auth = CreateAuth(clock);

        DefaultHttpContext acknowledge = CreateContext(
            new { Action = "acknowledge", RunId = run.RunId, PacketId = "review:packet" },
            pairing.PairingToken);
        IResult acknowledgeResult = await TelegramMiniAppTaskActionEndpoints.HandleAsync(
            task.TaskId,
            acknowledge,
            auth,
            miniAppOptions,
            ledger,
            pairingStore,
            acknowledgementStore,
            CancellationToken.None);
        await acknowledgeResult.ExecuteAsync(acknowledge);
        Assert.Equal(StatusCodes.Status200OK, acknowledge.Response.StatusCode);
        Assert.Single(await acknowledgementStore.ListAsync(42, CancellationToken.None));

        DefaultHttpContext handoff = CreateContext(
            new { Action = "handoff", RunId = run.RunId, PacketId = "review:packet" },
            pairing.PairingToken);
        IResult handoffResult = await TelegramMiniAppTaskActionEndpoints.HandleAsync(
            task.TaskId,
            handoff,
            auth,
            miniAppOptions,
            ledger,
            pairingStore,
            acknowledgementStore,
            CancellationToken.None);
        await handoffResult.ExecuteAsync(handoff);
        string handoffBody = await ReadResponseAsync(handoff);
        Assert.Equal(StatusCodes.Status200OK, handoff.Response.StatusCode);
        Assert.Contains("/handoff thread:1", handoffBody, StringComparison.Ordinal);
        Assert.Contains("telegram_handoff_ready", handoffBody, StringComparison.Ordinal);
    }

    private static TelegramMiniAppAcknowledgementStore CreateStore(string dataRoot, FixedTimeProvider clock)
        => new(CreateHostOptions(dataRoot), clock, dataRoot);

    private static TelegramMiniAppBrowserPairingStore CreatePairingStore(string dataRoot, FixedTimeProvider clock)
        => new(
            CreateHostOptions(dataRoot),
            CreateMiniAppOptions(),
            clock,
            dataRoot);

    private static TelegramMiniAppAuth CreateAuth(FixedTimeProvider clock)
        => new(
            Microsoft.Extensions.Options.Options.Create(new TelegramBotOptions
            {
                Token = "secret-token",
                AllowedUserIds = [42],
            }),
            CreateMiniAppOptions(),
            clock);

    private static IOptions<CodexTelegramOptions> CreateHostOptions(string dataRoot)
        => Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
        {
            Workspace = new CodexWorkspaceOptions { DataRoot = dataRoot },
        });

    private static IOptions<TelegramMiniAppOptions> CreateMiniAppOptions()
        => Microsoft.Extensions.Options.Options.Create(new TelegramMiniAppOptions
        {
            Enabled = true,
            BrowserPairingEnabled = true,
            BrowserPairingLifetimeMinutes = 10,
            BrowserSessionLifetimeHours = 12,
        });

    private static DefaultHttpContext CreateContext(object payload, string? sessionToken = null)
    {
        DefaultHttpContext context = new();
        context.RequestServices = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();
        if (sessionToken is not null)
        {
            context.Request.Headers["X-Codex-Browser-Session"] = sessionToken;
        }

        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using StreamReader reader = new(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
