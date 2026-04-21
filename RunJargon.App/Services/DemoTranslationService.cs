using RunJargon.App.Models;

namespace RunJargon.App.Services;

public sealed class DemoTranslationService : ITranslationService
{
    public string DisplayName => "Demo translator";

    public string ConfigurationHint =>
        "Если офлайн Argos не найден, можно добавить ключ Azure Translator в форму ниже или через env vars.";

    public Task<TranslationResponse> TranslateAsync(
        string text,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var preview = string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : $"[fallback] {text}";

        return Task.FromResult(
            new TranslationResponse(
                preview,
                DisplayName,
                "Реальный API еще не подключен, поэтому сейчас выводится безопасный fallback для проверки UX."));
    }
}
