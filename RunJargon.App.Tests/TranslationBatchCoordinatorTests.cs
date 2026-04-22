using RunJargon.App.Models;
using RunJargon.App.Services;

namespace RunJargon.App.Tests;

public sealed class TranslationBatchCoordinatorTests
{
    [Fact]
    public async Task TranslateAsync_UsesBatchServiceWhenAvailable()
    {
        var service = new FakeBatchTranslationService();

        var results = await TranslationBatchCoordinator.TranslateAsync(
            service,
            ["one", "two", "three"],
            "en",
            "ru",
            CancellationToken.None);

        Assert.Equal(1, service.BatchCallCount);
        Assert.Equal(0, service.SingleCallCount);
        Assert.Equal(["batch:one", "batch:two", "batch:three"], results.Select(item => item.TranslatedText).ToArray());
    }

    [Fact]
    public async Task TranslateAsync_FallsBackToSequentialCalls()
    {
        var service = new FakeSequentialTranslationService();

        var results = await TranslationBatchCoordinator.TranslateAsync(
            service,
            ["alpha", "beta"],
            "en",
            "ru",
            CancellationToken.None);

        Assert.Equal(2, service.CallCount);
        Assert.Equal(["single:alpha", "single:beta"], results.Select(item => item.TranslatedText).ToArray());
    }

    [Fact]
    public async Task TranslateAsync_UsesCacheBeforeCallingService()
    {
        var service = new FakeBatchTranslationService();
        var cache = new TranslationTextCache(8);
        cache.Set("en", "ru", "one", "cached:one");

        var results = await TranslationBatchCoordinator.TranslateAsync(
            service,
            ["one", "two"],
            "en",
            "ru",
            CancellationToken.None,
            cache);

        Assert.Equal(1, service.BatchCallCount);
        Assert.Equal(["cached:one", "batch:two"], results.Select(item => item.TranslatedText).ToArray());
    }

    private sealed class FakeBatchTranslationService : ITranslationService, IBatchTranslationService
    {
        public int BatchCallCount { get; private set; }

        public int SingleCallCount { get; private set; }

        public string DisplayName => "Fake batch";

        public string ConfigurationHint => string.Empty;

        public Task<TranslationResponse> TranslateAsync(
            string text,
            string? sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken)
        {
            SingleCallCount++;
            return Task.FromResult(new TranslationResponse($"single:{text}", DisplayName, string.Empty));
        }

        public Task<IReadOnlyList<TranslationResponse>> TranslateBatchAsync(
            IReadOnlyList<string> texts,
            string? sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken)
        {
            BatchCallCount++;
            IReadOnlyList<TranslationResponse> responses = texts
                .Select(text => new TranslationResponse($"batch:{text}", DisplayName, string.Empty))
                .ToArray();
            return Task.FromResult(responses);
        }
    }

    private sealed class FakeSequentialTranslationService : ITranslationService
    {
        public int CallCount { get; private set; }

        public string DisplayName => "Fake single";

        public string ConfigurationHint => string.Empty;

        public Task<TranslationResponse> TranslateAsync(
            string text,
            string? sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new TranslationResponse($"single:{text}", DisplayName, string.Empty));
        }
    }
}
