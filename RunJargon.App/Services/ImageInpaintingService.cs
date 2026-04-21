using OpenCvSharp;

namespace RunJargon.App.Services;

public sealed class ImageInpaintingService
{
    public byte[] Inpaint(byte[] sourcePngBytes, byte[] maskPngBytes)
    {
        using var source = Cv2.ImDecode(sourcePngBytes, ImreadModes.Color);
        using var mask = Cv2.ImDecode(maskPngBytes, ImreadModes.Grayscale);

        if (source.Empty())
        {
            return sourcePngBytes;
        }

        if (mask.Empty() || Cv2.CountNonZero(mask) == 0)
        {
            return sourcePngBytes;
        }

        using var cleaned = new Mat();
        Cv2.Inpaint(source, mask, cleaned, 3, InpaintTypes.Telea);

        return cleaned.ImEncode(".png");
    }
}
