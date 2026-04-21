namespace RunJargon.App.Models;

public sealed record LayoutTextSegment(
    string Text,
    ScreenRegion Bounds,
    IReadOnlyList<OcrLineRegion> SourceLines,
    TextLayoutKind Kind);
