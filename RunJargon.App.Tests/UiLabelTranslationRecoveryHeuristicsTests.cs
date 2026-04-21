using RunJargon.App.Services;

namespace RunJargon.App.Tests;

public class UiLabelTranslationRecoveryHeuristicsTests
{
    [Fact]
    public void ShouldRetryAfterTranslation_ReturnsTrueForNearNoOpDenseUiTranslation()
    {
        var shouldRetry = UiLabelTranslationRecoveryHeuristics.ShouldRetryAfterTranslation(
            "P aits",
            "P. Aits",
            isDenseUiRow: true);

        Assert.True(shouldRetry);
    }

    [Fact]
    public void ShouldRetryAfterTranslation_ReturnsTrueForUnchangedDenseUiTranslation()
    {
        var shouldRetry = UiLabelTranslationRecoveryHeuristics.ShouldRetryAfterTranslation(
            "Meclialnfc",
            "Meclialnfc",
            isDenseUiRow: true);

        Assert.True(shouldRetry);
    }

    [Fact]
    public void ShouldRetryAfterTranslation_ReturnsFalseForActualTranslation()
    {
        var shouldRetry = UiLabelTranslationRecoveryHeuristics.ShouldRetryAfterTranslation(
            "Archives",
            "Архивы",
            isDenseUiRow: true);

        Assert.False(shouldRetry);
    }

    [Fact]
    public void ShouldRetryAfterTranslation_ReturnsFalseOutsideDenseUiRows()
    {
        var shouldRetry = UiLabelTranslationRecoveryHeuristics.ShouldRetryAfterTranslation(
            "Camo Studio",
            "Camo Studio",
            isDenseUiRow: false);

        Assert.False(shouldRetry);
    }
}
