using RunJargon.App.Models;

namespace RunJargon.App.Services;

internal static class OcrQualityHeuristics
{
    internal static int ScoreResponse(OcrResponse response, string? preferredLanguageTag)
    {
        if (string.IsNullOrWhiteSpace(response.Text) || response.Lines.Count == 0)
        {
            return int.MinValue;
        }

        var text = Utilities.TextRegionIntelligence.NormalizeWhitespace(response.Text);
        var lettersOrDigits = text.Count(char.IsLetterOrDigit);
        var whitespace = text.Count(char.IsWhiteSpace);
        var suspicious = text.Count(ch => ch is '@' or '�' or '¤' or '§');
        var mixedScriptPenalty = CountMixedScriptTokens(text) * 28;
        var brokenTokenPenalty = CountBrokenTokens(text) * 20;
        var veryShortTokenPenalty = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(token => token.Length == 1) * 6;

        var score = (lettersOrDigits * 3)
                    + whitespace
                    - (suspicious * 15)
                    - mixedScriptPenalty
                    - brokenTokenPenalty
                    - veryShortTokenPenalty
                    + (response.Lines.Count * 10);

        var guessedLanguage = Utilities.TextRegionIntelligence.GuessSourceLanguage(text);
        var normalizedPreferredLanguage = NormalizeLanguageTag(preferredLanguageTag);
        if (!string.IsNullOrWhiteSpace(guessedLanguage)
            && string.Equals(guessedLanguage, normalizedPreferredLanguage, StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }

        return score;
    }

    internal static int ScoreLineText(string text)
    {
        var normalized = Utilities.TextRegionIntelligence.NormalizeWhitespace(text);
        var lettersOrDigits = normalized.Count(char.IsLetterOrDigit);
        var suspicious = normalized.Count(ch => ch is '@' or '�' or '¤' or '§');
        var mixedScriptPenalty = CountMixedScriptTokens(normalized) * 24;
        var brokenTokenPenalty = CountBrokenTokens(normalized) * 18;
        return (lettersOrDigits * 3) - (suspicious * 12) - mixedScriptPenalty - brokenTokenPenalty;
    }

    private static string? NormalizeLanguageTag(string? languageTag)
    {
        return languageTag switch
        {
            null or "" => null,
            "en-US" => "en",
            "ru-RU" => "ru",
            "de-DE" => "de",
            "ja-JP" => "ja",
            "zh-CN" => "zh-Hans",
            _ => languageTag
        };
    }

    private static int CountMixedScriptTokens(string text)
    {
        return text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(token => HasMixedScripts(token));
    }

    private static int CountBrokenTokens(string text)
    {
        return text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(IsBrokenToken);
    }

    private static bool HasMixedScripts(string token)
    {
        var hasLatin = token.Any(ch => ch is >= '\u0041' and <= '\u024F');
        var hasCyrillic = token.Any(ch => ch is >= '\u0400' and <= '\u052F');
        var hasKana = token.Any(ch => ch is >= '\u3040' and <= '\u30FF');
        var hasHan = token.Any(ch => ch is >= '\u3400' and <= '\u9FFF');

        var scriptCount = 0;
        scriptCount += hasLatin ? 1 : 0;
        scriptCount += hasCyrillic ? 1 : 0;
        scriptCount += hasKana ? 1 : 0;
        scriptCount += hasHan ? 1 : 0;
        return scriptCount > 1;
    }

    private static bool IsBrokenToken(string token)
    {
        if (token.Length <= 2)
        {
            return false;
        }

        var letters = token.Count(char.IsLetter);
        var digits = token.Count(char.IsDigit);
        var punctuation = token.Count(char.IsPunctuation);

        if (letters == 0)
        {
            return true;
        }

        if (digits >= 2 && letters >= 2)
        {
            return true;
        }

        return punctuation >= 2;
    }
}
