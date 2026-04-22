using RunJargon.App.Models;
using RunJargon.App.Services;

namespace RunJargon.App.Tests;

public sealed class CaptureProcessingModeClassifierTests
{
    private readonly CaptureProcessingModeClassifier _classifier = new();

    [Fact]
    public void Classify_ReturnsDocumentLike_ForParagraphDominatedCapture()
    {
        LayoutTextSegment[] segments =
        [
            Segment("Long paragraph text goes here", 10, 10, 260, 22, TextLayoutKind.Paragraph),
            Segment("Another long paragraph line", 12, 36, 258, 22, TextLayoutKind.TextLine)
        ];

        var mode = _classifier.Classify(segments);

        Assert.Equal(CaptureProcessingMode.DocumentLike, mode);
    }

    [Fact]
    public void Classify_ReturnsUiDense_ForDenseShortLabelRow()
    {
        LayoutTextSegment[] segments =
        [
            Segment("Archives", 10, 10, 58, 16, TextLayoutKind.UiLabel),
            Segment("Types", 84, 10, 38, 16, TextLayoutKind.UiLabel),
            Segment("Parts", 138, 10, 36, 16, TextLayoutKind.UiLabel),
            Segment("Attributes", 190, 10, 64, 16, TextLayoutKind.UiLabel),
            Segment("MediaInfo", 270, 10, 62, 16, TextLayoutKind.UiLabel)
        ];

        var mode = _classifier.Classify(segments);

        Assert.Equal(CaptureProcessingMode.UiDense, mode);
    }

    [Fact]
    public void Classify_ReturnsMixed_ForHybridCapture()
    {
        LayoutTextSegment[] segments =
        [
            Segment("Save", 10, 10, 34, 16, TextLayoutKind.UiLabel),
            Segment("This is a longer line of descriptive text", 12, 44, 280, 22, TextLayoutKind.TextLine),
            Segment("Another longer line of descriptive text", 12, 72, 286, 22, TextLayoutKind.TextLine)
        ];

        var mode = _classifier.Classify(segments);

        Assert.Equal(CaptureProcessingMode.Mixed, mode);
    }

    private static LayoutTextSegment Segment(
        string text,
        double left,
        double top,
        double width,
        double height,
        TextLayoutKind kind)
    {
        return new LayoutTextSegment(
            text,
            new ScreenRegion(left, top, width, height),
            [new OcrLineRegion(text, new ScreenRegion(left, top, width, height))],
            kind);
    }
}
