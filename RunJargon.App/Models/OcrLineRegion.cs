namespace RunJargon.App.Models;

public sealed record OcrLineRegion(
    string Text,
    ScreenRegion Bounds,
    IReadOnlyList<OcrWordRegion>? Words = null);
