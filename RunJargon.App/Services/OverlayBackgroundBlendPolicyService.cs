using RunJargon.App.Models;

namespace RunJargon.App.Services;

public sealed class OverlayBackgroundBlendPolicyService
{
    public byte ResolveOpacity(TextLayoutKind layoutKind, int sourceLineCount, double backgroundDeviation)
    {
        var paragraphLike = layoutKind == TextLayoutKind.Paragraph || sourceLineCount > 1;
        if (paragraphLike)
        {
            return backgroundDeviation switch
            {
                <= 8 => 244,
                <= 14 => 226,
                <= 22 => 188,
                _ => 128
            };
        }

        if (layoutKind == TextLayoutKind.UiLabel)
        {
            return backgroundDeviation switch
            {
                <= 8 => 138,
                <= 16 => 116,
                _ => 96
            };
        }

        return backgroundDeviation switch
        {
            <= 8 => 214,
            <= 16 => 168,
            _ => 112
        };
    }
}
