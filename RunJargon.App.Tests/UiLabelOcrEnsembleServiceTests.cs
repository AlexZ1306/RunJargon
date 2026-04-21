using RunJargon.App.Services;

namespace RunJargon.App.Tests;

public class UiLabelOcrEnsembleServiceTests
{
    [Fact]
    public void SelectBestCandidate_PrefersConsensusCompactLabelOverFragmentedVariants()
    {
        var selected = UiLabelOcrEnsembleService.SelectBestCandidate(
            "P aits",
            [
                "P aits",
                "Parts",
                "Pa rts",
                "Parts",
                "Paits"
            ]);

        Assert.Equal("Parts", selected);
    }

    [Fact]
    public void SelectBestCandidate_PrefersUnfragmentedSingleWordForShortUiLabel()
    {
        var selected = UiLabelOcrEnsembleService.SelectBestCandidate(
            "B utton",
            [
                "B utton",
                "But ton",
                "Button"
            ]);

        Assert.Equal("Button", selected);
    }

    [Fact]
    public void SelectBestCandidate_KeepsOriginalWhenCandidatesAreNotBetter()
    {
        var selected = UiLabelOcrEnsembleService.SelectBestCandidate(
            "Archives",
            [
                "",
                "A rchives",
                "Ar chives"
            ]);

        Assert.Equal("Archives", selected);
    }
}
