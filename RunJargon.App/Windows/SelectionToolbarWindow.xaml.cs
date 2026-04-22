using System.Windows;
using System.Windows.Input;
using Forms = System.Windows.Forms;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
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
        PreviewKeyDown += SelectionToolbarWindow_PreviewKeyDown;
        CaptureToolbar.CloseRequested += CaptureToolbar_CloseRequested;
        CaptureToolbar.LanguageSelectionChanged += CaptureToolbar_LanguageSelectionChanged;
        CaptureToolbar.SelectAreaRequested += CaptureToolbar_SelectAreaRequested;
        CaptureToolbar.SelectionCancelRequested += CaptureToolbar_SelectionCancelRequested;
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? LanguageSelectionChanged;
    public event EventHandler? SelectAreaRequested;
    public event EventHandler? SelectionCancelRequested;

    public string? SelectedSourceLanguageCode =>
        CaptureToolbar.SelectedSourceLanguageCode ?? _initialSourceLanguageCode;

    public string? SelectedTargetLanguageCode =>
        CaptureToolbar.SelectedTargetLanguageCode ?? _initialTargetLanguageCode;

    public bool IsSelectionActive => CaptureToolbar.IsSelectionActive;

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
        CaptureToolbar.SelectionCancelRequested -= CaptureToolbar_SelectionCancelRequested;
        PreviewKeyDown -= SelectionToolbarWindow_PreviewKeyDown;
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

    private void CaptureToolbar_SelectionCancelRequested(object? sender, EventArgs e)
    {
        SelectionCancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SelectionToolbarWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        if (IsSelectionActive)
        {
            CaptureToolbar.SetSelectionActive(false);
            SelectionCancelRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
