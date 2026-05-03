using System.Text;

namespace Incursa.Codex.Telegram.Services;

internal static class CodexTextFormatting
{
    private static readonly Encoding Cp437 = CreateCp437Encoding();

    public static string ResolveDisplayName(string? primary, string? fallback)
    {
        string? normalizedPrimary = CollapseWhitespace(RepairUtf8Mojibake(primary));
        if (!string.IsNullOrWhiteSpace(normalizedPrimary))
        {
            return normalizedPrimary;
        }

        string? normalizedFallback = CollapseWhitespace(RepairUtf8Mojibake(fallback));
        return string.IsNullOrWhiteSpace(normalizedFallback) ? string.Empty : normalizedFallback;
    }

    public static string? RepairUtf8Mojibake(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf("ΓÇ", StringComparison.Ordinal) < 0)
        {
            return value;
        }

        try
        {
            return Encoding.UTF8.GetString(Cp437.GetBytes(value));
        }
        catch (EncoderFallbackException)
        {
            return value;
        }
    }

    public static string TruncatePreview(string? value, int maxLength = 120)
    {
        string? normalized = CollapseWhitespace(RepairUtf8Mojibake(value));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return TruncateAtWordBoundary(normalized, maxLength);
    }

    public static string InferThreadName(
        string? explicitName,
        string? prompt,
        string? fallback = null,
        int maxLength = 64)
    {
        string? normalizedExplicit = CollapseWhitespace(RepairUtf8Mojibake(explicitName));
        if (!string.IsNullOrWhiteSpace(normalizedExplicit))
        {
            return normalizedExplicit;
        }

        string inferred = TruncatePreview(RepairUtf8Mojibake(prompt), maxLength);
        if (!string.IsNullOrWhiteSpace(inferred))
        {
            return inferred;
        }

        string? normalizedFallback = CollapseWhitespace(RepairUtf8Mojibake(fallback));
        return string.IsNullOrWhiteSpace(normalizedFallback) ? "Untitled thread" : normalizedFallback;
    }

    public static string ResolveProjectName(string? workingDirectory)
    {
        string? normalizedWorkingDirectory = CollapseWhitespace(RepairUtf8Mojibake(workingDirectory));
        if (string.IsNullOrWhiteSpace(normalizedWorkingDirectory))
        {
            return "Default project";
        }

        string trimmed = normalizedWorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string projectName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(projectName) ? normalizedWorkingDirectory : projectName;
    }

    private static string? CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string TruncateAtWordBoundary(string value, int maxLength)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        int cut = value.LastIndexOf(' ', Math.Min(value.Length, maxLength));
        if (cut <= 0 || cut < maxLength / 2)
        {
            cut = maxLength;
        }

        return value[..cut].TrimEnd() + "…";
    }

    private static Encoding CreateCp437Encoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            437,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }
}
