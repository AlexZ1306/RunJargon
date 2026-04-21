using RunJargon.App.Models;

namespace RunJargon.App.Services;

public static class TranslationBatchCoordinator
{
    public static async Task<IReadOnlyList<TranslationResponse>> TranslateAsync(
        ITranslationService translationService,
        IReadOnlyList<string> texts,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return Array.Empty<TranslationResponse>();
        }

        if (translationService is IBatchTranslationService batchTranslationService)
        {
            return await batchTranslationService.TranslateBatchAsync(
                texts,
                sourceLanguage,
                targetLanguage,
                cancellationToken);
        }

        var results = new TranslationResponse[texts.Count];
        for (var index = 0; index < texts.Count; index++)
        {
            results[index] = await translationService.TranslateAsync(
                texts[index],
                sourceLanguage,
                targetLanguage,
                cancellationToken);
        }

        return results;
    }
}
