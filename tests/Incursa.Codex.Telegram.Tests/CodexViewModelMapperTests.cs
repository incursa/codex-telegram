using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Services;
using Incursa.OpenAI.Codex;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexViewModelMapperTests
{
    [Fact]
    public void RepairUtf8Mojibake_RewritesCp437SmartQuotes()
    {
        string corrupted = "The repo itself doesnΓÇÖt expose the RFC label, so IΓÇÖm checking the local project shape and docs.";

        string? repaired = CodexTextFormatting.RepairUtf8Mojibake(corrupted);

        Assert.Equal("The repo itself doesn’t expose the RFC label, so I’m checking the local project shape and docs.", repaired);
    }

    [Fact]
    public void InferThreadName_RewritesCorruptedExplicitName()
    {
        string inferred = CodexTextFormatting.InferThreadName("The repo itself doesnΓÇÖt expose the RFC label", null);

        Assert.Equal("The repo itself doesn’t expose the RFC label", inferred);
    }

    [Fact]
    public void ToThreadListItemVm_RewritesCorruptedThreadText()
    {
        CodexThreadSummary summary = new()
        {
            Id = "thread-123",
            Name = "The repo itself doesnΓÇÖt expose the RFC label",
            Preview = "IΓÇÖm checking the local project shape and docs.",
            Status = new CodexIdleThreadStatus(),
            ModelProvider = "openai",
            CreatedAt = DateTimeOffset.Parse("2026-05-02T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-05-02T00:05:00Z"),
            Ephemeral = false,
            Path = @"C:\src\my-repo",
            AgentRole = "assistant",
            AgentNickname = "Codex",
        };

        CodexThreadListItemVm vm = CodexViewModelMapper.ToThreadListItemVm(summary, null);

        Assert.Equal("The repo itself doesn’t expose the RFC label", vm.Name);
        Assert.Equal("I’m checking the local project shape and docs.", vm.Preview);
    }

    [Fact]
    public void ToTurnVm_RewritesCorruptedFinalResponseAndErrorMessage()
    {
        CodexTurnRecord turn = new()
        {
            Id = "turn-456",
            Status = CodexTurnStatus.Completed,
            Error = new CodexTurnError
            {
                Message = "The repo itself doesnΓÇÖt expose the RFC label, so IΓÇÖm checking the local project shape and docs.",
            },
            Items =
            [
                new CodexAgentMessageItem
                {
                    Id = "item-1",
                    Type = "agentMessage",
                    Phase = CodexMessagePhase.FinalAnswer,
                    Text = "The repo itself doesnΓÇÖt expose the RFC label, so IΓÇÖm checking the local project shape and docs.",
                },
            ],
        };

        CodexTurnVm vm = CodexViewModelMapper.ToTurnVm(turn);

        Assert.Equal("The repo itself doesn’t expose the RFC label, so I’m checking the local project shape and docs.", vm.ErrorMessage);
        Assert.Equal("The repo itself doesn’t expose the RFC label, so I’m checking the local project shape and docs.", vm.FinalResponse);
    }
}
