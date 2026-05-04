using System.Text;

namespace Incursa.Codex.Telegram.Telegram;

public sealed class TelegramMessageChunker
{
    public IReadOnlyList<string> Split(string? text, int maxLength)
    {
        maxLength = Math.Clamp(maxLength, 1, 4000);
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
