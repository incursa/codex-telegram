using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramBotMetadataSyncServiceTests
{
    [Fact]
    public async Task SyncAsync_WhenDisabledDoesNotCallTelegram()
    {
        FakeTelegramBotProfileApiClient client = new();
        TelegramBotMetadataSyncService service = CreateService(enabled: false, client);

        await service.SyncAsync(CancellationToken.None);

        Assert.Empty(client.Commands);
        Assert.Empty(client.Descriptions);
        Assert.Empty(client.ShortDescriptions);
        Assert.Empty(client.DefaultGroupAdministratorRights);
    }

    [Fact]
    public async Task SyncAsync_AppliesCommandsDescriptionShortDescriptionAndDefaultRights()
    {
        FakeTelegramBotProfileApiClient client = new();
        TelegramBotMetadataSyncService service = CreateService(enabled: true, client);

        await service.SyncAsync(CancellationToken.None);

        BotCommand[] commands = Assert.Single(client.Commands);
        Assert.Equal("help", commands[0].Command);
        Assert.Equal("show this help", commands[0].Description);
        Assert.Contains(commands, command => command.Command == "queue");
        Assert.Contains(commands, command => command.Command == "restart");
        Assert.Contains(commands, command => command.Command == "launchpad");
        Assert.Contains(commands, command => command.Command == "launch");
        Assert.Contains(commands, command => command.Command == "goal");
        Assert.Equal(["Controls a local Codex installation from Telegram and routes prompts to sessions."], client.Descriptions);
        Assert.Equal(["Control local Codex sessions from Telegram."], client.ShortDescriptions);
        Assert.Single(client.DefaultGroupAdministratorRights);
        ChatAdministratorRights rights = client.DefaultGroupAdministratorRights[0];
        Assert.True(rights.CanManageTopics);
        Assert.False(rights.CanDeleteMessages);
        Assert.False(rights.CanInviteUsers);
    }

    private static TelegramBotMetadataSyncService CreateService(
        bool enabled,
        FakeTelegramBotProfileApiClient client)
        => new(
            new TelegramBotOptions
            {
                Enabled = enabled,
                Token = "123:token",
            },
            NullLogger<TelegramBotMetadataSyncService>.Instance,
            client);

    private sealed class FakeTelegramBotProfileApiClient : ITelegramBotProfileApiClient
    {
        public List<BotCommand[]> Commands { get; } = [];

        public List<string> Descriptions { get; } = [];

        public List<string> ShortDescriptions { get; } = [];

        public List<ChatAdministratorRights> DefaultGroupAdministratorRights { get; } = [];

        public Task SetMyCommandsAsync(IEnumerable<BotCommand> commands, CancellationToken cancellationToken)
        {
            Commands.Add(commands.ToArray());
            return Task.CompletedTask;
        }

        public Task SetMyDescriptionAsync(string description, CancellationToken cancellationToken)
        {
            Descriptions.Add(description);
            return Task.CompletedTask;
        }

        public Task SetMyShortDescriptionAsync(string shortDescription, CancellationToken cancellationToken)
        {
            ShortDescriptions.Add(shortDescription);
            return Task.CompletedTask;
        }

        public Task SetMyDefaultAdministratorRightsAsync(ChatAdministratorRights rights, CancellationToken cancellationToken)
        {
            DefaultGroupAdministratorRights.Add(rights);
            return Task.CompletedTask;
        }
    }
}
