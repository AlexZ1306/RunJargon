using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using RunJargon.App.Models;
using RunJargon.App.Utilities;

namespace RunJargon.App.Services;

public sealed class WindowsOcrService : IOcrService
{
    private const string UserProfileEngineKey = "$user-profile";
    private readonly object _engineCacheGate = new();
    private readonly Dictionary<string, OcrEngine?> _engineCache = new(StringComparer.OrdinalIgnoreCase);

    public string DisplayName => "Windows OCR";

    public async Task<OcrResponse> RecognizeAsync(
        Bitmap bitmap,
        string? preferredLanguageTag,
        CancellationToken cancellationToken,
        OcrRequestOptions? options = null)
    {
        options ??= new OcrRequestOptions();
        var performanceTrace = options.PerformanceTrace;
        var availableLanguages = OcrEngine.AvailableRecognizerLanguages
            .Where(language => language is not null)
            .ToArray();
        if (availableLanguages.Length == 0)
        {
            throw new InvalidOperationException(
                "Windows OCR недоступен. Проверь, что в системе установлены OCR language packs.");
        }

        var preprocessStopwatch = Stopwatch.StartNew();
        var candidates = GetCandidateLanguages(preferredLanguageTag, availableLanguages, options.Profile).ToArray();
        var variants = CreateVariants(bitmap, options.Profile);
        preprocessStopwatch.Stop();
        RecordPhaseDuration(options.Profile, performanceTrace, preprocessStopwatch.Elapsed, isPreprocess: true);
        try
        {
            OcrResponse? bestResponse = null;
            var bestScore = int.MinValue;
            var createdAtLeastOneEngine = false;
            var recognizeDuration = TimeSpan.Zero;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var engine = CreateEngine(candidate);
                if (engine is null)
                {
                    continue;
                }

                createdAtLeastOneEngine = true;

                OcrBitmapVariant? bestVariant = null;
                OcrResponse? bestVariantResponse = null;
                var bestVariantScore = int.MinValue;
                OcrResponse? runnerUpResponse = null;
                var runnerUpScore = int.MinValue;

                foreach (var variant in variants)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    OcrResponse response;
                    var recognizeStopwatch = Stopwatch.StartNew();
                    try
                    {
                        response = await RecognizeWithBitmapAsync(engine, variant.Bitmap, variant.Scale);
                    }
                    catch
                    {
                        recognizeStopwatch.Stop();
                        continue;
                    }
                    finally
                    {
                        recognizeStopwatch.Stop();
                        recognizeDuration += recognizeStopwatch.Elapsed;
                        performanceTrace?.Counters.IncrementOcrRequests();
                    }

                    var score = OcrQualityHeuristics.ScoreResponse(response, candidate.DisplayTag);

                    if (score > bestVariantScore)
                    {
                        runnerUpResponse = bestVariantResponse;
                        runnerUpScore = bestVariantScore;
                        bestVariant = variant;
                        bestVariantResponse = response;
                        bestVariantScore = score;
                        continue;
                    }

                    if (score > runnerUpScore)
                    {
                        runnerUpResponse = response;
                        runnerUpScore = score;
                    }
                }

                if (bestVariantResponse is null || bestVariant is null)
                {
                    continue;
                }

                var shouldMergeRunnerUp = runnerUpResponse is not null && runnerUpScore >= bestVariantScore - 28;
                var mergedLines = shouldMergeRunnerUp
                    ? MergeLines(bestVariantResponse.Lines, runnerUpResponse!.Lines)
                    : bestVariantResponse.Lines;
                var mergedText = mergedLines.Count > 0
                    ? string.Join(Environment.NewLine, mergedLines.Select(line => line.Text))
                    : bestVariantResponse.Text;

                var candidateResponse = new OcrResponse(
                    mergedText,
                    mergedLines,
                    BuildEngineName(candidate.DisplayTag, bestVariant.Name, shouldMergeRunnerUp));
                var candidateScore = OcrQualityHeuristics.ScoreResponse(candidateResponse, candidate.DisplayTag);

                if (bestResponse is not null && candidateScore <= bestScore)
                {
                    continue;
                }

                bestScore = candidateScore;
                bestResponse = candidateResponse;
            }

            if (bestResponse is not null)
            {
                RecordPhaseDuration(options.Profile, performanceTrace, recognizeDuration, isPreprocess: false);
                return bestResponse;
            }

            if (!createdAtLeastOneEngine)
            {
                throw new InvalidOperationException(BuildEngineCreationErrorMessage(preferredLanguageTag, availableLanguages));
            }

            RecordPhaseDuration(options.Profile, performanceTrace, recognizeDuration, isPreprocess: false);
            return new OcrResponse(
                string.Empty,
                Array.Empty<OcrLineRegion>(),
                BuildFallbackEngineName(preferredLanguageTag, availableLanguages));
        }
        finally
        {
            foreach (var variant in variants.Where(variant => variant.OwnsBitmap))
            {
                variant.Bitmap.Dispose();
            }
        }
    }

    private static async Task<OcrResponse> RecognizeWithBitmapAsync(OcrEngine engine, Bitmap bitmap, double scale)
    {
        var softwareBitmap = await ToSoftwareBitmapAsync(bitmap);
        try
        {
            var result = await engine.RecognizeAsync(softwareBitmap);

            var allLines = result.Lines
                .Select(line => ToOcrLineRegion(line, scale))
                .Where(line => !line.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(line.Text))
                .ToArray();

            var regionWidth = bitmap.Width / scale;
            var regionHeight = bitmap.Height / scale;
            var lines = allLines
                .Where(line => !TextRegionIntelligence.ShouldSkipLine(line, regionWidth, regionHeight))
                .ToArray();
            var mergedText = lines.Length > 0
                ? string.Join(Environment.NewLine, lines.Select(line => TextRegionIntelligence.NormalizeWhitespace(line.Text)))
                : string.Empty;

            return new OcrResponse(mergedText, lines, "Windows OCR");
        }
        finally
        {
            softwareBitmap.Dispose();
        }
    }

    private static IEnumerable<OcrLanguageCandidate> GetCandidateLanguages(
        string? preferredLanguageTag,
        IReadOnlyList<Language> availableLanguages,
        OcrExecutionProfile profile)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var limit = profile switch
        {
            OcrExecutionProfile.UiLabelEnsemble => 2,
            OcrExecutionProfile.UiLabelRecovery => 2,
            OcrExecutionProfile.SegmentRefinement => 3,
            _ => int.MaxValue
        };

        if (!string.IsNullOrWhiteSpace(preferredLanguageTag))
        {
            var preferredLanguage = TryCreateLanguage(preferredLanguageTag);
            if (preferredLanguage is not null && yielded.Add(preferredLanguage.LanguageTag))
            {
                yield return new OcrLanguageCandidate(preferredLanguage.LanguageTag, preferredLanguage);
                if (--limit == 0)
                {
                    yield break;
                }
            }
        }

        yield return OcrLanguageCandidate.UserProfile;
        if (--limit == 0)
        {
            yield break;
        }

        var preferredFamily = GetLanguageFamily(preferredLanguageTag);
        foreach (var availableLanguage in availableLanguages
                     .Where(language => string.Equals(
                         GetLanguageFamily(language.LanguageTag),
                         preferredFamily,
                         StringComparison.OrdinalIgnoreCase)))
        {
            if (!yielded.Add(availableLanguage.LanguageTag))
            {
                continue;
            }

            yield return new OcrLanguageCandidate(availableLanguage.LanguageTag, availableLanguage);
            if (--limit == 0)
            {
                yield break;
            }
        }

        foreach (var preferredTag in new[]
        {
            "en-US",
            "ru-RU",
            "de-DE",
            "ja-JP",
            "zh-CN"
        })
        {
            var matchingAvailable = FindBestAvailableLanguage(preferredTag, availableLanguages);
            if (matchingAvailable is null || !yielded.Add(matchingAvailable.LanguageTag))
            {
                continue;
            }

            yield return new OcrLanguageCandidate(matchingAvailable.LanguageTag, matchingAvailable);
            if (--limit == 0)
            {
                yield break;
            }
        }

        foreach (var availableLanguage in availableLanguages)
        {
            if (!yielded.Add(availableLanguage.LanguageTag))
            {
                continue;
            }

            yield return new OcrLanguageCandidate(availableLanguage.LanguageTag, availableLanguage);
            if (--limit == 0)
            {
                yield break;
            }
        }
    }

    private OcrEngine? CreateEngine(OcrLanguageCandidate candidate)
    {
        var cacheKey = candidate.UseUserProfile
            ? UserProfileEngineKey
            : candidate.Language?.LanguageTag ?? string.Empty;

        lock (_engineCacheGate)
        {
            if (_engineCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var engine = candidate.UseUserProfile
                ? OcrEngine.TryCreateFromUserProfileLanguages()
                : candidate.Language is null
                    ? null
                    : OcrEngine.TryCreateFromLanguage(candidate.Language);

            _engineCache[cacheKey] = engine;
            return engine;
        }
    }

    private static IReadOnlyList<OcrBitmapVariant> CreateVariants(Bitmap source, OcrExecutionProfile profile)
    {
        if (profile == OcrExecutionProfile.UiLabelEnsemble)
        {
            return
            [
                new OcrBitmapVariant("raw", source, 1, ownsBitmap: false)
            ];
        }

        if (profile == OcrExecutionProfile.UiLabelRecovery)
        {
            return
            [
                new OcrBitmapVariant("raw", source, 1, ownsBitmap: false)
            ];
        }

        var looksSmall = source.Width < 900 || source.Height < 260;
        var upscaleScale = looksSmall ? 4.0 : source.Width < 1400 ? 2.5 : 1.5;
        var binaryScale = looksSmall ? 3.0 : source.Width < 1400 ? 2.0 : 1.4;

        if (profile == OcrExecutionProfile.SegmentRefinement)
        {
            return
            [
                new OcrBitmapVariant("raw", source, 1, ownsBitmap: false),
                new OcrBitmapVariant("grayscale-upscaled", CreateVariantBitmap(source, upscaleScale, OcrPreprocessMode.Grayscale), upscaleScale, ownsBitmap: true)
            ];
        }

        return
        [
            new OcrBitmapVariant("raw", source, 1, ownsBitmap: false),
            new OcrBitmapVariant("raw-upscaled", CreateVariantBitmap(source, upscaleScale, OcrPreprocessMode.Raw), upscaleScale, ownsBitmap: true),
            new OcrBitmapVariant("grayscale-upscaled", CreateVariantBitmap(source, upscaleScale, OcrPreprocessMode.Grayscale), upscaleScale, ownsBitmap: true),
            new OcrBitmapVariant("binary-upscaled", CreateVariantBitmap(source, binaryScale, OcrPreprocessMode.Binary), binaryScale, ownsBitmap: true)
        ];
    }

    private static Bitmap CreateVariantBitmap(Bitmap source, double scale, OcrPreprocessMode mode)
    {
        var targetWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

        var prepared = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(prepared);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(System.Drawing.Color.White);
        graphics.DrawImage(source, new Rectangle(0, 0, targetWidth, targetHeight));

        if (mode == OcrPreprocessMode.Raw)
        {
            return prepared;
        }

        var rect = new Rectangle(0, 0, prepared.Width, prepared.Height);
        var bitmapData = prepared.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            var stride = Math.Abs(bitmapData.Stride);
            var length = stride * prepared.Height;
            var buffer = new byte[length];
            Marshal.Copy(bitmapData.Scan0, buffer, 0, length);

            for (var y = 0; y < prepared.Height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < prepared.Width; x++)
                {
                    var index = row + (x * 3);
                    var blue = buffer[index];
                    var green = buffer[index + 1];
                    var red = buffer[index + 2];
                    var luminance = (int)Math.Round((red * 0.299) + (green * 0.587) + (blue * 0.114));
                    var contrast = Math.Clamp((int)Math.Round(((luminance - 128) * 1.24) + 128), 0, 255);

                    if (mode == OcrPreprocessMode.Grayscale)
                    {
                        buffer[index] = (byte)contrast;
                        buffer[index + 1] = (byte)contrast;
                        buffer[index + 2] = (byte)contrast;
                        continue;
                    }

                    var normalized = (byte)(contrast < 190 ? 0 : 255);
                    buffer[index] = normalized;
                    buffer[index + 1] = normalized;
                    buffer[index + 2] = normalized;
                }
            }

            Marshal.Copy(buffer, 0, bitmapData.Scan0, length);
        }
        finally
        {
            prepared.UnlockBits(bitmapData);
        }

        return prepared;
    }

    private static void RecordPhaseDuration(
        OcrExecutionProfile profile,
        CapturePerformanceTrace? performanceTrace,
        TimeSpan duration,
        bool isPreprocess)
    {
        if (performanceTrace is null || duration <= TimeSpan.Zero)
        {
            return;
        }

        var phase = profile switch
        {
            OcrExecutionProfile.FullPage when isPreprocess => CapturePerformancePhase.FullPageOcrPreprocess,
            OcrExecutionProfile.FullPage => CapturePerformancePhase.FullPageOcrRecognize,
            OcrExecutionProfile.UiLabelRecovery => CapturePerformancePhase.UiLabelRecovery,
            _ => CapturePerformancePhase.SegmentRefinement
        };
        performanceTrace.AddDuration(phase, duration);
    }

    private static OcrLineRegion ToOcrLineRegion(OcrLine line, double scale)
    {
        var bounds = GetLineBounds(line);
        var words = line.Words
            .Select(word => new OcrWordRegion(
                word.Text,
                new ScreenRegion(
                    word.BoundingRect.X / scale,
                    word.BoundingRect.Y / scale,
                    word.BoundingRect.Width / scale,
                    word.BoundingRect.Height / scale)))
            .Where(word => !word.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(word.Text))
            .ToArray();
        var normalized = new ScreenRegion(
            bounds.X / scale,
            bounds.Y / scale,
            bounds.Width / scale,
            bounds.Height / scale);

        return new OcrLineRegion(line.Text, normalized, words);
    }

    private static Rect GetLineBounds(OcrLine line)
    {
        if (line.Words.Count == 0)
        {
            return Rect.Empty;
        }

        var left = double.MaxValue;
        var top = double.MaxValue;
        var right = double.MinValue;
        var bottom = double.MinValue;

        foreach (var word in line.Words)
        {
            var rect = word.BoundingRect;
            left = Math.Min(left, rect.X);
            top = Math.Min(top, rect.Y);
            right = Math.Max(right, rect.X + rect.Width);
            bottom = Math.Max(bottom, rect.Y + rect.Height);
        }

        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static IReadOnlyList<OcrLineRegion> MergeLines(
        IReadOnlyList<OcrLineRegion> preferred,
        IReadOnlyList<OcrLineRegion> secondary)
    {
        var merged = preferred.ToList();

        foreach (var candidate in secondary)
        {
            var overlapIndex = merged.FindIndex(existing => IsSameLine(existing, candidate));
            if (overlapIndex < 0)
            {
                merged.Add(candidate);
                continue;
            }

            if (OcrQualityHeuristics.ScoreLineText(candidate.Text) > OcrQualityHeuristics.ScoreLineText(merged[overlapIndex].Text))
            {
                merged[overlapIndex] = candidate;
            }
        }

        return merged
            .OrderBy(line => line.Bounds.Top)
            .ThenBy(line => line.Bounds.Left)
            .ToArray();
    }

    private static bool IsSameLine(OcrLineRegion first, OcrLineRegion second)
    {
        var overlapLeft = Math.Max(first.Bounds.Left, second.Bounds.Left);
        var overlapTop = Math.Max(first.Bounds.Top, second.Bounds.Top);
        var overlapRight = Math.Min(first.Bounds.Right, second.Bounds.Right);
        var overlapBottom = Math.Min(first.Bounds.Bottom, second.Bounds.Bottom);

        var overlapWidth = Math.Max(0, overlapRight - overlapLeft);
        var overlapHeight = Math.Max(0, overlapBottom - overlapTop);
        var overlapArea = overlapWidth * overlapHeight;

        var firstArea = Math.Max(1, first.Bounds.Width * first.Bounds.Height);
        var secondArea = Math.Max(1, second.Bounds.Width * second.Bounds.Height);
        var overlapRatio = overlapArea / Math.Min(firstArea, secondArea);

        if (overlapRatio >= 0.55)
        {
            return true;
        }

        var centerDeltaY = Math.Abs(
            (first.Bounds.Top + (first.Bounds.Height / 2))
            - (second.Bounds.Top + (second.Bounds.Height / 2)));
        var leftDelta = Math.Abs(first.Bounds.Left - second.Bounds.Left);

        return centerDeltaY <= Math.Max(4, Math.Min(first.Bounds.Height, second.Bounds.Height) * 0.45)
               && leftDelta <= Math.Max(12, Math.Min(first.Bounds.Width, second.Bounds.Width) * 0.25)
               && string.Equals(first.Text.Trim(), second.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildEngineName(string languageTag, string variantName, bool mergedRunnerUp)
    {
        var mergeSuffix = mergedRunnerUp ? " + merge" : string.Empty;
        return $"Windows OCR [{languageTag}] + {variantName}{mergeSuffix}";
    }

    private static string BuildFallbackEngineName(
        string? preferredLanguageTag,
        IReadOnlyList<Language> availableLanguages)
    {
        var preferred = string.IsNullOrWhiteSpace(preferredLanguageTag)
            ? "auto"
            : preferredLanguageTag;
        var available = string.Join(", ", availableLanguages.Select(language => language.LanguageTag));
        return $"Windows OCR [preferred: {preferred}; available: {available}]";
    }

    private static string BuildEngineCreationErrorMessage(
        string? preferredLanguageTag,
        IReadOnlyList<Language> availableLanguages)
    {
        var preferred = string.IsNullOrWhiteSpace(preferredLanguageTag)
            ? "auto"
            : preferredLanguageTag;
        var available = string.Join(", ", availableLanguages.Select(language => language.LanguageTag));
        return $"Windows OCR не смог создать распознаватель. Предпочтительный язык: {preferred}. Доступные OCR-языки: {available}.";
    }

    private static Language? FindBestAvailableLanguage(
        string preferredLanguageTag,
        IReadOnlyList<Language> availableLanguages)
    {
        var exactMatch = availableLanguages.FirstOrDefault(language =>
            string.Equals(language.LanguageTag, preferredLanguageTag, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null)
        {
            return exactMatch;
        }

        var preferredFamily = GetLanguageFamily(preferredLanguageTag);
        return availableLanguages.FirstOrDefault(language =>
            string.Equals(GetLanguageFamily(language.LanguageTag), preferredFamily, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetLanguageFamily(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return null;
        }

        var separatorIndex = languageTag.IndexOf('-');
        return separatorIndex > 0
            ? languageTag[..separatorIndex]
            : languageTag;
    }

    private static Language? TryCreateLanguage(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return null;
        }

        try
        {
            return new Language(languageTag);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
        var bytes = memoryStream.ToArray();

        using var randomAccessStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(randomAccessStream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        randomAccessStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    private enum OcrPreprocessMode
    {
        Raw,
        Grayscale,
        Binary
    }

    private sealed class OcrBitmapVariant(string name, Bitmap bitmap, double scale, bool ownsBitmap)
    {
        public string Name { get; } = name;
        public Bitmap Bitmap { get; } = bitmap;
        public double Scale { get; } = scale;
        public bool OwnsBitmap { get; } = ownsBitmap;
    }

    private sealed class OcrLanguageCandidate(string displayTag, Language? language, bool useUserProfile = false)
    {
        public static OcrLanguageCandidate UserProfile { get; } = new("user-profile", null, useUserProfile: true);

        public string DisplayTag { get; } = displayTag;
        public Language? Language { get; } = language;
        public bool UseUserProfile { get; } = useUserProfile;
    }
}
