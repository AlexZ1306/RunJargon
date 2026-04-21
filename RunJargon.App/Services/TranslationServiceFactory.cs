using RunJargon.App.Models;

namespace RunJargon.App.Services;

public static class TranslationServiceFactory
{
    public static ITranslationService Create(TranslationSettings? settings = null)
    {
        var hasConfiguredAzure = !string.IsNullOrWhiteSpace(settings?.AzureApiKey)
                                 || !string.IsNullOrWhiteSpace(ReadFirstNonEmpty("RUN_JARGON_TRANSLATOR_KEY", "TABTRANSLATE_TRANSLATOR_KEY", "AZURE_TRANSLATOR_KEY"));

        if (!hasConfiguredAzure && LocalArgosTranslationService.TryResolve(out var localArgosService))
        {
            return localArgosService!;
        }

        var key = FirstConfiguredValue(
            settings?.AzureApiKey,
            ReadFirstNonEmpty("RUN_JARGON_TRANSLATOR_KEY", "TABTRANSLATE_TRANSLATOR_KEY", "AZURE_TRANSLATOR_KEY"));

        var region = FirstConfiguredValue(
            settings?.AzureRegion,
            ReadFirstNonEmpty("RUN_JARGON_TRANSLATOR_REGION", "TABTRANSLATE_TRANSLATOR_REGION", "AZURE_TRANSLATOR_REGION"));

        var endpoint = FirstConfiguredValue(
            settings?.AzureEndpoint,
            ReadFirstNonEmpty("RUN_JARGON_TRANSLATOR_ENDPOINT", "TABTRANSLATE_TRANSLATOR_ENDPOINT", "AZURE_TRANSLATOR_ENDPOINT"))
            ?? "https://api.cognitive.microsofttranslator.com";

        if (!string.IsNullOrWhiteSpace(key))
        {
            return new AzureTranslatorService(key, region, endpoint);
        }

        if (LocalArgosTranslationService.TryResolve(out localArgosService))
        {
            return localArgosService!;
        }

        return new DemoTranslationService();
    }

    private static string? ReadFirstNonEmpty(params string[] variableNames)
    {
        foreach (var variableName in variableNames)
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? FirstConfiguredValue(string? explicitValue, string? fallbackValue)
    {
        if (!string.IsNullOrWhiteSpace(explicitValue))
        {
            return explicitValue;
        }

        return fallbackValue;
    }
}
