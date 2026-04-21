using System.Text;
using System.Text.RegularExpressions;
using RunJargon.App.Models;
using System.IO;

namespace RunJargon.App.Utilities;

internal static partial class TextRegionIntelligence
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiWhitespaceRegex();

    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex WordRegex();

    private static readonly (string Canonical, string Normalized, string Compact)[] CommonUiLabelPatterns =
    [
        CreateUiLabelPattern("Back"),
        CreateUiLabelPattern("Next"),
        CreateUiLabelPattern("Cancel"),
        CreateUiLabelPattern("Close"),
        CreateUiLabelPattern("Open"),
        CreateUiLabelPattern("Save"),
        CreateUiLabelPattern("Delete"),
        CreateUiLabelPattern("Edit"),
        CreateUiLabelPattern("View"),
        CreateUiLabelPattern("Help"),
        CreateUiLabelPattern("About"),
        CreateUiLabelPattern("Menu"),
        CreateUiLabelPattern("Settings"),
        CreateUiLabelPattern("Search"),
        CreateUiLabelPattern("Home"),
        CreateUiLabelPattern("Filter"),
        CreateUiLabelPattern("List"),
        CreateUiLabelPattern("Files"),
        CreateUiLabelPattern("File"),
        CreateUiLabelPattern("Path"),
        CreateUiLabelPattern("Size"),
        CreateUiLabelPattern("Load"),
        CreateUiLabelPattern("Clear"),
        CreateUiLabelPattern("Rename"),
        CreateUiLabelPattern("Episodes"),
        CreateUiLabelPattern("Subtitles"),
        CreateUiLabelPattern("SFV"),
        CreateUiLabelPattern("Extract All"),
        CreateUiLabelPattern("Profile"),
        CreateUiLabelPattern("Account"),
        CreateUiLabelPattern("Reports"),
        CreateUiLabelPattern("Analytics"),
        CreateUiLabelPattern("Dashboard"),
        CreateUiLabelPattern("Notifications"),
        CreateUiLabelPattern("Download"),
        CreateUiLabelPattern("Upload"),
        CreateUiLabelPattern("Support"),
        CreateUiLabelPattern("Support us"),
        CreateUiLabelPattern("Calculator"),
        CreateUiLabelPattern("Calendar"),
        CreateUiLabelPattern("Charts"),
        CreateUiLabelPattern("Sales"),
        CreateUiLabelPattern("Archives"),
        CreateUiLabelPattern("Types"),
        CreateUiLabelPattern("Parts"),
        CreateUiLabelPattern("Attributes"),
        CreateUiLabelPattern("MediaInfo"),
        CreateUiLabelPattern("Patches"),
        CreateUiLabelPattern("Tags"),
        CreateUiLabelPattern("Continue"),
        CreateUiLabelPattern("Finish"),
        CreateUiLabelPattern("Install"),
        CreateUiLabelPattern("Browse"),
        CreateUiLabelPattern("Apply"),
        CreateUiLabelPattern("Retry"),
        CreateUiLabelPattern("Skip"),
        CreateUiLabelPattern("Accept"),
        CreateUiLabelPattern("Decline"),
        CreateUiLabelPattern("Update"),
        CreateUiLabelPattern("Previous"),
        CreateUiLabelPattern("OK"),
        CreateUiLabelPattern("Yes"),
        CreateUiLabelPattern("No")
    ];

    internal static string NormalizeWhitespace(string text)
    {
        return MultiWhitespaceRegex().Replace(text ?? string.Empty, " ").Trim();
    }

    internal static bool TryParseCommonUiLabel(
        string sourceText,
        out string prefix,
        out string canonicalCore,
        out string suffix)
    {
        prefix = string.Empty;
        canonicalCore = string.Empty;
        suffix = string.Empty;

        var normalized = NormalizeWhitespace(sourceText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var start = 0;
        while (start < normalized.Length && IsUiLeftArrow(normalized[start]))
        {
            start++;
        }

        var end = normalized.Length - 1;
        while (end >= start && IsUiRightArrow(normalized[end]))
        {
            end--;
        }

        prefix = ExtractUiPrefix(normalized[..start]);
        suffix = end + 1 < normalized.Length
            ? ExtractUiSuffix(normalized[(end + 1)..])
            : string.Empty;

        var core = start <= end
            ? normalized[start..(end + 1)].Trim()
            : string.Empty;
        core = TrimUiWrapperPunctuation(core);
        if (string.IsNullOrWhiteSpace(core))
        {
            return false;
        }

        var tokens = WordRegex()
            .Matches(core)
            .Select(match => match.Value)
            .ToList();

        while (tokens.Count > 1 && tokens[0].Length == 1)
        {
            tokens.RemoveAt(0);
        }

        while (tokens.Count > 1 && tokens[^1].Length == 1)
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        if (tokens.Count == 0 || tokens.Count > 2)
        {
            return false;
        }

        var candidateCore = string.Join(" ", tokens);
        var normalizedCandidate = NormalizeUiLabelForMatching(candidateCore);
        if (string.IsNullOrWhiteSpace(normalizedCandidate) || normalizedCandidate.Length > 18)
        {
            return false;
        }

        return TryMatchCommonUiCore(
            candidateCore,
            !string.IsNullOrWhiteSpace(prefix),
            !string.IsNullOrWhiteSpace(suffix),
            out canonicalCore,
            out _);
    }

    internal static string ComposeUiLabel(string prefix, string core, string suffix)
    {
        var trimmedPrefix = prefix.Trim();
        var trimmedCore = NormalizeWhitespace(core);
        var trimmedSuffix = suffix.Trim();

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(trimmedPrefix))
        {
            builder.Append(trimmedPrefix);
            builder.Append(' ');
        }

        builder.Append(trimmedCore);

        if (!string.IsNullOrWhiteSpace(trimmedSuffix))
        {
            builder.Append(' ');
            builder.Append(trimmedSuffix);
        }

        return builder.ToString().Trim();
    }

    internal static bool ShouldSkipLine(OcrLineRegion line, double regionWidth, double regionHeight)
    {
        var text = NormalizeWhitespace(line.Text);
        if (string.IsNullOrWhiteSpace(text) || line.Bounds.IsEmpty)
        {
            return true;
        }

        if (LooksLikeWindowChrome(text))
        {
            return true;
        }

        if (!text.Any(char.IsLetter))
        {
            return true;
        }

        var widthRatio = line.Bounds.Width / Math.Max(1, regionWidth);
        var heightRatio = line.Bounds.Height / Math.Max(1, regionHeight);
        if (text.Length == 1 && widthRatio < 0.08 && heightRatio < 0.12)
        {
            return true;
        }

        return false;
    }

    internal static bool ShouldSkipSegment(string text, ScreenRegion bounds, double regionWidth, double regionHeight)
    {
        var normalized = NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(normalized) || bounds.IsEmpty)
        {
            return true;
        }

        if (LooksLikeWindowChrome(normalized))
        {
            return true;
        }

        if (!normalized.Any(char.IsLetter))
        {
            return true;
        }

        var widthRatio = bounds.Width / Math.Max(1, regionWidth);
        var heightRatio = bounds.Height / Math.Max(1, regionHeight);
        if (normalized.Length == 1 && widthRatio < 0.08 && heightRatio < 0.12)
        {
            return true;
        }

        return false;
    }

    internal static string? GuessSourceLanguage(string text)
    {
        var normalized = NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var latin = 0;
        var cyrillic = 0;
        var kana = 0;
        var han = 0;

        foreach (var ch in normalized)
        {
            if (ch is >= '\u0041' and <= '\u024F')
            {
                latin++;
                continue;
            }

            if (ch is >= '\u0400' and <= '\u052F')
            {
                cyrillic++;
                continue;
            }

            if (ch is >= '\u3040' and <= '\u30FF')
            {
                kana++;
                continue;
            }

            if (ch is >= '\u3400' and <= '\u9FFF')
            {
                han++;
            }
        }

        if (kana > 0)
        {
            return "ja";
        }

        if (han >= 2 && kana == 0)
        {
            return "zh-Hans";
        }

        if (cyrillic >= 2 && cyrillic >= latin)
        {
            return "ru";
        }

        if (latin >= 2)
        {
            return "en";
        }

        return null;
    }

    internal static byte[] BuildInpaintMask(
        int width,
        int height,
        IReadOnlyList<TranslatedOcrLine> overlayLines)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Black);

        foreach (var line in overlayLines)
        {
            var paddingScaleX = line.LayoutKind == TextLayoutKind.UiLabel ? 0.18 : 0.28;
            var paddingScaleY = line.LayoutKind == TextLayoutKind.UiLabel ? 0.12 : 0.18;
            var paddingX = Math.Clamp((int)Math.Round(line.Bounds.Height * paddingScaleX), 1, 18);
            var paddingY = Math.Clamp((int)Math.Round(line.Bounds.Height * paddingScaleY), 1, 14);
            var left = Math.Max(0, (int)Math.Floor(line.Bounds.Left) - paddingX);
            var top = Math.Max(0, (int)Math.Floor(line.Bounds.Top) - paddingY);
            var right = Math.Min(width, (int)Math.Ceiling(line.Bounds.Right) + paddingX);
            var bottom = Math.Min(height, (int)Math.Ceiling(line.Bounds.Bottom) + paddingY);
            var rectWidth = Math.Max(1, right - left);
            var rectHeight = Math.Max(1, bottom - top);

            graphics.FillRectangle(System.Drawing.Brushes.White, left, top, rectWidth, rectHeight);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    internal static string MergeTexts(IReadOnlyList<OcrLineRegion> lines)
    {
        var builder = new StringBuilder();

        foreach (var text in lines.Select(line => NormalizeWhitespace(line.Text)).Where(text => !string.IsNullOrWhiteSpace(text)))
        {
            if (builder.Length == 0)
            {
                builder.Append(text);
                continue;
            }

            if (builder[^1] == '-' && char.IsLetterOrDigit(text[0]))
            {
                builder.Append(text);
                continue;
            }

            builder.Append(' ');
            builder.Append(text);
        }

        return builder.ToString();
    }

    private static bool LooksLikeWindowChrome(string text)
    {
        if (text.Length == 1)
        {
            return text[0] is 'x' or 'X' or '×' or '-' or '_' or '□' or '◻' or '◽';
        }

        return text.All(ch => ch is 'x' or 'X' or '×' or '-' or '_' or '□' or '◻' or '◽' or ' ' or '·');
    }

    private static bool TryMatchCommonUiCore(
        string candidate,
        bool hasLeftArrow,
        bool hasRightArrow,
        out string canonicalCore,
        out int distance)
    {
        canonicalCore = string.Empty;
        distance = int.MaxValue;

        var normalizedCandidate = NormalizeUiLabelForMatching(candidate);
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return false;
        }

        var compactCandidate = normalizedCandidate.Replace(" ", string.Empty, StringComparison.Ordinal);
        (string Canonical, int RawDistance, int EffectiveDistance)? best = null;

        foreach (var pattern in CommonUiLabelPatterns)
        {
            var arrowPreferred = IsArrowPreferred(pattern.Canonical, hasLeftArrow, hasRightArrow);
            if (!IsViableUiLabelMatch(compactCandidate, pattern.Compact, arrowPreferred))
            {
                continue;
            }

            var currentDistance = compactCandidate == pattern.Compact
                ? 0
                : ComputeLevenshteinDistance(compactCandidate, pattern.Compact);

            var maxDistance = GetMaxUiLabelDistance(pattern.Compact.Length) + (arrowPreferred ? 2 : 0);
            if (Math.Abs(compactCandidate.Length - pattern.Compact.Length) > Math.Max(2, maxDistance + 1))
            {
                continue;
            }

            if (currentDistance > maxDistance)
            {
                continue;
            }

            var effectiveDistance = Math.Max(0, currentDistance - (arrowPreferred ? 1 : 0));
            var isBetter = best is null
                           || effectiveDistance < best.Value.EffectiveDistance
                           || (effectiveDistance == best.Value.EffectiveDistance
                               && currentDistance < best.Value.RawDistance);
            if (!isBetter)
            {
                continue;
            }

            best = (pattern.Canonical, currentDistance, effectiveDistance);
        }

        if (best is null)
        {
            return false;
        }

        canonicalCore = best.Value.Canonical;
        distance = best.Value.RawDistance;
        return true;
    }

    private static bool IsViableUiLabelMatch(string candidate, string pattern, bool arrowPreferred)
    {
        if (string.Equals(candidate, pattern, StringComparison.Ordinal))
        {
            return true;
        }

        if (candidate.Length == 0 || pattern.Length == 0)
        {
            return false;
        }

        if (arrowPreferred)
        {
            return candidate[0] == pattern[0];
        }

        if (candidate[0] != pattern[0])
        {
            return false;
        }

        var commonPrefix = CountCommonPrefix(candidate, pattern);
        if (candidate.Length <= 4 || pattern.Length <= 4)
        {
            return commonPrefix >= 1;
        }

        return commonPrefix >= 2;
    }

    private static string NormalizeUiLabelForMatching(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingWhitespace = false;

        foreach (var ch in text)
        {
            var mapped = MapConfusableToLatin(ch);
            if (char.IsLetterOrDigit(mapped))
            {
                if (pendingWhitespace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(mapped));
                pendingWhitespace = false;
                continue;
            }

            if (char.IsWhiteSpace(mapped))
            {
                pendingWhitespace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static char MapConfusableToLatin(char ch)
    {
        return ch switch
        {
            'А' or 'а' => 'A',
            'В' => 'B',
            'в' => 'B',
            'Е' or 'е' => 'E',
            'К' or 'к' => 'K',
            'М' or 'м' => 'M',
            'Н' => 'H',
            'н' => 'H',
            'О' or 'о' => 'O',
            'Р' or 'р' => 'P',
            'С' or 'с' => 'C',
            'Т' or 'т' => 'T',
            'У' or 'у' => 'Y',
            'Х' or 'х' => 'X',
            'І' or 'і' => 'I',
            'Ј' or 'ј' => 'J',
            'Ь' or 'ъ' => 'b',
            _ => ch
        };
    }

    private static string ExtractUiPrefix(string rawPrefix)
    {
        var chars = rawPrefix
            .Where(IsUiLeftArrow)
            .ToArray();

        return chars.Length == 0 ? string.Empty : new string(chars);
    }

    private static string ExtractUiSuffix(string rawSuffix)
    {
        var chars = rawSuffix
            .Where(IsUiRightArrow)
            .ToArray();

        return chars.Length == 0 ? string.Empty : new string(chars);
    }

    private static string TrimUiWrapperPunctuation(string text)
    {
        return text.Trim(' ', '.', ',', ':', ';', '|', '/', '\\', '(', ')', '[', ']', '{', '}');
    }

    private static bool IsArrowPreferred(string canonical, bool hasLeftArrow, bool hasRightArrow)
    {
        if (hasLeftArrow && canonical is "Back" or "Previous")
        {
            return true;
        }

        if (hasRightArrow && canonical is "Next" or "Continue")
        {
            return true;
        }

        return false;
    }

    private static bool IsUiLeftArrow(char ch)
    {
        return ch is '<' or '‹' or '«' or '←';
    }

    private static bool IsUiRightArrow(char ch)
    {
        return ch is '>' or '›' or '»' or '→';
    }

    private static (string Canonical, string Normalized, string Compact) CreateUiLabelPattern(string canonical)
    {
        var normalized = NormalizeUiLabelForMatching(canonical);
        return (canonical, normalized, normalized.Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    private static int GetMaxUiLabelDistance(int labelLength)
    {
        if (labelLength <= 3)
        {
            return 0;
        }

        if (labelLength <= 4)
        {
            return 1;
        }

        if (labelLength <= 8)
        {
            return 2;
        }

        return 3;
    }

    private static int ComputeLevenshteinDistance(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 0;
        }

        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= right.Length; j++)
            {
                var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static int CountCommonPrefix(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        var matched = 0;

        while (matched < length && left[matched] == right[matched])
        {
            matched++;
        }

        return matched;
    }
}
