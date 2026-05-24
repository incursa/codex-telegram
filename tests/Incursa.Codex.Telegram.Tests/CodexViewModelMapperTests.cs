using System.Text.Json.Nodes;
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

    [Fact]
    public void ToTurnVm_DoesNotUseToolOutputAsFinalResponseWhenNoFinalAnswerExists()
    {
        CodexTurnRecord turn = new()
        {
            Id = "turn-789",
            Status = CodexTurnStatus.Completed,
            Items =
            [
                new CodexCommandExecutionItem
                {
                    Id = "item-1",
                    Type = "item.command",
                    Command = "dotnet test",
                    Status = CodexCommandExecutionStatus.Completed,
                },
            ],
        };

        CodexTurnVm vm = CodexViewModelMapper.ToTurnVm(turn);

        Assert.Null(vm.FinalResponse);
    }

    [Fact]
    public void ToTimelineEntryVm_MapsUnknownAgentMessageDelta()
    {
        CodexTimelineEntryVm entry = CodexViewModelMapper.ToTimelineEntryVm(
            new CodexUnknownThreadEvent("item.agentMessage.delta")
            {
                RawPayload = new JsonObject
                {
                    ["threadId"] = "thread-raw",
                    ["turnId"] = "turn-raw",
                    ["delta"] = "streamed assistant text",
                },
            });

        Assert.Equal("item.agentMessage.delta", entry.Type);
        Assert.Equal("thread-raw", entry.ThreadId);
        Assert.Equal("turn-raw", entry.TurnId);
        Assert.Equal("streamed assistant text", entry.Body);
        Assert.True(entry.IsInternal);
    }

    [Fact]
    public void ToTimelineEntryVm_MapsNormalizedFinalResponseAsVisibleOutput()
    {
        CodexTimelineEntryVm entry = CodexViewModelMapper.ToTimelineEntryVm(
            new CodexTurnEvent
            {
                RawEventType = "turn.completed",
                Kind = CodexTurnEventKind.FinalResponse,
                Importance = CodexTurnEventImportance.High,
                ThreadId = "thread-normalized",
                TurnId = "turn-normalized",
                Text = "final answer",
                Metadata = new Dictionary<string, string?>
                {
                    ["source"] = CodexFinalResponseSource.TerminalEvent.ToString(),
                    ["complete"] = true.ToString(),
                },
                ContributesToFinalOutput = true,
                IsUserVisibleByDefault = true,
            });

        Assert.Equal("turn.finalResponse", entry.Type);
        Assert.Equal("Final response", entry.Title);
        Assert.Equal("final answer", entry.Body);
        Assert.Equal("thread-normalized", entry.ThreadId);
        Assert.Equal("turn-normalized", entry.TurnId);
        Assert.Equal("FinalResponse", entry.Metadata["normalizedKind"]);
        Assert.Equal("turn.completed", entry.Metadata["rawEventType"]);
        Assert.False(entry.IsInternal);
    }

    [Fact]
    public void ToTimelineEntryVm_MapsIncompleteNormalizedTerminalAsLifecycleState()
    {
        CodexTimelineEntryVm entry = CodexViewModelMapper.ToTimelineEntryVm(
            new CodexTurnEvent
            {
                RawEventType = "turn.stream.ended",
                Kind = CodexTurnEventKind.Terminal,
                Importance = CodexTurnEventImportance.Critical,
                ThreadId = "thread-normalized",
                TurnId = "turn-normalized",
                Text = "The SDK did not observe turn.completed or turn.failed.",
                IsTerminal = true,
                TerminalState = CodexTurnTerminalState.Incomplete,
                IsUserVisibleByDefault = true,
            });

        Assert.Equal("turn.stream.ended", entry.Type);
        Assert.Equal("Turn stream ended without a terminal event", entry.Title);
        Assert.Equal("danger", entry.Severity);
        Assert.True(bool.Parse(entry.Metadata["terminal"]!));
        Assert.Equal("Incomplete", entry.Metadata["terminalState"]);
        Assert.False(entry.IsInternal);
    }

    [Fact]
    public void ToTurnItemVm_MarksExplicitImageViewMedia()
    {
        CodexTimelineEntryVm item = CodexViewModelMapper.ToTurnItemVm(new CodexImageViewItem
        {
            Id = "item-image",
            Type = "image_view",
            Path = @"C:\temp\screenshot.png",
        });

        Assert.Equal("image-view", item.Metadata["explicitMediaKind"]);
        Assert.Equal(@"C:\temp\screenshot.png", item.Metadata["path"]);
    }

    [Fact]
    public void ToTurnItemVm_MarksUnknownImageGenerationMedia()
    {
        CodexTimelineEntryVm item = CodexViewModelMapper.ToTurnItemVm(new CodexUnknownThreadItem("imageGeneration")
        {
            Id = "ig-test",
            RawPayload = new JsonObject
            {
                ["type"] = "image_generation_call",
                ["result"] = "base64-result",
                ["status"] = "completed",
                ["mime_type"] = "image/png",
            },
        });

        Assert.Equal("image-generation", item.Metadata["explicitMediaKind"]);
        Assert.Equal("base64-result", item.Metadata["result"]);
        Assert.Equal("completed", item.Metadata["status"]);
        Assert.Equal("ig-test", item.Metadata["id"]);
        Assert.Equal("image/png", item.Metadata["contentType"]);
    }

    [Fact]
    public void ToTimelineEntryVm_MapsPlanUpdatedEventsAsVisiblePlanModeUpdates()
    {
        CodexTimelineEntryVm entry = CodexViewModelMapper.ToTimelineEntryVm(
            new CodexTurnPlanUpdatedEvent
            {
                ThreadId = "thread-plan",
                TurnId = "turn-plan",
                Explanation = "Need to confirm scope.",
                Plan =
                [
                    new CodexTurnPlanStep { Step = "Inspect repo", Status = CodexTurnPlanStepStatus.Completed },
                    new CodexTurnPlanStep { Step = "Ask clarifying question", Status = CodexTurnPlanStepStatus.InProgress },
                    new CodexTurnPlanStep { Step = "Draft implementation", Status = CodexTurnPlanStepStatus.Pending },
                ],
            });

        Assert.False(entry.IsInternal);
        Assert.Equal("Plan mode update", entry.Title);
        Assert.Equal("thread-plan", entry.ThreadId);
        Assert.Equal("turn-plan", entry.TurnId);
        Assert.Equal("plan", entry.Metadata["mode"]);
        Assert.Contains("[x] Inspect repo", entry.Body);
        Assert.Contains("[>] Ask clarifying question", entry.Body);
        Assert.Contains("[ ] Draft implementation", entry.Body);
    }
}
