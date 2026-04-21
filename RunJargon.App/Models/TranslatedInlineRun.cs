namespace RunJargon.App.Models;

public sealed record TranslatedInlineRun(
    string Text,
    ScreenRegion? SourceBounds = null,
    bool PreserveSourceColor = false,
    bool PreserveBold = false);
