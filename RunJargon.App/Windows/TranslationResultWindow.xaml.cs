using System.Windows;
using System.Windows.Input;
using RunJargon.App.Models;
using RunJargon.App.Utilities;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace RunJargon.App.Windows;

public partial class TranslationResultWindow : Window
{
    public TranslationResultWindow(CaptureSessionResult sessionResult)
    {
        InitializeComponent();
        Apply(sessionResult);
    }

    public void PositionNear(ScreenRegion region)
    {
        Width = region.Width;
        Height = region.Height;
        Left = region.Left;
        Top = region.Top;
    }

    private void Apply(CaptureSessionResult sessionResult)
    {
        ApplySizing(sessionResult.Region);
        ApplyTypography(sessionResult.Region, sessionResult.Ocr);
        TranslationTextBox.Text = string.IsNullOrWhiteSpace(sessionResult.Translation.TranslatedText)
            ? "Перевод пока отсутствует."
            : sessionResult.Translation.TranslatedText;
    }

    private void ApplySizing(ScreenRegion region)
    {
        Width = region.Width;
        Height = region.Height;

        var padding = Math.Clamp(Math.Min(region.Width, region.Height) * 0.045, 8, 14);
        RootBorder.Padding = new Thickness(padding);

        var closeButtonSize = Math.Clamp(Math.Min(region.Width, region.Height) * 0.11, 20, 28);
        CloseButton.Width = closeButtonSize;
        CloseButton.Height = closeButtonSize;
        CloseButton.FontSize = Math.Clamp(closeButtonSize * 0.5, 11, 14);
        TranslationTextBox.Margin = new Thickness(0, 0, closeButtonSize + 6, 0);
    }

    private void ApplyTypography(ScreenRegion region, OcrResponse ocr)
    {
        var estimatedFromOcr = EstimateFontSizeFromOcr(ocr);
        var fallbackFromRegion = Math.Min(region.Width / 30, region.Height / 15);
        var fontSize = Math.Clamp(
            estimatedFromOcr ?? fallbackFromRegion,
            10.5,
            20);

        TranslationTextBox.FontSize = Math.Round(fontSize, 1);
    }

    private static double? EstimateFontSizeFromOcr(OcrResponse ocr)
    {
        var heights = ocr.Lines
            .Select(line => line.Bounds.Height)
            .Where(height => height > 4)
            .OrderBy(height => height)
            .ToArray();

        if (heights.Length == 0)
        {
            return null;
        }

        var median = heights[heights.Length / 2];
        return median * 0.72;
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

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
