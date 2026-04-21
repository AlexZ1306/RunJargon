using RunJargon.App.Models;

namespace RunJargon.App.Services;

public interface IBatchTranslationService
{
    Task<IReadOnlyList<TranslationResponse>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}
