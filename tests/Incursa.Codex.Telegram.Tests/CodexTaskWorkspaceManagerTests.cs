using System.Diagnostics;
using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Services;
using Microsoft.Extensions.Options;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexTaskWorkspaceManagerTests
{
    [Fact]
    public async Task CreateProvisionsIsolatedWorktreeAndBoundedResources()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string repository = temp.CreateDirectory("repository");
        string dataRoot = temp.CreateDirectory("state");
        await InitializeRepositoryAsync(repository);
        using CodexTaskWorkspaceManager manager = CreateManager(dataRoot, temp.CreateDirectory("worktrees"));

        CodexTaskWorkspaceRecord first = await manager.CreateAsync("task:alpha", repository, "HEAD", CancellationToken.None);
        CodexTaskWorkspaceRecord replay = await manager.CreateAsync("task:alpha", repository, "main", CancellationToken.None);

        Assert.Equal(first, replay);
        Assert.Equal(CodexTaskWorkspaceState.Provisioned, first.State);
        Assert.True(Directory.Exists(first.WorktreePath));
        Assert.True(File.Exists(Path.Combine(first.WorktreePath, "README.md")));
        Assert.StartsWith("codex/task/task-alpha-", first.Branch, StringComparison.Ordinal);
        Assert.InRange(first.DevelopmentPort, 45100, 45110);
        Assert.StartsWith("codex_task_task-alpha_", first.DatabaseNamespace, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, first.DatabaseNamespace);
        Assert.Single(await manager.ListAsync(CancellationToken.None));

        string statePath = Path.Combine(dataRoot, "codex-task-workspaces.json");
        string persisted = await File.ReadAllTextAsync(statePath);
        Assert.Contains(first.WorktreePath.Replace("\\", "\\\\", StringComparison.Ordinal), persisted, StringComparison.Ordinal);
        Assert.Contains(first.DatabaseNamespace, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeparateTasksReceiveSeparateWorktreesAndPorts()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string repository = temp.CreateDirectory("repository");
        await InitializeRepositoryAsync(repository);
        using CodexTaskWorkspaceManager manager = CreateManager(temp.CreateDirectory("state"), temp.CreateDirectory("worktrees"));

        CodexTaskWorkspaceRecord first = await manager.CreateAsync("task:first", repository, null, CancellationToken.None);
        CodexTaskWorkspaceRecord second = await manager.CreateAsync("task:second", repository, null, CancellationToken.None);

        Assert.NotEqual(first.WorktreePath, second.WorktreePath);
        Assert.NotEqual(first.Branch, second.Branch);
        Assert.NotEqual(first.DatabaseNamespace, second.DatabaseNamespace);
        Assert.NotEqual(first.DevelopmentPort, second.DevelopmentPort);
        Assert.Equal(2, (await manager.ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task CleanReleaseRemovesOnlyRecordedWorktreeAndRetainsEvidence()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string repository = temp.CreateDirectory("repository");
        await InitializeRepositoryAsync(repository);
        using CodexTaskWorkspaceManager manager = CreateManager(temp.CreateDirectory("state"), temp.CreateDirectory("worktrees"));
        CodexTaskWorkspaceRecord created = await manager.CreateAsync("task:release", repository, null, CancellationToken.None);

        CodexTaskWorkspaceRecord? released = await manager.ReleaseAsync(created.TaskId, discardChanges: false, CancellationToken.None);

        Assert.NotNull(released);
        Assert.Equal(CodexTaskWorkspaceState.Released, released.State);
        Assert.Equal("released_clean", released.OutcomeCode);
        Assert.False(Directory.Exists(created.WorktreePath));
        Assert.Equal(released, await manager.GetAsync(created.TaskId, CancellationToken.None));
        Assert.Equal(released, await manager.ReleaseAsync(created.TaskId, discardChanges: false, CancellationToken.None));
    }

    [Fact]
    public async Task DirtyReleaseFailsClosedUntilExplicitDiscard()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        string repository = temp.CreateDirectory("repository");
        await InitializeRepositoryAsync(repository);
        using CodexTaskWorkspaceManager manager = CreateManager(temp.CreateDirectory("state"), temp.CreateDirectory("worktrees"));
        CodexTaskWorkspaceRecord created = await manager.CreateAsync("task:dirty", repository, null, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(created.WorktreePath, "README.md"), "uncommitted change");

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReleaseAsync(created.TaskId, discardChanges: false, CancellationToken.None));

        CodexTaskWorkspaceRecord? stillProvisioned = await manager.GetAsync(created.TaskId, CancellationToken.None);
        Assert.NotNull(stillProvisioned);
        Assert.Equal(CodexTaskWorkspaceState.Provisioned, stillProvisioned.State);
        CodexTaskWorkspaceRecord? discarded = await manager.ReleaseAsync(created.TaskId, discardChanges: true, CancellationToken.None);
        Assert.NotNull(discarded);
        Assert.Equal("released_discarded", discarded.OutcomeCode);
        Assert.False(Directory.Exists(created.WorktreePath));
    }

    [Fact]
    public async Task InvalidRepositoryAndBaseRefAreRejectedWithoutState()
    {
        using TemporaryDirectory temp = TemporaryDirectory.Create();
        using CodexTaskWorkspaceManager manager = CreateManager(temp.CreateDirectory("state"), temp.CreateDirectory("worktrees"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            manager.CreateAsync("task:missing", Path.Combine(temp.Path, "missing"), null, CancellationToken.None));

        string repository = temp.CreateDirectory("repository");
        await InitializeRepositoryAsync(repository);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.CreateAsync("task:bad", repository, "main\n--force", CancellationToken.None));

        Assert.Empty(await manager.ListAsync(CancellationToken.None));
    }

    private static CodexTaskWorkspaceManager CreateManager(string dataRoot, string worktreeRoot)
        => new(
            Microsoft.Extensions.Options.Options.Create(new CodexTelegramOptions
            {
                Workspace = new CodexWorkspaceOptions
                {
                    DataRoot = dataRoot,
                    TaskWorktreeRoot = worktreeRoot,
                    TaskDevelopmentPortRangeStart = 45100,
                    TaskDevelopmentPortRangeEnd = 45110,
                },
            }),
            TimeProvider.System,
            dataRoot);

    private static async Task InitializeRepositoryAsync(string repository)
    {
        await RunGitAsync(repository, ["init", "-b", "main"]);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "task workspace fixture");
        await RunGitAsync(repository, ["add", "README.md"]);
        await RunGitAsync(repository, ["-c", "user.name=Codex Test", "-c", "user.email=codex-test@example.test", "commit", "-m", "initial"]);
    }

    private static async Task RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        Assert.True(process.Start());
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git failed: {error}{output}");
    }
}
