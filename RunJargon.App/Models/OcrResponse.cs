namespace RunJargon.App.Models;

public sealed record OcrResponse(
    string Text,
    IReadOnlyList<OcrLineRegion> Lines,
    string EngineName);
