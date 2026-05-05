using Incursa.Codex.Telegram.Options;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Creates Telegram forum topics for topic-scoped Codex sessions.
/// </summary>
internal interface ITelegramForumTopicService
{
    /// <summary>
    /// Creates a Telegram forum topic in a supergroup.
    /// </summary>
    /// <param name="chatId">Telegram supergroup chat ID.</param>
    /// <param name="name">Requested topic name.</param>
    /// <param name="cancellationToken">Cancellation token for request aborts.</param>
    /// <returns>Created topic details.</returns>
    Task<TelegramForumTopicCreationResult> CreateForumTopicAsync(long chatId, string name, CancellationToken cancellationToken);
}

/// <summary>
/// Result returned after creating a Telegram forum topic.
/// </summary>
/// <param name="MessageThreadId">Telegram message thread ID for the topic.</param>
/// <param name="Name">Created topic name.</param>
internal sealed record TelegramForumTopicCreationResult(int MessageThreadId, string Name);

/// <summary>
/// Telegram Bot API implementation of forum-topic creation.
/// </summary>
internal sealed class TelegramForumTopicService : ITelegramForumTopicService
{
    private readonly TelegramBotOptions _options;
    private readonly Lazy<ITelegramBotClient> _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelegramForumTopicService"/> class.
    /// </summary>
    /// <param name="options">Telegram bot options.</param>
    public TelegramForumTopicService(IOptions<TelegramBotOptions> options)
    {
        _options = options.Value;
        _client = new Lazy<ITelegramBotClient>(() => new TelegramBotClient(RequireToken()));
    }

    /// <inheritdoc />
    public async Task<TelegramForumTopicCreationResult> CreateForumTopicAsync(long chatId, string name, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Telegram bot is disabled.");
        }

        string topicName = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Topic name cannot be empty.", nameof(name))
            : name.Trim();

        ForumTopic forumTopic = await _client.Value.CreateForumTopic(
            new ChatId(chatId),
            topicName,
            iconColor: null,
            iconCustomEmojiId: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new TelegramForumTopicCreationResult(forumTopic.MessageThreadId, forumTopic.Name);
    }

    private string RequireToken()
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException("TelegramBot:Token must be configured when TelegramBot:Enabled is true.");
        }

        return _options.Token.Trim();
    }
}
