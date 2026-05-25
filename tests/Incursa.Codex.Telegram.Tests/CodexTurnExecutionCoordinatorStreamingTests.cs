using Incursa.Codex.Telegram.Options;
using Incursa.Codex.Telegram.Models;
using Incursa.Codex.Telegram.Services;
using Incursa.Codex.Telegram.Telegram;
using Incursa.Codex.Telegram.Tests.TestSupport;
using Incursa.OpenAI.Codex;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace Incursa.Codex.Telegram.Tests;

public sealed class CodexTurnExecutionCoordinatorStreamingTests
{
    [Fact]
    public async Task StartAsync_StreamsLongAssistantResponseThroughRelay()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddDelta("hello world.\nremaining").Complete("hello world.\nremaining");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        CodexThreadExecutionVm execution = await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("thread-1", execution.ThreadId);
        Assert.Equal(script.TurnId, execution.TurnId);
        Assert.Contains(queue.Messages, message => message.Text == "hello world.");
        Assert.Contains(queue.Messages, message => message.Text == "remaining");
    }

    [Fact]
    public async Task StartAsync_PublishesLiveCardWhenTurnIsAcceptedBeforeStreamEvents()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.WithStartDelay(TimeSpan.FromMilliseconds(150)).Complete("done");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        RecordingTelegramBotMessageSender sender = new();
        TelegramTurnOutputRelay relay = CreateRelay(
            queue,
            followRegistry,
            messageSender: sender,
            presentationMode: TelegramOutputPresentationMode.LiveCard);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);

        SentTelegramMessage card = Assert.Single(sender.Sent);
        Assert.Contains("Codex is working", card.Text);
        Assert.Contains("Activity: Turn started.", card.Text);

        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task StartAsync_StreamsRawAssistantResponseDeltasThroughRelay()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddDelta("raw output.").Complete("raw output.");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForConditionAsync(
            () => queue.Messages.Any(message => message.Text == "raw output."),
            () => $"Messages: {string.Join(" | ", queue.Messages.Select(message => message.Text))}");

        Assert.Contains(queue.Messages, message => message.Text == "raw output.");
        Assert.DoesNotContain(queue.Messages, message => string.Equals(message.Text, "Turn completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_ConsumesObservableNormalizedStream()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddDelta("observable output.").Complete("observable output.");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForConditionAsync(
            () => queue.Messages.Any(message => message.Text.Contains("observable", StringComparison.OrdinalIgnoreCase)),
            () => $"Messages: {string.Join(" | ", queue.Messages.Select(message => message.Text))}");

        Assert.Equal(1, script.NormalizedObservableSubscriptionCount);
        Assert.Equal(0, script.NormalizedStreamEnumerationCount);
        Assert.Equal(0, script.RawObservableSubscriptionCount);
        Assert.Equal(0, script.RawStreamEnumerationCount);
    }

    [Fact]
    public async Task StartAsync_ObservablePipelinePublishesRepresentativeSdkMessageKinds()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script
            .AddDelta("streamed ")
            .AddAssistantMessage("assistant message")
            .AddPlanUpdate(
                "Plan changed",
                [
                    new CodexTurnPlanStep { Step = "Inspect", Status = CodexTurnPlanStepStatus.Completed },
                    new CodexTurnPlanStep { Step = "Patch", Status = CodexTurnPlanStepStatus.InProgress },
                ])
            .AddPlanDelta("Patch")
            .AddCommandProgress("dotnet test", CodexCommandExecutionStatus.InProgress)
            .AddContextCompaction()
            .AddThreadCompacted()
            .AddApprovalRequest()
            .AddImageArtifact("C:\\temp\\image.png")
            .AddUnknownProgress("custom.telemetry")
            .AddThreadError("recoverable warning", willRetry: false)
            .Complete("streamed final");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        RecordingTurnOutputRelay relay = new();
        CodexSessionEventLog eventLog = new();
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay, eventLog: eventLog);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForConditionAsync(() => relay.Entries.Any(entry =>
            entry.Metadata.TryGetValue("terminal", out string? terminal)
            && bool.TryParse(terminal, out bool terminalValue)
            && terminalValue));

        HashSet<string> normalizedKinds = relay.Entries
            .Select(entry => entry.Metadata.TryGetValue("normalizedKind", out string? value) ? value : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(nameof(CodexTurnEventKind.Activity), normalizedKinds);
        Assert.Contains(nameof(CodexTurnEventKind.Progress), normalizedKinds);
        Assert.Contains(nameof(CodexTurnEventKind.AssistantDelta), normalizedKinds);
        Assert.Contains(nameof(CodexTurnEventKind.AssistantMessage), normalizedKinds);
        Assert.Contains(nameof(CodexTurnEventKind.FinalResponse), normalizedKinds);
        Assert.Contains(nameof(CodexTurnEventKind.Artifact), normalizedKinds);
        Assert.Contains(nameof(CodexTurnEventKind.ApprovalNeeded), normalizedKinds);
        Assert.Contains(nameof(CodexTurnEventKind.Error), normalizedKinds);
        Assert.Contains(nameof(CodexTurnEventKind.Terminal), normalizedKinds);
        Assert.Contains(relay.Entries, entry => entry.Type == "custom.telemetry");
        Assert.Contains(relay.Entries, entry => entry.Type == "turn.finalResponse" && entry.Body == "streamed final");
        Assert.Contains(relay.Entries, entry => entry.Type == "turn.completed");
        Assert.Equal(1, script.NormalizedObservableSubscriptionCount);
        Assert.Equal(0, script.NormalizedStreamEnumerationCount);
    }

    [Fact]
    public async Task StartAsync_LeavesTurnActiveUntilDelayedCompletionGateIsReleased()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddDelta("waiting on Codex").HoldCompletion().Complete("done");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(coordinator.HasActiveTurnForThread("thread-1"));

        script.ReleaseCompletion();
        await WaitForConditionAsync(() => !coordinator.HasActiveTurnForThread("thread-1"));

        Assert.False(coordinator.HasActiveTurnForThread("thread-1"));
    }

    [Fact]
    public async Task StartAsync_PublishesStreamingFaultsAsTurnFailures()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddDelta("context full").Fault(new InvalidOperationException("context full"));

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexSessionEventLog eventLog = new();
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay, eventLog: eventLog);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await WaitForConditionAsync(() =>
            queue.Messages.Any(message =>
                message.Kind == CodexOutboundMessageKind.Error
                && message.Text.Contains("context full", StringComparison.OrdinalIgnoreCase)));

        Assert.Contains(queue.Messages, message =>
            message.Kind == CodexOutboundMessageKind.Error
            && message.Text.Contains("context full", StringComparison.OrdinalIgnoreCase));

        CodexTurnCloseoutSummary? closeout = eventLog.GetLastCloseout("thread-1");
        Assert.NotNull(closeout);
        Assert.Equal("failed", closeout.Status);
        Assert.Contains("context full", closeout.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_RetriesCapacityThreadErrorsAndSuppressesRawError()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript firstScript = runtime.QueueTurn("thread-1");
        firstScript.AddThreadError("Selected model is at capacity. Please try a different model.");
        DisableAutoCompletion(firstScript);
        ScriptedCodexTurnScript retryScript = runtime.QueueTurn("thread-1");
        retryScript.AddDelta("retry worked").Complete("retry worked");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        CodexSessionEventLog eventLog = new();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry, eventLog);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(
            relay,
            capacityRetryDelays: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            eventLog: eventLog);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await firstScript.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await retryScript.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Func<bool> sawRetryWorked = () => queue.Messages.Any(message => message.Text == "retry worked")
            || (queue.Messages.Any(message => message.Text == "retry") && queue.Messages.Any(message => message.Text == "worked"));
        await WaitForConditionAsync(
            sawRetryWorked,
            () => $"Messages: {string.Join(" | ", queue.Messages.Select(message => message.Text))}");

        Assert.Contains(queue.Messages, message =>
            message.Text.Contains("Selected model is at capacity", StringComparison.OrdinalIgnoreCase)
            && message.Text.Contains("(1/3)", StringComparison.OrdinalIgnoreCase));
        Assert.True(sawRetryWorked(), $"Messages: {string.Join(" | ", queue.Messages.Select(message => message.Text))}");
        Assert.DoesNotContain(queue.Messages, message => message.Text.StartsWith("Thread error", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queue.Messages, message => message.Text.Contains("Please try a different model", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartAsync_StopsCapacityThreadErrorRetryAfterThreeAttempts()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript firstScript = runtime.QueueTurn("thread-1");
        firstScript.AddThreadError("Selected model is at capacity. Please try a different model.");
        DisableAutoCompletion(firstScript);
        ScriptedCodexTurnScript secondScript = runtime.QueueTurn("thread-1");
        secondScript.AddThreadError("Selected model is at capacity. Please try a different model.");
        DisableAutoCompletion(secondScript);
        ScriptedCodexTurnScript thirdScript = runtime.QueueTurn("thread-1");
        thirdScript.AddThreadError("Selected model is at capacity. Please try a different model.");
        DisableAutoCompletion(thirdScript);
        ScriptedCodexTurnScript fourthScript = runtime.QueueTurn("thread-1");
        fourthScript.AddThreadError("Selected model is at capacity. Please try a different model.");
        DisableAutoCompletion(fourthScript);

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexSessionEventLog eventLog = new();
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(
            relay,
            capacityRetryDelays: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            eventLog: eventLog);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await fourthScript.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForConditionAsync(
            () => queue.Messages.Any(message =>
                message.Text.Contains("Selected model is still at capacity", StringComparison.OrdinalIgnoreCase)),
            () => $"Messages: {string.Join(" | ", queue.Messages.Select(message => message.Text))}",
            TimeSpan.FromSeconds(5));

        Assert.Equal(
            3,
            queue.Messages.Count(message => message.Text.Contains("Selected model is at capacity", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(queue.Messages, message =>
            message.Kind == CodexOutboundMessageKind.Error
            && message.Text.Contains("Stopped after 3 retries", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queue.Messages, message => message.Text.StartsWith("Thread error", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartAsync_RetriesCompletionWithoutVisibleOutput()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript firstScript = runtime.QueueTurn("thread-1");
        ScriptedCodexTurnScript retryScript = runtime.QueueTurn("thread-1");
        retryScript.AddDelta("done").Complete("done");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexSessionEventLog eventLog = new();
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(
            relay,
            capacityRetryDelays: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            eventLog: eventLog);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await firstScript.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await retryScript.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForConditionAsync(() => queue.Messages.Any(message => message.Text == "done"));

        Assert.Contains(queue.Messages, message =>
            message.Text.Contains("Codex completed without visible output", StringComparison.OrdinalIgnoreCase)
            && message.Text.Contains("(1/3)", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queue.Messages, message => message.Text == "done");
        Assert.DoesNotContain(queue.Messages, message => string.Equals(message.Text, "Turn completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_TreatsStreamedAssistantTextAsVisibleCompletionWhenTerminalLacksFinalBody()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddDelta("I am checking that now.");
        script.AddCompletionEvent(
            terminalItems:
            [
                new CodexAgentMessageItem
                {
                    Type = "agentMessage",
                    Phase = CodexMessagePhase.Commentary,
                    Text = "I am checking that now.",
                },
            ]);

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexSessionEventLog eventLog = new();
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(
            relay,
            capacityRetryDelays: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            eventLog: eventLog);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForConditionAsync(
            () => queue.Messages.Any(message => message.Text.Contains("checkin", StringComparison.OrdinalIgnoreCase)),
            () => $"Messages: {string.Join(" | ", queue.Messages.Select(message => message.Text))}");

        Assert.DoesNotContain(queue.Messages, message =>
            message.Text.Contains("Codex completed without visible output", StringComparison.OrdinalIgnoreCase)
            && message.Text.Contains("(1/3)", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queue.Messages, message =>
            message.Text.Contains("Turn completed without a final response", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queue.Messages, message => message.Text.Contains("checkin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queue.Messages, message => string.Equals(message.Text, "Turn completed", StringComparison.Ordinal));

        CodexTurnCloseoutSummary? closeout = eventLog.GetLastCloseout("thread-1");
        Assert.NotNull(closeout);
        Assert.True(closeout.AssistantTextSeen);
        Assert.True(closeout.FinalResponseSeen);
        Assert.False(closeout.Warning);
    }

    [Fact]
    public async Task StartAsync_RetriesCompletionWithOnlyToolProgressAndNoAssistantOutput()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript firstScript = runtime.QueueTurn("thread-1");
        firstScript.AddCommandProgress("dotnet test", CodexCommandExecutionStatus.Completed);
        ScriptedCodexTurnScript retryScript = runtime.QueueTurn("thread-1");
        retryScript.AddDelta("done").Complete("done");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(
            relay,
            capacityRetryDelays: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await firstScript.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await retryScript.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForConditionAsync(() => queue.Messages.Any(message => message.Text == "done"));

        Assert.Contains(queue.Messages, message => message.Text.Contains("Command finished: dotnet test", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queue.Messages, message =>
            message.Text.Contains("Codex completed without visible output", StringComparison.OrdinalIgnoreCase)
            && message.Text.Contains("(1/3)", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(queue.Messages, message => message.Text == "done");
        Assert.DoesNotContain(queue.Messages, message => string.Equals(message.Text, "Turn completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_StopsCompletionWithoutVisibleOutputAfterThreeAttempts()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript firstScript = runtime.QueueTurn("thread-1");
        ScriptedCodexTurnScript secondScript = runtime.QueueTurn("thread-1");
        ScriptedCodexTurnScript thirdScript = runtime.QueueTurn("thread-1");
        ScriptedCodexTurnScript fourthScript = runtime.QueueTurn("thread-1");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(
            relay,
            capacityRetryDelays: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await WaitForConditionAsync(() =>
            queue.Messages.Any(message =>
                message.Kind == CodexOutboundMessageKind.Error
                && message.Text.Contains("Codex completed without visible output", StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(
            3,
            queue.Messages.Count(message =>
                message.Kind == CodexOutboundMessageKind.Update
                && message.Text.Contains("Codex completed without visible output", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(queue.Messages, message =>
            message.Kind == CodexOutboundMessageKind.Error
            && message.Text.Contains("Stopped after 3 retries", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queue.Messages, message => string.Equals(message.Text, "Turn completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_FlushesBufferedAssistantTextWhenStreamEndsWithoutTerminalEvent()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddDelta("abcd");
        DisableAutoCompletion(script);

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForConditionAsync(
            () => queue.Messages.Any(message => string.Equals(message.Text, "abcd", StringComparison.Ordinal)),
            () => $"Count={queue.Messages.Count}; messages={string.Join(" || ", queue.Messages.Select(message => $"{message.Kind}:{message.Text}"))}");

        OutboundTelegramMessage message = queue.Messages.Single(message => string.Equals(message.Text, "abcd", StringComparison.Ordinal));
        Assert.Equal(CodexOutboundMessageKind.Completion, message.Kind);
    }

    [Fact]
    public async Task StartAsync_DoesNotPublishCompletionTitleWhenStreamEndsWithoutTerminalEventAfterLiveProgress()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddDelta("hello world.\n");
        DisableAutoCompletion(script);

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForConditionAsync(
            () => queue.Messages.Any(message => message.Text.Contains("hello world.", StringComparison.Ordinal)),
            () => $"Count={queue.Messages.Count}; messages={string.Join(" || ", queue.Messages.Select(message => $"{message.Kind}:{message.Text}"))}");

        Assert.Contains(queue.Messages, message => message.Text.Contains("hello world.", StringComparison.Ordinal));
        Assert.DoesNotContain(queue.Messages, message => string.Equals(message.Text, "Turn completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_DoesNotCarryBufferedAssistantTextIntoTheNextTurnOnTheSameThread()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript firstScript = runtime.QueueTurn("thread-1");
        firstScript.AddDelta("repro.");
        DisableAutoCompletion(firstScript);

        ScriptedCodexTurnScript secondScript = runtime.QueueTurn("thread-1");
        string secondText = "The queue itself has a wake signal and a periodic loop, so I am checking the relay heuristics next.";
        secondScript.AddDelta(secondText);
        DisableAutoCompletion(secondScript);

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await firstScript.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForConditionAsync(
            () => queue.Messages.Any(message => string.Equals(message.Text, "repro.", StringComparison.Ordinal)),
            () => $"First turn did not flush cleanly. Count={queue.Messages.Count}; messages={string.Join(" || ", queue.Messages.Select(message => $"{message.Kind}:{message.Text}"))}");

        int firstTurnMessageCount = queue.Messages.Count;

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await secondScript.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitForConditionAsync(
            () => queue.Messages.Count > firstTurnMessageCount,
            () => $"Second turn did not publish any output. Count={queue.Messages.Count}; messages={string.Join(" || ", queue.Messages.Select(message => $"{message.Kind}:{message.Text}"))}");

        IReadOnlyList<OutboundTelegramMessage> secondTurnMessages = queue.Messages.Skip(firstTurnMessageCount).ToArray();
        Assert.DoesNotContain(secondTurnMessages, message => message.Text.Contains("repro.", StringComparison.Ordinal));
        Assert.DoesNotContain(secondTurnMessages, message => string.Equals(message.Text, "Turn completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAsync_DefersTerminalPublicationUntilLaterEventsDrain()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddCommandProgress("echo before", CodexCommandExecutionStatus.Completed);
        script.AddCompletionEvent("done");
        script.AddCommandProgress("echo after", CodexCommandExecutionStatus.Completed);

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));

        string[] messages = queue.Messages.Select(message => message.Text).ToArray();
        int beforeIndex = Array.FindIndex(messages, text => text.Contains("Command finished: echo before", StringComparison.OrdinalIgnoreCase));
        int afterIndex = Array.FindIndex(messages, text => text.Contains("Command finished: echo after", StringComparison.OrdinalIgnoreCase));
        int completionIndex = Array.FindIndex(messages, text => string.Equals(text, "done", StringComparison.Ordinal));

        Assert.True(beforeIndex >= 0, "Expected the pre-completion command progress to be published.");
        Assert.True(afterIndex >= 0, "Expected the post-completion command progress to be published.");
        Assert.True(completionIndex >= 0, "Expected the terminal completion message to be published.");
        Assert.True(beforeIndex < completionIndex, "Expected completion to be published after earlier turn events.");
        Assert.True(afterIndex < completionIndex, "Expected completion to be published after later turn events.");
    }

    [Fact]
    public async Task StartAsync_SuppressesContextCompactionNoise()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddContextCompaction().Complete("done");

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay);

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.DoesNotContain(queue.Messages, message => message.Text.Contains("Context compaction", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartAsync_FlushesBufferedAssistantTextAfterTheHoldExpiresEvenIfTheStreamStaysOpen()
    {
        using ScriptedCodexRuntime runtime = new();
        ScriptedCodexTurnScript script = runtime.QueueTurn("thread-1");
        script.AddDelta("visible progress");
        script.AddCompletionEvent().HoldCompletion();

        ICodexThreadHandle thread = runtime.CreateThread("thread-1");
        FakeOutboundTelegramQueue queue = new();
        TelegramThreadFollowRegistry followRegistry = FollowThread();
        TelegramTurnOutputRelay relay = CreateRelay(queue, followRegistry);
        CodexTurnExecutionCoordinator coordinator = CreateCoordinator(relay, terminalHoldDuration: TimeSpan.FromMilliseconds(50));

        await coordinator.StartAsync(thread, [], new CodexTurnOptions(), CancellationToken.None);
        await script.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(coordinator.HasActiveTurnForThread("thread-1"));

        await WaitForConditionAsync(() =>
            queue.Messages.Any(message => message.Kind == CodexOutboundMessageKind.Completion),
            () => $"Count={queue.Messages.Count}; messages={string.Join(" || ", queue.Messages.Select(message => $"{message.Kind}:{message.Text}"))}");
        await script.Finished.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(coordinator.HasActiveTurnForThread("thread-1"));
        Assert.Contains(queue.Messages, message => message.Kind == CodexOutboundMessageKind.Completion);
    }

    private static CodexTurnExecutionCoordinator CreateCoordinator(
        ITelegramTurnOutputRelay relay,
        TimeProvider? timeProvider = null,
        TimeSpan? terminalHoldDuration = null,
        IReadOnlyList<TimeSpan>? capacityRetryDelays = null,
        ICodexSessionEventLog? eventLog = null)
        => new(
            new NullCodexRealtimeBroadcaster(),
            relay,
            new TestApplicationLifetime(),
            timeProvider ?? TimeProvider.System,
            terminalHoldDuration ?? TimeSpan.FromSeconds(3),
            NullLogger<CodexTurnExecutionCoordinator>.Instance,
            capacityRetryDelays,
            eventLog);

    private static TelegramTurnOutputRelay CreateRelay(
        FakeOutboundTelegramQueue queue,
        TelegramThreadFollowRegistry followRegistry,
        ICodexSessionEventLog? eventLog = null,
        ITelegramBotMessageSender? messageSender = null,
        TelegramOutputPresentationMode presentationMode = TelegramOutputPresentationMode.Verbose)
        => new(
            queue,
            followRegistry,
            new TelegramTurnReactionRegistry(),
            messageSender ?? new NoopTelegramBotMessageSender(),
            Microsoft.Extensions.Options.Options.Create(new TelegramOutboundOptions
            {
                AgentMessageUpdateMinChars = 5,
                AgentMessageUpdateMaxChars = 12,
            }),
            Microsoft.Extensions.Options.Options.Create(new TelegramOutputOptions
            {
                PresentationMode = presentationMode,
            }),
            new TestTelegramOutputModeState(presentationMode),
            NullLogger<TelegramTurnOutputRelay>.Instance,
            eventLog);

    private static TelegramThreadFollowRegistry FollowThread()
    {
        TelegramThreadFollowRegistry followRegistry = new();
        followRegistry.FollowThread(new TelegramConversationScope(1234, 55), "thread-1");
        return followRegistry;
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        Func<string>? failureMessageFactory = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(2);
        TimeSpan effectivePollInterval = pollInterval ?? TimeSpan.FromMilliseconds(20);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + effectiveTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(effectivePollInterval);
        }

        Assert.True(condition(), failureMessageFactory?.Invoke() ?? "Timed out waiting for the expected condition.");
    }

    private sealed class FakeOutboundTelegramQueue : IOutboundTelegramQueue
    {
        private readonly object _gate = new();
        private readonly List<OutboundTelegramMessage> _messages = [];

        public IReadOnlyList<OutboundTelegramMessage> Messages
        {
            get
            {
                lock (_gate)
                {
                    return _messages.ToArray();
                }
            }
        }

        public ValueTask EnqueueAsync(OutboundTelegramMessage message, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _messages.Add(message);
            }

            return ValueTask.CompletedTask;
        }

        public Task<TelegramOutboundQueueStatus> GetStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(new TelegramOutboundQueueStatus(0, 0, 0, 0, null, null, null, []));
    }

    private sealed class RecordingTurnOutputRelay : ITelegramTurnOutputRelay
    {
        private readonly object _gate = new();
        private readonly List<CodexTimelineEntryVm> _entries = [];

        public IReadOnlyList<CodexTimelineEntryVm> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.ToArray();
                }
            }
        }

        public List<(string ThreadId, string TurnId)> AcceptedTurns { get; } = [];

        public Task PublishTurnAcceptedAsync(string threadId, string turnId, CancellationToken cancellationToken)
        {
            AcceptedTurns.Add((threadId, turnId));
            return Task.CompletedTask;
        }

        public Task PublishTurnEventAsync(CodexTimelineEntryVm entry, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class NoopTelegramBotMessageSender : ITelegramBotMessageSender
    {
        public Task SendTextMessageAsync(
            TelegramConversationScope conversation,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
            => Task.CompletedTask;

        public Task EditTextMessageAsync(
            TelegramConversationScope conversation,
            int messageId,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
            => Task.CompletedTask;

        public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AcknowledgeMessageAsync(TelegramMessageAcknowledgement acknowledgement, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SendTypingActionAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ReactToMessageAsync(TelegramMessageReaction reaction, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecordingTelegramBotMessageSender : ITelegramBotMessageSender
    {
        public List<SentTelegramMessage> Sent { get; } = [];

        public Task SendTextMessageAsync(
            TelegramConversationScope conversation,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
        {
            Sent.Add(new SentTelegramMessage(conversation, text, buttons));
            return Task.CompletedTask;
        }

        public Task<int?> SendTextMessageAndGetIdAsync(
            TelegramConversationScope conversation,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
        {
            Sent.Add(new SentTelegramMessage(conversation, text, buttons));
            return Task.FromResult<int?>(Sent.Count);
        }

        public Task EditTextMessageAsync(
            TelegramConversationScope conversation,
            int messageId,
            string text,
            IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons,
            CancellationToken cancellationToken,
            TelegramDebugMessageContext? debugContext = null)
            => Task.CompletedTask;

        public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AcknowledgeMessageAsync(TelegramMessageAcknowledgement acknowledgement, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SendTypingActionAsync(TelegramConversationScope conversation, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ReactToMessageAsync(TelegramMessageReaction reaction, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class TestTelegramOutputModeState : ITelegramOutputModeState
    {
        public TestTelegramOutputModeState(TelegramOutputPresentationMode mode = TelegramOutputPresentationMode.Verbose)
        {
            CurrentMode = mode;
        }

        public TelegramOutputPresentationMode CurrentMode { get; private set; }

        public bool HasRuntimeOverride { get; private set; }

        public void SetRuntimeMode(TelegramOutputPresentationMode mode)
        {
            CurrentMode = mode;
            HasRuntimeOverride = true;
        }

        public void ClearRuntimeMode()
        {
            CurrentMode = TelegramOutputPresentationMode.Verbose;
            HasRuntimeOverride = false;
        }
    }

    private sealed record SentTelegramMessage(
        TelegramConversationScope Conversation,
        string Text,
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? Buttons);

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
            => _utcNow;

        public void Advance(TimeSpan value)
            => _utcNow += value;
    }

    private static void DisableAutoCompletion(ScriptedCodexTurnScript script)
    {
        FieldInfo emitAutoCompletionField = typeof(ScriptedCodexTurnScript).GetField("_emitAutoCompletion", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ScriptedCodexTurnScript auto-completion flag was not found.");
        emitAutoCompletionField.SetValue(script, false);
    }
}
