using System.Drawing;
using RunJargon.App.Models;

namespace RunJargon.App.Services;

public sealed class CompositeOcrService : IOcrService
{
    private readonly IReadOnlyList<IOcrService> _services;

    public CompositeOcrService(params IOcrService[] services)
    {
        _services = services
            .Where(service => service is not null)
            .ToArray();

        if (_services.Count == 0)
        {
            throw new ArgumentException("At least one OCR service must be configured.", nameof(services));
        }
    }

    public string DisplayName => _services.Count == 1
        ? _services[0].DisplayName
        : "Composite OCR";

    public async Task<OcrResponse> RecognizeAsync(
        Bitmap bitmap,
        string? preferredLanguageTag,
        CancellationToken cancellationToken,
        OcrRequestOptions? options = null)
    {
        var responses = new List<OcrResponse>(_services.Count);

        foreach (var service in _services)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await service.RecognizeAsync(bitmap, preferredLanguageTag, cancellationToken, options);
            if (response.Lines.Count == 0 && string.IsNullOrWhiteSpace(response.Text))
            {
                continue;
            }

            responses.Add(response);
        }

        if (responses.Count == 0)
        {
            return new OcrResponse(string.Empty, Array.Empty<OcrLineRegion>(), DisplayName);
        }

        if (responses.Count == 1)
        {
            return responses[0];
        }

        var orderedResponses = responses
            .OrderByDescending(response => OcrQualityHeuristics.ScoreResponse(response, preferredLanguageTag))
            .ToArray();

        var mergedLines = MergeLines(orderedResponses);
        var mergedText = mergedLines.Count > 0
            ? string.Join(Environment.NewLine, mergedLines.Select(line => Utilities.TextRegionIntelligence.NormalizeWhitespace(line.Text)))
            : orderedResponses[0].Text;

        return new OcrResponse(
            mergedText,
            mergedLines,
            $"Composite OCR ({string.Join(" + ", orderedResponses.Select(response => response.EngineName).Distinct())})");
    }

    private static IReadOnlyList<OcrLineRegion> MergeLines(IReadOnlyList<OcrResponse> responses)
    {
        var merged = new List<OcrLineRegion>();

        foreach (var response in responses)
        {
            foreach (var candidate in response.Lines)
            {
                var overlapIndex = merged.FindIndex(existing => IsSameLine(existing, candidate));
                if (overlapIndex < 0)
                {
                    merged.Add(candidate);
                    continue;
                }

                if (OcrQualityHeuristics.ScoreLineText(candidate.Text) > OcrQualityHeuristics.ScoreLineText(merged[overlapIndex].Text))
                {
                    merged[overlapIndex] = candidate;
                }
            }
        }

        return merged
            .OrderBy(line => line.Bounds.Top)
            .ThenBy(line => line.Bounds.Left)
            .ToArray();
    }

    private static bool IsSameLine(OcrLineRegion first, OcrLineRegion second)
    {
        var overlapLeft = Math.Max(first.Bounds.Left, second.Bounds.Left);
        var overlapTop = Math.Max(first.Bounds.Top, second.Bounds.Top);
        var overlapRight = Math.Min(first.Bounds.Right, second.Bounds.Right);
        var overlapBottom = Math.Min(first.Bounds.Bottom, second.Bounds.Bottom);

        var overlapWidth = Math.Max(0, overlapRight - overlapLeft);
        var overlapHeight = Math.Max(0, overlapBottom - overlapTop);
        var overlapArea = overlapWidth * overlapHeight;

        var firstArea = Math.Max(1, first.Bounds.Width * first.Bounds.Height);
        var secondArea = Math.Max(1, second.Bounds.Width * second.Bounds.Height);
        var overlapRatio = overlapArea / Math.Min(firstArea, secondArea);

        if (overlapRatio >= 0.55)
        {
            return true;
        }

        var centerDeltaY = Math.Abs(
            (first.Bounds.Top + (first.Bounds.Height / 2))
            - (second.Bounds.Top + (second.Bounds.Height / 2)));
        var leftDelta = Math.Abs(first.Bounds.Left - second.Bounds.Left);

        return centerDeltaY <= Math.Max(4, Math.Min(first.Bounds.Height, second.Bounds.Height) * 0.45)
               && leftDelta <= Math.Max(12, Math.Min(first.Bounds.Width, second.Bounds.Width) * 0.25)
               && string.Equals(first.Text.Trim(), second.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
