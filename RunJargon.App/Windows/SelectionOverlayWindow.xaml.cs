using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using RunJargon.App.Models;
using RunJargon.App.Services;
using RunJargon.App.Utilities;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace RunJargon.App.Windows;

public partial class SelectionOverlayWindow : Window
{
    private const double MinimumSelectionSize = 8;
    private readonly bool _showToolbar;

    private readonly IReadOnlyList<TranslationLanguageOption> _sourceLanguages;
    private readonly IReadOnlyList<TranslationLanguageOption> _targetLanguages;
    private readonly string _initialSourceLanguageCode;
    private readonly string _initialTargetLanguageCode;
    private TaskCompletionSource<ScreenRegion?> _selectionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Point? _dragStart;

    public SelectionOverlayWindow(
        IReadOnlyList<TranslationLanguageOption> sourceLanguages,
        IReadOnlyList<TranslationLanguageOption> targetLanguages,
        string? selectedSourceLanguageCode,
        string? selectedTargetLanguageCode,
        string recognizedTextToCopy,
        string translatedTextToCopy,
        bool showToolbar = true)
    {
        InitializeComponent();
        _showToolbar = showToolbar;

        _sourceLanguages = sourceLanguages;
        _targetLanguages = targetLanguages;
        _initialSourceLanguageCode = selectedSourceLanguageCode
                                     ?? TranslationLanguageCatalog.GetDefaultSourceLanguage().Code;
        _initialTargetLanguageCode = selectedTargetLanguageCode
                                     ?? TranslationLanguageCatalog.GetDefaultTargetLanguage().Code;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        if (_showToolbar)
        {
            CaptureToolbar.Configure(
                _sourceLanguages,
                _targetLanguages,
                _initialSourceLanguageCode,
                _initialTargetLanguageCode,
                recognizedTextToCopy,
                translatedTextToCopy);
            CaptureToolbar.SetSelectionActive(true);
            CaptureToolbar.SetBusy(false);
        }
        else
        {
            CaptureToolbar.Visibility = Visibility.Collapsed;
        }

        Loaded += SelectionOverlayWindow_Loaded;
        Closed += SelectionOverlayWindow_Closed;
        if (_showToolbar)
        {
            CaptureToolbar.CloseRequested += CaptureToolbar_CloseRequested;
            CaptureToolbar.LanguageSelectionChanged += CaptureToolbar_LanguageSelectionChanged;
            CaptureToolbar.SelectAreaRequested += CaptureToolbar_SelectAreaRequested;
        }
    }

    public event EventHandler? LanguageSelectionChanged;
    public event EventHandler? SelectAreaRequested;

    public ScreenRegion? SelectedRegion { get; private set; }

    public string? SelectedSourceLanguageCode =>
        _showToolbar
            ? CaptureToolbar.SelectedSourceLanguageCode ?? _initialSourceLanguageCode
            : _initialSourceLanguageCode;

    public string? SelectedTargetLanguageCode =>
        _showToolbar
            ? CaptureToolbar.SelectedTargetLanguageCode ?? _initialTargetLanguageCode
            : _initialTargetLanguageCode;

    public Task<ScreenRegion?> WaitForSelectionAsync()
    {
        return _selectionTcs.Task;
    }

    public void BeginSelectionMode()
    {
        if (!CheckAccess())
        {
            Dispatcher.Invoke(BeginSelectionMode);
            return;
        }

        if (!_selectionTcs.Task.IsCompleted)
        {
            return;
        }

        _selectionTcs = new TaskCompletionSource<ScreenRegion?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        SelectedRegion = null;
        _dragStart = null;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        RootCanvas.Width = Width;
        RootCanvas.Height = Height;
        Cursor = System.Windows.Input.Cursors.Cross;
        RootCanvas.Focusable = true;
        RootCanvas.IsHitTestVisible = true;

        DimOverlay.Visibility = Visibility.Visible;
        SelectionBorder.Visibility = Visibility.Collapsed;
        CaptureToolbar.Visibility = Visibility.Visible;
        CaptureToolbar.SetBusy(false);
        CaptureToolbar.SetSelectionActive(true);
        PositionCaptureToolbar();

        if (!IsVisible)
        {
            Show();
        }

        Activate();
        RootCanvas.Focus();
    }

    public void SetBusy(bool isBusy)
    {
        if (!_showToolbar)
        {
            return;
        }

        if (!CheckAccess())
        {
            Dispatcher.Invoke(() => SetBusy(isBusy));
            return;
        }

        CaptureToolbar.SetBusy(isBusy);
    }

    public void UpdateCopyTexts(string recognizedTextToCopy, string translatedTextToCopy)
    {
        if (!_showToolbar)
        {
            return;
        }

        if (!CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateCopyTexts(recognizedTextToCopy, translatedTextToCopy));
            return;
        }

        CaptureToolbar.UpdateCopyTexts(recognizedTextToCopy, translatedTextToCopy);
    }

    public async Task PrepareForCleanCaptureAsync()
    {
        if (!CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => PrepareForCleanCaptureAsync()).Task.Unwrap();
            return;
        }

        if (!IsLoaded || !IsVisible)
        {
            return;
        }

        DimOverlay.Visibility = Visibility.Collapsed;
        SelectionBorder.Visibility = Visibility.Collapsed;
        if (_showToolbar)
        {
            CaptureToolbar.Visibility = Visibility.Visible;
        }
        RootCanvas.IsHitTestVisible = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(20);
    }

    public void ShowToolbarOnly(bool isBusy)
    {
        if (!_showToolbar)
        {
            return;
        }

        if (!CheckAccess())
        {
            Dispatcher.Invoke(() => ShowToolbarOnly(isBusy));
            return;
        }

        if (SelectedRegion is null)
        {
            return;
        }

        var region = SelectedRegion.Value;
        var screenBounds = Forms.Screen.FromRectangle(
            new System.Drawing.Rectangle(
                (int)Math.Floor(region.Left),
                (int)Math.Floor(region.Top),
                Math.Max(1, (int)Math.Ceiling(region.Width)),
                Math.Max(1, (int)Math.Ceiling(region.Height)))).Bounds;

        var toolbarWidth = CaptureToolbar.Width > 0 ? CaptureToolbar.Width : 643d;
        var toolbarHeight = CaptureToolbar.Height > 0 ? CaptureToolbar.Height : 46d;

        Left = screenBounds.Left + Math.Max(0, (screenBounds.Width - toolbarWidth) / 2d);
        Top = screenBounds.Top;
        Width = toolbarWidth;
        Height = toolbarHeight;
        Cursor = System.Windows.Input.Cursors.Arrow;

        RootCanvas.Width = toolbarWidth;
        RootCanvas.Height = toolbarHeight;
        RootCanvas.Focusable = true;
        RootCanvas.IsHitTestVisible = true;

        DimOverlay.Visibility = Visibility.Collapsed;
        SelectionBorder.Visibility = Visibility.Collapsed;
        CaptureToolbar.Visibility = Visibility.Visible;
        Canvas.SetLeft(CaptureToolbar, 0);
        Canvas.SetTop(CaptureToolbar, 0);
        CaptureToolbar.SetSelectionActive(false);
        CaptureToolbar.SetBusy(isBusy);

        if (!IsVisible)
        {
            Show();
        }

        Activate();
        RootCanvas.Focus();
    }

    private void SelectionOverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        WindowCaptureProtection.TryExcludeFromCapture(this);
        RootCanvas.Focus();
        if (_showToolbar)
        {
            PositionCaptureToolbar();
        }
    }

    private void SelectionOverlayWindow_Closed(object? sender, EventArgs e)
    {
        Mouse.Capture(null);
        if (_showToolbar)
        {
            CaptureToolbar.CloseRequested -= CaptureToolbar_CloseRequested;
            CaptureToolbar.LanguageSelectionChanged -= CaptureToolbar_LanguageSelectionChanged;
            CaptureToolbar.SelectAreaRequested -= CaptureToolbar_SelectAreaRequested;
        }
        _selectionTcs.TrySetResult(null);
    }

    private void CaptureToolbar_CloseRequested(object? sender, EventArgs e)
    {
        _selectionTcs.TrySetResult(null);
        Close();
    }

    private void CaptureToolbar_LanguageSelectionChanged(object? sender, EventArgs e)
    {
        LanguageSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CaptureToolbar_SelectAreaRequested(object? sender, EventArgs e)
    {
        SelectAreaRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_selectionTcs.Task.IsCompleted
            || IsToolbarInteraction(e.OriginalSource as DependencyObject))
        {
            return;
        }

        Cursor = System.Windows.Input.Cursors.Cross;
        _dragStart = e.GetPosition(RootCanvas);
        SelectionBorder.Visibility = Visibility.Visible;
        Mouse.Capture(RootCanvas);
        UpdateSelection(e.GetPosition(RootCanvas));
    }

    private void RootCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateSelection(e.GetPosition(RootCanvas));
    }

    private void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null)
        {
            return;
        }

        UpdateSelection(e.GetPosition(RootCanvas));
        Mouse.Capture(null);

        var left = Canvas.GetLeft(SelectionBorder);
        var top = Canvas.GetTop(SelectionBorder);
        var width = SelectionBorder.Width;
        var height = SelectionBorder.Height;

        _dragStart = null;

        if (width < MinimumSelectionSize || height < MinimumSelectionSize)
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            return;
        }

        SelectedRegion = new ScreenRegion(Left + left, Top + top, width, height);
        _selectionTcs.TrySetResult(SelectedRegion);
        if (_showToolbar)
        {
            ShowToolbarOnly(isBusy: true);
        }
        else
        {
            Close();
        }
    }

    private void RootCanvas_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Mouse.Capture(null);
        _dragStart = null;
        SelectionBorder.Visibility = Visibility.Collapsed;
        _selectionTcs.TrySetResult(null);
        Close();
    }

    private void UpdateSelection(Point current)
    {
        if (_dragStart is null)
        {
            return;
        }

        var left = Math.Min(_dragStart.Value.X, current.X);
        var top = Math.Min(_dragStart.Value.Y, current.Y);
        var width = Math.Abs(current.X - _dragStart.Value.X);
        var height = Math.Abs(current.Y - _dragStart.Value.Y);

        Canvas.SetLeft(SelectionBorder, left);
        Canvas.SetTop(SelectionBorder, top);
        SelectionBorder.Width = width;
        SelectionBorder.Height = height;
    }

    private void PositionCaptureToolbar()
    {
        var toolbarWidth = CaptureToolbar.Width > 0 ? CaptureToolbar.Width : 643d;
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var relativeScreenLeft = screen.Bounds.Left - Left;
        var relativeScreenTop = screen.Bounds.Top - Top;
        var left = relativeScreenLeft + Math.Max(0, (screen.Bounds.Width - toolbarWidth) / 2d);

        Canvas.SetLeft(CaptureToolbar, left);
        Canvas.SetTop(CaptureToolbar, Math.Max(0, relativeScreenTop));
    }

    private bool IsToolbarInteraction(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, CaptureToolbar))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
