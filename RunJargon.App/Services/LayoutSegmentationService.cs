using RunJargon.App.Models;
using RunJargon.App.Utilities;

namespace RunJargon.App.Services;

public sealed class LayoutSegmentationService
{
    public IReadOnlyList<LayoutTextSegment> BuildSegments(IReadOnlyList<OcrLineRegion> lines)
    {
        if (lines.Count == 0)
        {
            return Array.Empty<LayoutTextSegment>();
        }

        var fullRegion = CombineBounds(lines);
        var normalizedLines = MergeSameRowFragments(lines)
            .Where(line => !TextRegionIntelligence.ShouldSkipLine(line, fullRegion.Width, fullRegion.Height))
            .SelectMany(ExpandLayoutAwareLine)
            .ToArray();

        var sorted = normalizedLines
            .Where(item => !string.IsNullOrWhiteSpace(item.Line.Text) && !item.Line.Bounds.IsEmpty)
            .OrderBy(item => item.Line.Bounds.Top)
            .ThenBy(item => item.Line.Bounds.Left)
            .ToList();

        if (sorted.Count == 0)
        {
            return Array.Empty<LayoutTextSegment>();
        }

        var regionLeft = sorted.Min(item => item.Line.Bounds.Left);
        var regionRight = sorted.Max(item => item.Line.Bounds.Right);
        var regionWidth = Math.Max(1, regionRight - regionLeft);

        var segments = new List<LayoutTextSegment>();
        var currentGroup = new List<SegmentSourceLine> { sorted[0] };

        foreach (var candidate in sorted.Skip(1))
        {
            if (ShouldMergeIntoParagraphBlock(currentGroup, candidate, regionWidth))
            {
                currentGroup.Add(candidate);
                continue;
            }

            FlushSegmentGroup(segments, currentGroup, fullRegion.Width, fullRegion.Height);
            currentGroup = new List<SegmentSourceLine> { candidate };
        }

        FlushSegmentGroup(segments, currentGroup, fullRegion.Width, fullRegion.Height);
        return segments;
    }

    private static IReadOnlyList<SegmentSourceLine> ExpandLayoutAwareLine(OcrLineRegion line)
    {
        var normalizedLine = NormalizeCommonUiLabel(line);

        if (TrySplitKnownUiLabelSequence(normalizedLine, out var knownLabels))
        {
            return knownLabels
                .Select(splitLine => new SegmentSourceLine(NormalizeCommonUiLabel(splitLine), TextLayoutKind.UiLabel))
                .ToArray();
        }

        if (TrySplitSingleTokenUiRow(normalizedLine, out var singleTokenLabels))
        {
            return singleTokenLabels
                .Select(splitLine => new SegmentSourceLine(NormalizeCommonUiLabel(splitLine), TextLayoutKind.UiLabel))
                .ToArray();
        }

        if (TrySplitStructuredWordRow(normalizedLine.Bounds, GetOrderedWords(normalizedLine), out var structuredLabels))
        {
            return structuredLabels
                .Select(splitLine => new SegmentSourceLine(NormalizeCommonUiLabel(splitLine), TextLayoutKind.UiLabel))
                .ToArray();
        }

        if (TrySplitSparseGapRow(normalizedLine, out var sparseLabels))
        {
            return sparseLabels
                .Select(splitLine => new SegmentSourceLine(NormalizeCommonUiLabel(splitLine), TextLayoutKind.UiLabel))
                .ToArray();
        }

        return
        [
            new SegmentSourceLine(normalizedLine, ClassifyStandaloneLine(normalizedLine))
        ];
    }

    private static TextLayoutKind ClassifyStandaloneLine(OcrLineRegion line)
    {
        var normalizedText = TextRegionIntelligence.NormalizeWhitespace(line.Text);
        var wordCount = line.Words?.Count ?? CountWords(normalizedText);

        if (wordCount >= 6 || normalizedText.Length >= 30 || ContainsSentencePunctuation(normalizedText))
        {
            return TextLayoutKind.Paragraph;
        }

        return TextLayoutKind.TextLine;
    }

    private static void FlushSegmentGroup(
        List<LayoutTextSegment> segments,
        IReadOnlyList<SegmentSourceLine> group,
        double regionWidth,
        double regionHeight)
    {
        if (group.Count == 0)
        {
            return;
        }

        var lines = group.Select(item => item.Line).ToArray();
        var text = TextRegionIntelligence.MergeTexts(lines);
        var bounds = CombineBounds(lines);
        if (TextRegionIntelligence.ShouldSkipSegment(text, bounds, regionWidth, regionHeight))
        {
            return;
        }

        var kind = group.Any(item => item.Kind == TextLayoutKind.UiLabel)
            ? TextLayoutKind.UiLabel
            : group.Count > 1 || group.Any(item => item.Kind == TextLayoutKind.Paragraph)
                ? TextLayoutKind.Paragraph
                : TextLayoutKind.TextLine;

        segments.Add(new LayoutTextSegment(text, bounds, lines, kind));
    }

    private static bool ShouldMergeIntoParagraphBlock(
        IReadOnlyList<SegmentSourceLine> currentGroup,
        SegmentSourceLine candidate,
        double regionWidth)
    {
        if (currentGroup.Count == 0
            || currentGroup.Any(item => item.Kind == TextLayoutKind.UiLabel)
            || candidate.Kind == TextLayoutKind.UiLabel)
        {
            return false;
        }

        var last = currentGroup[^1].Line;
        var groupBounds = CombineBounds(currentGroup.Select(item => item.Line).ToArray());
        var typicalHeight = Math.Max(1, Math.Max(last.Bounds.Height, candidate.Line.Bounds.Height));
        var minHeight = Math.Max(1, Math.Min(last.Bounds.Height, candidate.Line.Bounds.Height));
        var heightRatio = typicalHeight / minHeight;
        var verticalGap = candidate.Line.Bounds.Top - last.Bounds.Bottom;

        if (verticalGap < -2 || verticalGap > Math.Max(10, typicalHeight * 0.95))
        {
            return false;
        }

        if (heightRatio > 1.26)
        {
            return false;
        }

        var groupWidthRatio = groupBounds.Width / regionWidth;
        var candidateWidthRatio = candidate.Line.Bounds.Width / regionWidth;
        if (groupWidthRatio < 0.45 || candidateWidthRatio < 0.45)
        {
            return false;
        }

        var leftDelta = Math.Abs(candidate.Line.Bounds.Left - groupBounds.Left);
        var rightDelta = Math.Abs(candidate.Line.Bounds.Right - groupBounds.Right);
        var widthDelta = Math.Abs(candidate.Line.Bounds.Width - groupBounds.Width);

        var alignedLeft = leftDelta <= Math.Max(18, typicalHeight * 1.5);
        var alignedRight = rightDelta <= Math.Max(28, typicalHeight * 2.1)
                           || widthDelta <= Math.Max(36, regionWidth * 0.18);
        var paragraphLike = currentGroup.Any(item => item.Kind == TextLayoutKind.Paragraph)
                            || candidate.Kind == TextLayoutKind.Paragraph;
        var textLongEnough = paragraphLike
                             || last.Text.Trim().Length >= 24
                             || candidate.Line.Text.Trim().Length >= 24;

        return alignedLeft && alignedRight && textLongEnough;
    }

    private static IReadOnlyList<OcrLineRegion> MergeSameRowFragments(IReadOnlyList<OcrLineRegion> lines)
    {
        var sorted = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text) && !line.Bounds.IsEmpty)
            .OrderBy(line => line.Bounds.Top)
            .ThenBy(line => line.Bounds.Left)
            .ToList();

        if (sorted.Count == 0)
        {
            return Array.Empty<OcrLineRegion>();
        }

        var merged = new List<OcrLineRegion>();
        var current = sorted[0];

        foreach (var candidate in sorted.Skip(1))
        {
            if (ShouldMergeInline(current, candidate))
            {
                current = new OcrLineRegion(
                    MergeInlineText(current.Text, candidate.Text),
                    CombineBounds([current, candidate]),
                    MergeWords(current.Words, candidate.Words));
                continue;
            }

            merged.Add(current);
            current = candidate;
        }

        merged.Add(current);
        return merged;
    }

    private static bool ShouldMergeInline(OcrLineRegion current, OcrLineRegion candidate)
    {
        var currentCenterY = current.Bounds.Top + (current.Bounds.Height / 2);
        var candidateCenterY = candidate.Bounds.Top + (candidate.Bounds.Height / 2);
        var centerDelta = Math.Abs(currentCenterY - candidateCenterY);
        var typicalHeight = Math.Max(1, Math.Max(current.Bounds.Height, candidate.Bounds.Height));
        if (centerDelta > Math.Max(5, typicalHeight * 0.38))
        {
            return false;
        }

        var horizontalGap = candidate.Bounds.Left - current.Bounds.Right;
        if (horizontalGap < -2 || horizontalGap > Math.Max(26, typicalHeight * 2.4))
        {
            return false;
        }

        if (LooksLikeSeparateUiLabels(current, candidate, horizontalGap, typicalHeight))
        {
            return false;
        }

        return true;
    }

    private static string MergeInlineText(string left, string right)
    {
        var first = left.Trim();
        var second = right.Trim();
        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        if (first.EndsWith('-') || second.StartsWith('.') || second.StartsWith(',') || second.StartsWith(':'))
        {
            return first + second;
        }

        return $"{first} {second}";
    }

    private static IReadOnlyList<OcrWordRegion> MergeWords(
        IReadOnlyList<OcrWordRegion>? left,
        IReadOnlyList<OcrWordRegion>? right)
    {
        return (left ?? Array.Empty<OcrWordRegion>())
            .Concat(right ?? Array.Empty<OcrWordRegion>())
            .OrderBy(word => word.Bounds.Left)
            .ToArray();
    }

    private static bool LooksLikeSeparateUiLabels(
        OcrLineRegion current,
        OcrLineRegion candidate,
        double horizontalGap,
        double typicalHeight)
    {
        var currentText = TextRegionIntelligence.NormalizeWhitespace(current.Text);
        var candidateText = TextRegionIntelligence.NormalizeWhitespace(candidate.Text);
        var currentWordCount = current.Words?.Count ?? CountWords(currentText);
        var candidateWordCount = candidate.Words?.Count ?? CountWords(candidateText);

        var bothShortLabels = currentText.Length <= 18
                              && candidateText.Length <= 18
                              && currentWordCount <= 2
                              && candidateWordCount <= 2;
        if (!bothShortLabels)
        {
            return false;
        }

        var currentIsUiLabel = TextRegionIntelligence.TryParseCommonUiLabel(currentText, out _, out _, out _);
        var candidateIsUiLabel = TextRegionIntelligence.TryParseCommonUiLabel(candidateText, out _, out _, out _);
        if (currentIsUiLabel && candidateIsUiLabel)
        {
            return true;
        }

        var gapLooksIntentional = horizontalGap > Math.Max(14, typicalHeight * 0.95)
                                  || horizontalGap > Math.Min(current.Bounds.Width, candidate.Bounds.Width) * 0.32;

        return gapLooksIntentional;
    }

    private static int CountWords(string text)
    {
        return text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Length;
    }

    private static OcrLineRegion NormalizeCommonUiLabel(OcrLineRegion line)
    {
        if (!TextRegionIntelligence.TryParseCommonUiLabel(line.Text, out var prefix, out var canonicalCore, out var suffix))
        {
            return line;
        }

        var correctedText = TextRegionIntelligence.ComposeUiLabel(prefix, canonicalCore, suffix);
        if (string.Equals(
                TextRegionIntelligence.NormalizeWhitespace(correctedText),
                TextRegionIntelligence.NormalizeWhitespace(line.Text),
                StringComparison.Ordinal))
        {
            return line;
        }

        return new OcrLineRegion(correctedText, line.Bounds, line.Words);
    }

    private static bool TrySplitKnownUiLabelSequence(
        OcrLineRegion line,
        out IReadOnlyList<OcrLineRegion> splitLines)
    {
        splitLines = Array.Empty<OcrLineRegion>();
        var words = GetOrderedWords(line);
        if (words.Count < 2 || words.Count > 10)
        {
            return false;
        }

        var clusters = new List<IReadOnlyList<OcrWordRegion>>();
        var index = 0;

        while (index < words.Count)
        {
            if (index + 1 < words.Count)
            {
                var pairText = $"{words[index].Text} {words[index + 1].Text}";
                if (TextRegionIntelligence.TryParseCommonUiLabel(pairText, out _, out _, out _))
                {
                    clusters.Add([words[index], words[index + 1]]);
                    index += 2;
                    continue;
                }
            }

            if (!TextRegionIntelligence.TryParseCommonUiLabel(words[index].Text, out _, out _, out _))
            {
                return false;
            }

            clusters.Add([words[index]]);
            index++;
        }

        if (clusters.Count < 2)
        {
            return false;
        }

        splitLines = clusters
            .Select(BuildLineFromWords)
            .ToArray();

        return true;
    }

    private static bool TrySplitSingleTokenUiRow(
        OcrLineRegion line,
        out IReadOnlyList<OcrLineRegion> splitLines)
    {
        splitLines = Array.Empty<OcrLineRegion>();
        var words = GetOrderedWords(line);
        if (!LooksLikeSingleTokenUiRow(line.Bounds, words))
        {
            return false;
        }

        splitLines = words
            .Select(word => BuildLineFromWords([word]))
            .ToArray();

        return splitLines.Count >= 2;
    }

    private static bool LooksLikeSingleTokenUiRow(
        ScreenRegion lineBounds,
        IReadOnlyList<OcrWordRegion> words)
    {
        if (words.Count < 3 || words.Count > 10)
        {
            return false;
        }

        var normalizedWords = words
            .Select(word => TextRegionIntelligence.NormalizeWhitespace(word.Text))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        if (normalizedWords.Length != words.Count)
        {
            return false;
        }

        if (normalizedWords.Any(text => text.Length > 18 || ContainsSentencePunctuation(text)))
        {
            return false;
        }

        var gaps = GetPositiveGaps(words).ToArray();
        if (gaps.Length < words.Count - 1)
        {
            return false;
        }

        var medianHeight = Median(words.Select(word => word.Bounds.Height));
        var medianGap = Median(gaps);
        var totalWordWidth = words.Sum(word => word.Bounds.Width);
        var occupancy = totalWordWidth / Math.Max(1, lineBounds.Width);
        var lineAspectRatio = lineBounds.Width / Math.Max(1, medianHeight);
        if (occupancy > 0.78 || lineAspectRatio < 5.5 || medianGap < Math.Max(4, medianHeight * 0.32))
        {
            return false;
        }

        var headerishWords = normalizedWords.Count(text =>
            IsLikelyHeaderWord(text)
            || TextRegionIntelligence.TryParseCommonUiLabel(text, out _, out _, out _));

        return headerishWords >= Math.Max(2, (int)Math.Ceiling(normalizedWords.Length * 0.5))
               || normalizedWords.All(text => text.Length <= 12);
    }

    private static bool TrySplitStructuredWordRow(
        ScreenRegion lineBounds,
        IReadOnlyList<OcrWordRegion> words,
        out IReadOnlyList<OcrLineRegion> splitLines)
    {
        splitLines = Array.Empty<OcrLineRegion>();
        if (!LooksLikeStructuredWordRow(lineBounds, words))
        {
            return false;
        }

        var gaps = GetPositiveGaps(words).ToArray();
        if (gaps.Length == 0)
        {
            return false;
        }

        var medianGap = Median(gaps);
        var minGap = gaps.Min();
        var medianHeight = Median(words.Select(word => word.Bounds.Height));
        var intraLabelGapThreshold = Math.Min(
            Math.Max(6, medianHeight * 0.45),
            Math.Max(minGap * 1.35, medianGap * 0.58));

        var clusters = new List<List<OcrWordRegion>>
        {
            new() { words[0] }
        };

        for (var i = 1; i < words.Count; i++)
        {
            var previous = words[i - 1];
            var current = words[i];
            var gap = current.Bounds.Left - previous.Bounds.Right;

            if (gap > intraLabelGapThreshold)
            {
                clusters.Add(new List<OcrWordRegion> { current });
                continue;
            }

            clusters[^1].Add(current);
        }

        var standaloneClusterRatio = clusters.Count / (double)words.Count;
        if (clusters.Count < 2
            || standaloneClusterRatio < 0.55
            || clusters.Any(cluster => cluster.Count > 3))
        {
            return false;
        }

        splitLines = clusters
            .Select(BuildLineFromWords)
            .Where(clusterLine => !string.IsNullOrWhiteSpace(clusterLine.Text) && !clusterLine.Bounds.IsEmpty)
            .ToArray();

        return splitLines.Count >= 2;
    }

    private static bool LooksLikeStructuredWordRow(
        ScreenRegion lineBounds,
        IReadOnlyList<OcrWordRegion> words)
    {
        if (words.Count < 3 || words.Count > 10)
        {
            return false;
        }

        var normalizedWords = words
            .Select(word => TextRegionIntelligence.NormalizeWhitespace(word.Text))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        if (normalizedWords.Length != words.Count)
        {
            return false;
        }

        if (normalizedWords.Any(text => text.Length > 18 || ContainsSentencePunctuation(text)))
        {
            return false;
        }

        var medianHeight = Median(words.Select(word => word.Bounds.Height));
        var medianWordWidth = Median(words.Select(word => word.Bounds.Width));
        var lineAspectRatio = lineBounds.Width / Math.Max(1, medianHeight);
        if (lineAspectRatio < 6.5)
        {
            return false;
        }

        var totalWordWidth = words.Sum(word => word.Bounds.Width);
        var occupancy = totalWordWidth / Math.Max(1, lineBounds.Width);
        if (occupancy > 0.82)
        {
            return false;
        }

        var gaps = GetPositiveGaps(words).ToArray();
        if (gaps.Length == 0)
        {
            return false;
        }

        var medianGap = Median(gaps);
        var gapToWidthRatio = medianGap / Math.Max(1, medianWordWidth);
        var gapToHeightRatio = medianGap / Math.Max(1, medianHeight);
        if (gapToWidthRatio < 0.14 && gapToHeightRatio < 0.18 && occupancy > 0.66)
        {
            return false;
        }

        var titleCaseLikeWords = normalizedWords.Count(IsLikelyHeaderWord);
        var enoughHeaderWords = titleCaseLikeWords >= Math.Max(2, (int)Math.Ceiling(normalizedWords.Length * 0.55));
        if (!enoughHeaderWords)
        {
            var allShort = normalizedWords.All(text => text.Length <= 12);
            if (!allShort || occupancy > 0.68)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TrySplitSparseGapRow(
        OcrLineRegion line,
        out IReadOnlyList<OcrLineRegion> splitLines)
    {
        splitLines = Array.Empty<OcrLineRegion>();

        var words = GetOrderedWords(line);
        if (words.Count < 2 || words.Count > 10)
        {
            return false;
        }

        var clusters = new List<List<OcrWordRegion>>
        {
            new() { words[0] }
        };
        var typicalHeight = Math.Max(1, line.Bounds.Height);
        var wideGapCount = 0;

        for (var i = 1; i < words.Count; i++)
        {
            var previous = words[i - 1];
            var current = words[i];
            var gap = current.Bounds.Left - previous.Bounds.Right;
            var threshold = Math.Max(
                18,
                Math.Max(typicalHeight * 1.15, Math.Min(previous.Bounds.Width, current.Bounds.Width) * 0.65));

            if (gap > threshold)
            {
                clusters.Add(new List<OcrWordRegion> { current });
                wideGapCount++;
                continue;
            }

            clusters[^1].Add(current);
        }

        if (clusters.Count < 2 || wideGapCount == 0 || clusters.Count > 6)
        {
            return false;
        }

        var looksLikeUiRow = clusters.All(cluster =>
        {
            var text = TextRegionIntelligence.NormalizeWhitespace(string.Join(" ", cluster.Select(word => word.Text)));
            return !string.IsNullOrWhiteSpace(text)
                   && cluster.Count <= 2
                   && text.Length <= 22;
        });
        if (!looksLikeUiRow)
        {
            return false;
        }

        splitLines = clusters
            .Select(BuildLineFromWords)
            .Where(clusterLine => !string.IsNullOrWhiteSpace(clusterLine.Text) && !clusterLine.Bounds.IsEmpty)
            .ToArray();

        return splitLines.Count >= 2;
    }

    private static IReadOnlyList<OcrWordRegion> GetOrderedWords(OcrLineRegion line)
    {
        return line.Words?
            .Where(word => !string.IsNullOrWhiteSpace(word.Text) && !word.Bounds.IsEmpty)
            .OrderBy(word => word.Bounds.Left)
            .ToArray()
            ?? Array.Empty<OcrWordRegion>();
    }

    private static IEnumerable<double> GetPositiveGaps(IReadOnlyList<OcrWordRegion> words)
    {
        for (var i = 1; i < words.Count; i++)
        {
            var gap = words[i].Bounds.Left - words[i - 1].Bounds.Right;
            if (gap > 0)
            {
                yield return gap;
            }
        }
    }

    private static bool ContainsSentencePunctuation(string text)
    {
        return text.Any(ch => ch is '.' or ',' or ';' or ':' or '!' or '?' or '"' or '\'' or '(' or ')' or '[' or ']' or '{' or '}' or '—' or '-' or '/');
    }

    private static bool IsLikelyHeaderWord(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var token = text.Trim();
        if (token.Length == 1)
        {
            return char.IsUpper(token[0]) || char.IsDigit(token[0]);
        }

        var letters = token.Where(char.IsLetter).ToArray();
        if (letters.Length == 0)
        {
            return false;
        }

        var uppercaseLetters = letters.Count(char.IsUpper);
        return char.IsUpper(token[0]) || uppercaseLetters >= Math.Max(2, letters.Length - 1);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values
            .Where(value => value > 0)
            .OrderBy(value => value)
            .ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var middle = ordered.Length / 2;
        if (ordered.Length % 2 == 1)
        {
            return ordered[middle];
        }

        return (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private static OcrLineRegion BuildLineFromWords(IReadOnlyList<OcrWordRegion> words)
    {
        var left = words.Min(word => word.Bounds.Left);
        var top = words.Min(word => word.Bounds.Top);
        var right = words.Max(word => word.Bounds.Right);
        var bottom = words.Max(word => word.Bounds.Bottom);
        var text = string.Join(" ", words.Select(word => word.Text));

        return new OcrLineRegion(
            TextRegionIntelligence.NormalizeWhitespace(text),
            new ScreenRegion(left, top, right - left, bottom - top),
            words.ToArray());
    }

    private static ScreenRegion CombineBounds(IReadOnlyList<OcrLineRegion> lines)
    {
        var left = lines.Min(line => line.Bounds.Left);
        var top = lines.Min(line => line.Bounds.Top);
        var right = lines.Max(line => line.Bounds.Right);
        var bottom = lines.Max(line => line.Bounds.Bottom);

        return new ScreenRegion(left, top, right - left, bottom - top);
    }

    private static ScreenRegion CombineBounds(IReadOnlyList<ScreenRegion> bounds)
    {
        var left = bounds.Min(item => item.Left);
        var top = bounds.Min(item => item.Top);
        var right = bounds.Max(item => item.Right);
        var bottom = bounds.Max(item => item.Bottom);

        return new ScreenRegion(left, top, right - left, bottom - top);
    }

    private readonly record struct SegmentSourceLine(OcrLineRegion Line, TextLayoutKind Kind);
}
