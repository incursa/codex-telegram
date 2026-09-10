using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Telegram.Bot.Types;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Holds non-pairing updates observed while the setup wizard temporarily owns
/// a bot's polling stream. The normal hosted receiver drains these updates
/// before asking Telegram for newer work, so setup does not silently discard a
/// message that arrived while pairing.
/// </summary>
internal static class TelegramSetupUpdateBuffer
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<Update>> Queues = new(StringComparer.Ordinal);

    internal static void Enqueue(string token, Update update)
        => Queues.GetOrAdd(GetKey(token), static _ => new ConcurrentQueue<Update>()).Enqueue(update);

    internal static IReadOnlyList<Update> Drain(string token)
    {
        if (!Queues.TryGetValue(GetKey(token), out ConcurrentQueue<Update>? queue))
        {
            return [];
        }

        List<Update> updates = [];
        while (queue.TryDequeue(out Update? update))
        {
            updates.Add(update);
        }

        if (queue.IsEmpty)
        {
            Queues.TryRemove(GetKey(token), out _);
        }

        return updates;
    }

    private static string GetKey(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
}
