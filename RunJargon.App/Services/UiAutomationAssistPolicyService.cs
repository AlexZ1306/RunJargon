using RunJargon.App.Models;
using RunJargon.App.Utilities;

namespace RunJargon.App.Services;

public sealed class UiAutomationAssistPolicyService
{
    public bool ShouldUseUiAutomation(IReadOnlyList<LayoutTextSegment> ocrSegments)
    {
        if (ocrSegments.Count == 0)
        {
            return false;
        }

        if (ocrSegments.Count > 14)
        {
            return false;
        }

        var paragraphCount = ocrSegments.Count(segment => segment.Kind == TextLayoutKind.Paragraph);
        if (paragraphCount > 0 && ocrSegments.Count > 6)
        {
            return false;
        }

        var shortLabelLikeCount = ocrSegments.Count(IsShortLabelLike);
        if (shortLabelLikeCount < Math.Max(2, (int)Math.Ceiling(ocrSegments.Count * 0.6)))
        {
            return false;
        }

        var rowCount = CountVisualRows(ocrSegments);
        if (rowCount > 4)
        {
            return false;
        }

        return GetMaxSegmentsInSingleRow(ocrSegments) >= 2;
    }

    private static bool IsShortLabelLike(LayoutTextSegment segment)
    {
        if (segment.Kind == TextLayoutKind.Paragraph || segment.Bounds.IsEmpty)
        {
            return false;
        }

        var text = TextRegionIntelligence.NormalizeWhitespace(segment.Text);
        if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetter))
        {
            return false;
        }

        var wordCount = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
        if (wordCount == 0 || wordCount > 4 || text.Length > 36)
        {
            return false;
        }

        return segment.Bounds.Height <= 42;
    }

    private static int CountVisualRows(IReadOnlyList<LayoutTextSegment> segments)
    {
        var ordered = segments
            .Where(segment => !segment.Bounds.IsEmpty)
            .OrderBy(segment => segment.Bounds.Top)
            .ThenBy(segment => segment.Bounds.Left)
            .ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var rows = 1;
        var currentRowAnchor = ordered[0];
        foreach (var segment in ordered.Skip(1))
        {
            if (AreLikelyInSameRow(currentRowAnchor, segment))
            {
                continue;
            }

            rows++;
            currentRowAnchor = segment;
        }

        return rows;
    }

    private static int GetMaxSegmentsInSingleRow(IReadOnlyList<LayoutTextSegment> segments)
    {
        var ordered = segments
            .Where(segment => !segment.Bounds.IsEmpty)
            .OrderBy(segment => segment.Bounds.Top)
            .ThenBy(segment => segment.Bounds.Left)
            .ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var maxInRow = 1;
        var currentInRow = 1;
        var currentRowAnchor = ordered[0];

        foreach (var segment in ordered.Skip(1))
        {
            if (AreLikelyInSameRow(currentRowAnchor, segment))
            {
                currentInRow++;
                maxInRow = Math.Max(maxInRow, currentInRow);
                continue;
            }

            currentInRow = 1;
            currentRowAnchor = segment;
        }

        return maxInRow;
    }

    private static bool AreLikelyInSameRow(LayoutTextSegment left, LayoutTextSegment right)
    {
        var leftCenterY = left.Bounds.Top + (left.Bounds.Height / 2);
        var rightCenterY = right.Bounds.Top + (right.Bounds.Height / 2);
        var centerDelta = Math.Abs(leftCenterY - rightCenterY);
        var typicalHeight = Math.Max(1, Math.Max(left.Bounds.Height, right.Bounds.Height));

        return centerDelta <= Math.Max(10, typicalHeight * 0.8);
    }
}
