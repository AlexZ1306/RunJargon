using System.Drawing;
using RunJargon.App.Models;

namespace RunJargon.App.Services;

public sealed class QueuedOcrService : IOcrService, IDisposable
{
    private readonly IOcrService _inner;
    private readonly IOcrExecutionWorker _worker;
    private bool _disposed;

    public QueuedOcrService(IOcrService inner, IOcrExecutionWorker worker)
    {
        _inner = inner;
        _worker = worker;
    }

    public string DisplayName => _inner.DisplayName;

    public Task<OcrResponse> RecognizeAsync(
        Bitmap bitmap,
        string? preferredLanguageTag,
        CancellationToken cancellationToken,
        OcrRequestOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _worker.ExecuteAsync(
            token => _inner.RecognizeAsync(bitmap, preferredLanguageTag, token, options),
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _worker.Dispose();
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
