namespace RunJargon.App.Services;

public interface ITranslationPairWarmupService
{
    Task WarmUpPairAsync(string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken);
}
