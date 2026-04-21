namespace RunJargon.App.Models;

public sealed record TranslationResponse(
    string TranslatedText,
    string ProviderName,
    string? Note);
