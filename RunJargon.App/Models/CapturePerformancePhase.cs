namespace RunJargon.App.Models;

public enum CapturePerformancePhase
{
    Capture,
    SnapshotEncode,
    FullPageOcrPreprocess,
    FullPageOcrRecognize,
    LayoutSegmentation,
    UiAutomationRead,
    VisualRefinement,
    SegmentRefinement,
    UiLabelRecovery,
    TranslationBatch,
    Inpainting,
    OverlayCompose
}
