namespace RunJargon.App.Models;

public sealed record TranslatedOcrLine(
    string SourceText,
    string TranslatedText,
    ScreenRegion Bounds,
    int SourceLineCount = 1,
    double PreferredFontSize = 0,
    IReadOnlyList<TranslatedInlineRun>? InlineRuns = null,
    TextLayoutKind LayoutKind = TextLayoutKind.TextLine);
