namespace RunJargon.App.Models;

public sealed record CaptureSessionResult(
    ScreenRegion Region,
    byte[] SnapshotPngBytes,
    byte[] PreparedBackgroundPngBytes,
    OcrResponse Ocr,
    TranslationResponse Translation,
    IReadOnlyList<TranslatedOcrLine> OverlayLines,
    DateTimeOffset CapturedAt);
