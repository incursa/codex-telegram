using Incursa.Codex.Telegram.Configuration;
using Incursa.Codex.Telegram.Options;
using Telegram.Bot.Types;

namespace Incursa.Codex.Telegram.Tests;

public sealed class TelegramProfileSetupTests
{
    [Fact]
    public void CommandCatalogIsAppOwnedAndReturnsFreshLists()
    {
        IReadOnlyList<BotCommand> first = TelegramCommandCatalog.CreateDefaultCommands();
        IReadOnlyList<BotCommand> second = TelegramCommandCatalog.CreateDefaultCommands();

        Assert.Equal(first.Select(command => command.Command), second.Select(command => command.Command));
        Assert.Contains(first, command => command.Command == "doctor");
        Assert.Contains(first, command => command.Command == "topic");
        Assert.Contains(first, command => command.Command == "home");
        Assert.Contains(first, command => command.Command == "resume");
        Assert.DoesNotContain(first, command => command.Command is "model" or "thinking" or "debug" or "outbound");
        Assert.DoesNotContain(first, command => command.Command.StartsWith('/'));
        Assert.NotSame(first, second);
    }

    [Fact]
    public void RepositoryCommandCatalogOmitsProjectSwitchingAndAddsRepositorySummary()
    {
        IReadOnlyList<BotCommand> commands = TelegramCommandCatalog.CreateDefaultCommands(CodexTelegramMode.Repository);

        Assert.Contains(commands, command => command.Command == "repo");
        Assert.DoesNotContain(commands, command => command.Command is "project" or "projects");
    }

    [Fact]
    public async Task ApplyRunsAppOwnedOperationsIncludingOptedInProfileFields()
    {
        FakeProfileClient client = new();
        TelegramProfileSetup setup = new(client);

        TelegramProfileSetupResult result = await setup.ApplyAsync(
            "123456:token-value",
            new TelegramProfileSetupOptions
            {
                Name = "A name",
                Description = "A description",
                ShortDescription = "A short description",
            });

        Assert.True(result.Succeeded);
        Assert.Equal(["setMyCommands", "setChatMenuButton", "setMyName", "setMyDescription", "setMyShortDescription"], client.Operations);
        Assert.All(result.Operations.Take(2), operation => Assert.Equal(TelegramSetupOperationStatus.Succeeded, operation.Status));
        Assert.All(result.Operations, operation => Assert.Equal(TelegramSetupOperationStatus.Succeeded, operation.Status));
    }

    [Fact]
    public async Task ExplicitManualOwnershipLeavesProfileFieldUntouched()
    {
        FakeProfileClient client = new();
        TelegramProfileSetupResult result = await new TelegramProfileSetup(client).ApplyAsync(
            "123456:token-value",
            new TelegramProfileSetupOptions
            {
                Name = "BotFather name",
                NameOwnership = TelegramSetupOwnership.Manual,
            });

        TelegramSetupOperationResult name = Assert.Single(result.Operations, operation => operation.Operation == "setMyName");
        Assert.Equal(TelegramSetupOperationStatus.Skipped, name.Status);
        Assert.DoesNotContain("setMyName", client.Operations);
    }

    [Fact]
    public async Task OneFailureDoesNotHideOtherOperationResultsOrToken()
    {
        FakeProfileClient client = new()
        {
            Failure = new InvalidOperationException("request included 123456:token-value"),
            FailureOperation = "setMyCommands",
        };
        TelegramProfileSetup setup = new(client);

        TelegramProfileSetupResult result = await setup.ApplyAsync("123456:token-value");

        Assert.True(result.HasFailures);
        Assert.Equal(2, result.Operations.Count);
        Assert.Contains("[redacted]", result.Operations[0].FailureReason);
        Assert.DoesNotContain("123456:token-value", result.Operations[0].FailureReason);
        Assert.Equal(TelegramSetupOperationStatus.Failed, result.Operations[0].Status);
        Assert.Equal(TelegramSetupOperationStatus.Succeeded, result.Operations[1].Status);
    }

    [Fact]
    public async Task FailedProfilePhotoIncludesBotFatherFallbackWithoutLeakingPath()
    {
        FakeProfileClient client = new()
        {
            Failure = new InvalidOperationException("photo failed"),
            FailureOperation = "setMyProfilePhoto",
        };
        TelegramProfileSetup setup = new(client);

        TelegramProfileSetupResult result = await setup.ApplyAsync(
            "123456:token-value",
            new TelegramProfileSetupOptions
            {
                ProfilePhotoPath = "C:\\private\\photo.jpg",
                ProfilePhotoOwnership = TelegramSetupOwnership.AppManaged,
            });

        TelegramSetupOperationResult photo = Assert.Single(result.Operations, operation => operation.Operation == "setMyProfilePhoto");
        Assert.Equal(TelegramSetupOperationStatus.Failed, photo.Status);
        Assert.Contains("@BotFather", photo.BotFatherFallback);
        Assert.DoesNotContain("C:\\private", photo.BotFatherFallback);
    }

    private sealed class FakeProfileClient : ITelegramProfileSetupClient
    {
        public List<string> Operations { get; } = [];

        public Exception? Failure { get; init; }

        public string? FailureOperation { get; init; }

        public Task SetMyCommandsAsync(string token, IEnumerable<BotCommand> commands, string? languageCode, CancellationToken cancellationToken)
            => RunAsync("setMyCommands");

        public Task SetChatMenuButtonAsync(string token, long? chatId, MenuButton menuButton, CancellationToken cancellationToken)
            => RunAsync("setChatMenuButton");

        public Task SetMyNameAsync(string token, string name, string? languageCode, CancellationToken cancellationToken)
            => RunAsync("setMyName");

        public Task SetMyDescriptionAsync(string token, string description, string? languageCode, CancellationToken cancellationToken)
            => RunAsync("setMyDescription");

        public Task SetMyShortDescriptionAsync(string token, string shortDescription, string? languageCode, CancellationToken cancellationToken)
            => RunAsync("setMyShortDescription");

        public Task SetMyProfilePhotoAsync(string token, string profilePhotoPath, CancellationToken cancellationToken)
            => RunAsync("setMyProfilePhoto");

        private Task RunAsync(string operation)
        {
            Operations.Add(operation);
            return Failure is not null && (FailureOperation is null || FailureOperation == operation)
                ? Task.FromException(Failure)
                : Task.CompletedTask;
        }
    }
}
