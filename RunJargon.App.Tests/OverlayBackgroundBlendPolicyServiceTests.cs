using RunJargon.App.Models;
using RunJargon.App.Services;

namespace RunJargon.App.Tests;

public sealed class OverlayBackgroundBlendPolicyServiceTests
{
    private readonly OverlayBackgroundBlendPolicyService _service = new();

    [Fact]
    public void ResolveOpacity_UsesNearlyOpaqueFillForParagraphOnFlatBackground()
    {
        var opacity = _service.ResolveOpacity(TextLayoutKind.Paragraph, 4, 5.5);

        Assert.True(opacity >= 240);
    }

    [Fact]
    public void ResolveOpacity_KeepsUiLabelsLighterThanParagraphs()
    {
        var paragraphOpacity = _service.ResolveOpacity(TextLayoutKind.Paragraph, 1, 6);
        var labelOpacity = _service.ResolveOpacity(TextLayoutKind.UiLabel, 1, 6);

        Assert.True(paragraphOpacity > labelOpacity);
    }

    [Fact]
    public void ResolveOpacity_ReducesOpacityOnBusyBackgrounds()
    {
        var flatOpacity = _service.ResolveOpacity(TextLayoutKind.TextLine, 1, 6);
        var busyOpacity = _service.ResolveOpacity(TextLayoutKind.TextLine, 1, 24);

        Assert.True(flatOpacity > busyOpacity);
    }
}
