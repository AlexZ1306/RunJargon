using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using RunJargon.App.Models;
using RunJargon.App.Utilities;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace RunJargon.App.Windows;

public partial class TranslationOverlayWindow : Window
{
    private readonly CaptureSessionResult _sessionResult;
    private readonly Bitmap _backgroundBitmap;
    private readonly Bitmap _sourceBitmap;

    public TranslationOverlayWindow(CaptureSessionResult sessionResult)
    {
        _sessionResult = sessionResult;
        _backgroundBitmap = CreateSnapshotBitmap(GetBackgroundBytes(sessionResult));
        _sourceBitmap = CreateSnapshotBitmap(sessionResult.SnapshotPngBytes);

        InitializeComponent();

        Left = sessionResult.Region.Left;
        Top = sessionResult.Region.Top;
        Width = sessionResult.Region.Width;
        Height = sessionResult.Region.Height;

        Loaded += TranslationOverlayWindow_Loaded;
        Closed += TranslationOverlayWindow_Closed;
    }

    private void TranslationOverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SnapshotImage.Source = CreateSnapshotImage(GetBackgroundBytes(_sessionResult));
        RenderOverlay();
    }

    private void TranslationOverlayWindow_Closed(object? sender, EventArgs e)
    {
        _backgroundBitmap.Dispose();
        _sourceBitmap.Dispose();
    }

    private void RenderOverlay()
    {
        OverlayCanvas.Children.Clear();

        var orderedLines = _sessionResult.OverlayLines
            .OrderBy(item => item.Bounds.Top)
            .ThenBy(item => item.Bounds.Left)
            .ToList();

        for (var index = 0; index < orderedLines.Count; index++)
        {
            var line = orderedLines[index];
            if (string.IsNullOrWhiteSpace(line.TranslatedText) || line.Bounds.IsEmpty)
            {
                continue;
            }

            var targetFontSize = line.PreferredFontSize > 0
                ? line.PreferredFontSize
                : EstimateTargetFontSize(line);
            var nextTop = index < orderedLines.Count - 1
                ? orderedLines[index + 1].Bounds.Top
                : Height - 6;

            var allowWrap = line.LayoutKind == TextLayoutKind.Paragraph || line.SourceLineCount > 1;
            var placement = CreatePlacement(line, targetFontSize, allowWrap);
            if (placement.Width <= 10 || placement.Height <= 6)
            {
                continue;
            }

            if (!allowWrap && line.LayoutKind != TextLayoutKind.UiLabel)
            {
                placement = TryPromoteToWrappedPlacement(line, placement, targetFontSize, nextTop);
                allowWrap = placement.Height > (line.Bounds.Height * 1.6);
            }

            var sample = SampleBackground(line.Bounds);
            var background = CreateOverlayBackground(sample);
            var defaultForegroundColor = TryExtractSourceTextColor(line, sample, out var extractedColor)
                ? extractedColor
                : ChooseForegroundColor(sample);
            var foreground = new SolidColorBrush(defaultForegroundColor);
            var shadowColor = CreateShadowColor(sample);
            var fontSize = FindBestFontSize(
                line.SourceText,
                line.TranslatedText,
                placement.Width - 8,
                placement.Height - 4,
                targetFontSize,
                allowWrap);

            var fittedPlacement = FitPlacementToText(
                line,
                placement,
                fontSize,
                allowWrap,
                nextTop);
            placement = fittedPlacement;

            var border = new Border
            {
                Width = placement.Width,
                Height = placement.Height,
                Background = background,
                CornerRadius = new CornerRadius(Math.Min(4, placement.Height / 2)),
                Padding = line.LayoutKind switch
                {
                    TextLayoutKind.Paragraph => new Thickness(4, 2, 4, 2),
                    TextLayoutKind.UiLabel => new Thickness(2, 1, 2, 1),
                    _ => allowWrap ? new Thickness(4, 2, 4, 2) : new Thickness(2, 1, 2, 1)
                }
            };

            var textBlock = CreateTextBlock(
                line,
                sample,
                foreground,
                shadowColor,
                fontSize,
                allowWrap);

            border.Child = textBlock;

            Canvas.SetLeft(border, placement.Left);
            Canvas.SetTop(border, placement.Top);
            OverlayCanvas.Children.Add(border);
        }
    }

    private Rect CreatePlacement(TranslatedOcrLine line, double targetFontSize, bool allowWrap)
    {
        return allowWrap
            ? CreateWrappedPlacement(line, targetFontSize)
            : CreateSingleLinePlacement(line, targetFontSize);
    }

    private Rect CreateSingleLinePlacement(TranslatedOcrLine line, double targetFontSize)
    {
        var top = Math.Max(0, line.Bounds.Top - 1);
        var leftPadding = 2d;
        var rightGutter = top < 30 ? 34d : 6d;
        var preferredLeft = Math.Max(0, line.Bounds.Left - leftPadding);

        var measuredWidth = MeasureTextWidth(line.TranslatedText, targetFontSize);
        var lengthRatio = (double)Math.Max(1, line.TranslatedText.Length) / Math.Max(1, line.SourceText.Length);
        var ratioWidth = line.Bounds.Width * Math.Clamp(lengthRatio * 0.98, 1.12, 2.9);
        var desiredWidth = Math.Max(line.Bounds.Width + 10, Math.Max(measuredWidth + 18, ratioWidth));

        var maxWidthFromPreferredLeft = Math.Max(40, Width - preferredLeft - rightGutter);
        var width = Math.Min(desiredWidth, maxWidthFromPreferredLeft);
        var left = preferredLeft;

        if (desiredWidth > maxWidthFromPreferredLeft)
        {
            var anchoredLeft = Math.Max(0, line.Bounds.Right - desiredWidth - leftPadding);
            var maxWidthFromAnchoredLeft = Math.Max(40, Width - anchoredLeft - rightGutter);
            if (maxWidthFromAnchoredLeft > maxWidthFromPreferredLeft)
            {
                left = anchoredLeft;
                width = Math.Min(desiredWidth, maxWidthFromAnchoredLeft);
            }
        }

        var height = Math.Min(
            Height - top,
            Math.Max(12, Math.Max(line.Bounds.Height + 2, targetFontSize * 1.34)));

        width = Math.Max(10, width);

        return new Rect(left, top, width, height);
    }

    private Rect CreateWrappedPlacement(TranslatedOcrLine line, double targetFontSize)
    {
        var top = Math.Max(0, line.Bounds.Top - 1);
        var left = Math.Max(0, line.Bounds.Left - 3);
        var rightGutter = top < 30 ? 34d : 8d;
        var maxWidth = Math.Max(80, Width - left - rightGutter);
        var width = Math.Min(maxWidth, Math.Max(line.Bounds.Width + 22, line.Bounds.Width * 1.18));
        var height = Math.Min(
            Height - top,
            Math.Max(
                line.Bounds.Height + 6,
                (targetFontSize * 1.38 * Math.Max(2, line.SourceLineCount)) + 8));

        return new Rect(left, top, width, height);
    }

    private Rect TryPromoteToWrappedPlacement(
        TranslatedOcrLine line,
        Rect currentPlacement,
        double targetFontSize,
        double nextTop)
    {
        var singleLineWidth = MeasureTextWidth(line.TranslatedText, targetFontSize);
        var availableWidth = Math.Max(10, currentPlacement.Width - 8);
        if (singleLineWidth <= availableWidth)
        {
            return currentPlacement;
        }

        var availableHeight = Math.Max(0, nextTop - currentPlacement.Top - 2);
        if (availableHeight < targetFontSize * 2.1)
        {
            return currentPlacement;
        }

        var wrappedSize = MeasureText(line.TranslatedText, targetFontSize, availableWidth, true);
        var height = Math.Min(availableHeight, Math.Max(currentPlacement.Height, wrappedSize.Height + 4));

        if (height <= currentPlacement.Height + 2)
        {
            return currentPlacement;
        }

        return new Rect(currentPlacement.Left, currentPlacement.Top, currentPlacement.Width, height);
    }

    private Rect FitPlacementToText(
        TranslatedOcrLine line,
        Rect placement,
        double fontSize,
        bool allowWrap,
        double nextTop)
    {
        var measured = MeasureText(
            line.TranslatedText,
            fontSize,
            Math.Max(10, placement.Width - 10),
            allowWrap);

        if (!allowWrap)
        {
            return placement;
        }

        var availableHeight = Math.Max(placement.Height, nextTop - placement.Top - 2);
        var desiredHeight = Math.Min(
            availableHeight,
            Math.Max(placement.Height, measured.Height + 8));

        return new Rect(placement.Left, placement.Top, placement.Width, desiredHeight);
    }

    private SolidColorBrush CreateOverlayBackground(System.Drawing.Color sampled)
    {
        var color = System.Windows.Media.Color.FromArgb(
            96,
            sampled.R,
            sampled.G,
            sampled.B);

        return new SolidColorBrush(color);
    }

    private TextBlock CreateTextBlock(
        TranslatedOcrLine line,
        System.Drawing.Color sampledBackground,
        SolidColorBrush defaultForeground,
        System.Windows.Media.Color shadowColor,
        double fontSize,
        bool allowWrap)
    {
        var hasInlineRuns = line.InlineRuns is { Count: > 0 };
        var textBlock = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = hasInlineRuns
                ? (fontSize >= 16 ? FontWeights.Medium : FontWeights.Normal)
                : (fontSize >= 13 ? FontWeights.SemiBold : FontWeights.Medium),
            Foreground = defaultForeground,
            TextWrapping = allowWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None,
            VerticalAlignment = allowWrap ? VerticalAlignment.Top : VerticalAlignment.Center,
            LineHeight = allowWrap ? fontSize * 1.24 : double.NaN,
            Effect = new DropShadowEffect
            {
                BlurRadius = 3,
                ShadowDepth = 0,
                Opacity = 0.55,
                Color = shadowColor
            }
        };

        if (!hasInlineRuns)
        {
            textBlock.Text = line.TranslatedText;
            return textBlock;
        }

        foreach (var run in line.InlineRuns!)
        {
            var runForeground = defaultForeground;
            if (run.PreserveSourceColor
                && TryExtractStyledRunColor(run, sampledBackground, out var runColor))
            {
                runForeground = new SolidColorBrush(runColor);
            }

            textBlock.Inlines.Add(new Run(run.Text)
            {
                Foreground = runForeground,
                FontWeight = run.PreserveBold ? FontWeights.SemiBold : textBlock.FontWeight
            });
        }

        return textBlock;
    }

    private static System.Windows.Media.Color ChooseForegroundColor(System.Drawing.Color sampled)
    {
        var luminance = (sampled.R * 0.299) + (sampled.G * 0.587) + (sampled.B * 0.114);
        return luminance < 140
            ? System.Windows.Media.Color.FromRgb(248, 250, 252)
            : System.Windows.Media.Color.FromRgb(15, 23, 42);
    }

    private static System.Windows.Media.Color CreateShadowColor(System.Drawing.Color sampled)
    {
        var luminance = (sampled.R * 0.299) + (sampled.G * 0.587) + (sampled.B * 0.114);
        return luminance < 140
            ? System.Windows.Media.Color.FromArgb(200, 15, 23, 42)
            : System.Windows.Media.Color.FromArgb(180, 248, 250, 252);
    }

    private double FindBestFontSize(
        string sourceText,
        string translatedText,
        double availableWidth,
        double availableHeight,
        double seed,
        bool allowWrap)
    {
        var fontSize = Math.Clamp(seed, 10.5, 24);
        var minimum = Math.Max(10.5, fontSize * 0.84);

        while (fontSize > minimum)
        {
            var measuredSize = MeasureText(translatedText, fontSize, availableWidth, allowWrap);
            if (measuredSize.Width <= availableWidth && measuredSize.Height <= availableHeight)
            {
                break;
            }

            fontSize -= 0.5;
        }

        if (fontSize <= minimum && translatedText.Length <= sourceText.Length + 4)
        {
            return Math.Round(Math.Max(minimum, seed * 0.9), 1);
        }

        return Math.Max(minimum, Math.Round(fontSize, 1));
    }

    private static double EstimateTargetFontSize(TranslatedOcrLine line)
    {
        var estimated = line.Bounds.Height * 0.94;
        return Math.Clamp(estimated, 11.5, 26);
    }

    private static double MeasureTextWidth(string text, double fontSize)
    {
        return MeasureText(text, fontSize, double.PositiveInfinity, false).Width;
    }

    private static System.Windows.Size MeasureText(
        string text,
        double fontSize,
        double availableWidth,
        bool allowWrap)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = fontSize >= 13 ? FontWeights.SemiBold : FontWeights.Medium,
            TextWrapping = allowWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None
        };

        var measureWidth = double.IsInfinity(availableWidth) ? double.PositiveInfinity : Math.Max(10, availableWidth);
        textBlock.Measure(new System.Windows.Size(measureWidth, double.PositiveInfinity));
        return textBlock.DesiredSize;
    }

    private System.Drawing.Color SampleBackground(ScreenRegion bounds)
    {
        var x0 = Math.Clamp((int)Math.Floor(bounds.Left), 0, Math.Max(0, _backgroundBitmap.Width - 1));
        var y0 = Math.Clamp((int)Math.Floor(bounds.Top), 0, Math.Max(0, _backgroundBitmap.Height - 1));
        var x1 = Math.Clamp((int)Math.Ceiling(bounds.Right), 0, _backgroundBitmap.Width);
        var y1 = Math.Clamp((int)Math.Ceiling(bounds.Bottom), 0, _backgroundBitmap.Height);

        if (x1 <= x0 || y1 <= y0)
        {
            return System.Drawing.Color.FromArgb(245, 248, 250);
        }

        long r = 0;
        long g = 0;
        long b = 0;
        long count = 0;
        var step = Math.Max(1, Math.Min((x1 - x0) / 18, (y1 - y0) / 4));

        for (var y = y0; y < y1; y += step)
        {
            for (var x = x0; x < x1; x += step)
            {
                var pixel = _backgroundBitmap.GetPixel(x, y);
                r += pixel.R;
                g += pixel.G;
                b += pixel.B;
                count++;
            }
        }

        if (count == 0)
        {
            return System.Drawing.Color.FromArgb(245, 248, 250);
        }

        return System.Drawing.Color.FromArgb(
            (int)(r / count),
            (int)(g / count),
            (int)(b / count));
    }

    private bool TryExtractSourceTextColor(
        TranslatedOcrLine line,
        System.Drawing.Color sampledBackground,
        out System.Windows.Media.Color textColor)
    {
        textColor = ChooseForegroundColor(sampledBackground);
        if (!ShouldUseSourceTextColor(line))
        {
            return false;
        }

        return TryExtractTextColorFromBounds(line.Bounds, sampledBackground, out textColor);
    }

    private bool TryExtractStyledRunColor(
        TranslatedInlineRun run,
        System.Drawing.Color sampledBackground,
        out System.Windows.Media.Color textColor)
    {
        textColor = ChooseForegroundColor(sampledBackground);
        if (run.SourceBounds is not ScreenRegion bounds || bounds.IsEmpty)
        {
            return false;
        }

        var localBackground = SampleBackground(bounds);
        return TryExtractTextColorFromBounds(bounds, localBackground, out textColor);
    }

    private bool TryExtractTextColorFromBounds(
        ScreenRegion bounds,
        System.Drawing.Color sampledBackground,
        out System.Windows.Media.Color textColor)
    {
        textColor = ChooseForegroundColor(sampledBackground);

        var x0 = Math.Clamp((int)Math.Floor(bounds.Left), 0, Math.Max(0, _sourceBitmap.Width - 1));
        var y0 = Math.Clamp((int)Math.Floor(bounds.Top), 0, Math.Max(0, _sourceBitmap.Height - 1));
        var x1 = Math.Clamp((int)Math.Ceiling(bounds.Right), 0, _sourceBitmap.Width);
        var y1 = Math.Clamp((int)Math.Ceiling(bounds.Bottom), 0, _sourceBitmap.Height);

        if (x1 <= x0 || y1 <= y0)
        {
            return false;
        }

        long weightedR = 0;
        long weightedG = 0;
        long weightedB = 0;
        long weightSum = 0;
        var changedPixelCount = 0;
        var sampledPixelCount = 0;

        var width = x1 - x0;
        var height = y1 - y0;
        var stepX = width > 260 ? 2 : 1;
        var stepY = height > 120 ? 2 : 1;

        for (var y = y0; y < y1; y += stepY)
        {
            for (var x = x0; x < x1; x += stepX)
            {
                sampledPixelCount++;

                var original = _sourceBitmap.GetPixel(x, y);
                var background = _backgroundBitmap.GetPixel(x, y);
                var difference = ComputeColorDistance(original, background);
                if (difference < 26)
                {
                    continue;
                }

                changedPixelCount++;
                var weight = difference * difference;
                weightedR += original.R * weight;
                weightedG += original.G * weight;
                weightedB += original.B * weight;
                weightSum += weight;
            }
        }

        if (weightSum <= 0 || sampledPixelCount == 0)
        {
            return false;
        }

        var changedRatio = (double)changedPixelCount / sampledPixelCount;
        if (changedPixelCount < 5 || changedRatio < 0.008 || changedRatio > 0.78)
        {
            return false;
        }

        var average = System.Drawing.Color.FromArgb(
            (int)(weightedR / weightSum),
            (int)(weightedG / weightSum),
            (int)(weightedB / weightSum));

        var contrast = ComputeColorDistance(average, sampledBackground);
        if (contrast < 42)
        {
            return false;
        }

        long dominantWeight = 0;
        long deviationWeightedSum = 0;

        for (var y = y0; y < y1; y += stepY)
        {
            for (var x = x0; x < x1; x += stepX)
            {
                var original = _sourceBitmap.GetPixel(x, y);
                var background = _backgroundBitmap.GetPixel(x, y);
                var difference = ComputeColorDistance(original, background);
                if (difference < 26)
                {
                    continue;
                }

                var weight = difference * difference;
                var deviation = ComputeColorDistance(original, average);
                deviationWeightedSum += deviation * weight;
                if (deviation <= 58)
                {
                    dominantWeight += weight;
                }
            }
        }

        var averageDeviation = (double)deviationWeightedSum / weightSum;
        var dominantRatio = (double)dominantWeight / weightSum;
        if (averageDeviation > 68 || dominantRatio < 0.46)
        {
            return false;
        }

        textColor = System.Windows.Media.Color.FromRgb(average.R, average.G, average.B);
        return true;
    }

    private static bool ShouldUseSourceTextColor(TranslatedOcrLine line)
    {
        if (line.SourceLineCount > 1)
        {
            return false;
        }

        var normalized = line.SourceText.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized.Length <= 56)
        {
            return true;
        }

        var wordCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (line.Bounds.Height >= 20 && wordCount <= 10)
        {
            return true;
        }

        return false;
    }

    private static int ComputeColorDistance(System.Drawing.Color left, System.Drawing.Color right)
    {
        return Math.Abs(left.R - right.R)
               + Math.Abs(left.G - right.G)
               + Math.Abs(left.B - right.B);
    }

    private static BitmapImage CreateSnapshotImage(byte[] pngBytes)
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(pngBytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static Bitmap CreateSnapshotBitmap(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes);
        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }

    private static byte[] GetBackgroundBytes(CaptureSessionResult sessionResult)
    {
        return sessionResult.PreparedBackgroundPngBytes.Length > 0
            ? sessionResult.PreparedBackgroundPngBytes
            : sessionResult.SnapshotPngBytes;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }
}
