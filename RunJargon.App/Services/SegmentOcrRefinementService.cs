using System.Drawing;
using System.Drawing.Drawing2D;
using RunJargon.App.Models;
using RunJargon.App.Utilities;

namespace RunJargon.App.Services;

public sealed class SegmentOcrRefinementService
{
    private readonly IOcrService _ocrService;
    private readonly UiLabelOcrEnsembleService _uiLabelOcrEnsembleService;

    public SegmentOcrRefinementService(IOcrService ocrService)
    {
        _ocrService = ocrService;
        _uiLabelOcrEnsembleService = new UiLabelOcrEnsembleService(ocrService);
    }

    public async Task<IReadOnlyList<LayoutTextSegment>> RefineAsync(
        IReadOnlyList<LayoutTextSegment> segments,
        Bitmap snapshotBitmap,
        string? preferredLanguageTag,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            return segments;
        }

        var refinedSegments = new List<LayoutTextSegment>(segments.Count);
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            refinedSegments.Add(await RefineSegmentAsync(
                segment,
                snapshotBitmap,
                preferredLanguageTag,
                cancellationToken));
        }

        return refinedSegments;
    }

    public async Task<LayoutTextSegment> RecoverLowConfidenceUiLabelAsync(
        LayoutTextSegment segment,
        Bitmap snapshotBitmap,
        string? preferredLanguageTag,
        CancellationToken cancellationToken)
    {
        if (segment.Kind != TextLayoutKind.UiLabel)
        {
            return segment;
        }

        var recoveredCandidate = await _uiLabelOcrEnsembleService.RecognizeBestAsync(
            segment,
            snapshotBitmap,
            preferredLanguageTag,
            cancellationToken,
            allowDeepRecovery: true);
        if (!ShouldAdoptRefinement(segment.Text, recoveredCandidate, segment.Kind))
        {
            return segment;
        }

        return ApplyRefinedText(segment, recoveredCandidate);
    }

    private async Task<LayoutTextSegment> RefineSegmentAsync(
        LayoutTextSegment segment,
        Bitmap snapshotBitmap,
        string? preferredLanguageTag,
        CancellationToken cancellationToken)
    {
        if (!ShouldAttemptRefinement(segment))
        {
            return segment;
        }

        if (segment.Kind == TextLayoutKind.UiLabel)
        {
            var bestUiLabelCandidate = await _uiLabelOcrEnsembleService.RecognizeBestAsync(
                segment,
                snapshotBitmap,
                preferredLanguageTag,
                cancellationToken);
            if (ShouldAdoptRefinement(segment.Text, bestUiLabelCandidate, segment.Kind))
            {
                return ApplyRefinedText(segment, bestUiLabelCandidate);
            }
        }

        using var crop = CreateSegmentCrop(snapshotBitmap, segment.Bounds, segment.Kind);
        var response = await _ocrService.RecognizeAsync(
            crop,
            preferredLanguageTag,
            cancellationToken,
            new OcrRequestOptions(OcrExecutionProfile.SegmentRefinement));
        var candidateText = TextRegionIntelligence.NormalizeWhitespace(response.Text);
        if (!ShouldAdoptRefinement(segment.Text, candidateText, segment.Kind))
        {
            return segment;
        }

        return ApplyRefinedText(segment, candidateText);
    }

    private static bool ShouldAttemptRefinement(LayoutTextSegment segment)
    {
        var normalized = TextRegionIntelligence.NormalizeWhitespace(segment.Text);
        if (string.IsNullOrWhiteSpace(normalized) || segment.Bounds.IsEmpty)
        {
            return false;
        }

        if (segment.Kind == TextLayoutKind.Paragraph)
        {
            return false;
        }

        var wordCount = CountWords(normalized);
        if (segment.Kind == TextLayoutKind.UiLabel)
        {
            return wordCount <= 4 && normalized.Length <= 36;
        }

        return wordCount <= 5 && normalized.Length <= 42;
    }

    private static Bitmap CreateSegmentCrop(
        Bitmap snapshotBitmap,
        ScreenRegion bounds,
        TextLayoutKind kind)
    {
        var basePadX = kind == TextLayoutKind.UiLabel ? 8 : 10;
        var basePadY = kind == TextLayoutKind.UiLabel ? 6 : 8;
        var dynamicPadX = Math.Clamp((int)Math.Round(bounds.Height * 0.45), 2, 14);
        var dynamicPadY = Math.Clamp((int)Math.Round(bounds.Height * 0.28), 2, 10);

        var paddingX = Math.Max(basePadX, dynamicPadX);
        var paddingY = Math.Max(basePadY, dynamicPadY);

        var left = Math.Clamp((int)Math.Floor(bounds.Left) - paddingX, 0, Math.Max(0, snapshotBitmap.Width - 1));
        var top = Math.Clamp((int)Math.Floor(bounds.Top) - paddingY, 0, Math.Max(0, snapshotBitmap.Height - 1));
        var right = Math.Clamp((int)Math.Ceiling(bounds.Right) + paddingX, 0, snapshotBitmap.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom) + paddingY, 0, snapshotBitmap.Height);
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);

        var border = kind == TextLayoutKind.UiLabel ? 10 : 8;
        var crop = new Bitmap(width + (border * 2), height + (border * 2), System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(crop);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.White);
        graphics.DrawImage(
            snapshotBitmap,
            new Rectangle(border, border, width, height),
            new Rectangle(left, top, width, height),
            GraphicsUnit.Pixel);

        return crop;
    }

    private static bool ShouldAdoptRefinement(
        string originalText,
        string candidateText,
        TextLayoutKind kind)
    {
        var original = TextRegionIntelligence.NormalizeWhitespace(originalText);
        var candidate = TextRegionIntelligence.NormalizeWhitespace(candidateText);

        if (string.IsNullOrWhiteSpace(candidate)
            || string.Equals(original, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (kind == TextLayoutKind.UiLabel)
        {
            var maxLength = Math.Max(original.Length + 10, (int)Math.Ceiling(original.Length * 1.75));
            if (candidate.Length > maxLength || CountWords(candidate) > Math.Max(4, CountWords(original) + 1))
            {
                return false;
            }
        }

        var originalLooksKnown = TextRegionIntelligence.TryParseCommonUiLabel(original, out _, out _, out _);
        var candidateLooksKnown = TextRegionIntelligence.TryParseCommonUiLabel(candidate, out _, out _, out _);
        if (candidateLooksKnown && !originalLooksKnown)
        {
            return true;
        }

        var originalScore = OcrQualityHeuristics.ScoreLineText(original);
        var candidateScore = OcrQualityHeuristics.ScoreLineText(candidate);
        var requiredGain = kind == TextLayoutKind.UiLabel ? 0 : 4;

        return candidateScore >= originalScore + requiredGain;
    }

    private static LayoutTextSegment ApplyRefinedText(LayoutTextSegment segment, string refinedText)
    {
        if (segment.SourceLines.Count == 1)
        {
            var sourceLine = segment.SourceLines[0];
            return segment with
            {
                Text = refinedText,
                SourceLines = [sourceLine with { Text = refinedText }]
            };
        }

        return segment with { Text = refinedText };
    }

    private static int CountWords(string text)
    {
        return text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }
}
