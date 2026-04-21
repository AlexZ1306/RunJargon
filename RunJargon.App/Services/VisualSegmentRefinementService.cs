using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using OpenCvSharp;
using RunJargon.App.Models;
using RunJargon.App.Utilities;

namespace RunJargon.App.Services;

public sealed class VisualSegmentRefinementService
{
    public IReadOnlyList<LayoutTextSegment> Refine(
        IReadOnlyList<LayoutTextSegment> segments,
        Bitmap snapshotBitmap)
    {
        if (segments.Count == 0)
        {
            return segments;
        }

        using var snapshot = ToMat(snapshotBitmap);
        if (snapshot.Empty())
        {
            return segments;
        }

        var refined = new List<LayoutTextSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var splitSegments = TrySplitSegment(segment, snapshot);
            if (splitSegments.Count == 0)
            {
                refined.Add(segment);
                continue;
            }

            refined.AddRange(splitSegments);
        }

        return refined
            .OrderBy(segment => segment.Bounds.Top)
            .ThenBy(segment => segment.Bounds.Left)
            .ToArray();
    }

    private static IReadOnlyList<LayoutTextSegment> TrySplitSegment(
        LayoutTextSegment segment,
        Mat snapshot)
    {
        if (!ShouldAttemptSplit(segment))
        {
            return Array.Empty<LayoutTextSegment>();
        }

        var cropBounds = BuildCropBounds(segment.Bounds, snapshot.Width, snapshot.Height);
        if (cropBounds.Width <= 1 || cropBounds.Height <= 1)
        {
            return Array.Empty<LayoutTextSegment>();
        }

        using var crop = new Mat(snapshot, cropBounds);
        using var mask = BuildTextMask(crop);
        var wordBoxes = DetectWordLikeBoxes(mask);
        if (wordBoxes.Count < 2)
        {
            return Array.Empty<LayoutTextSegment>();
        }

        var labelBoxes = BuildLabelBoxes(wordBoxes);
        if (!LooksLikeValidSplit(segment, labelBoxes))
        {
            return Array.Empty<LayoutTextSegment>();
        }

        return BuildSplitSegments(segment, cropBounds, labelBoxes);
    }

    private static bool ShouldAttemptSplit(LayoutTextSegment segment)
    {
        if (segment.Kind == TextLayoutKind.Paragraph || segment.Bounds.IsEmpty || segment.SourceLines.Count != 1)
        {
            return false;
        }

        var normalized = TextRegionIntelligence.NormalizeWhitespace(segment.Text);
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.Any(char.IsLetter))
        {
            return false;
        }

        var wordCount = CountWords(normalized);
        if (wordCount < 3 || wordCount > 8 || normalized.Length > 72)
        {
            return false;
        }

        if (segment.Bounds.Width < 80 || segment.Bounds.Height < 10)
        {
            return false;
        }

        var aspectRatio = segment.Bounds.Width / Math.Max(1, segment.Bounds.Height);
        return aspectRatio >= 5.6;
    }

    private static Rect BuildCropBounds(ScreenRegion bounds, int imageWidth, int imageHeight)
    {
        var paddingX = Math.Clamp((int)Math.Round(bounds.Height * 0.55), 4, 16);
        var paddingY = Math.Clamp((int)Math.Round(bounds.Height * 0.4), 3, 12);

        var left = Math.Clamp((int)Math.Floor(bounds.Left) - paddingX, 0, Math.Max(0, imageWidth - 1));
        var top = Math.Clamp((int)Math.Floor(bounds.Top) - paddingY, 0, Math.Max(0, imageHeight - 1));
        var right = Math.Clamp((int)Math.Ceiling(bounds.Right) + paddingX, 1, imageWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom) + paddingY, 1, imageHeight);

        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static Mat BuildTextMask(Mat crop)
    {
        using var grayscale = new Mat();
        Cv2.CvtColor(crop, grayscale, ColorConversionCodes.BGR2GRAY);

        using var normalized = new Mat();
        using (var clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8)))
        {
            clahe.Apply(grayscale, normalized);
        }

        var mask = new Mat();
        Cv2.AdaptiveThreshold(
            normalized,
            mask,
            255,
            AdaptiveThresholdTypes.GaussianC,
            ThresholdTypes.BinaryInv,
            31,
            11);

        var kernelWidth = Math.Clamp((int)Math.Round(crop.Height * 0.14), 2, 7);
        var kernelHeight = Math.Clamp((int)Math.Round(crop.Height * 0.08), 1, 3);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(kernelWidth, kernelHeight));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

        return mask;
    }

    private static IReadOnlyList<Rect> DetectWordLikeBoxes(Mat mask)
    {
        Cv2.FindContours(
            mask,
            out var contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        var minWidth = Math.Max(4, mask.Height / 6);
        var minHeight = Math.Max(5, mask.Height / 4);
        var candidates = contours
            .Select(Cv2.BoundingRect)
            .Where(rect =>
                rect.Width >= minWidth
                && rect.Height >= minHeight
                && rect.Width <= mask.Width * 0.92
                && rect.Height <= mask.Height * 0.94)
            .OrderBy(rect => rect.X)
            .ToList();
        if (candidates.Count < 2)
        {
            return Array.Empty<Rect>();
        }

        var merged = MergeBoxes(candidates, Math.Max(3, mask.Height / 5));
        if (merged.Count < 2)
        {
            return Array.Empty<Rect>();
        }

        var medianCenterY = Median(merged.Select(rect => rect.Y + (rect.Height / 2.0)));
        var medianHeight = Median(merged.Select(rect => (double)rect.Height));

        return merged
            .Where(rect =>
                Math.Abs((rect.Y + (rect.Height / 2.0)) - medianCenterY) <= Math.Max(6, medianHeight * 0.8))
            .OrderBy(rect => rect.X)
            .ToArray();
    }

    private static IReadOnlyList<Rect> MergeBoxes(IReadOnlyList<Rect> boxes, int gapThreshold)
    {
        if (boxes.Count == 0)
        {
            return Array.Empty<Rect>();
        }

        var merged = new List<Rect> { boxes[0] };
        foreach (var candidate in boxes.Skip(1))
        {
            var current = merged[^1];
            var gap = candidate.X - current.Right;
            var sameRow = Math.Abs((candidate.Y + (candidate.Height / 2.0)) - (current.Y + (current.Height / 2.0)))
                          <= Math.Max(4, Math.Min(current.Height, candidate.Height) * 0.55);

            if (sameRow && gap <= gapThreshold)
            {
                merged[^1] = Combine(current, candidate);
                continue;
            }

            merged.Add(candidate);
        }

        return merged;
    }

    private static IReadOnlyList<Rect> BuildLabelBoxes(IReadOnlyList<Rect> wordBoxes)
    {
        if (wordBoxes.Count < 2)
        {
            return wordBoxes;
        }

        var medianHeight = Median(wordBoxes.Select(rect => (double)rect.Height));
        var splitGapThreshold = Math.Max(8, (int)Math.Round(medianHeight * 0.6));

        var groups = new List<List<Rect>>
        {
            new() { wordBoxes[0] }
        };

        for (var i = 1; i < wordBoxes.Count; i++)
        {
            var previous = wordBoxes[i - 1];
            var current = wordBoxes[i];
            var gap = current.X - previous.Right;

            if (gap > splitGapThreshold)
            {
                groups.Add(new List<Rect> { current });
                continue;
            }

            groups[^1].Add(current);
        }

        return groups
            .Select(group => group.Aggregate(Combine))
            .OrderBy(rect => rect.X)
            .ToArray();
    }

    private static bool LooksLikeValidSplit(LayoutTextSegment segment, IReadOnlyList<Rect> labelBoxes)
    {
        if (labelBoxes.Count < 2 || labelBoxes.Count > 8)
        {
            return false;
        }

        var wordCount = CountWords(segment.Text);
        if (labelBoxes.Count > wordCount + 1)
        {
            return false;
        }

        var combined = labelBoxes.Aggregate(Combine);
        var coverage = (combined.Width * combined.Height) / Math.Max(1, segment.Bounds.Width * segment.Bounds.Height);
        if (coverage < 0.08)
        {
            return false;
        }

        var medianHeight = Median(labelBoxes.Select(rect => (double)rect.Height));
        var maxHeight = labelBoxes.Max(rect => rect.Height);
        if (maxHeight > medianHeight * 1.8)
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyList<LayoutTextSegment> BuildSplitSegments(
        LayoutTextSegment segment,
        Rect cropBounds,
        IReadOnlyList<Rect> labelBoxes)
    {
        var orderedWords = segment.SourceLines
            .SelectMany(line => line.Words ?? Array.Empty<OcrWordRegion>())
            .Where(word => !string.IsNullOrWhiteSpace(word.Text) && !word.Bounds.IsEmpty)
            .OrderBy(word => word.Bounds.Left)
            .ToArray();
        var fallbackTokens = TextRegionIntelligence.NormalizeWhitespace(segment.Text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = new List<LayoutTextSegment>(labelBoxes.Count);
        for (var i = 0; i < labelBoxes.Count; i++)
        {
            var localBox = labelBoxes[i];
            var bounds = new ScreenRegion(
                cropBounds.X + localBox.X,
                cropBounds.Y + localBox.Y,
                localBox.Width,
                localBox.Height);

            var assignedWords = orderedWords
                .Where(word => Coverage(bounds, word.Bounds) >= 0.25 || ContainsCenter(bounds, word.Bounds))
                .ToArray();

            if (assignedWords.Length == 0 && orderedWords.Length == labelBoxes.Count)
            {
                assignedWords = [orderedWords[i]];
            }

            var text = assignedWords.Length > 0
                ? string.Join(" ", assignedWords.Select(word => word.Text))
                : TakeFallbackText(fallbackTokens, i, labelBoxes.Count);
            text = TextRegionIntelligence.NormalizeWhitespace(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var line = new OcrLineRegion(
                text,
                bounds,
                assignedWords.Length > 0 ? assignedWords : null);

            result.Add(new LayoutTextSegment(
                text,
                bounds,
                [line],
                InferKind(segment.Kind, text)));
        }

        return result.Count >= 2 ? result : Array.Empty<LayoutTextSegment>();
    }

    private static string TakeFallbackText(
        IReadOnlyList<string> tokens,
        int index,
        int totalBoxes)
    {
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        if (tokens.Count == totalBoxes)
        {
            return tokens[index];
        }

        var start = (int)Math.Round(index * tokens.Count / (double)totalBoxes);
        var endExclusive = (int)Math.Round((index + 1) * tokens.Count / (double)totalBoxes);
        start = Math.Clamp(start, 0, tokens.Count - 1);
        endExclusive = Math.Clamp(endExclusive, start + 1, tokens.Count);

        return string.Join(" ", tokens.Skip(start).Take(endExclusive - start));
    }

    private static TextLayoutKind InferKind(TextLayoutKind originalKind, string text)
    {
        if (originalKind == TextLayoutKind.UiLabel)
        {
            return TextLayoutKind.UiLabel;
        }

        var normalized = TextRegionIntelligence.NormalizeWhitespace(text);
        return CountWords(normalized) <= 3 && normalized.Length <= 24
            ? TextLayoutKind.UiLabel
            : originalKind;
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

    private static double Coverage(ScreenRegion target, ScreenRegion candidate)
    {
        var left = Math.Max(target.Left, candidate.Left);
        var top = Math.Max(target.Top, candidate.Top);
        var right = Math.Min(target.Right, candidate.Right);
        var bottom = Math.Min(target.Bottom, candidate.Bottom);

        if (right <= left || bottom <= top)
        {
            return 0;
        }

        var candidateArea = Math.Max(1, candidate.Width * candidate.Height);
        return ((right - left) * (bottom - top)) / candidateArea;
    }

    private static int CountWords(string text)
    {
        return TextRegionIntelligence.NormalizeWhitespace(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
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
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private static Rect Combine(Rect first, Rect second)
    {
        var left = Math.Min(first.Left, second.Left);
        var top = Math.Min(first.Top, second.Top);
        var right = Math.Max(first.Right, second.Right);
        var bottom = Math.Max(first.Bottom, second.Bottom);

        return new Rect(left, top, right - left, bottom - top);
    }

    private static Mat ToMat(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Cv2.ImDecode(stream.ToArray(), ImreadModes.Color);
    }
}
