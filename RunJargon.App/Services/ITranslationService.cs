using RunJargon.App.Models;

namespace RunJargon.App.Services;

public interface ITranslationService
{
    string DisplayName { get; }

    string ConfigurationHint { get; }

    Task<TranslationResponse> TranslateAsync(
        string text,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}
