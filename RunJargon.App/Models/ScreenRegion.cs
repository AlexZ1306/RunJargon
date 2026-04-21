namespace RunJargon.App.Models;

public readonly record struct ScreenRegion(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}
