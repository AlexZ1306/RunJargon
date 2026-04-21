using System.Drawing;
using RunJargon.App.Models;

namespace RunJargon.App.Services;

public sealed record OverlayTextColorSample(
    Color Color,
    double Contrast,
    double ChangedRatio,
    double DominantRatio,
    double AverageDeviation)
{
    public double ConfidenceScore
    {
        get
        {
            var contrastScore = Math.Clamp((Contrast - 42) / 88d, 0, 1);
            var changedScore = ChangedRatio switch
            {
                < 0.01 => 0,
                > 0.42 => 0.35,
                _ => 1 - Math.Min(1, Math.Abs(ChangedRatio - 0.14) / 0.22)
            };
            var dominantScore = Math.Clamp((DominantRatio - 0.46) / 0.44, 0, 1);
            var deviationScore = 1 - Math.Clamp((AverageDeviation - 20) / 76d, 0, 1);

            return (contrastScore * 0.38)
                   + (changedScore * 0.14)
                   + (dominantScore * 0.28)
                   + (deviationScore * 0.20);
        }
    }
}

public sealed record OverlayTextStyleCandidate(
    TextLayoutKind LayoutKind,
    ScreenRegion Bounds,
    double PreferredFontSize,
    Color BackgroundColor,
    Color DefaultForegroundColor,
    OverlayTextColorSample? ExtractedForegroundSample,
    bool HasInlineRuns = false);

public sealed record OverlayResolvedTextStyle(
    Color LineColor,
    bool UsedExtractedColor,
    bool UsedGroupNormalization);

public sealed class OverlayTextStyleNormalizationService
{
    public IReadOnlyList<OverlayResolvedTextStyle> Resolve(IReadOnlyList<OverlayTextStyleCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return Array.Empty<OverlayResolvedTextStyle>();
        }

        var resolved = new OverlayResolvedTextStyle[candidates.Count];
        var groups = BuildGroups(candidates);

        foreach (var group in groups)
        {
            Color? normalizedGroupColor = TryResolveGroupColor(
                candidates,
                group,
                out var groupColor,
                out var coverageRatio)
                ? groupColor
                : null;

            foreach (var index in group)
            {
                resolved[index] = ResolveCandidateStyle(
                    candidates[index],
                    normalizedGroupColor,
                    coverageRatio);
            }
        }

        return resolved;
    }

    private static IReadOnlyList<int[]> BuildGroups(IReadOnlyList<OverlayTextStyleCandidate> candidates)
    {
        return candidates
            .Select((candidate, index) => new
            {
                Index = index,
                Key = new StyleGroupKey(
                    candidate.LayoutKind,
                    GetFontBucket(candidate),
                    GetBackgroundBucket(candidate.BackgroundColor),
                    candidate.HasInlineRuns)
            })
            .GroupBy(item => item.Key)
            .Select(group => group.Select(item => item.Index).ToArray())
            .ToArray();
    }

    private static OverlayResolvedTextStyle ResolveCandidateStyle(
        OverlayTextStyleCandidate candidate,
        Color? normalizedGroupColor,
        double coverageRatio)
    {
        var sample = candidate.ExtractedForegroundSample;
        if (normalizedGroupColor is Color normalizedColor
            && coverageRatio >= 0.55)
        {
            if (sample is not null
                && sample.ConfidenceScore >= 0.9
                && ColorDistance(sample.Color, normalizedColor) >= 118)
            {
                return new OverlayResolvedTextStyle(sample.Color, true, false);
            }

            return new OverlayResolvedTextStyle(normalizedColor, true, true);
        }

        if (sample is not null && sample.ConfidenceScore >= 0.72)
        {
            return new OverlayResolvedTextStyle(sample.Color, true, false);
        }

        return new OverlayResolvedTextStyle(candidate.DefaultForegroundColor, false, false);
    }

    private static bool TryResolveGroupColor(
        IReadOnlyList<OverlayTextStyleCandidate> candidates,
        IReadOnlyList<int> group,
        out Color groupColor,
        out double coverageRatio)
    {
        groupColor = Color.Empty;
        coverageRatio = 0;

        if (group.Count < 2)
        {
            return false;
        }

        var confident = group
            .Select(index => candidates[index].ExtractedForegroundSample)
            .Where(sample => sample is not null && sample.ConfidenceScore >= 0.6)
            .Cast<OverlayTextColorSample>()
            .ToArray();

        if (confident.Length < 2)
        {
            return false;
        }

        IReadOnlyList<OverlayTextColorSample>? bestCluster = null;
        foreach (var pivot in confident)
        {
            var cluster = confident
                .Where(sample => ColorDistance(sample.Color, pivot.Color) <= 54)
                .ToArray();

            if (bestCluster is null || cluster.Length > bestCluster.Count)
            {
                bestCluster = cluster;
                continue;
            }

            if (cluster.Length == bestCluster.Count
                && cluster.Sum(sample => sample.ConfidenceScore) > bestCluster.Sum(sample => sample.ConfidenceScore))
            {
                bestCluster = cluster;
            }
        }

        if (bestCluster is null || bestCluster.Count < 2)
        {
            return false;
        }

        coverageRatio = (double)bestCluster.Count / group.Count;
        if (coverageRatio < 0.4)
        {
            return false;
        }

        groupColor = WeightedAverage(bestCluster);
        return true;
    }

    private static int GetFontBucket(OverlayTextStyleCandidate candidate)
    {
        var basis = candidate.PreferredFontSize > 0
            ? candidate.PreferredFontSize
            : Math.Max(10, candidate.Bounds.Height * 0.94);
        return (int)Math.Round(basis / 1.6);
    }

    private static int GetBackgroundBucket(Color color)
    {
        var luminance = (color.R * 0.299) + (color.G * 0.587) + (color.B * 0.114);
        return (int)Math.Round(luminance / 24d);
    }

    private static Color WeightedAverage(IReadOnlyList<OverlayTextColorSample> samples)
    {
        double weightedR = 0;
        double weightedG = 0;
        double weightedB = 0;
        double weightSum = 0;

        foreach (var sample in samples)
        {
            var weight = Math.Max(0.2, sample.ConfidenceScore);
            weightedR += sample.Color.R * weight;
            weightedG += sample.Color.G * weight;
            weightedB += sample.Color.B * weight;
            weightSum += weight;
        }

        if (weightSum <= 0)
        {
            return Color.White;
        }

        return Color.FromArgb(
            (int)Math.Round(weightedR / weightSum),
            (int)Math.Round(weightedG / weightSum),
            (int)Math.Round(weightedB / weightSum));
    }

    private static int ColorDistance(Color left, Color right)
    {
        return Math.Abs(left.R - right.R)
               + Math.Abs(left.G - right.G)
               + Math.Abs(left.B - right.B);
    }

    private readonly record struct StyleGroupKey(
        TextLayoutKind LayoutKind,
        int FontBucket,
        int BackgroundBucket,
        bool HasInlineRuns);
}
