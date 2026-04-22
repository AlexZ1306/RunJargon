using System.Threading;

namespace RunJargon.App.Models;

public sealed class CapturePerformanceCounters
{
    private int _ocrRequests;
    private int _cropRefinements;
    private int _recoveryAttempts;
    private int _translationRequests;
    private int _inpaintCalls;

    public int OcrRequests => _ocrRequests;

    public int CropRefinements => _cropRefinements;

    public int RecoveryAttempts => _recoveryAttempts;

    public int TranslationRequests => _translationRequests;

    public int InpaintCalls => _inpaintCalls;

    public void IncrementOcrRequests(int value = 1)
    {
        Interlocked.Add(ref _ocrRequests, value);
    }

    public void IncrementCropRefinements(int value = 1)
    {
        Interlocked.Add(ref _cropRefinements, value);
    }

    public void IncrementRecoveryAttempts(int value = 1)
    {
        Interlocked.Add(ref _recoveryAttempts, value);
    }

    public void IncrementTranslationRequests(int value = 1)
    {
        Interlocked.Add(ref _translationRequests, value);
    }

    public void IncrementInpaintCalls(int value = 1)
    {
        Interlocked.Add(ref _inpaintCalls, value);
    }
}
