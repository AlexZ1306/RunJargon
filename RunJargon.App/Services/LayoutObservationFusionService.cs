using RunJargon.App.Models;
using RunJargon.App.Utilities;

namespace RunJargon.App.Services;

public sealed class LayoutObservationFusionService
{
    public IReadOnlyList<LayoutTextSegment> Merge(
        IReadOnlyList<LayoutTextSegment> primarySegments,
        IReadOnlyList<LayoutTextSegment> uiAutomationSegments)
    {
        if (uiAutomationSegments.Count == 0)
        {
            return primarySegments;
        }

        var suppressedPrimaryIndexes = new HashSet<int>();
        var hasParagraphPrimary = primarySegments.Any(segment => segment.Kind == TextLayoutKind.Paragraph);

        for (var i = 0; i < primarySegments.Count; i++)
        {
            var primary = primarySegments[i];
            if (primary.Kind == TextLayoutKind.Paragraph)
            {
                continue;
            }

            var overlappingUi = uiAutomationSegments
                .Where(segment => IsRelated(primary, segment))
                .ToArray();
            if (overlappingUi.Length == 0)
            {
                continue;
            }

            if (overlappingUi.Length >= 2)
            {
                suppressedPrimaryIndexes.Add(i);
                continue;
            }

            var uiSegment = overlappingUi[0];
            if (string.Equals(
                    TextRegionIntelligence.NormalizeWhitespace(primary.Text),
                    TextRegionIntelligence.NormalizeWhitespace(uiSegment.Text),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var primaryScore = OcrQualityHeuristics.ScoreLineText(primary.Text);
            var uiScore = OcrQualityHeuristics.ScoreLineText(uiSegment.Text);
            var overlapCoverage = Coverage(primary.Bounds, uiSegment.Bounds);

            if (overlapCoverage >= 0.28
                && (uiScore >= primaryScore || primary.Bounds.Width > uiSegment.Bounds.Width * 1.45))
            {
                suppressedPrimaryIndexes.Add(i);
            }
        }

        var merged = primarySegments
            .Where((segment, index) => !suppressedPrimaryIndexes.Contains(index))
            .ToList();

        foreach (var uiSegment in uiAutomationSegments)
        {
            var relatedToPrimary = primarySegments.Any(primary => IsRelated(primary, uiSegment));
            if (!relatedToPrimary)
            {
                continue;
            }

            var duplicate = merged.Any(existing =>
                string.Equals(
                    TextRegionIntelligence.NormalizeWhitespace(existing.Text),
                    TextRegionIntelligence.NormalizeWhitespace(uiSegment.Text),
                    StringComparison.OrdinalIgnoreCase)
                && Coverage(existing.Bounds, uiSegment.Bounds) >= 0.7);
            if (duplicate)
            {
                continue;
            }

            if (hasParagraphPrimary && !primarySegments.Any(primary => primary.Kind != TextLayoutKind.Paragraph && IsRelated(primary, uiSegment)))
            {
                continue;
            }

            merged.Add(uiSegment);
        }

        return merged
            .OrderBy(segment => segment.Bounds.Top)
            .ThenBy(segment => segment.Bounds.Left)
            .ToArray();
    }

    private static bool IsRelated(LayoutTextSegment primary, LayoutTextSegment uiSegment)
    {
        if (!Intersects(primary.Bounds, uiSegment.Bounds))
        {
            return false;
        }

        var sameRow = Math.Abs(
            (primary.Bounds.Top + (primary.Bounds.Height / 2))
            - (uiSegment.Bounds.Top + (uiSegment.Bounds.Height / 2)))
            <= Math.Max(10, Math.Max(primary.Bounds.Height, uiSegment.Bounds.Height) * 0.8);

        return sameRow || ContainsCenter(primary.Bounds, uiSegment.Bounds);
    }

    private static bool ContainsCenter(ScreenRegion outer, ScreenRegion inner)
    {
        var centerX = inner.Left + (inner.Width / 2);
        var centerY = inner.Top + (inner.Height / 2);
        return centerX >= outer.Left
               && centerX <= outer.Right
               && centerY >= outer.Top
               && centerY <= outer.Bottom;
    }

    private static bool Intersects(ScreenRegion first, ScreenRegion second)
    {
        return !(first.Right <= second.Left
                 || second.Right <= first.Left
                 || first.Bottom <= second.Top
                 || second.Bottom <= first.Top);
    }

    private static ScreenRegion? Intersect(ScreenRegion first, ScreenRegion second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);

        if (right <= left || bottom <= top)
        {
            return null;
        }

        return new ScreenRegion(left, top, right - left, bottom - top);
    }

    private static double Coverage(ScreenRegion target, ScreenRegion candidate)
    {
        var intersection = Intersect(target, candidate);
        if (intersection is null)
        {
            return 0;
        }

        var targetArea = Math.Max(1, target.Width * target.Height);
        return (intersection.Value.Width * intersection.Value.Height) / targetArea;
    }
}
