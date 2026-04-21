using RunJargon.App.Utilities;

namespace RunJargon.App.Services;

public static class UiLabelTranslationRecoveryHeuristics
{
    public static bool ShouldRetryAfterTranslation(
        string sourceText,
        string translatedText,
        bool isDenseUiRow)
    {
        if (!isDenseUiRow)
        {
            return false;
        }

        var source = TextRegionIntelligence.NormalizeWhitespace(sourceText);
        var translated = TextRegionIntelligence.NormalizeWhitespace(translatedText);
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated))
        {
            return false;
        }

        var sourceWordCount = CountWords(source);
        if (sourceWordCount == 0 || sourceWordCount > 2 || source.Length > 22)
        {
            return false;
        }

        if (!source.Any(char.IsLetter))
        {
            return false;
        }

        var sourceCompact = BuildCompactKey(source);
        var translatedCompact = BuildCompactKey(translated);
        if (string.IsNullOrWhiteSpace(sourceCompact) || string.IsNullOrWhiteSpace(translatedCompact))
        {
            return false;
        }

        if (string.Equals(sourceCompact, translatedCompact, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var similarity = ComputeSimilarity(sourceCompact, translatedCompact);
        return similarity >= 0.84;
    }

    private static string BuildCompactKey(string text)
    {
        return new string(text
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static int CountWords(string text)
    {
        return text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }

    private static double ComputeSimilarity(string left, string right)
    {
        var maxLength = Math.Max(left.Length, right.Length);
        if (maxLength == 0)
        {
            return 1;
        }

        var distance = ComputeLevenshteinDistance(left, right);
        return 1.0 - (distance / (double)maxLength);
    }

    private static int ComputeLevenshteinDistance(string left, string right)
    {
        var rows = left.Length + 1;
        var columns = right.Length + 1;
        var matrix = new int[rows, columns];

        for (var i = 0; i < rows; i++)
        {
            matrix[i, 0] = i;
        }

        for (var j = 0; j < columns; j++)
        {
            matrix[0, j] = j;
        }

        for (var i = 1; i < rows; i++)
        {
            for (var j = 1; j < columns; j++)
            {
                var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(
                        matrix[i - 1, j] + 1,
                        matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + substitutionCost);
            }
        }

        return matrix[rows - 1, columns - 1];
    }
}
