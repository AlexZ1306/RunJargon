namespace RunJargon.App.Models;

public sealed record OcrWordRegion(
    string Text,
    ScreenRegion Bounds);
