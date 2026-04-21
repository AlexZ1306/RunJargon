using System.Drawing;
using RunJargon.App.Models;
using RunJargon.App.Services;

namespace RunJargon.App.Tests;

public sealed class OverlayTextStyleNormalizationServiceTests
{
    private readonly OverlayTextStyleNormalizationService _service = new();

    [Fact]
    public void Resolve_NormalizesDominantColorAcrossUniformGroup()
    {
        var candidates = new[]
        {
            CreateCandidate(13, CreateSample(Color.FromArgb(182, 184, 188), 72, 0.12, 0.82, 18)),
            CreateCandidate(13, CreateSample(Color.FromArgb(178, 181, 185), 70, 0.11, 0.79, 20)),
            CreateCandidate(13, CreateSample(Color.FromArgb(184, 186, 189), 74, 0.13, 0.84, 16)),
            CreateCandidate(13, CreateSample(Color.FromArgb(221, 223, 228), 55, 0.06, 0.52, 34))
        };

        var resolved = _service.Resolve(candidates);
        var reference = resolved[0].LineColor;

        Assert.All(resolved, style => Assert.True(style.UsedExtractedColor));
        Assert.All(resolved, style => Assert.True(style.UsedGroupNormalization));
        Assert.All(resolved, style => Assert.InRange(ColorDistance(style.LineColor, reference), 0, 12));
    }

    [Fact]
    public void Resolve_SeparatesDifferentFontBands()
    {
        var candidates = new[]
        {
            CreateCandidate(18, CreateSample(Color.FromArgb(236, 239, 242), 88, 0.16, 0.9, 12)),
            CreateCandidate(13, CreateSample(Color.FromArgb(181, 183, 186), 68, 0.1, 0.81, 19)),
            CreateCandidate(13, CreateSample(Color.FromArgb(179, 182, 185), 69, 0.11, 0.82, 18))
        };

        var resolved = _service.Resolve(candidates);

        Assert.InRange(ColorDistance(resolved[1].LineColor, resolved[2].LineColor), 0, 10);
        Assert.InRange(ColorDistance(resolved[0].LineColor, resolved[1].LineColor), 90, 255);
    }

    [Fact]
    public void Resolve_FallsBackToStableDefaultWhenSamplesAreWeak()
    {
        var candidates = new[]
        {
            CreateCandidate(13, CreateSample(Color.FromArgb(166, 168, 171), 46, 0.03, 0.49, 61)),
            CreateCandidate(13, CreateSample(Color.FromArgb(158, 161, 165), 45, 0.02, 0.48, 64)),
            CreateCandidate(13, null)
        };

        var resolved = _service.Resolve(candidates);

        Assert.All(resolved, style =>
        {
            Assert.False(style.UsedGroupNormalization);
            Assert.False(style.UsedExtractedColor);
            Assert.Equal(Color.FromArgb(248, 250, 252), style.LineColor);
        });
    }

    private static OverlayTextStyleCandidate CreateCandidate(double fontSize, OverlayTextColorSample? sample)
    {
        return new OverlayTextStyleCandidate(
            TextLayoutKind.TextLine,
            new ScreenRegion(0, 0, 220, 22),
            fontSize,
            Color.FromArgb(18, 20, 24),
            Color.FromArgb(248, 250, 252),
            sample);
    }

    private static OverlayTextColorSample CreateSample(
        Color color,
        double contrast,
        double changedRatio,
        double dominantRatio,
        double averageDeviation)
    {
        return new OverlayTextColorSample(
            color,
            contrast,
            changedRatio,
            dominantRatio,
            averageDeviation);
    }

    private static int ColorDistance(Color left, Color right)
    {
        return Math.Abs(left.R - right.R)
               + Math.Abs(left.G - right.G)
               + Math.Abs(left.B - right.B);
    }
}
