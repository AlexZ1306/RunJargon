using RunJargon.App.Models;
using RunJargon.App.Services;

namespace RunJargon.App.Tests;

public class LayoutSegmentationServiceTests
{
    private readonly LayoutSegmentationService _service = new();

    [Fact]
    public void BuildSegments_SplitsDenseUiTabRowIntoSeparateLabels()
    {
        var line = CreateLine(
            "Archives Types Parts Attributes Medialnfo",
            10,
            10,
            336,
            16,
            [
                Word("Archives", 10, 10, 58, 16),
                Word("Types", 88, 10, 38, 16),
                Word("Parts", 145, 10, 36, 16),
                Word("Attributes", 201, 10, 64, 16),
                Word("Medialnfo", 286, 10, 60, 16)
            ]);

        var segments = _service.BuildSegments([line]);

        Assert.Equal(5, segments.Count);
        Assert.Collection(
            segments,
            segment => AssertSegment(segment, "Archives", TextLayoutKind.UiLabel),
            segment => AssertSegment(segment, "Types", TextLayoutKind.UiLabel),
            segment => AssertSegment(segment, "Parts", TextLayoutKind.UiLabel),
            segment => AssertSegment(segment, "Attributes", TextLayoutKind.UiLabel),
            segment => AssertSegment(segment, "MediaInfo", TextLayoutKind.UiLabel));
    }

    [Fact]
    public void BuildSegments_SplitsColumnHeaderRowIntoSeparateLabels()
    {
        var line = CreateLine(
            "File Path Size",
            12,
            40,
            170,
            15,
            [
                Word("File", 12, 40, 24, 15),
                Word("Path", 74, 40, 28, 15),
                Word("Size", 142, 40, 26, 15)
            ]);

        var segments = _service.BuildSegments([line]);

        Assert.Equal(3, segments.Count);
        Assert.Collection(
            segments,
            segment => AssertSegment(segment, "File", TextLayoutKind.UiLabel),
            segment => AssertSegment(segment, "Path", TextLayoutKind.UiLabel),
            segment => AssertSegment(segment, "Size", TextLayoutKind.UiLabel));
    }

    [Fact]
    public void BuildSegments_MergesAlignedParagraphLinesIntoSingleParagraphSegment()
    {
        var first = CreateLine(
            "This paragraph line is intentionally long",
            10,
            10,
            250,
            18,
            [
                Word("This", 10, 10, 24, 18),
                Word("paragraph", 40, 10, 58, 18),
                Word("line", 104, 10, 24, 18),
                Word("is", 134, 10, 10, 18),
                Word("intentionally", 150, 10, 74, 18),
                Word("long", 230, 10, 30, 18)
            ]);
        var second = CreateLine(
            "and should be grouped with the next line",
            11,
            31,
            249,
            18,
            [
                Word("and", 11, 31, 20, 18),
                Word("should", 38, 31, 36, 18),
                Word("be", 80, 31, 14, 18),
                Word("grouped", 100, 31, 46, 18),
                Word("with", 152, 31, 24, 18),
                Word("the", 182, 31, 18, 18),
                Word("next", 206, 31, 24, 18),
                Word("line", 236, 31, 24, 18)
            ]);

        var segments = _service.BuildSegments([first, second]);

        var segment = Assert.Single(segments);
        Assert.Equal(TextLayoutKind.Paragraph, segment.Kind);
        Assert.Equal(
            "This paragraph line is intentionally long and should be grouped with the next line",
            segment.Text);
        Assert.Equal(2, segment.SourceLines.Count);
    }

    [Fact]
    public void BuildSegments_DoesNotMergeUiRowWithParagraphBelow()
    {
        var headerRow = CreateLine(
            "File Path Size",
            12,
            10,
            170,
            15,
            [
                Word("File", 12, 10, 24, 15),
                Word("Path", 74, 10, 28, 15),
                Word("Size", 142, 10, 26, 15)
            ]);
        var paragraphLine = CreateLine(
            "This paragraph should remain separate",
            10,
            36,
            210,
            18,
            [
                Word("This", 10, 36, 24, 18),
                Word("paragraph", 40, 36, 58, 18),
                Word("should", 104, 36, 36, 18),
                Word("remain", 146, 36, 40, 18),
                Word("separate", 192, 36, 28, 18)
            ]);

        var segments = _service.BuildSegments([headerRow, paragraphLine]);

        Assert.Equal(4, segments.Count);
        Assert.All(segments.Take(3), segment => Assert.Equal(TextLayoutKind.UiLabel, segment.Kind));
        Assert.Equal(TextLayoutKind.Paragraph, segments[3].Kind);
    }

    private static void AssertSegment(LayoutTextSegment segment, string expectedText, TextLayoutKind expectedKind)
    {
        Assert.Equal(expectedText, segment.Text);
        Assert.Equal(expectedKind, segment.Kind);
        Assert.False(segment.Bounds.IsEmpty);
        Assert.NotEmpty(segment.SourceLines);
    }

    private static OcrLineRegion CreateLine(
        string text,
        double left,
        double top,
        double width,
        double height,
        IReadOnlyList<OcrWordRegion> words)
    {
        return new OcrLineRegion(
            text,
            new ScreenRegion(left, top, width, height),
            words);
    }

    private static OcrWordRegion Word(string text, double left, double top, double width, double height)
    {
        return new OcrWordRegion(
            text,
            new ScreenRegion(left, top, width, height));
    }
}
