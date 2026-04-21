namespace RunJargon.App.Models;

public sealed record TranslationSettings(
    string? AzureApiKey,
    string? AzureRegion,
    string? AzureEndpoint);
