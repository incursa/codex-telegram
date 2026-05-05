using System.Text;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Splits bot replies into Telegram-safe text chunks.
/// </summary>
internal sealed class TelegramMessageChunker
{
    /// <summary>
    /// Conservative Telegram text length cap used when a caller provides an unsafe value.
    /// </summary>
    public const int MaxTelegramTextCharacters = 4000;

    /// <summary>
    /// Smallest usable chunk length.
    /// </summary>
    public const int MinTelegramTextCharacters = 1;

    /// <summary>
    /// Splits text into chunks that do not exceed the requested length.
    /// </summary>
    /// <param name="text">Text to split. Empty text is normalized to a single space so Telegram still receives a message.</param>
    /// <param name="maxLength">Maximum characters per chunk.</param>
    /// <returns>One or more chunks ready for Telegram delivery.</returns>
    public IReadOnlyList<string> Split(string? text, int maxLength)
    {
        maxLength = Math.Clamp(maxLength, MinTelegramTextCharacters, MaxTelegramTextCharacters);
        string normalized = string.IsNullOrEmpty(text)
            ? " "
            : text.Replace("\r\n", "\n", StringComparison.Ordinal);

        if (normalized.Length <= maxLength)
        {
            return [normalized];
        }

        List<string> chunks = [];
        StringBuilder current = new();

        foreach (string line in normalized.Split('\n'))
        {
            if (line.Length > maxLength)
            {
                FlushCurrent(chunks, current);
                // Telegram does not accept an oversized single line, so hard-split only after
                // preserving every natural newline boundary we can preserve.
                for (int index = 0; index < line.Length; index += maxLength)
                {
                    chunks.Add(line.Substring(index, Math.Min(maxLength, line.Length - index)));
                }

                continue;
            }

            int additionalLength = current.Length == 0 ? line.Length : line.Length + 1;
            if (current.Length + additionalLength > maxLength)
            {
                FlushCurrent(chunks, current);
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(line);
        }

        FlushCurrent(chunks, current);
        return chunks;
    }

    private static void FlushCurrent(List<string> chunks, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        chunks.Add(current.ToString());
        current.Clear();
    }
}
