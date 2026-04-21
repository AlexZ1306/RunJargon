using System.Drawing;
using RunJargon.App.Models;

namespace RunJargon.App.Services;

public interface IOcrService
{
    string DisplayName { get; }

    Task<OcrResponse> RecognizeAsync(
        Bitmap bitmap,
        string? preferredLanguageTag,
        CancellationToken cancellationToken,
        OcrRequestOptions? options = null);
}
