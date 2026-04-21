using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RunJargon.App.Models;

namespace RunJargon.App.Services;

public sealed class AzureTranslatorService : ITranslationService, IBatchTranslationService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string? _region;
    private readonly string _endpoint;

    public AzureTranslatorService(string apiKey, string? region, string endpoint)
    {
        _httpClient = new HttpClient();
        _apiKey = apiKey;
        _region = string.IsNullOrWhiteSpace(region) ? null : region;
        _endpoint = endpoint.TrimEnd('/');
    }

    public string DisplayName => "Azure Translator";

    public string ConfigurationHint =>
        "Ключ найден. Приложение будет отправлять в облако только распознанный текст, а не снимок экрана.";

    public async Task<TranslationResponse> TranslateAsync(
        string text,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var responses = await TranslateBatchInternalAsync(
            [text],
            sourceLanguage,
            targetLanguage,
            cancellationToken);

        return responses[0];
    }

    public Task<IReadOnlyList<TranslationResponse>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        return TranslateBatchInternalAsync(texts, sourceLanguage, targetLanguage, cancellationToken);
    }

    private async Task<IReadOnlyList<TranslationResponse>> TranslateBatchInternalAsync(
        IReadOnlyList<string> texts,
        string? sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return Array.Empty<TranslationResponse>();
        }

        var requestUri = new StringBuilder($"{_endpoint}/translate?api-version=3.0");
        if (!string.IsNullOrWhiteSpace(sourceLanguage))
        {
            requestUri.Append("&from=").Append(Uri.EscapeDataString(sourceLanguage));
        }

        requestUri.Append("&to=").Append(Uri.EscapeDataString(targetLanguage));

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri.ToString());
        request.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);

        if (!string.IsNullOrWhiteSpace(_region))
        {
            request.Headers.Add("Ocp-Apim-Subscription-Region", _region);
        }

        request.Headers.Add("X-ClientTraceId", Guid.NewGuid().ToString());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var payload = JsonSerializer.Serialize(texts.Select(text => new AzureTranslationRequest(text)).ToArray());
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure Translator вернул ошибку {(int)response.StatusCode}: {body}");
        }

        var items = JsonSerializer.Deserialize<List<AzureTranslationEnvelope>>(body);
        var results = new List<TranslationResponse>(texts.Count);
        for (var index = 0; index < texts.Count; index++)
        {
            var translated = items is not null && index < items.Count
                ? items[index].Translations?.FirstOrDefault()?.Text ?? string.Empty
                : string.Empty;
            results.Add(new TranslationResponse(
                translated,
                DisplayName,
                "Облако получило только текст из OCR."));
        }

        return results;
    }

    private sealed record AzureTranslationRequest(string Text);

    private sealed record AzureTranslationEnvelope(List<AzureTranslationItem>? Translations);

    private sealed record AzureTranslationItem(string Text, string To);
}
