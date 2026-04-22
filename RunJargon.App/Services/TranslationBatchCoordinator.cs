using RunJargon.App.Models;

namespace RunJargon.App.Services;

public static class TranslationBatchCoordinator
{
    public static async Task<IReadOnlyList<TranslationResponse>> TranslateAsync(
        ITranslationService translationService,
        IReadOnlyList<string> texts,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken,
        ITranslationTextCache? cache = null)
    {
        if (texts.Count == 0)
        {
            return Array.Empty<TranslationResponse>();
        }

        var results = new TranslationResponse[texts.Count];
        var missingTexts = new List<string>();
        var missingIndexes = new List<int>();

        for (var index = 0; index < texts.Count; index++)
        {
            var normalizedText = texts[index];
            if (cache is not null
                && cache.TryGet(sourceLanguage, targetLanguage, normalizedText, out var cachedTranslatedText))
            {
                results[index] = new TranslationResponse(cachedTranslatedText, translationService.DisplayName, null);
                continue;
            }

            missingTexts.Add(normalizedText);
            missingIndexes.Add(index);
        }

        if (missingTexts.Count == 0)
        {
            return results;
        }

        if (translationService is IBatchTranslationService batchTranslationService)
        {
            var batchResponses = await batchTranslationService.TranslateBatchAsync(
                missingTexts,
                sourceLanguage,
                targetLanguage,
                cancellationToken);

            for (var index = 0; index < missingIndexes.Count; index++)
            {
                var sourceIndex = missingIndexes[index];
                var response = index < batchResponses.Count
                    ? batchResponses[index]
                    : new TranslationResponse(string.Empty, translationService.DisplayName, null);
                results[sourceIndex] = response;
                cache?.Set(sourceLanguage, targetLanguage, texts[sourceIndex], response.TranslatedText);
            }

            return results;
        }

        for (var index = 0; index < missingIndexes.Count; index++)
        {
            var sourceIndex = missingIndexes[index];
            var response = await translationService.TranslateAsync(
                texts[sourceIndex],
                sourceLanguage,
                targetLanguage,
                cancellationToken);
            results[sourceIndex] = response;
            cache?.Set(sourceLanguage, targetLanguage, texts[sourceIndex], response.TranslatedText);
        }

        return results;
    }
}
