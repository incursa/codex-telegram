using System.Text;
using Incursa.Codex.Telegram.Options;

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

        string[] lines = normalized.Split('\n');
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex];
            bool hasFollowingLine = lineIndex < lines.Length - 1;
            if (line.Length > maxLength)
            {
                if (current.Length > 0)
                {
                    if (current.Length < maxLength)
                    {
                        current.Append('\n');
                    }
                    else
                    {
                        FlushCurrent(chunks, current);
                        chunks.Add("\n");
                    }
                }

                FlushCurrent(chunks, current);
                // Telegram does not accept an oversized single line, so hard-split only after
                // preserving every natural newline boundary we can preserve.
                for (int index = 0; index < line.Length; index += maxLength)
                {
                    chunks.Add(line.Substring(index, Math.Min(maxLength, line.Length - index)));
                }

                if (hasFollowingLine)
                {
                    chunks.Add("\n");
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

    /// <summary>
    /// Chunks source text while retaining the requested format for each chunk.
    /// If a boundary could leave MarkdownV2 markup incomplete, the affected
    /// payload falls back to literal plain text.
    /// </summary>
    public IReadOnlyList<TelegramTextChunk> SplitFormatted(string? text, int maxLength, TelegramTextFormat format)
    {
        if (format != TelegramTextFormat.SafeMarkdownV2)
        {
            return Split(text, maxLength)
                .Select(chunk => new TelegramTextChunk(chunk, TelegramTextFormat.PlainText))
                .ToArray();
        }

        string normalized = string.IsNullOrEmpty(text) ? " " : text.Replace("\r\n", "\n", StringComparison.Ordinal);
        IReadOnlyList<string> sourceChunks = Split(normalized, maxLength);
        if (sourceChunks.All(chunk => IsStandaloneSafeMarkdownV2SourceChunk(chunk, maxLength)))
        {
            // Keep source text in the queue. The sender renders each chunk
            // exactly once, which preserves plain fallback capability when a
            // transport lacks parse-mode support and keeps trace/context text
            // faithful to what Codex produced.
            return sourceChunks.Select(chunk => new TelegramTextChunk(chunk, format)).ToArray();
        }

        // A plain fallback keeps Telegram from rejecting a chunk when a
        // natural boundary intersects a code block or link.
        return Split(normalized, maxLength)
            .Select(chunk => new TelegramTextChunk(chunk, TelegramTextFormat.PlainText))
            .ToArray();
    }

    private static bool IsStandaloneSafeMarkdownV2SourceChunk(string text, int maxLength)
    {
        int fenceCount = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text.AsSpan(index).StartsWith("```", StringComparison.Ordinal))
            {
                fenceCount++;
                index += 2;
            }
        }

        if (fenceCount % 2 != 0)
        {
            return false;
        }

        int inlineCodeDelimiterCount = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                index++;
                continue;
            }

            if (text[index] == '`')
            {
                inlineCodeDelimiterCount++;
            }
        }

        if (inlineCodeDelimiterCount % 2 != 0)
        {
            return false;
        }

        // A hard boundary can also split emphasis or strikethrough markers.
        // Escaped markers are literal text and therefore do not participate in
        // the balance check. Falling back to plain text is preferable to
        // allowing Telegram to reject a partially rendered chunk.
        if (!HasBalancedDelimiter(text, '*')
            || !HasBalancedDelimiter(text, '_')
            || !HasBalancedDelimiter(text, '~'))
        {
            return false;
        }

        int openBrackets = 0;
        int openParentheses = 0;
        bool inLinkUrl = false;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                index++;
                continue;
            }

            if (text[index] == '[')
            {
                openBrackets++;
            }
            else if (text[index] == ']')
            {
                openBrackets--;
                if (openBrackets < 0)
                {
                    return false;
                }

                if (index + 1 < text.Length && text[index + 1] == '(')
                {
                    inLinkUrl = true;
                    openParentheses = 1;
                    index++;
                }
            }
            else if (inLinkUrl && text[index] == '(')
            {
                openParentheses++;
            }
            else if (inLinkUrl && text[index] == ')')
            {
                if (--openParentheses == 0)
                {
                    inLinkUrl = false;
                }
            }
        }

        if (openBrackets != 0 || inLinkUrl || openParentheses != 0)
        {
            return false;
        }

        // A rendered chunk may be longer than the source because escaping
        // MarkdownV2 characters adds bytes. In that case plain chunks are the
        // deterministic safe fallback.
        return TelegramTextFormatter.Format(text, TelegramTextFormat.SafeMarkdownV2).Length <= maxLength;
    }

    private static bool HasBalancedDelimiter(string text, char delimiter)
    {
        int count = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                index++;
                continue;
            }

            if (text[index] == delimiter)
            {
                count++;
            }
        }

        return count % 2 == 0;
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

/// <summary>One Telegram-safe chunk and the parse mode it requires.</summary>
internal sealed record TelegramTextChunk(string Text, TelegramTextFormat Format);
