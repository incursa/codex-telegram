using System.Text;
using Incursa.Codex.Telegram.Options;
using Telegram.Bot.Types.Enums;

namespace Incursa.Codex.Telegram.Telegram;

/// <summary>
/// Converts ordinary Codex text to a deliberately small, safe MarkdownV2
/// subset. Invalid or unsupported markup is emitted as literal text.
/// </summary>
internal static class TelegramTextFormatter
{
    private const string MarkdownV2SpecialCharacters = "_*[]()~`>#+-=|{}.!\\";

    internal static string Format(string? text, TelegramTextFormat format)
    {
        if (format != TelegramTextFormat.SafeMarkdownV2)
        {
            // Preserve the legacy sender's literal payload. Outbound queue
            // chunking still normalizes line endings at its existing boundary.
            return string.IsNullOrEmpty(text) ? " " : text;
        }

        string normalized = string.IsNullOrEmpty(text)
            ? " "
            : text.Replace("\r\n", "\n", StringComparison.Ordinal);

        return FormatMarkdownV2(normalized);
    }

    internal static ParseMode ToParseMode(TelegramTextFormat format)
        => format == TelegramTextFormat.SafeMarkdownV2 ? ParseMode.MarkdownV2 : ParseMode.None;

    private static string FormatMarkdownV2(string text)
    {
        StringBuilder output = new(text.Length + Math.Min(text.Length / 8, 512));
        for (int index = 0; index < text.Length;)
        {
            if (text.AsSpan(index).StartsWith("```", StringComparison.Ordinal)
                && TryReadCodeFence(text, index, out int fenceEnd, out string code))
            {
                output.Append("```\n");
                AppendEscapedCode(output, code);
                output.Append("\n```");
                index = fenceEnd;
                continue;
            }

            if (text[index] == '`' && TryReadInlineCode(text, index, out int inlineCodeEnd, out string inlineCode))
            {
                output.Append('`');
                AppendEscapedCode(output, inlineCode);
                output.Append('`');
                index = inlineCodeEnd;
                continue;
            }

            if (text[index] == '[' && TryReadLink(text, index, out int linkEnd, out string label, out string url))
            {
                output.Append('[');
                AppendEscaped(output, label);
                output.Append("](");
                output.Append(url
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("(", "\\(", StringComparison.Ordinal)
                    .Replace(")", "\\)", StringComparison.Ordinal));
                output.Append(')');
                index = linkEnd;
                continue;
            }

            if (IsLineStart(text, index) && TryReadLinePrefix(text, index, out int prefixEnd, out string linePrefix, out string lineContent))
            {
                if (linePrefix is "#" or "##" or "###")
                {
                    output.Append('*');
                    AppendEscaped(output, lineContent);
                    output.Append('*');
                }
                else
                {
                    output.Append('•');
                    output.Append(' ');
                    output.Append(FormatMarkdownV2(lineContent));
                }

                if (prefixEnd > 0 && text[prefixEnd - 1] == '\n')
                {
                    output.Append('\n');
                }

                index = prefixEnd;
                continue;
            }

            if (TryReadDelimited(text, index, out int delimitedEnd, out string delimiter, out string delimitedContent))
            {
                string renderedDelimiter = delimiter switch
                {
                    "**" => "*",
                    "__" => "_",
                    "~~" => "~",
                    // Telegram MarkdownV2 uses underscore for italic. Treat a
                    // single asterisk as the common Markdown italic shorthand.
                    "*" => "_",
                    _ => delimiter,
                };
                output.Append(renderedDelimiter);
                output.Append(FormatMarkdownV2(delimitedContent));
                output.Append(renderedDelimiter);
                index = delimitedEnd;
                continue;
            }

            AppendEscaped(output, text[index].ToString());
            index++;
        }

        return output.ToString();
    }

    private static bool TryReadInlineCode(string text, int start, out int end, out string code)
    {
        int closing = text.IndexOf('`', start + 1);
        if (closing < 0 || text[start..closing].Contains('\n'))
        {
            end = 0;
            code = string.Empty;
            return false;
        }

        end = closing + 1;
        code = text[(start + 1)..closing];
        return code.Length > 0;
    }

    private static bool TryReadDelimited(string text, int start, out int end, out string delimiter, out string content)
    {
        delimiter = text.AsSpan(start) switch
        {
            ['*', '*', ..] => "**",
            ['_', '_', ..] => "__",
            ['~', '~', ..] => "~~",
            ['*', ..] => "*",
            ['_', ..] => "_",
            _ => string.Empty,
        };

        if (delimiter.Length == 0 || (delimiter.Length == 1 && start + 1 < text.Length && text[start + 1] == delimiter[0]))
        {
            end = 0;
            content = string.Empty;
            return false;
        }

        int closing = text.IndexOf(delimiter, start + delimiter.Length, StringComparison.Ordinal);
        if (closing <= start + delimiter.Length || text[(start + delimiter.Length)..closing].Contains('\n'))
        {
            end = 0;
            content = string.Empty;
            return false;
        }

        end = closing + delimiter.Length;
        content = text[(start + delimiter.Length)..closing];
        return true;
    }

    private static bool IsLineStart(string text, int index)
        => index == 0 || text[index - 1] == '\n';

    private static bool TryReadLinePrefix(string text, int start, out int end, out string prefix, out string content)
    {
        int lineEnd = text.IndexOf('\n', start);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        string line = text[start..lineEnd];
        int space = line.IndexOf(' ');
        if (space <= 0)
        {
            end = 0;
            prefix = string.Empty;
            content = string.Empty;
            return false;
        }

        string candidate = line[..space];
        bool heading = candidate.Length <= 3 && candidate.All(character => character == '#');
        bool bullet = candidate is "-" or "*" or "+";
        bool numbered = candidate.Length <= 4
            && (candidate.All(char.IsDigit)
                || (candidate.EndsWith(".", StringComparison.Ordinal)
                    && candidate.Length > 1
                    && candidate[..^1].All(char.IsDigit)));
        if (!heading && !bullet && !numbered)
        {
            end = 0;
            prefix = string.Empty;
            content = string.Empty;
            return false;
        }

        end = lineEnd;
        if (end < text.Length)
        {
            end++;
        }

        prefix = heading ? candidate : "-";
        content = line[(space + 1)..];
        return true;
    }

    private static bool TryReadCodeFence(string text, int start, out int end, out string code)
    {
        int openingLineEnd = text.IndexOf('\n', start + 3);
        if (openingLineEnd < 0)
        {
            end = 0;
            code = string.Empty;
            return false;
        }

        int closing = text.IndexOf("```", openingLineEnd + 1, StringComparison.Ordinal);
        if (closing < 0)
        {
            end = 0;
            code = string.Empty;
            return false;
        }

        code = text[(openingLineEnd + 1)..closing];
        end = closing + 3;
        return true;
    }

    private static bool TryReadLink(string text, int start, out int end, out string label, out string url)
    {
        int closeLabel = text.IndexOf(']', start + 1);
        if (closeLabel < 0 || closeLabel + 1 >= text.Length || text[closeLabel + 1] != '(')
        {
            end = 0;
            label = string.Empty;
            url = string.Empty;
            return false;
        }

        int urlStart = closeLabel + 2;
        int depth = 1;
        int closeUrl = -1;
        for (int index = urlStart; index < text.Length; index++)
        {
            char character = text[index];
            if (character == '\\' && index + 1 < text.Length)
            {
                index++;
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && --depth == 0)
            {
                closeUrl = index;
                break;
            }
        }

        if (closeUrl < urlStart)
        {
            end = 0;
            label = string.Empty;
            url = string.Empty;
            return false;
        }

        string candidate = text[urlStart..closeUrl];
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            end = 0;
            label = string.Empty;
            url = string.Empty;
            return false;
        }

        end = closeUrl + 1;
        label = text[(start + 1)..closeLabel];
        url = candidate;
        return true;
    }

    private static void AppendEscapedCode(StringBuilder output, string code)
    {
        foreach (char character in code)
        {
            if (character is '\\' or '`')
            {
                output.Append('\\');
            }

            output.Append(character);
        }
    }

    private static void AppendEscaped(StringBuilder output, string value)
    {
        foreach (char character in value)
        {
            if (character == '\\' || MarkdownV2SpecialCharacters.Contains(character, StringComparison.Ordinal))
            {
                output.Append('\\');
            }

            output.Append(character);
        }
    }
}
