using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading;
using RunJargon.App.Models;
using RunJargon.App.Services;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using FontFamily = System.Windows.Media.FontFamily;
using Grid = System.Windows.Controls.Grid;
using Panel = System.Windows.Controls.Panel;
using TextBlock = System.Windows.Controls.TextBlock;
using ToolTip = System.Windows.Controls.ToolTip;

namespace RunJargon.App.Controls;

public partial class CaptureSelectionToolbar : System.Windows.Controls.UserControl
{
    private static readonly TimeSpan SwapAnimationCycleDuration = TimeSpan.FromMilliseconds(28d / 24d * 1000d);

    private IReadOnlyList<TranslationLanguageOption> _sourceLanguages = [];
    private IReadOnlyList<TranslationLanguageOption> _targetLanguages = [];
    private string _recognizedTextToCopy = string.Empty;
    private string _translatedTextToCopy = string.Empty;
    private bool _isBusy;
    private bool _isSelectionActive = true;
    private bool _isSelectAreaPointerOver;
    private bool _isSelectAreaPressed;
    private bool _isSwapPointerOver;
    private bool _isSwapPressed;
    private bool _isSwapCompletingCycle;
    private DateTimeOffset? _swapAnimationStartedAt;
    private CancellationTokenSource? _swapAnimationStopCts;

    public CaptureSelectionToolbar()
    {
        InitializeComponent();
        InitializeSwapAnimationVisuals();
        Configure(
            TranslationLanguageCatalog.GetSourceLanguages(),
            TranslationLanguageCatalog.GetTargetLanguages(),
            TranslationLanguageCatalog.GetDefaultSourceLanguage().Code,
            TranslationLanguageCatalog.GetDefaultTargetLanguage().Code,
            string.Empty,
            string.Empty);
    }

    public string? SelectedSourceLanguageCode { get; private set; }

    public string? SelectedTargetLanguageCode { get; private set; }

    public event EventHandler? CloseRequested;
    public event EventHandler? LanguageSelectionChanged;
    public event EventHandler? SelectAreaRequested;

    public void UpdateCopyTexts(string recognizedTextToCopy, string translatedTextToCopy)
    {
        _recognizedTextToCopy = recognizedTextToCopy ?? string.Empty;
        _translatedTextToCopy = translatedTextToCopy ?? string.Empty;
        UpdateCopyButtonStates();
    }

    public void SetBusy(bool isBusy)
    {
        if (_isBusy == isBusy)
        {
            return;
        }

        _isBusy = isBusy;
        ApplyBusyVisualState();
    }

    public void SetSelectionActive(bool isSelectionActive)
    {
        if (_isSelectionActive == isSelectionActive)
        {
            return;
        }

        _isSelectionActive = isSelectionActive;
        UpdateSelectAreaButtonState();
    }

    public void Configure(
        IReadOnlyList<TranslationLanguageOption> sourceLanguages,
        IReadOnlyList<TranslationLanguageOption> targetLanguages,
        string? selectedSourceLanguageCode,
        string? selectedTargetLanguageCode,
        string recognizedTextToCopy,
        string translatedTextToCopy)
    {
        _sourceLanguages = sourceLanguages;
        _targetLanguages = targetLanguages;
        _recognizedTextToCopy = recognizedTextToCopy ?? string.Empty;
        _translatedTextToCopy = translatedTextToCopy ?? string.Empty;

        var selectedSource = TranslationLanguageCatalog.FindSourceLanguage(selectedSourceLanguageCode)
                             ?? sourceLanguages.FirstOrDefault()
                             ?? TranslationLanguageCatalog.GetDefaultSourceLanguage();
        var selectedTarget = TranslationLanguageCatalog.FindTargetLanguage(selectedTargetLanguageCode)
                             ?? targetLanguages.FirstOrDefault()
                             ?? TranslationLanguageCatalog.GetDefaultTargetLanguage();

        SelectedSourceLanguageCode = selectedSource.Code;
        SelectedTargetLanguageCode = selectedTarget.Code;

        SourceLanguageText.Text = selectedSource.DisplayName;
        TargetLanguageText.Text = selectedTarget.DisplayName;

        RebuildMenuButtons();
        UpdateCopyButtonStates();
        UpdateSwapButtonState();
        UpdateSelectAreaButtonState();
        ApplyBusyVisualState();
    }

    private void SourceSelectorButton_Click(object sender, RoutedEventArgs e)
    {
        TargetPopup.IsOpen = false;
        SourcePopup.IsOpen = !SourcePopup.IsOpen;
    }

    private void TargetSelectorButton_Click(object sender, RoutedEventArgs e)
    {
        SourcePopup.IsOpen = false;
        TargetPopup.IsOpen = !TargetPopup.IsOpen;
    }

    private void CopyOriginalButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(_recognizedTextToCopy);
    }

    private void CopyTranslatedButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(_translatedTextToCopy);
    }

    private void SwapLanguagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !CanSwapLanguages())
        {
            return;
        }

        var newSourceCode = SelectedTargetLanguageCode ?? string.Empty;
        var newTargetCode = SelectedSourceLanguageCode ?? string.Empty;

        var newSource = TranslationLanguageCatalog.FindSourceLanguage(newSourceCode);
        var newTarget = TranslationLanguageCatalog.FindTargetLanguage(newTargetCode);
        if (newSource is null || newTarget is null)
        {
            return;
        }

        SelectedSourceLanguageCode = newSource.Code;
        SelectedTargetLanguageCode = newTarget.Code;
        SourceLanguageText.Text = newSource.DisplayName;
        TargetLanguageText.Text = newTarget.DisplayName;

        RebuildMenuButtons();
        UpdateSwapButtonState();
        LanguageSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SwapLanguagesButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isSwapPointerOver = true;
        UpdateSwapButtonState();
    }

    private void SwapLanguagesButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isSwapPointerOver = false;
        _isSwapPressed = false;
        UpdateSwapButtonState();
    }

    private void SwapLanguagesButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isBusy || !CanSwapLanguages())
        {
            return;
        }

        _isSwapPressed = true;
        UpdateSwapButtonState();
    }

    private void SwapLanguagesButton_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isSwapPressed = false;
        UpdateSwapButtonState();
    }

    private void CloseToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SelectAreaButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _isSelectionActive)
        {
            return;
        }

        _isSelectionActive = true;
        UpdateSelectAreaButtonState();
        SelectAreaRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SelectAreaButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isSelectAreaPointerOver = true;
        UpdateSelectAreaButtonState();
    }

    private void SelectAreaButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isSelectAreaPointerOver = false;
        _isSelectAreaPressed = false;
        UpdateSelectAreaButtonState();
    }

    private void SelectAreaButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isSelectAreaPressed = true;
        UpdateSelectAreaButtonState();
    }

    private void SelectAreaButton_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isSelectAreaPressed = false;
        UpdateSelectAreaButtonState();
    }

    private void RebuildMenuButtons()
    {
        BuildMenu(
            SourceMenuPanel,
            _sourceLanguages,
            SelectedSourceLanguageCode,
            option =>
            {
                SelectedSourceLanguageCode = option.Code;
                SourceLanguageText.Text = option.DisplayName;
                SourcePopup.IsOpen = false;
                LanguageSelectionChanged?.Invoke(this, EventArgs.Empty);
            });

        BuildMenu(
            TargetMenuPanel,
            _targetLanguages,
            SelectedTargetLanguageCode,
            option =>
            {
                SelectedTargetLanguageCode = option.Code;
                TargetLanguageText.Text = option.DisplayName;
                TargetPopup.IsOpen = false;
                LanguageSelectionChanged?.Invoke(this, EventArgs.Empty);
            });
    }

    private void BuildMenu(
        System.Windows.Controls.Panel panel,
        IReadOnlyList<TranslationLanguageOption> languages,
        string? selectedCode,
        Action<TranslationLanguageOption> onSelect)
    {
        panel.Children.Clear();

        foreach (var language in languages)
        {
            var isSelected = string.Equals(language.Code, selectedCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            var button = new Button
            {
                Style = (Style)Resources["ToolbarMenuItemButtonStyle"],
                Background = isSelected ? new SolidColorBrush(Color.FromArgb(255, 45, 56, 64)) : Brushes.Transparent,
                Foreground = isSelected ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CFEFFF")) : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D7D7D7")),
                Tag = language
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textBlock = new TextBlock
            {
                Text = language.DisplayName,
                FontFamily = new FontFamily("Inter"),
                FontSize = 12,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            grid.Children.Add(textBlock);

            if (isSelected)
            {
                var marker = new Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(999),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#63AADE")),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                Grid.SetColumn(marker, 1);
                grid.Children.Add(marker);
            }

            button.Content = grid;
            button.Click += (_, _) =>
            {
                onSelect(language);
                RebuildMenuButtons();
                UpdateSwapButtonState();
            };

            panel.Children.Add(button);
        }
    }

    private void UpdateCopyButtonStates()
    {
        CopyOriginalButton.IsEnabled = !string.IsNullOrWhiteSpace(_recognizedTextToCopy);
        CopyTranslatedButton.IsEnabled = !_isBusy && !string.IsNullOrWhiteSpace(_translatedTextToCopy);
    }

    private void UpdateSwapButtonState()
    {
        var canSwap = CanSwapLanguages();

        SwapLanguagesButton.IsEnabled = true;
        SwapLanguagesButton.Cursor = !_isBusy && canSwap
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
        SwapLanguagesButton.Opacity = 1;
        UpdateSwapAnimationVisual(canSwap);
    }

    private void UpdateSelectAreaButtonState()
    {
        SelectAreaButton.IsEnabled = !_isBusy;
        SelectAreaButton.Cursor = _isBusy
            ? System.Windows.Input.Cursors.Arrow
            : System.Windows.Input.Cursors.Hand;
        SelectAreaButton.Foreground = CreateIconBrush(
            _isSelectAreaPressed
                ? "#3D7CA9"
                : (_isSelectionActive || _isSelectAreaPointerOver ? "#63AADE" : "#C7C7C7"));
    }

    private bool CanSwapLanguages()
    {
        if (string.IsNullOrWhiteSpace(SelectedSourceLanguageCode)
            || string.IsNullOrWhiteSpace(SelectedTargetLanguageCode))
        {
            return false;
        }

        return TranslationLanguageCatalog.FindTargetLanguage(SelectedSourceLanguageCode) is not null
               && TranslationLanguageCatalog.FindSourceLanguage(SelectedTargetLanguageCode) is not null;
    }

    private static void CopyToClipboard(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
        }
    }

    private void ApplyBusyVisualState()
    {
        SourceSelectorButton.IsEnabled = !_isBusy;
        TargetSelectorButton.IsEnabled = !_isBusy;

        if (_isBusy)
        {
            SourcePopup.IsOpen = false;
            TargetPopup.IsOpen = false;
        }

        UpdateCopyButtonStates();
        UpdateSwapButtonState();
        UpdateSelectAreaButtonState();
        SwapLanguagesButton.ToolTip = CreateToolbarToolTip(_isBusy ? "Перевод выполняется" : "Обратный перевод");
    }

    private static Brush CreateIconBrush(string colorHex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
    }

    private void UpdateSwapAnimationVisual(bool canSwap)
    {
        if (_isBusy)
        {
            CancelPendingSwapAnimationStop();
            _isSwapCompletingCycle = false;
            SetSwapStaticVisual(SwapVisualState.Hidden);
            BusySwapAnimationView.Visibility = Visibility.Visible;

            if (!BusySwapAnimationView.IsPlaying)
            {
                _swapAnimationStartedAt = DateTimeOffset.UtcNow;
                BusySwapAnimationView.PlayAnimation();
            }

            return;
        }

        if (_isSwapCompletingCycle)
        {
            return;
        }

        if (BusySwapAnimationView.IsPlaying)
        {
            _isSwapCompletingCycle = true;
            _ = CompleteSwapAnimationCycleAsync();
            return;
        }

        ApplyIdleSwapVisual(canSwap);
    }

    private ToolTip CreateToolbarToolTip(string content)
    {
        return new ToolTip
        {
            Style = (Style)Resources["ToolbarToolTipStyle"],
            Content = content
        };
    }

    private async Task CompleteSwapAnimationCycleAsync()
    {
        CancelPendingSwapAnimationStop();
        var stopCts = new CancellationTokenSource();
        _swapAnimationStopCts = stopCts;

        try
        {
            var remaining = GetRemainingSwapCycleDuration();
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, stopCts.Token);
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (_isBusy)
                {
                    _isSwapCompletingCycle = false;
                    UpdateSwapAnimationVisual(CanSwapLanguages());
                    return;
                }

                BusySwapAnimationView.StopAnimation();
                BusySwapAnimationView.Visibility = Visibility.Collapsed;
                _isSwapCompletingCycle = false;
                ApplyIdleSwapVisual(CanSwapLanguages());
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_swapAnimationStopCts, stopCts))
            {
                _swapAnimationStopCts = null;
            }

            stopCts.Dispose();
        }
    }

    private void ApplyIdleSwapVisual(bool canSwap)
    {
        BusySwapAnimationView.Visibility = Visibility.Collapsed;
        SetSwapStaticVisual(
            _isSwapPressed && canSwap
                ? SwapVisualState.Pressed
                : _isSwapPointerOver && canSwap
                    ? SwapVisualState.Hover
                    : SwapVisualState.Idle);
    }

    private TimeSpan GetRemainingSwapCycleDuration()
    {
        if (_swapAnimationStartedAt is null)
        {
            return TimeSpan.Zero;
        }

        var elapsed = DateTimeOffset.UtcNow - _swapAnimationStartedAt.Value;
        var elapsedWithinCycleTicks = elapsed.Ticks % SwapAnimationCycleDuration.Ticks;
        if (elapsedWithinCycleTicks == 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromTicks(SwapAnimationCycleDuration.Ticks - elapsedWithinCycleTicks);
    }

    private void CancelPendingSwapAnimationStop()
    {
        if (_swapAnimationStopCts is null)
        {
            return;
        }

        _swapAnimationStopCts.Cancel();
        _swapAnimationStopCts.Dispose();
        _swapAnimationStopCts = null;
    }

    private void InitializeSwapAnimationVisuals()
    {
        IdleSwapAnimationView.StopAnimation();
        HoverSwapAnimationView.StopAnimation();
        PressedSwapAnimationView.StopAnimation();
        BusySwapAnimationView.StopAnimation();
        SetSwapStaticVisual(SwapVisualState.Idle);
    }

    private void SetSwapStaticVisual(SwapVisualState state)
    {
        IdleSwapAnimationView.Visibility = state == SwapVisualState.Idle ? Visibility.Visible : Visibility.Collapsed;
        HoverSwapAnimationView.Visibility = state == SwapVisualState.Hover ? Visibility.Visible : Visibility.Collapsed;
        PressedSwapAnimationView.Visibility = state == SwapVisualState.Pressed ? Visibility.Visible : Visibility.Collapsed;
    }

    private enum SwapVisualState
    {
        Hidden,
        Idle,
        Hover,
        Pressed
    }
}
