using RunJargon.App.Models;

namespace RunJargon.App.Services;

public static class TranslationLanguageCatalog
{
    private static readonly IReadOnlyList<TranslationLanguageOption> SourceLanguages =
    [
        new(string.Empty, "Определить язык"),
        new("en", "English"),
        new("de", "Deutsch"),
        new("ja", "Japanese"),
        new("zh-Hans", "Chinese Simplified"),
        new("ru", "Русский")
    ];

    private static readonly IReadOnlyList<TranslationLanguageOption> TargetLanguages =
    [
        new("ru", "Русский"),
        new("en", "English"),
        new("de", "Deutsch"),
        new("ja", "Japanese"),
        new("zh-Hans", "Chinese Simplified")
    ];

    public static IReadOnlyList<TranslationLanguageOption> GetSourceLanguages()
    {
        return SourceLanguages;
    }

    public static IReadOnlyList<TranslationLanguageOption> GetTargetLanguages()
    {
        return TargetLanguages;
    }

    public static string? ResolvePreferredOcrLanguageTag(string? languageCode)
    {
        return languageCode switch
        {
            null or "" => null,
            "en" => "en-US",
            "ru" => "ru-RU",
            "de" => "de-DE",
            "ja" => "ja-JP",
            "zh-Hans" => "zh-CN",
            _ => null
        };
    }

    public static TranslationLanguageOption GetDefaultSourceLanguage()
    {
        return SourceLanguages[0];
    }

    public static TranslationLanguageOption GetDefaultTargetLanguage()
    {
        return TargetLanguages[0];
    }

    public static TranslationLanguageOption? FindSourceLanguage(string? code)
    {
        return FindByCode(SourceLanguages, code);
    }

    public static TranslationLanguageOption? FindTargetLanguage(string? code)
    {
        return FindByCode(TargetLanguages, code);
    }

    private static TranslationLanguageOption? FindByCode(
        IReadOnlyList<TranslationLanguageOption> languages,
        string? code)
    {
        var effectiveCode = code ?? string.Empty;
        return languages.FirstOrDefault(language => string.Equals(
            language.Code,
            effectiveCode,
            StringComparison.OrdinalIgnoreCase));
    }
}
