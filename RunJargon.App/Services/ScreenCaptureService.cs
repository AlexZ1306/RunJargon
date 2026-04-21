using System.Drawing;
using System.Drawing.Imaging;
using RunJargon.App.Models;

namespace RunJargon.App.Services;

public sealed class ScreenCaptureService
{
    public Bitmap Capture(ScreenRegion region)
    {
        if (region.IsEmpty)
        {
            throw new InvalidOperationException("Нельзя сделать снимок пустой области.");
        }

        var width = Math.Max(1, (int)Math.Ceiling(region.Width));
        var height = Math.Max(1, (int)Math.Ceiling(region.Height));
        var x = (int)Math.Floor(region.Left);
        var y = (int)Math.Floor(region.Top);

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

        return bitmap;
    }
}
