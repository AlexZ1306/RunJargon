using System.Drawing;
using RunJargon.App.Models;
using RunJargon.App.Services;

namespace RunJargon.App.Tests;

public class VisualSegmentRefinementServiceTests
{
    private readonly VisualSegmentRefinementService _service = new();

    [Fact]
    public void Refine_SplitsWideUiRowUsingImageGeometry()
    {
        using var bitmap = new Bitmap(420, 100);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            FillLabel(graphics, 14, 32, 54, 18);
            FillLabel(graphics, 92, 32, 38, 18);
            FillLabel(graphics, 149, 32, 34, 18);
            FillLabel(graphics, 202, 32, 66, 18);
            FillLabel(graphics, 288, 32, 62, 18);
        }

        LayoutTextSegment[] segments =
        [
            Segment(
                "Archives Types Parts Attributes Medialnfo",
                10, 28, 350, 26,
                TextLayoutKind.TextLine)
        ];

        var refined = _service.Refine(segments, bitmap);

        Assert.Equal(5, refined.Count);
        Assert.Collection(
            refined,
            segment => AssertSplitSegment(segment, "Archives"),
            segment => AssertSplitSegment(segment, "Types"),
            segment => AssertSplitSegment(segment, "Parts"),
            segment => AssertSplitSegment(segment, "Attributes"),
            segment => AssertSplitSegment(segment, "Medialnfo"));
    }

    [Fact]
    public void Refine_DoesNotSplitParagraphSegments()
    {
        using var bitmap = new Bitmap(420, 120);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Black, 20, 40, 320, 24);
        }

        LayoutTextSegment[] segments =
        [
            Segment(
                "This paragraph should remain one block",
                16, 36, 332, 32,
                TextLayoutKind.Paragraph)
        ];

        var refined = _service.Refine(segments, bitmap);

        var segment = Assert.Single(refined);
        Assert.Equal("This paragraph should remain one block", segment.Text);
        Assert.Equal(TextLayoutKind.Paragraph, segment.Kind);
    }

    private static void FillLabel(Graphics graphics, int left, int top, int width, int height)
    {
        graphics.FillRectangle(Brushes.Black, left, top, width, height);
    }

    private static void AssertSplitSegment(LayoutTextSegment segment, string expectedText)
    {
        Assert.Equal(expectedText, segment.Text);
        Assert.Equal(TextLayoutKind.UiLabel, segment.Kind);
        Assert.False(segment.Bounds.IsEmpty);
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
