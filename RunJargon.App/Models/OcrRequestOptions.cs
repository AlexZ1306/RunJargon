namespace RunJargon.App.Models;

public sealed record OcrRequestOptions(
    OcrExecutionProfile Profile = OcrExecutionProfile.FullPage,
    CapturePerformanceTrace? PerformanceTrace = null);
