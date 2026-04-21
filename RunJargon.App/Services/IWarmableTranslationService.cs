namespace RunJargon.App.Services;

public interface IWarmableTranslationService
{
    Task WarmUpAsync(CancellationToken cancellationToken);
}
