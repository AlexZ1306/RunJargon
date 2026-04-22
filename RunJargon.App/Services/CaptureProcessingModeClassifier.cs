using RunJargon.App.Models;
using RunJargon.App.Utilities;

namespace RunJargon.App.Services;

public sealed class CaptureProcessingModeClassifier
{
    public CaptureProcessingMode Classify(IReadOnlyList<LayoutTextSegment> segments)
    {
        if (segments.Count == 0)
        {
            return CaptureProcessingMode.Mixed;
        }

        var paragraphCount = segments.Count(segment => segment.Kind == TextLayoutKind.Paragraph);
        var textLineCount = segments.Count(segment => segment.Kind == TextLayoutKind.TextLine);
        var uiCount = segments.Count(segment => segment.Kind == TextLayoutKind.UiLabel);
        var totalCount = Math.Max(1, segments.Count);
        var paragraphLineRatio = (double)(paragraphCount + textLineCount) / totalCount;
        var uiRatio = (double)uiCount / totalCount;
        var hasDenseUiRow = HasDenseUiRow(segments);

        var looksDocumentLike = (paragraphCount > 0 || paragraphLineRatio >= 0.8)
                                && uiRatio <= 0.2
                                && !hasDenseUiRow;
        if (looksDocumentLike)
        {
            return CaptureProcessingMode.DocumentLike;
        }

        var shortUiCount = segments.Count(IsShortUiLikeSegment);
        if (uiRatio >= 0.5
            || hasDenseUiRow
            || (shortUiCount >= Math.Max(4, (int)Math.Ceiling(totalCount * 0.6))
                && MedianHeight(segments) <= 38))
        {
            return CaptureProcessingMode.UiDense;
        }

        return CaptureProcessingMode.Mixed;
    }

    public bool HasDenseUiRow(IReadOnlyList<LayoutTextSegment> segments)
    {
        var rowGroups = segments
            .Where(segment => IsShortUiLikeSegment(segment))
            .OrderBy(segment => segment.Bounds.Top)
            .ThenBy(segment => segment.Bounds.Left)
            .GroupBy(FindRowAnchor)
            .Select(group => group.Count())
            .DefaultIfEmpty(0);

        return rowGroups.Max() >= 4;
    }

    public bool IsDenseUiRowSegment(LayoutTextSegment segment, IReadOnlyList<LayoutTextSegment> allSegments)
    {
        if (!IsShortUiLikeSegment(segment))
        {
            return false;
        }

        var sameRowCount = allSegments.Count(candidate =>
            !ReferenceEquals(candidate, segment)
            && IsShortUiLikeSegment(candidate)
            && AreLikelyInSameRow(segment, candidate));

        return sameRowCount >= 3;
    }

    private static bool IsShortUiLikeSegment(LayoutTextSegment segment)
    {
        if (segment.Bounds.IsEmpty || segment.Kind == TextLayoutKind.Paragraph)
        {
            return false;
        }

        var text = TextRegionIntelligence.NormalizeWhitespace(segment.Text);
        if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetter))
        {
            return false;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length <= 4
               && text.Length <= 36
               && segment.Bounds.Height <= 42;
    }

    private static double FindRowAnchor(LayoutTextSegment segment)
    {
        return Math.Round(segment.Bounds.Top + (segment.Bounds.Height / 2), MidpointRounding.AwayFromZero);
    }

    private static bool AreLikelyInSameRow(LayoutTextSegment left, LayoutTextSegment right)
    {
        var leftCenterY = left.Bounds.Top + (left.Bounds.Height / 2);
        var rightCenterY = right.Bounds.Top + (right.Bounds.Height / 2);
        var centerDelta = Math.Abs(leftCenterY - rightCenterY);
        var typicalHeight = Math.Max(1, Math.Max(left.Bounds.Height, right.Bounds.Height));
        return centerDelta <= Math.Max(10, typicalHeight * 0.8);
    }

    private static double MedianHeight(IReadOnlyList<LayoutTextSegment> segments)
    {
        var heights = segments
            .Where(segment => !segment.Bounds.IsEmpty)
            .Select(segment => segment.Bounds.Height)
            .OrderBy(value => value)
            .ToArray();
        if (heights.Length == 0)
        {
            return 0;
        }

        var middle = heights.Length / 2;
        if (heights.Length % 2 == 1)
        {
            return heights[middle];
        }

        return (heights[middle - 1] + heights[middle]) / 2d;
    }
}
