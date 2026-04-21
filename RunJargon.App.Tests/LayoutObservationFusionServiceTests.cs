using RunJargon.App.Models;
using RunJargon.App.Services;

namespace RunJargon.App.Tests;

public class LayoutObservationFusionServiceTests
{
    private readonly LayoutObservationFusionService _service = new();

    [Fact]
    public void Merge_ReplacesSingleBroadOcrRowWithMultipleUiAutomationLabels()
    {
        LayoutTextSegment[] ocrSegments =
        [
            Segment(
                "Archives Types Parts Attributes Medialnfo",
                10, 10, 336, 16,
                TextLayoutKind.TextLine)
        ];

        LayoutTextSegment[] automationSegments =
        [
            Segment("Archives", 10, 10, 58, 16, TextLayoutKind.UiLabel),
            Segment("Types", 88, 10, 38, 16, TextLayoutKind.UiLabel),
            Segment("Parts", 145, 10, 36, 16, TextLayoutKind.UiLabel),
            Segment("Attributes", 201, 10, 64, 16, TextLayoutKind.UiLabel),
            Segment("MediaInfo", 286, 10, 60, 16, TextLayoutKind.UiLabel)
        ];

        var merged = _service.Merge(ocrSegments, automationSegments);

        Assert.Equal(5, merged.Count);
        Assert.All(merged, segment => Assert.Equal(TextLayoutKind.UiLabel, segment.Kind));
        Assert.DoesNotContain(merged, segment => segment.Text.Contains("Archives Types", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_DoesNotInjectUnrelatedUiAutomationLabelsNearParagraph()
    {
        LayoutTextSegment[] ocrSegments =
        [
            Segment(
                "This paragraph should stay intact across the full line",
                10, 40, 320, 20,
                TextLayoutKind.Paragraph)
        ];

        LayoutTextSegment[] automationSegments =
        [
            Segment("File", 12, 10, 24, 15, TextLayoutKind.UiLabel),
            Segment("Path", 74, 10, 28, 15, TextLayoutKind.UiLabel)
        ];

        var merged = _service.Merge(ocrSegments, automationSegments);

        var paragraph = Assert.Single(merged);
        Assert.Equal(TextLayoutKind.Paragraph, paragraph.Kind);
        Assert.Equal("This paragraph should stay intact across the full line", paragraph.Text);
    }

    [Fact]
    public void Merge_DoesNotInjectOrphanUiAutomationLabelsWhenNoOcrSegmentMatches()
    {
        LayoutTextSegment[] ocrSegments =
        [
            Segment("Camo Studio", 12, 8, 120, 18, TextLayoutKind.TextLine),
            Segment("Runs on your Mac or PC and makes it available.", 12, 36, 280, 20, TextLayoutKind.Paragraph)
        ];

        LayoutTextSegment[] automationSegments =
        [
            Segment("This PC", 320, 8, 48, 18, TextLayoutKind.UiLabel),
            Segment("Downloads", 320, 32, 64, 18, TextLayoutKind.UiLabel),
            Segment("OpenCvSharp.dll", 320, 56, 92, 18, TextLayoutKind.UiLabel)
        ];

        var merged = _service.Merge(ocrSegments, automationSegments);

        Assert.Equal(2, merged.Count);
        Assert.DoesNotContain(merged, segment => segment.Text == "This PC");
        Assert.DoesNotContain(merged, segment => segment.Text == "Downloads");
        Assert.DoesNotContain(merged, segment => segment.Text == "OpenCvSharp.dll");
    }

    private static LayoutTextSegment Segment(
        string text,
        double left,
        double top,
        double width,
        double height,
        TextLayoutKind kind)
    {
        var line = new OcrLineRegion(
            text,
            new ScreenRegion(left, top, width, height));

        return new LayoutTextSegment(
            text,
            new ScreenRegion(left, top, width, height),
            [line],
            kind);
    }
}
