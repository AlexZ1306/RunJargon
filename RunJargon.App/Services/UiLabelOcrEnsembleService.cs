using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using OpenCvSharp;
using RunJargon.App.Models;
using RunJargon.App.Utilities;

namespace RunJargon.App.Services;

public sealed class UiLabelOcrEnsembleService
{
    private readonly IOcrService _ocrService;

    public UiLabelOcrEnsembleService(IOcrService ocrService)
    {
        _ocrService = ocrService;
    }

    public async Task<string> RecognizeBestAsync(
        LayoutTextSegment segment,
        Bitmap snapshotBitmap,
        string? preferredLanguageTag,
        CancellationToken cancellationToken,
        CapturePerformanceTrace? performanceTrace = null,
        bool allowDeepRecovery = false)
    {
        var originalText = TextRegionIntelligence.NormalizeWhitespace(segment.Text);
        if (!ShouldAttemptEnsemble(segment, originalText))
        {
            return originalText;
        }

        var effectiveLanguageTag = ResolveRecognitionLanguageTag(originalText, preferredLanguageTag);

        using var baseCrop = CreateBaseCrop(snapshotBitmap, segment.Bounds);
        if (baseCrop.Width <= 1 || baseCrop.Height <= 1)
        {
            return originalText;
        }

        var candidateTexts = new List<string> { originalText };
        foreach (var variant in CreateFastVariants(baseCrop))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (variant)
            {
                OcrResponse response;
                try
                {
                    response = await _ocrService.RecognizeAsync(
                        variant,
                        effectiveLanguageTag,
                        cancellationToken,
                        new OcrRequestOptions(OcrExecutionProfile.UiLabelEnsemble, performanceTrace));
                }
                catch
                {
                    continue;
                }

                var candidateText = NormalizeOcrText(response.Text);
                if (!string.IsNullOrWhiteSpace(candidateText))
                {
                    candidateTexts.Add(candidateText);
                    if (TryGetStableConsensus(candidateTexts.Skip(1), out var stableCandidate))
                    {
                        var stableSelection = SelectBestCandidate(originalText, [originalText, stableCandidate]);
                        if (!allowDeepRecovery)
                        {
                            return stableSelection;
                        }
                    }
                }
            }
        }

        var fastSelection = SelectBestCandidate(originalText, candidateTexts);
        if (!allowDeepRecovery)
        {
            return fastSelection;
        }

        foreach (var variant in CreateRecoveryVariants(baseCrop))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (variant)
            {
                OcrResponse response;
                try
                {
                    response = await _ocrService.RecognizeAsync(
                        variant,
                        effectiveLanguageTag,
                        cancellationToken,
                        new OcrRequestOptions(OcrExecutionProfile.UiLabelRecovery, performanceTrace));
                }
                catch
                {
                    continue;
                }

                var candidateText = NormalizeOcrText(response.Text);
                if (!string.IsNullOrWhiteSpace(candidateText))
                {
                    candidateTexts.Add(candidateText);
                }
            }
        }

        return SelectBestCandidate(originalText, candidateTexts);
    }

    public static string SelectBestCandidate(
        string originalText,
        IEnumerable<string> candidateTexts)
    {
        var original = TextRegionIntelligence.NormalizeWhitespace(originalText);
        var collected = candidateTexts
            .Select((text, index) => BuildCandidate(text, index))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToList();

        if (collected.Count == 0)
        {
            return original;
        }

        if (!string.IsNullOrWhiteSpace(original)
            && collected.All(candidate => !string.Equals(candidate.Text, original, StringComparison.OrdinalIgnoreCase)))
        {
            collected.Insert(0, BuildCandidate(original, -1)!);
        }

        var bestGroup = collected
            .GroupBy(candidate => candidate.CompactKey)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => new CandidateGroup(
                group.Key,
                group.Count(),
                group.Max(candidate => candidate.Score),
                group.Average(candidate => candidate.Score),
                group.Any(candidate => string.Equals(candidate.Text, original, StringComparison.OrdinalIgnoreCase)),
                group.OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.SpacePenalty)
                    .ThenBy(candidate => candidate.SourceIndex)
                    .First()))
            .OrderByDescending(group => (group.SupportCount * 36)
                                        + group.BestScore
                                        + (group.AverageScore / 3.0)
                                        + (group.ContainsOriginal ? 4 : 0))
            .FirstOrDefault();

        return bestGroup?.BestCandidate.Text ?? original;
    }

    private static bool ShouldAttemptEnsemble(LayoutTextSegment segment, string normalizedText)
    {
        if (segment.Kind != TextLayoutKind.UiLabel || segment.Bounds.IsEmpty)
        {
            return false;
        }

        var wordCount = CountWords(normalizedText);
        if (wordCount == 0 || wordCount > 4 || normalizedText.Length > 36)
        {
            return false;
        }

        if (!normalizedText.Any(char.IsLetter))
        {
            return false;
        }

        return segment.Bounds.Height <= 36 && segment.Bounds.Width >= 18;
    }

    private static Bitmap CreateBaseCrop(Bitmap snapshotBitmap, ScreenRegion bounds)
    {
        var paddingX = Math.Clamp((int)Math.Round(bounds.Height * 0.9), 6, 18);
        var paddingY = Math.Clamp((int)Math.Round(bounds.Height * 0.75), 5, 16);

        var left = Math.Clamp((int)Math.Floor(bounds.Left) - paddingX, 0, Math.Max(0, snapshotBitmap.Width - 1));
        var top = Math.Clamp((int)Math.Floor(bounds.Top) - paddingY, 0, Math.Max(0, snapshotBitmap.Height - 1));
        var right = Math.Clamp((int)Math.Ceiling(bounds.Right) + paddingX, 1, snapshotBitmap.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom) + paddingY, 1, snapshotBitmap.Height);
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);

        var border = Math.Clamp((int)Math.Round(bounds.Height * 0.6), 8, 18);
        var crop = new Bitmap(width + (border * 2), height + (border * 2), PixelFormat.Format24bppRgb);
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

    private static IReadOnlyList<Bitmap> CreateFastVariants(Bitmap baseCrop)
    {
        const double scale = 6.0;

        return
        [
            CreateVariantBitmap(baseCrop, scale, UiCropMode.RawColor),
            CreateVariantBitmap(baseCrop, scale, UiCropMode.InvertedGrayscale),
            CreateVariantBitmap(baseCrop, scale, UiCropMode.BinaryInverted),
            CreateVariantBitmap(baseCrop, scale, UiCropMode.Grayscale)
        ];
    }

    private static IReadOnlyList<Bitmap> CreateRecoveryVariants(Bitmap baseCrop)
    {
        const double recoveryScale = 8.0;

        return
        [
            CreateVariantBitmap(baseCrop, recoveryScale, UiCropMode.RawColor),
            CreateVariantBitmap(baseCrop, recoveryScale, UiCropMode.Binary),
            CreateVariantBitmap(baseCrop, recoveryScale, UiCropMode.InvertedGrayscale),
            CreateVariantBitmap(baseCrop, recoveryScale, UiCropMode.BinaryInverted)
        ];
    }

    private static Bitmap CreateVariantBitmap(Bitmap baseCrop, double scale, UiCropMode mode)
    {
        using var source = ToMat(baseCrop);
        using var resized = new Mat();
        Cv2.Resize(
            source,
            resized,
            new OpenCvSharp.Size(
                Math.Max(1, (int)Math.Round(source.Width * scale)),
                Math.Max(1, (int)Math.Round(source.Height * scale))),
            0,
            0,
            InterpolationFlags.Cubic);

        Mat prepared;
        switch (mode)
        {
            case UiCropMode.RawColor:
                prepared = resized.Clone();
                break;
            case UiCropMode.Grayscale:
                prepared = CreateGrayscaleVariant(resized, invert: false);
                break;
            case UiCropMode.InvertedGrayscale:
                prepared = CreateGrayscaleVariant(resized, invert: true);
                break;
            case UiCropMode.Binary:
                prepared = CreateBinaryVariant(resized, invert: false);
                break;
            case UiCropMode.BinaryInverted:
                prepared = CreateBinaryVariant(resized, invert: true);
                break;
            default:
                prepared = resized.Clone();
                break;
        }

        using (prepared)
        {
            return ToBitmap(prepared);
        }
    }

    private static Mat CreateGrayscaleVariant(Mat source, bool invert)
    {
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);

        using var normalized = new Mat();
        using (var clahe = Cv2.CreateCLAHE(2.4, new OpenCvSharp.Size(8, 8)))
        {
            clahe.Apply(gray, normalized);
        }

        if (invert)
        {
            Cv2.BitwiseNot(normalized, normalized);
        }

        var bgr = new Mat();
        Cv2.CvtColor(normalized, bgr, ColorConversionCodes.GRAY2BGR);
        return bgr;
    }

    private static Mat CreateBinaryVariant(Mat source, bool invert)
    {
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);

        using var normalized = new Mat();
        using (var clahe = Cv2.CreateCLAHE(2.6, new OpenCvSharp.Size(8, 8)))
        {
            clahe.Apply(gray, normalized);
        }

        var binary = new Mat();
        Cv2.AdaptiveThreshold(
            normalized,
            binary,
            255,
            AdaptiveThresholdTypes.GaussianC,
            invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary,
            31,
            9);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2, 2));
        Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel);

        var bgr = new Mat();
        Cv2.CvtColor(binary, bgr, ColorConversionCodes.GRAY2BGR);
        return bgr;
    }

    private static string NormalizeOcrText(string text)
    {
        return TextRegionIntelligence.NormalizeWhitespace(text)
            .Replace(" ,", ",", StringComparison.Ordinal)
            .Replace(" .", ".", StringComparison.Ordinal);
    }

    private static Candidate? BuildCandidate(string text, int sourceIndex)
    {
        var normalized = NormalizeOcrText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var compactKey = BuildCompactKey(normalized);
        if (string.IsNullOrWhiteSpace(compactKey))
        {
            return null;
        }

        return new Candidate(
            normalized,
            compactKey,
            ComputeCandidateScore(normalized),
            CountWhitespace(normalized),
            sourceIndex);
    }

    private static string BuildCompactKey(string text)
    {
        return new string(text
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static int ComputeCandidateScore(string text)
    {
        var normalized = NormalizeOcrText(text);
        var words = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var punctuationCount = normalized.Count(ch => char.IsPunctuation(ch) && ch is not '-' and not '_');
        var singleLetterTokens = words.Count(word => word.Length == 1);
        var alphaNumericCount = normalized.Count(char.IsLetterOrDigit);
        var letterOnlyWords = words.Count(word => word.All(char.IsLetter));
        var mixedLetterDigitWords = words.Count(word => word.Any(char.IsLetter) && word.Any(char.IsDigit));

        var score = OcrQualityHeuristics.ScoreLineText(normalized);
        score += words.Length switch
        {
            1 => 18,
            2 => 10,
            3 => 2,
            _ => -14
        };

        score += letterOnlyWords * 3;
        score -= singleLetterTokens * 24;
        score -= punctuationCount * 12;
        score -= mixedLetterDigitWords * 18;

        if (alphaNumericCount >= 4 && punctuationCount == 0)
        {
            score += 8;
        }

        if (words.Length == 1 && normalized.Length >= 4)
        {
            score += 6;
        }

        if (words.Length == 2 && words.All(word => word.Length >= 3))
        {
            score += 4;
        }

        return score;
    }

    private static int CountWhitespace(string text)
    {
        return text.Count(char.IsWhiteSpace);
    }

    private static int CountWords(string text)
    {
        return TextRegionIntelligence.NormalizeWhitespace(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }

    private static string? ResolveRecognitionLanguageTag(string originalText, string? preferredLanguageTag)
    {
        if (!string.IsNullOrWhiteSpace(preferredLanguageTag))
        {
            return preferredLanguageTag;
        }

        return TextRegionIntelligence.GuessSourceLanguage(originalText) switch
        {
            "en" => "en-US",
            "ru" => "ru-RU",
            "de" => "de-DE",
            "ja" => "ja-JP",
            "zh-Hans" => "zh-CN",
            _ => null
        };
    }

    private static bool TryGetStableConsensus(
        IEnumerable<string> ocrCandidates,
        out string stableCandidate)
    {
        stableCandidate = string.Empty;

        var grouped = ocrCandidates
            .Select((text, index) => BuildCandidate(text, index))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .GroupBy(candidate => candidate.CompactKey)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() >= 2)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.SpacePenalty)
                .ThenBy(candidate => candidate.SourceIndex)
                .First())
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();

        if (grouped is null || grouped.Score < 18)
        {
            return false;
        }

        stableCandidate = grouped.Text;
        return true;
    }

    private static Mat ToMat(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Cv2.ImDecode(stream.ToArray(), ImreadModes.Color);
    }

    private static Bitmap ToBitmap(Mat mat)
    {
        using var stream = new MemoryStream(mat.ImEncode(".png"));
        using var bitmap = new Bitmap(stream);
        return new Bitmap(bitmap);
    }

    private enum UiCropMode
    {
        RawColor,
        Grayscale,
        InvertedGrayscale,
        Binary,
        BinaryInverted
    }

    private sealed record Candidate(
        string Text,
        string CompactKey,
        int Score,
        int SpacePenalty,
        int SourceIndex);

    private sealed record CandidateGroup(
        string CompactKey,
        int SupportCount,
        int BestScore,
        double AverageScore,
        bool ContainsOriginal,
        Candidate BestCandidate);
}
