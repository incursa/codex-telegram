using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Incursa.Codex.Telegram.Telegram;

internal interface ITelegramPlanInputCoordinator
{
    JsonObject? HandleApprovalRequest(string action, JsonObject? request);

    Task<bool> TryAnswerPendingAsync(
        TelegramConversationScope conversation,
        string text,
        CancellationToken cancellationToken);

    Task<bool> TryAnswerCallbackAsync(
        string token,
        TelegramConversationScope conversation,
        string callbackQueryId,
        CancellationToken cancellationToken);
}

internal sealed class TelegramPlanInputCoordinator : ITelegramPlanInputCoordinator
{
    private const string RequestUserInputAction = "item/tool/requestUserInput";
    private const int ShortIdLength = 8;

    private readonly ConcurrentDictionary<TelegramConversationScope, PendingPlanInputRequest> _pendingByConversation = new();
    private readonly ConcurrentDictionary<string, PlanInputOptionToken> _optionTokens = new(StringComparer.Ordinal);
    private readonly ITelegramThreadFollowRegistry _followRegistry;
    private readonly ITelegramBotMessageSender _sender;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<TelegramPlanInputCoordinator> _logger;

    public TelegramPlanInputCoordinator(
        ITelegramThreadFollowRegistry followRegistry,
        ITelegramBotMessageSender sender,
        IHostApplicationLifetime applicationLifetime,
        ILogger<TelegramPlanInputCoordinator> logger)
    {
        _followRegistry = followRegistry;
        _sender = sender;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    public JsonObject? HandleApprovalRequest(string action, JsonObject? request)
    {
        if (!IsRequestUserInput(action))
        {
            return null;
        }

        try
        {
            PendingPlanInputRequest? pending = CreatePendingRequest(request);
            if (pending is null)
            {
                return new JsonObject();
            }

            int published = PublishPromptAsync(pending, _applicationLifetime.ApplicationStopping).GetAwaiter().GetResult();
            if (published == 0)
            {
                Cleanup(pending);
                return new JsonObject();
            }

            PlanInputAnswer answer = pending.Completion.Task
                .WaitAsync(_applicationLifetime.ApplicationStopping)
                .GetAwaiter()
                .GetResult();
            return BuildAnswerResponse(answer);
        }
        catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            return new JsonObject();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to handle Codex plan-mode user-input request.");
            return new JsonObject();
        }
    }

    public async Task<bool> TryAnswerPendingAsync(
        TelegramConversationScope conversation,
        string text,
        CancellationToken cancellationToken)
    {
        if (!_pendingByConversation.TryGetValue(conversation, out PendingPlanInputRequest? pending))
        {
            return false;
        }

        PlanInputAnswer? answer = BuildAnswerFromText(pending, text);
        if (answer is null)
        {
            await _sender.SendTextMessageAsync(
                conversation,
                BuildAnswerGuidance(pending),
                null,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryComplete(pending, answer))
        {
            await _sender.SendTextMessageAsync(
                conversation,
                "Plan mode: answer sent to Codex.",
                null,
                cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<bool> TryAnswerCallbackAsync(
        string token,
        TelegramConversationScope conversation,
        string callbackQueryId,
        CancellationToken cancellationToken)
    {
        if (!_optionTokens.TryGetValue(token, out PlanInputOptionToken? optionToken))
        {
            await _sender.AnswerCallbackQueryAsync(callbackQueryId, "That plan answer is no longer pending.", cancellationToken).ConfigureAwait(false);
            return true;
        }

        PlanInputAnswer answer = new(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [optionToken.QuestionId] = [optionToken.Answer],
        });

        if (TryComplete(optionToken.Pending, answer))
        {
            await _sender.AnswerCallbackQueryAsync(callbackQueryId, "Answer sent.", cancellationToken).ConfigureAwait(false);
            await _sender.SendTextMessageAsync(
                conversation,
                $"Plan mode: answered \"{optionToken.Answer}\".",
                null,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        await _sender.AnswerCallbackQueryAsync(callbackQueryId, "That plan question was already answered.", cancellationToken).ConfigureAwait(false);
        return true;
    }

    private PendingPlanInputRequest? CreatePendingRequest(JsonObject? request)
    {
        JsonObject? payload = GetObjectAny(request, "params") ?? request;
        if (payload is null)
        {
            return null;
        }

        string? threadId = GetStringAny(payload, "threadId", "thread_id");
        string? turnId = GetStringAny(payload, "turnId", "turn_id");
        string? itemId = GetStringAny(payload, "itemId", "item_id");
        JsonArray? questionsPayload = GetArrayAny(payload, "questions");
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId) || questionsPayload is null)
        {
            _logger.LogWarning("Codex requestUserInput payload was missing thread, turn, or questions.");
            return null;
        }

        IReadOnlyList<TelegramConversationScope> targets = _followRegistry.GetTargets(threadId).ToArray();
        if (targets.Count == 0)
        {
            _logger.LogWarning("Codex requested plan input for thread {ThreadId}, but no Telegram conversation is following it.", threadId);
            return null;
        }

        List<PlanInputQuestion> questions = [];
        int questionIndex = 1;
        foreach (JsonNode? node in questionsPayload)
        {
            if (node is not JsonObject questionPayload)
            {
                continue;
            }

            PlanInputQuestion? question = ParseQuestion(questionPayload, questionIndex);
            if (question is not null)
            {
                questions.Add(question);
                questionIndex++;
            }
        }

        if (questions.Count == 0)
        {
            _logger.LogWarning("Codex requestUserInput payload did not include any parseable questions.");
            return null;
        }

        PendingPlanInputRequest pending = new(threadId, turnId, itemId, targets, questions);
        foreach (TelegramConversationScope target in targets)
        {
            _pendingByConversation[target] = pending;
        }

        return pending;
    }

    private async Task<int> PublishPromptAsync(PendingPlanInputRequest pending, CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? buttons = BuildAnswerButtons(pending);
        string prompt = FormatPrompt(pending);
        int published = 0;
        foreach (TelegramConversationScope target in pending.Targets)
        {
            try
            {
                await _sender.SendTextMessageAsync(target, prompt, buttons, cancellationToken).ConfigureAwait(false);
                published++;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to publish plan-mode input request for thread {ThreadId} to Telegram destination {Destination}.", pending.ThreadId, target);
            }
        }

        return published;
    }

    private IReadOnlyList<IReadOnlyList<TelegramReplyButton>>? BuildAnswerButtons(PendingPlanInputRequest pending)
    {
        if (pending.Questions.Count != 1)
        {
            return null;
        }

        PlanInputQuestion question = pending.Questions[0];
        if (question.IsSecret || question.Options.Count == 0)
        {
            return null;
        }

        List<IReadOnlyList<TelegramReplyButton>> rows = [];
        List<TelegramReplyButton> row = [];
        foreach (PlanInputOption option in question.Options.Take(8))
        {
            string token = Guid.NewGuid().ToString("n")[..16];
            pending.OptionTokens.Add(token);
            _optionTokens[token] = new PlanInputOptionToken(pending, question.Id, option.Label, token);
            row.Add(new TelegramReplyButton(TruncateButtonLabel(option.Label), $"pans:{token}"));
            if (row.Count == 2)
            {
                rows.Add(row.ToArray());
                row.Clear();
            }
        }

        if (row.Count > 0)
        {
            rows.Add(row.ToArray());
        }

        return rows.Count == 0 ? null : rows;
    }

    private static string FormatPrompt(PendingPlanInputRequest pending)
    {
        StringBuilder builder = new();
        builder.AppendLine("Plan mode: input needed");
        builder.AppendLine($"Session: {ShortId(pending.ThreadId)}");
        builder.AppendLine($"Turn: {ShortId(pending.TurnId)}");
        builder.AppendLine();

        for (int index = 0; index < pending.Questions.Count; index++)
        {
            PlanInputQuestion question = pending.Questions[index];
            if (pending.Questions.Count > 1)
            {
                builder.AppendLine($"{index + 1}. {question.Header ?? question.Id}");
            }
            else if (!string.IsNullOrWhiteSpace(question.Header))
            {
                builder.AppendLine(question.Header);
            }

            builder.AppendLine(question.Question);

            if (question.Options.Count > 0)
            {
                builder.AppendLine("Options:");
                for (int optionIndex = 0; optionIndex < question.Options.Count; optionIndex++)
                {
                    PlanInputOption option = question.Options[optionIndex];
                    string suffix = string.IsNullOrWhiteSpace(option.Description) ? string.Empty : $" - {option.Description}";
                    builder.AppendLine($"{optionIndex + 1}. {option.Label}{suffix}");
                }
            }

            if (pending.Questions.Count > 1)
            {
                builder.AppendLine($"Answer key: {question.Id}");
            }

            builder.AppendLine();
        }

        builder.AppendLine(pending.Questions.Count == 1
            ? "Reply with the answer, or use /answer <answer>."
            : "Use /answer question_id=value; other_id=value.");
        return builder.ToString().TrimEnd();
    }

    private static PlanInputQuestion? ParseQuestion(JsonObject payload, int index)
    {
        string id = GetStringAny(payload, "id") ?? $"question_{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        string? question = GetStringAny(payload, "question", "text", "prompt");
        if (string.IsNullOrWhiteSpace(question))
        {
            return null;
        }

        List<PlanInputOption> options = [];
        if (GetArrayAny(payload, "options") is { } optionsPayload)
        {
            foreach (JsonNode? optionNode in optionsPayload)
            {
                if (optionNode is not JsonObject optionPayload)
                {
                    continue;
                }

                string? label = GetStringAny(optionPayload, "label");
                if (!string.IsNullOrWhiteSpace(label))
                {
                    options.Add(new PlanInputOption(label, GetStringAny(optionPayload, "description")));
                }
            }
        }

        return new PlanInputQuestion(
            id,
            GetStringAny(payload, "header"),
            question,
            GetBooleanAny(payload, "isOther", "is_other"),
            GetBooleanAny(payload, "isSecret", "is_secret"),
            options);
    }

    private static PlanInputAnswer? BuildAnswerFromText(PendingPlanInputRequest pending, string text)
    {
        string trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        Dictionary<string, IReadOnlyList<string>> answers = new(StringComparer.Ordinal);
        if (pending.Questions.Count == 1)
        {
            PlanInputQuestion question = pending.Questions[0];
            answers[question.Id] = [ResolveSingleAnswer(question, trimmed)];
            return new PlanInputAnswer(answers);
        }

        foreach (string assignment in SplitAssignments(trimmed))
        {
            int separatorIndex = assignment.IndexOf('=');
            if (separatorIndex < 0)
            {
                separatorIndex = assignment.IndexOf(':');
            }

            if (separatorIndex <= 0 || separatorIndex >= assignment.Length - 1)
            {
                continue;
            }

            string key = assignment[..separatorIndex].Trim();
            string value = assignment[(separatorIndex + 1)..].Trim();
            PlanInputQuestion? question = pending.Questions.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Header, key, StringComparison.OrdinalIgnoreCase));
            if (question is not null && !string.IsNullOrWhiteSpace(value))
            {
                answers[question.Id] = [ResolveSingleAnswer(question, value)];
            }
        }

        return answers.Count == pending.Questions.Count ? new PlanInputAnswer(answers) : null;
    }

    private static string ResolveSingleAnswer(PlanInputQuestion question, string text)
    {
        if (question.Options.Count > 0
            && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int optionNumber)
            && optionNumber >= 1
            && optionNumber <= question.Options.Count)
        {
            return question.Options[optionNumber - 1].Label;
        }

        PlanInputOption? matchingOption = question.Options.FirstOrDefault(option => string.Equals(option.Label, text, StringComparison.OrdinalIgnoreCase));
        return matchingOption?.Label ?? text;
    }

    private static string BuildAnswerGuidance(PendingPlanInputRequest pending)
    {
        if (pending.Questions.Count == 1)
        {
            return "Plan mode: reply with an answer, or use /answer <answer>.";
        }

        return "Plan mode: answer all questions with /answer "
            + string.Join("; ", pending.Questions.Select(question => $"{question.Id}=<answer>"));
    }

    private static JsonObject BuildAnswerResponse(PlanInputAnswer answer)
    {
        JsonObject answers = new();
        foreach ((string questionId, IReadOnlyList<string> values) in answer.Answers)
        {
            JsonArray answerValues = [];
            foreach (string value in values)
            {
                answerValues.Add(JsonValue.Create(value));
            }

            answers[questionId] = new JsonObject
            {
                ["answers"] = answerValues,
            };
        }

        return new JsonObject
        {
            ["answers"] = answers,
        };
    }

    private bool TryComplete(PendingPlanInputRequest pending, PlanInputAnswer answer)
    {
        bool completed = pending.Completion.TrySetResult(answer);
        if (completed)
        {
            Cleanup(pending);
        }

        return completed;
    }

    private void Cleanup(PendingPlanInputRequest pending)
    {
        foreach (TelegramConversationScope target in pending.Targets)
        {
            if (_pendingByConversation.TryGetValue(target, out PendingPlanInputRequest? current) && ReferenceEquals(current, pending))
            {
                _pendingByConversation.TryRemove(target, out _);
            }
        }

        foreach (string token in pending.OptionTokens)
        {
            if (_optionTokens.TryGetValue(token, out PlanInputOptionToken? current) && ReferenceEquals(current.Pending, pending))
            {
                _optionTokens.TryRemove(token, out _);
            }
        }
    }

    private static bool IsRequestUserInput(string action)
        => string.Equals(action, RequestUserInputAction, StringComparison.Ordinal)
            || action.EndsWith("/requestUserInput", StringComparison.Ordinal);

    private static string[] SplitAssignments(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split(['\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string TruncateButtonLabel(string label)
        => label.Length <= 40 ? label : label[..39] + "...";

    private static string ShortId(string value)
        => value.Length <= ShortIdLength ? value : value[..ShortIdLength];

    private static JsonObject? GetObjectAny(JsonObject? payload, params string[] names)
    {
        if (payload is null)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (payload.TryGetPropertyValue(name, out JsonNode? node) && node is JsonObject obj)
            {
                return obj;
            }
        }

        return null;
    }

    private static JsonArray? GetArrayAny(JsonObject? payload, params string[] names)
    {
        if (payload is null)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (payload.TryGetPropertyValue(name, out JsonNode? node) && node is JsonArray array)
            {
                return array;
            }
        }

        return null;
    }

    private static string? GetStringAny(JsonObject? payload, params string[] names)
    {
        if (payload is null)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (!payload.TryGetPropertyValue(name, out JsonNode? node) || node is not JsonValue value)
            {
                continue;
            }

            try
            {
                string? text = value.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        return null;
    }

    private static bool GetBooleanAny(JsonObject? payload, params string[] names)
    {
        if (payload is null)
        {
            return false;
        }

        foreach (string name in names)
        {
            if (!payload.TryGetPropertyValue(name, out JsonNode? node) || node is not JsonValue value)
            {
                continue;
            }

            try
            {
                return value.GetValue<bool>();
            }
            catch (InvalidOperationException)
            {
            }
        }

        return false;
    }

    private sealed class PendingPlanInputRequest
    {
        public PendingPlanInputRequest(
            string threadId,
            string turnId,
            string? itemId,
            IReadOnlyList<TelegramConversationScope> targets,
            IReadOnlyList<PlanInputQuestion> questions)
        {
            ThreadId = threadId;
            TurnId = turnId;
            ItemId = itemId;
            Targets = targets;
            Questions = questions;
        }

        public string ThreadId { get; }

        public string TurnId { get; }

        public string? ItemId { get; }

        public IReadOnlyList<TelegramConversationScope> Targets { get; }

        public IReadOnlyList<PlanInputQuestion> Questions { get; }

        public List<string> OptionTokens { get; } = [];

        public TaskCompletionSource<PlanInputAnswer> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PlanInputQuestion(
        string Id,
        string? Header,
        string Question,
        bool IsOther,
        bool IsSecret,
        IReadOnlyList<PlanInputOption> Options);

    private sealed record PlanInputOption(string Label, string? Description);

    private sealed record PlanInputOptionToken(PendingPlanInputRequest Pending, string QuestionId, string Answer, string Token);

    private sealed record PlanInputAnswer(IReadOnlyDictionary<string, IReadOnlyList<string>> Answers);
}
