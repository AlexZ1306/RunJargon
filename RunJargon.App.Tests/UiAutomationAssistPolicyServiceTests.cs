using RunJargon.App.Models;
using RunJargon.App.Services;

namespace RunJargon.App.Tests;

public class UiAutomationAssistPolicyServiceTests
{
    private readonly UiAutomationAssistPolicyService _service = new();

    [Fact]
    public void ShouldUseUiAutomation_ReturnsTrueForSmallDenseControlSurface()
    {
        LayoutTextSegment[] segments =
        [
            Segment("Archives", 10, 10, 54, 18, TextLayoutKind.UiLabel),
            Segment("Types", 74, 10, 36, 18, TextLayoutKind.UiLabel),
            Segment("Parts", 120, 10, 38, 18, TextLayoutKind.UiLabel),
            Segment("Attributes", 168, 10, 64, 18, TextLayoutKind.UiLabel),
            Segment("MediaInfo", 242, 10, 62, 18, TextLayoutKind.UiLabel),
            Segment("File", 20, 38, 28, 16, TextLayoutKind.TextLine),
            Segment("Path", 110, 38, 30, 16, TextLayoutKind.TextLine),
            Segment("Size", 220, 38, 28, 16, TextLayoutKind.TextLine)
        ];

        var shouldUse = _service.ShouldUseUiAutomation(segments);

        Assert.True(shouldUse);
    }

    [Fact]
    public void ShouldUseUiAutomation_ReturnsFalseForLargeWebsiteFooterGrid()
    {
        LayoutTextSegment[] segments =
        [
            Segment("Company", 20, 20, 90, 18, TextLayoutKind.UiLabel),
            Segment("Use cases", 300, 20, 110, 18, TextLayoutKind.UiLabel),
            Segment("Connect", 580, 20, 90, 18, TextLayoutKind.UiLabel),
            Segment("Support", 860, 20, 90, 18, TextLayoutKind.UiLabel),
            Segment("About & manifesto", 20, 60, 150, 18, TextLayoutKind.TextLine),
            Segment("Careers", 20, 100, 90, 18, TextLayoutKind.TextLine),
            Segment("Reviews & testimonials", 20, 140, 200, 18, TextLayoutKind.TextLine),
            Segment("Press kit", 20, 180, 90, 18, TextLayoutKind.TextLine),
            Segment("Privacy & security", 20, 220, 160, 18, TextLayoutKind.TextLine),
            Segment("Terms & conditions", 20, 260, 160, 18, TextLayoutKind.TextLine),
            Segment("Meeting & presenting", 300, 60, 170, 18, TextLayoutKind.TextLine),
            Segment("Teaching & education", 300, 100, 170, 18, TextLayoutKind.TextLine),
            Segment("Live streaming", 300, 140, 130, 18, TextLayoutKind.TextLine),
            Segment("Content creation", 300, 180, 140, 18, TextLayoutKind.TextLine),
            Segment("Phone as a webcam", 300, 220, 160, 18, TextLayoutKind.TextLine),
            Segment("News & updates", 580, 60, 130, 18, TextLayoutKind.TextLine),
            Segment("Community", 580, 100, 110, 18, TextLayoutKind.TextLine),
            Segment("YouTube", 580, 140, 90, 18, TextLayoutKind.TextLine),
            Segment("X (Twitter)", 580, 180, 110, 18, TextLayoutKind.TextLine),
            Segment("LinkedIn", 580, 220, 100, 18, TextLayoutKind.TextLine),
            Segment("Help center", 860, 60, 120, 18, TextLayoutKind.TextLine),
            Segment("Contact us", 860, 100, 110, 18, TextLayoutKind.TextLine),
            Segment("Manage licenses", 860, 140, 140, 18, TextLayoutKind.TextLine),
            Segment("Pricing", 860, 180, 90, 18, TextLayoutKind.TextLine),
            Segment("English", 860, 240, 90, 18, TextLayoutKind.TextLine)
        ];

        var shouldUse = _service.ShouldUseUiAutomation(segments);

        Assert.False(shouldUse);
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
