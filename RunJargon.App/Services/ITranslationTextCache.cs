namespace RunJargon.App.Services;

public interface ITranslationTextCache
{
    bool TryGet(
        string? sourceLanguage,
        string targetLanguage,
        string normalizedSourceText,
        out string translatedText);

    void Set(
        string? sourceLanguage,
        string targetLanguage,
        string normalizedSourceText,
        string translatedText);
}
