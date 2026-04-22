using System.Windows;
using Forms = System.Windows.Forms;
using RunJargon.App.Models;
using RunJargon.App.Services;
using RunJargon.App.Utilities;

namespace RunJargon.App.Windows;

public partial class SelectionToolbarWindow : Window
{
    private readonly string _initialSourceLanguageCode;
    private readonly string _initialTargetLanguageCode;

    public SelectionToolbarWindow(
        IReadOnlyList<TranslationLanguageOption> sourceLanguages,
        IReadOnlyList<TranslationLanguageOption> targetLanguages,
        string? selectedSourceLanguageCode,
        string? selectedTargetLanguageCode,
        string recognizedTextToCopy,
        string translatedTextToCopy)
    {
        InitializeComponent();

        _initialSourceLanguageCode = selectedSourceLanguageCode
                                     ?? TranslationLanguageCatalog.GetDefaultSourceLanguage().Code;
        _initialTargetLanguageCode = selectedTargetLanguageCode
                                     ?? TranslationLanguageCatalog.GetDefaultTargetLanguage().Code;

        CaptureToolbar.Configure(
            sourceLanguages,
            targetLanguages,
            _initialSourceLanguageCode,
            _initialTargetLanguageCode,
            recognizedTextToCopy,
            translatedTextToCopy);
        CaptureToolbar.SetSelectionActive(true);
        CaptureToolbar.SetBusy(false);

        Loaded += SelectionToolbarWindow_Loaded;
        Closed += SelectionToolbarWindow_Closed;
        CaptureToolbar.CloseRequested += CaptureToolbar_CloseRequested;
        CaptureToolbar.LanguageSelectionChanged += CaptureToolbar_LanguageSelectionChanged;
        CaptureToolbar.SelectAreaRequested += CaptureToolbar_SelectAreaRequested;
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? LanguageSelectionChanged;
    public event EventHandler? SelectAreaRequested;

    public string? SelectedSourceLanguageCode =>
        CaptureToolbar.SelectedSourceLanguageCode ?? _initialSourceLanguageCode;

    public string? SelectedTargetLanguageCode =>
        CaptureToolbar.SelectedTargetLanguageCode ?? _initialTargetLanguageCode;

    public void SetBusy(bool isBusy)
    {
        CaptureToolbar.SetBusy(isBusy);
    }

    public void SetSelectionActive(bool isSelectionActive)
    {
        CaptureToolbar.SetSelectionActive(isSelectionActive);
    }

    public void UpdateCopyTexts(string recognizedTextToCopy, string translatedTextToCopy)
    {
        CaptureToolbar.UpdateCopyTexts(recognizedTextToCopy, translatedTextToCopy);
    }

    public void PositionOnScreen(Forms.Screen screen)
    {
        var toolbarWidth = CaptureToolbar.Width > 0 ? CaptureToolbar.Width : Width;
        var toolbarHeight = CaptureToolbar.Height > 0 ? CaptureToolbar.Height : Height;
        Width = toolbarWidth;
        Height = toolbarHeight;
        Left = screen.Bounds.Left + Math.Max(0, (screen.Bounds.Width - toolbarWidth) / 2d);
        Top = screen.Bounds.Top;
    }

    public void PositionForRegion(ScreenRegion region)
    {
        var screen = Forms.Screen.FromRectangle(
            new System.Drawing.Rectangle(
                (int)Math.Floor(region.Left),
                (int)Math.Floor(region.Top),
                Math.Max(1, (int)Math.Ceiling(region.Width)),
                Math.Max(1, (int)Math.Ceiling(region.Height))));
        PositionOnScreen(screen);
    }

    public void BringToFront()
    {
        Topmost = false;
        Topmost = true;
        Activate();
    }

    private void SelectionToolbarWindow_Loaded(object sender, RoutedEventArgs e)
    {
        WindowCaptureProtection.TryExcludeFromCapture(this);
        PositionOnScreen(Forms.Screen.FromPoint(Forms.Cursor.Position));
        Activate();
    }

    private void SelectionToolbarWindow_Closed(object? sender, EventArgs e)
    {
        CaptureToolbar.CloseRequested -= CaptureToolbar_CloseRequested;
        CaptureToolbar.LanguageSelectionChanged -= CaptureToolbar_LanguageSelectionChanged;
        CaptureToolbar.SelectAreaRequested -= CaptureToolbar_SelectAreaRequested;
    }

    private void CaptureToolbar_CloseRequested(object? sender, EventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CaptureToolbar_LanguageSelectionChanged(object? sender, EventArgs e)
    {
        LanguageSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CaptureToolbar_SelectAreaRequested(object? sender, EventArgs e)
    {
        SelectAreaRequested?.Invoke(this, EventArgs.Empty);
    }
}
