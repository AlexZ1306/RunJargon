using RunJargon.App.Services;

namespace RunJargon.App.Tests;

public sealed class TranslationTextCacheTests
{
    [Fact]
    public void TryGet_ReturnsCachedValue_ForMatchingLanguagePairAndText()
    {
        var cache = new TranslationTextCache(4);
        cache.Set("en", "ru", "hello", "привет");

        var found = cache.TryGet("en", "ru", "hello", out var value);

        Assert.True(found);
        Assert.Equal("привет", value);
    }

    [Fact]
    public void Set_EvictsLeastRecentlyUsedItem_WhenCapacityExceeded()
    {
        var cache = new TranslationTextCache(2);
        cache.Set("en", "ru", "one", "один");
        cache.Set("en", "ru", "two", "два");
        cache.TryGet("en", "ru", "one", out _);
        cache.Set("en", "ru", "three", "три");

        Assert.True(cache.TryGet("en", "ru", "one", out _));
        Assert.False(cache.TryGet("en", "ru", "two", out _));
        Assert.True(cache.TryGet("en", "ru", "three", out _));
    }
}
