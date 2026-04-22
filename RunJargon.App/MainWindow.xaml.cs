using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RunJargon.App.Models;
using RunJargon.App.Services;
using RunJargon.App.Utilities;
using RunJargon.App.Windows;

namespace RunJargon.App;

public partial class MainWindow : Window
{
    private readonly ScreenCaptureService _screenCaptureService = new();
    private readonly AppSettingsService _appSettingsService = new();
    private readonly ImageInpaintingService _imageInpaintingService = new();
    private readonly LayoutSegmentationService _layoutSegmentationService = new();
    private readonly LayoutObservationFusionService _layoutObservationFusionService = new();
    private readonly CaptureProcessingModeClassifier _captureProcessingModeClassifier = new();
    private readonly UiAutomationAssistPolicyService _uiAutomationAssistPolicyService = new();
    private readonly VisualSegmentRefinementService _visualSegmentRefinementService = new();
    private readonly ICapturePipelineExecutor _capturePipelineExecutor = new CapturePipelineExecutor();
    private readonly BackgroundProcessingWorker _backgroundProcessingWorker = new();
    private readonly CapturePerformanceTraceStore _performanceTraceStore = new();
    private readonly ITranslationTextCache _translationTextCache = new TranslationTextCache();
    private readonly IOcrService _ocrService;
    private readonly SegmentOcrRefinementService _segmentOcrRefinementService;
    private readonly UiAutomationTextService _uiAutomationTextService = new();

    private ITranslationService _translationService;
    private TranslationSettings _translationSettings;
    private GlobalHotKeyService? _hotKeyService;
    private TrayIconService? _trayIconService;
    private TranslationResultWindow? _translationResultWindow;
    private TranslationOverlayWindow? _translationOverlayWindow;
    private SelectionOverlayHost? _selectionOverlayHost;
    private ScreenRegion? _lastCapturedRegion;
    private string _lastRecognizedTextForCopy = string.Empty;
    private string _lastTranslatedTextForCopy = string.Empty;
    private string? _lastWarmedSourceLanguage;
    private string? _lastWarmedTargetLanguage;
    private bool _allowAppExit;
    private bool _hasShownTrayHint;
    private bool _isBusy;

    private static readonly Regex StyledMarkerRegex = new(
        @"<\s*(/?)\s*ts(?<id>\d+)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public MainWindow()
    {
        _ocrService = new QueuedOcrService(
            new CompositeOcrService(new WindowsOcrService()),
            new OcrExecutionWorker());
        _segmentOcrRefinementService = new SegmentOcrRefinementService(_ocrService);
        _translationSettings = _appSettingsService.Load();
        _translationService = TranslationServiceFactory.Create(_translationSettings);
        InitializeComponent();

        RepeatLastAreaButton.IsEnabled = false;
        SourceLanguageCombo.SelectionChanged += LanguageCombo_SelectionChanged;
        TargetLanguageCombo.SelectionChanged += LanguageCombo_SelectionChanged;
        LoadLanguageSelectorsIntoUi();
        LoadTranslatorSettingsIntoUi();
        RefreshTranslatorPresentation();

        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        InitializeTrayIcon();

        _hotKeyService = new GlobalHotKeyService(this);
        _hotKeyService.Pressed += HotKeyService_Pressed;

        if (_hotKeyService.Register(ModifierKeys.Control | ModifierKeys.Shift, Key.T))
        {
            SetStatus("Глобальная горячая клавиша зарегистрирована. Можно выделять область с любого окна.");
        }
        else
        {
            SetStatus("Не удалось зарегистрировать Ctrl + Shift + T. Кнопка в окне продолжает работать.");
        }

        _ = WarmUpTranslatorAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_trayIconService is not null)
        {
            _trayIconService.OpenRequested -= TrayIconService_OpenRequested;
            _trayIconService.ExitRequested -= TrayIconService_ExitRequested;
            _trayIconService.Dispose();
            _trayIconService = null;
        }

        _hotKeyService?.Dispose();
        CloseSelectionOverlayAsync().GetAwaiter().GetResult();
        _translationOverlayWindow?.Close();
        _translationResultWindow?.Close();
        DisposeTranslationService();
        _backgroundProcessingWorker.Dispose();
        if (_ocrService is IDisposable disposableOcr)
        {
            disposableOcr.Dispose();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowAppExit)
        {
            return;
        }

        e.Cancel = true;
        HideToTray(showBalloonTip: true);
    }

    private async void HotKeyService_Pressed(object? sender, EventArgs e)
    {
        await RunCaptureFlowAsync(forcePickNewRegion: true);
    }

    private async void CaptureAreaButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCaptureFlowAsync(forcePickNewRegion: true);
    }

    private async void RepeatLastAreaButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCaptureFlowAsync(forcePickNewRegion: false);
    }

    private async Task RunCaptureFlowAsync(bool forcePickNewRegion)
    {
        if (_isBusy)
        {
            return;
        }

        var preserveSelectionOverlay = forcePickNewRegion && _selectionOverlayHost is not null;

        try
        {
            _isBusy = true;
            CaptureAreaButton.IsEnabled = false;
            RepeatLastAreaButton.IsEnabled = false;
            await PrepareForFreshCaptureAsync(preserveSelectionOverlay);

            ScreenRegion? region = forcePickNewRegion ? await PickRegionAsync() : _lastCapturedRegion;
            if (region is null)
            {
                SetStatus("Выделение отменено.");
                return;
            }

            _lastCapturedRegion = region;
            RepeatLastAreaButton.IsEnabled = true;

            ClearDisplayedTexts();
            _lastRecognizedTextForCopy = string.Empty;
            _lastTranslatedTextForCopy = string.Empty;
            if (_selectionOverlayHost is not null)
            {
                await _selectionOverlayHost.UpdateCopyTextsAsync(string.Empty, string.Empty);
                await _selectionOverlayHost.PrepareForCleanCaptureAsync();
            }

            var performanceTrace = new CapturePerformanceTrace();
            SetStatus("Делаю снимок выделенной области...");
            System.Drawing.Bitmap bitmap;
            using (performanceTrace.Measure(CapturePerformancePhase.Capture))
            {
                bitmap = _screenCaptureService.Capture(region.Value);
            }

            using var capturedBitmap = bitmap;
            byte[] snapshotPngBytes;
            using (performanceTrace.Measure(CapturePerformancePhase.SnapshotEncode))
            {
                snapshotPngBytes = EncodeSnapshotPng(capturedBitmap);
            }

            if (_selectionOverlayHost is not null)
            {
                await _selectionOverlayHost.ShowToolbarOnlyAsync(isBusy: true);
            }

            SetStatus("Распознаю текст локально через Windows OCR...");
            var ocrResult = await _ocrService.RecognizeAsync(
                capturedBitmap,
                GetPreferredOcrLanguageTag(),
                CancellationToken.None,
                new OcrRequestOptions(OcrExecutionProfile.FullPage, performanceTrace));
            var recognizedTextForDisplay = string.IsNullOrWhiteSpace(ocrResult.Text)
                ? "Windows OCR не нашел текста в выделенной области."
                : ocrResult.Text;
            RecognizedTextBox.Text = recognizedTextForDisplay;
            _lastRecognizedTextForCopy = string.IsNullOrWhiteSpace(ocrResult.Text)
                ? string.Empty
                : ocrResult.Text;
            if (_selectionOverlayHost is not null)
            {
                await _selectionOverlayHost.UpdateCopyTextsAsync(_lastRecognizedTextForCopy, string.Empty);
            }

            var selectedSourceLanguage = GetSelectedLanguageCode(SourceLanguageCombo);
            var targetLanguage = GetSelectedLanguageCode(TargetLanguageCombo) ?? "ru";
            var preferredOcrLanguageTag = GetPreferredOcrLanguageTag();

            SetStatus($"Перевожу текст через {_translationService.DisplayName}...");
            var processedCapture = await _capturePipelineExecutor.ExecuteAsync(
                async cancellationToken => await ProcessPostOcrAsync(
                    ocrResult,
                    region.Value,
                    capturedBitmap,
                    snapshotPngBytes,
                    preferredOcrLanguageTag,
                    selectedSourceLanguage,
                    targetLanguage,
                    performanceTrace,
                    cancellationToken),
                CancellationToken.None);
            var overlayLines = processedCapture.OverlayLines;
            var translationResult = processedCapture.Translation;

            if (!string.IsNullOrWhiteSpace(processedCapture.RecognizedPreview))
            {
                RecognizedTextBox.Text = processedCapture.RecognizedPreview;
                _lastRecognizedTextForCopy = processedCapture.RecognizedPreview;
                if (_selectionOverlayHost is not null)
                {
                    await _selectionOverlayHost.UpdateCopyTextsAsync(_lastRecognizedTextForCopy, string.Empty);
                }
            }

            var sessionResult = new CaptureSessionResult(
                region.Value,
                snapshotPngBytes,
                processedCapture.PreparedBackgroundPngBytes,
                ocrResult,
                translationResult,
                overlayLines,
                DateTimeOffset.Now);

            TranslatedTextBox.Text = string.IsNullOrWhiteSpace(translationResult.TranslatedText)
                ? "Перевод пока недоступен. Подключи API ключи, и тот же поток сразу станет рабочим."
                : translationResult.TranslatedText;
            _lastTranslatedTextForCopy = string.IsNullOrWhiteSpace(translationResult.TranslatedText)
                ? string.Empty
                : translationResult.TranslatedText;
            if (_selectionOverlayHost is not null)
            {
                await _selectionOverlayHost.UpdateCopyTextsAsync(_lastRecognizedTextForCopy, _lastTranslatedTextForCopy);
                await _selectionOverlayHost.SetBusyAsync(false);
            }

            using (performanceTrace.Measure(CapturePerformancePhase.OverlayCompose))
            {
                ShowTranslationPresentation(sessionResult);
            }
            _performanceTraceStore.Add(performanceTrace);

            var tail = string.IsNullOrWhiteSpace(translationResult.Note)
                ? string.Empty
                : $" {translationResult.Note}";

            SetStatus($"Готово. OCR: {ocrResult.EngineName}. Переводчик: {translationResult.ProviderName}.{tail}");
        }
        catch (Exception ex)
        {
            if (_selectionOverlayHost is not null)
            {
                await _selectionOverlayHost.SetBusyAsync(false);
            }
            SetStatus("Во время обработки произошла ошибка.");
            if (IsVisible)
            {
                System.Windows.MessageBox.Show(
                    this,
                    ex.Message,
                    "Run Jargon",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            else
            {
                System.Windows.MessageBox.Show(
                    ex.Message,
                    "Run Jargon",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
        finally
        {
            _isBusy = false;
            CaptureAreaButton.IsEnabled = true;
            RepeatLastAreaButton.IsEnabled = _lastCapturedRegion is not null;
        }
    }

    private async Task<ScreenRegion?> PickRegionAsync()
    {
        if (_selectionOverlayHost is not null)
        {
            await _selectionOverlayHost.BeginSelectionAsync();
            return await _selectionOverlayHost.WaitForSelectionAsync();
        }

        var selector = new SelectionOverlayHost(
            TranslationLanguageCatalog.GetSourceLanguages(),
            TranslationLanguageCatalog.GetTargetLanguages(),
            GetSelectedLanguageCode(SourceLanguageCombo),
            GetSelectedLanguageCode(TargetLanguageCombo) ?? TranslationLanguageCatalog.GetDefaultTargetLanguage().Code,
            _lastRecognizedTextForCopy,
            _lastTranslatedTextForCopy);
        selector.Closed += SelectionOverlayHost_Closed;
        selector.LanguageSelectionChanged += SelectionOverlayHost_LanguageSelectionChanged;
        selector.SelectAreaRequested += SelectionOverlayHost_SelectAreaRequested;
        _selectionOverlayHost = selector;
        await selector.StartAsync();
        await selector.BeginSelectionAsync();

        var region = await selector.WaitForSelectionAsync();
        ApplyLanguageSelection(
            selector.SelectedSourceLanguageCode,
            selector.SelectedTargetLanguageCode);

        return region;
    }

    private async Task<ProcessedCaptureData> ProcessPostOcrAsync(
        OcrResponse ocrResult,
        ScreenRegion region,
        System.Drawing.Bitmap bitmap,
        byte[] snapshotPngBytes,
        string? preferredOcrLanguageTag,
        string? selectedSourceLanguage,
        string targetLanguage,
        CapturePerformanceTrace performanceTrace,
        CancellationToken cancellationToken)
    {
        TranslationResponse translationResult;
        IReadOnlyList<TranslatedOcrLine> overlayLines;
        string recognizedPreview = string.Empty;

        if (string.IsNullOrWhiteSpace(ocrResult.Text))
        {
            overlayLines = Array.Empty<TranslatedOcrLine>();
            translationResult = new TranslationResponse(
                string.Empty,
                _translationService.DisplayName,
                "Перевод пропущен, потому что OCR не распознал текст.");
        }
        else
        {
            var overlayBuild = await BuildOverlayLinesAsync(
                ocrResult,
                region,
                bitmap,
                preferredOcrLanguageTag,
                selectedSourceLanguage,
                targetLanguage,
                performanceTrace,
                cancellationToken);
            overlayLines = overlayBuild.Lines;
            recognizedPreview = overlayBuild.RecognizedPreview;
            translationResult = overlayLines.Count > 0
                ? BuildTranslationResponseFromOverlayLines(overlayLines)
                : await TranslateWholeTextAsync(
                    ocrResult.Text,
                    selectedSourceLanguage,
                    targetLanguage,
                    performanceTrace,
                    cancellationToken);
        }

        byte[] preparedBackgroundPngBytes;
        if (overlayLines.Count > 0)
        {
            performanceTrace.Counters.IncrementInpaintCalls();
            using (performanceTrace.Measure(CapturePerformancePhase.Inpainting))
            {
                preparedBackgroundPngBytes = await _backgroundProcessingWorker.ExecuteAsync(
                    () => BuildPreparedBackground(snapshotPngBytes, bitmap.Width, bitmap.Height, overlayLines),
                    cancellationToken);
            }
        }
        else
        {
            preparedBackgroundPngBytes = snapshotPngBytes;
        }

        return new ProcessedCaptureData(
            recognizedPreview,
            translationResult,
            overlayLines,
            preparedBackgroundPngBytes);
    }

    private void ShowTranslationPresentation(CaptureSessionResult sessionResult)
    {
        CloseExistingPresentation();
        if (sessionResult.OverlayLines.Count > 0 && sessionResult.SnapshotPngBytes.Length > 0)
        {
            _translationOverlayWindow = new TranslationOverlayWindow(sessionResult);
            _translationOverlayWindow.Show();
            return;
        }

        _translationOverlayWindow = null;

        _translationResultWindow = new TranslationResultWindow(sessionResult);
        _translationResultWindow.Show();
        _translationResultWindow.PositionNear(sessionResult.Region);
    }

    private void LanguageCombo_SelectionChanged(object sender, RoutedEventArgs e)
    {
        _ = PreloadCurrentTranslationPairAsync();
    }

    private void InitializeTrayIcon()
    {
        if (_trayIconService is not null)
        {
            return;
        }

        var trayIconPath = Path.Combine(AppContext.BaseDirectory, "icon", "tray.ico");
        _trayIconService = new TrayIconService(trayIconPath, Title);
        _trayIconService.OpenRequested += TrayIconService_OpenRequested;
        _trayIconService.ExitRequested += TrayIconService_ExitRequested;
    }

    private void TrayIconService_OpenRequested(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(RestoreFromTray);
    }

    private void TrayIconService_ExitRequested(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(ExitApplicationFromTray);
    }

    private void SelectionOverlayHost_Closed(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => SelectionOverlayHost_Closed(sender, e));
            return;
        }

        if (sender is not SelectionOverlayHost selector)
        {
            return;
        }

        selector.Closed -= SelectionOverlayHost_Closed;
        selector.LanguageSelectionChanged -= SelectionOverlayHost_LanguageSelectionChanged;
        selector.SelectAreaRequested -= SelectionOverlayHost_SelectAreaRequested;

        if (ReferenceEquals(_selectionOverlayHost, selector))
        {
            _selectionOverlayHost = null;
        }
    }

    private void SelectionOverlayHost_LanguageSelectionChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => SelectionOverlayHost_LanguageSelectionChanged(sender, e));
            return;
        }

        if (sender is not SelectionOverlayHost selector)
        {
            return;
        }

        ApplyLanguageSelection(
            selector.SelectedSourceLanguageCode,
            selector.SelectedTargetLanguageCode);
    }

    private async void SelectionOverlayHost_SelectAreaRequested(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => SelectionOverlayHost_SelectAreaRequested(sender, e));
            return;
        }

        await RunCaptureFlowAsync(forcePickNewRegion: true);
    }

    private void HideToTray(bool showBalloonTip)
    {
        ShowInTaskbar = false;
        Hide();

        if (showBalloonTip && !_hasShownTrayHint)
        {
            _trayIconService?.ShowBalloonTip(
                "Run Jargon",
                "Приложение свернуто в трей. Открыть или полностью закрыть его можно через значок в области уведомлений.");
            _hasShownTrayHint = true;
        }
    }

    private void RestoreFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitApplicationFromTray()
    {
        _allowAppExit = true;
        Close();
    }

    private void SaveTranslatorSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _translationSettings = new TranslationSettings(
            AzureApiKeyBox.Password.Trim(),
            AzureRegionTextBox.Text.Trim(),
            AzureEndpointTextBox.Text.Trim());

        _appSettingsService.Save(_translationSettings);
        SwapTranslationService(TranslationServiceFactory.Create(_translationSettings));
        RefreshTranslatorPresentation();

        SetStatus($"Настройки переводчика сохранены. Активный режим: {_translationService.DisplayName}.");
    }

    private void ClearTranslatorSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _translationSettings = new TranslationSettings(string.Empty, string.Empty, string.Empty);
        _appSettingsService.Save(_translationSettings);

        AzureApiKeyBox.Password = string.Empty;
        AzureRegionTextBox.Text = "global";
        AzureEndpointTextBox.Text = "https://api.cognitive.microsofttranslator.com";

        SwapTranslationService(TranslationServiceFactory.Create(_translationSettings));
        RefreshTranslatorPresentation();

        SetStatus("Локально сохраненные ключи очищены. Приложение вернулось в demo/fallback режим.");
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private static string? GetSelectedLanguageCode(System.Windows.Controls.ComboBox comboBox)
    {
        return comboBox.SelectedItem switch
        {
            TranslationLanguageOption option => option.Code,
            System.Windows.Controls.ComboBoxItem item => item.Tag?.ToString(),
            _ => null
        };
    }

    private string? GetPreferredOcrLanguageTag()
    {
        return TranslationLanguageCatalog.ResolvePreferredOcrLanguageTag(
            GetSelectedLanguageCode(SourceLanguageCombo));
    }

    private void LoadLanguageSelectorsIntoUi()
    {
        var sourceLanguages = TranslationLanguageCatalog.GetSourceLanguages();
        var targetLanguages = TranslationLanguageCatalog.GetTargetLanguages();

        SourceLanguageCombo.ItemsSource = sourceLanguages;
        SourceLanguageCombo.DisplayMemberPath = nameof(TranslationLanguageOption.DisplayName);
        TargetLanguageCombo.ItemsSource = targetLanguages;
        TargetLanguageCombo.DisplayMemberPath = nameof(TranslationLanguageOption.DisplayName);

        ApplyLanguageSelection(
            TranslationLanguageCatalog.GetDefaultSourceLanguage().Code,
            TranslationLanguageCatalog.GetDefaultTargetLanguage().Code);
    }

    private void ApplyLanguageSelection(string? sourceLanguageCode, string? targetLanguageCode)
    {
        SetSelectedLanguageCode(
            SourceLanguageCombo,
            TranslationLanguageCatalog.GetSourceLanguages(),
            sourceLanguageCode ?? TranslationLanguageCatalog.GetDefaultSourceLanguage().Code);
        SetSelectedLanguageCode(
            TargetLanguageCombo,
            TranslationLanguageCatalog.GetTargetLanguages(),
            targetLanguageCode ?? TranslationLanguageCatalog.GetDefaultTargetLanguage().Code);
        _ = PreloadCurrentTranslationPairAsync();
    }

    private static void SetSelectedLanguageCode(
        System.Windows.Controls.ComboBox comboBox,
        IReadOnlyList<TranslationLanguageOption> availableLanguages,
        string? languageCode)
    {
        var effectiveCode = languageCode ?? string.Empty;
        comboBox.SelectedItem = availableLanguages.FirstOrDefault(language =>
                                   string.Equals(language.Code, effectiveCode, StringComparison.OrdinalIgnoreCase))
                               ?? availableLanguages.FirstOrDefault();
    }

    private void LoadTranslatorSettingsIntoUi()
    {
        AzureApiKeyBox.Password = _translationSettings.AzureApiKey ?? string.Empty;
        AzureRegionTextBox.Text = string.IsNullOrWhiteSpace(_translationSettings.AzureRegion)
            ? "global"
            : _translationSettings.AzureRegion;
        AzureEndpointTextBox.Text = string.IsNullOrWhiteSpace(_translationSettings.AzureEndpoint)
            ? "https://api.cognitive.microsofttranslator.com"
            : _translationSettings.AzureEndpoint;
    }

    private void RefreshTranslatorPresentation()
    {
        ProviderNameText.Text = _translationService.DisplayName;
        ProviderHintText.Text = _translationService.ConfigurationHint;
    }

    private TranslationResponse BuildTranslationResponseFromOverlayLines(
        IReadOnlyList<TranslatedOcrLine> overlayLines)
    {
        var mergedText = string.Join(
            Environment.NewLine,
            overlayLines.Select(line => line.TranslatedText));

        return new TranslationResponse(
            mergedText,
            _translationService.DisplayName,
            "Перевод собран по OCR-блокам с сохранением структуры области.");
    }

    private async Task<OverlayBuildResult> BuildOverlayLinesAsync(
        OcrResponse ocrResult,
        ScreenRegion captureRegion,
        System.Drawing.Bitmap snapshotBitmap,
        string? preferredOcrLanguageTag,
        string? sourceLanguage,
        string targetLanguage,
        CapturePerformanceTrace performanceTrace,
        CancellationToken cancellationToken)
    {
        var overlayLines = new List<TranslatedOcrLine>();
        IReadOnlyList<LayoutTextSegment> ocrSegments;
        using (performanceTrace.Measure(CapturePerformancePhase.LayoutSegmentation))
        {
            ocrSegments = await _backgroundProcessingWorker.ExecuteAsync(
                () => _layoutSegmentationService.BuildSegments(ocrResult.Lines),
                cancellationToken);
        }

        var processingMode = _captureProcessingModeClassifier.Classify(ocrSegments);
        performanceTrace.SetProcessingMode(processingMode);

        IReadOnlyList<LayoutTextSegment> automationSegments = Array.Empty<LayoutTextSegment>();
        var shouldUseUiAutomation = processingMode == CaptureProcessingMode.UiDense
                                    || (processingMode == CaptureProcessingMode.Mixed
                                        && _uiAutomationAssistPolicyService.ShouldUseUiAutomation(ocrSegments));
        if (shouldUseUiAutomation)
        {
            using (performanceTrace.Measure(CapturePerformancePhase.UiAutomationRead))
            {
                automationSegments = await _backgroundProcessingWorker.ExecuteAsync(
                    () => _uiAutomationTextService.GetSegments(captureRegion),
                    cancellationToken);
            }
        }

        var fusedSegments = automationSegments.Count == 0
            ? ocrSegments
            : await _backgroundProcessingWorker.ExecuteAsync(
                () => _layoutObservationFusionService.Merge(ocrSegments, automationSegments),
                cancellationToken);

        IReadOnlyList<LayoutTextSegment> visuallyRefinedSegments = fusedSegments;
        if (processingMode != CaptureProcessingMode.DocumentLike)
        {
            using (performanceTrace.Measure(CapturePerformancePhase.VisualRefinement))
            {
                visuallyRefinedSegments = await _backgroundProcessingWorker.ExecuteAsync(
                    () => _visualSegmentRefinementService.Refine(fusedSegments, snapshotBitmap, processingMode),
                    cancellationToken);
            }
        }

        IReadOnlyList<LayoutTextSegment> segments;
        using (performanceTrace.Measure(CapturePerformancePhase.SegmentRefinement))
        {
            segments = await _segmentOcrRefinementService.RefineAsync(
                visuallyRefinedSegments,
                snapshotBitmap,
                preferredOcrLanguageTag,
                processingMode switch
                {
                    CaptureProcessingMode.DocumentLike => SegmentRefinementMode.DocumentLike,
                    CaptureProcessingMode.UiDense => SegmentRefinementMode.UiDense,
                    _ => SegmentRefinementMode.Mixed
                },
                performanceTrace,
                cancellationToken);
        }

        var cache = await PretranslatePlainSourceTextsAsync(
            segments,
            sourceLanguage,
            targetLanguage,
            performanceTrace,
            cancellationToken);
        var allowStyledTranslation = processingMode != CaptureProcessingMode.DocumentLike;
        var allowUiRecovery = processingMode != CaptureProcessingMode.DocumentLike;

        foreach (var segment in segments)
        {
            var workingSegment = segment;
            var sourceText = TextRegionIntelligence.NormalizeWhitespace(workingSegment.Text);
            if (string.IsNullOrWhiteSpace(sourceText) || segment.Bounds.IsEmpty)
            {
                continue;
            }

            SegmentTranslationResult translation;
            SegmentTranslationResult? styledTranslation = null;
            if (allowStyledTranslation)
            {
                styledTranslation = await TryTranslateStyledSegmentAsync(
                    workingSegment,
                    snapshotBitmap,
                    sourceLanguage,
                    targetLanguage,
                    performanceTrace,
                    cancellationToken);
            }
            if (styledTranslation is not null)
            {
                translation = styledTranslation.Value;
            }
            else
            {
                if (!cache.TryGetValue(sourceText, out var translatedText))
                {
                    translatedText = await TranslateSourceTextAsync(
                        sourceText,
                        sourceLanguage,
                        targetLanguage,
                        performanceTrace,
                        cancellationToken);

                    cache[sourceText] = translatedText;
                }

                translation = new SegmentTranslationResult(translatedText);
            }

            if (string.IsNullOrWhiteSpace(translation.TranslatedText))
            {
                continue;
            }

            if (allowUiRecovery
                && workingSegment.Kind == TextLayoutKind.UiLabel
                && UiLabelTranslationRecoveryHeuristics.ShouldRetryAfterTranslation(
                    sourceText,
                    translation.TranslatedText,
                    _captureProcessingModeClassifier.IsDenseUiRowSegment(workingSegment, segments)))
            {
                LayoutTextSegment recoveredSegment;
                using (performanceTrace.Measure(CapturePerformancePhase.UiLabelRecovery))
                {
                    recoveredSegment = await _segmentOcrRefinementService.RecoverLowConfidenceUiLabelAsync(
                        workingSegment,
                        snapshotBitmap,
                        preferredOcrLanguageTag,
                        performanceTrace,
                        cancellationToken);
                }
                var recoveredSourceText = TextRegionIntelligence.NormalizeWhitespace(recoveredSegment.Text);
                if (!string.IsNullOrWhiteSpace(recoveredSourceText)
                    && !string.Equals(recoveredSourceText, sourceText, StringComparison.OrdinalIgnoreCase))
                {
                    workingSegment = recoveredSegment;
                    sourceText = recoveredSourceText;

                    if (!cache.TryGetValue(sourceText, out var recoveredTranslationText))
                    {
                        recoveredTranslationText = await TranslateSourceTextAsync(
                            sourceText,
                            sourceLanguage,
                            targetLanguage,
                            performanceTrace,
                            cancellationToken);
                        cache[sourceText] = recoveredTranslationText;
                    }

                    translation = new SegmentTranslationResult(recoveredTranslationText);
                    if (string.IsNullOrWhiteSpace(translation.TranslatedText))
                    {
                        continue;
                    }
                }
            }

            var renderedLines = LayoutTranslatedSegment(workingSegment, translation);
            overlayLines.AddRange(renderedLines);
        }

        return new OverlayBuildResult(
            overlayLines,
            BuildRecognizedPreview(segments));
    }

    private static string BuildRecognizedPreview(IReadOnlyList<LayoutTextSegment> segments)
    {
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            segments.Select(segment =>
            {
                var prefix = segment.Kind switch
                {
                    TextLayoutKind.UiLabel => "[UI]",
                    TextLayoutKind.Paragraph => "[P]",
                    _ => "[L]"
                };

                return $"{prefix} {TextRegionIntelligence.NormalizeWhitespace(segment.Text)}";
            }));
    }

    private static bool IsDenseUiRowSegment(
        LayoutTextSegment segment,
        IReadOnlyList<LayoutTextSegment> allSegments)
    {
        if (segment.Kind != TextLayoutKind.UiLabel)
        {
            return false;
        }

        var neighborCount = allSegments.Count(candidate =>
            !ReferenceEquals(candidate, segment)
            && candidate.Kind == TextLayoutKind.UiLabel
            && AreLikelyInSameUiRow(segment, candidate));

        return neighborCount >= 2;
    }

    private static bool AreLikelyInSameUiRow(LayoutTextSegment left, LayoutTextSegment right)
    {
        var leftCenterY = left.Bounds.Top + (left.Bounds.Height / 2);
        var rightCenterY = right.Bounds.Top + (right.Bounds.Height / 2);
        var centerDelta = Math.Abs(leftCenterY - rightCenterY);
        var typicalHeight = Math.Max(1, Math.Max(left.Bounds.Height, right.Bounds.Height));

        return centerDelta <= Math.Max(10, typicalHeight * 0.8);
    }

    private static ScreenRegion CombineBounds(IReadOnlyList<OcrLineRegion> lines)
    {
        var left = lines.Min(line => line.Bounds.Left);
        var top = lines.Min(line => line.Bounds.Top);
        var right = lines.Max(line => line.Bounds.Right);
        var bottom = lines.Max(line => line.Bounds.Bottom);

        return new ScreenRegion(left, top, right - left, bottom - top);
    }

    private static ScreenRegion CombineBounds(IReadOnlyList<ScreenRegion> bounds)
    {
        var left = bounds.Min(item => item.Left);
        var top = bounds.Min(item => item.Top);
        var right = bounds.Max(item => item.Right);
        var bottom = bounds.Max(item => item.Bottom);

        return new ScreenRegion(left, top, right - left, bottom - top);
    }

    private async Task<SegmentTranslationResult?> TryTranslateStyledSegmentAsync(
        LayoutTextSegment segment,
        System.Drawing.Bitmap snapshotBitmap,
        string? sourceLanguage,
        string targetLanguage,
        CapturePerformanceTrace performanceTrace,
        CancellationToken cancellationToken)
    {
        // Mixed inline styling inside long or multi-line paragraphs is too unstable with the
        // current OCR + offline translation pipeline. Keep color preservation only for short,
        // single-line segments where we can preserve it predictably.
        if (segment.SourceLines.Count != 1)
        {
            return null;
        }

        var segmentWords = FlattenSegmentWords(segment);
        if (segmentWords.Count < 2 || segmentWords.Count > 8)
        {
            return null;
        }

        var styleFragments = DetectInlineStyleFragments(snapshotBitmap, segmentWords);
        if (styleFragments.Count == 0)
        {
            return null;
        }

        var colorFragments = styleFragments
            .Where(fragment => fragment.PreserveSourceColor)
            .ToArray();
        if (colorFragments.Length != 1)
        {
            return null;
        }

        var fragmentWordCount = colorFragments[0].EndWordIndex - colorFragments[0].StartWordIndex + 1;
        if (fragmentWordCount > Math.Max(1, segmentWords.Count / 2))
        {
            return null;
        }

        var markedSourceText = BuildMarkedSourceText(segmentWords, colorFragments, out var plainSourceText);
        if (string.IsNullOrWhiteSpace(markedSourceText) || string.IsNullOrWhiteSpace(plainSourceText))
        {
            return null;
        }

        var effectiveSourceLanguage = ResolveSourceLanguage(sourceLanguage, plainSourceText);
        if (string.Equals(effectiveSourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        performanceTrace.Counters.IncrementTranslationRequests();
        TranslationResponse response;
        using (performanceTrace.Measure(CapturePerformancePhase.TranslationBatch))
        {
            response = await _translationService.TranslateAsync(
                markedSourceText,
                effectiveSourceLanguage,
                targetLanguage,
                cancellationToken);
        }
        if (!TryParseStyledTranslatedRuns(response.TranslatedText, colorFragments, out var translatedText, out var inlineRuns))
        {
            return null;
        }

        return new SegmentTranslationResult(translatedText, inlineRuns);
    }

    private static IReadOnlyList<SegmentWord> FlattenSegmentWords(LayoutTextSegment segment)
    {
        var words = new List<SegmentWord>();
        var index = 0;

        for (var lineIndex = 0; lineIndex < segment.SourceLines.Count; lineIndex++)
        {
            var line = segment.SourceLines[lineIndex];
            var lineWords = line.Words?
                .Where(word => !string.IsNullOrWhiteSpace(word.Text) && !word.Bounds.IsEmpty)
                .OrderBy(word => word.Bounds.Left)
                .ToArray();

            if (lineWords is null || lineWords.Length == 0)
            {
                continue;
            }

            foreach (var word in lineWords)
            {
                words.Add(new SegmentWord(index, lineIndex, word.Text.Trim(), word.Bounds));
                index++;
            }
        }

        return words;
    }

    private static IReadOnlyList<InlineStyleSourceFragment> DetectInlineStyleFragments(
        System.Drawing.Bitmap snapshotBitmap,
        IReadOnlyList<SegmentWord> segmentWords)
    {
        var analyzedWords = new List<AnalyzedSegmentWord>();
        foreach (var word in segmentWords)
        {
            if (TryAnalyzeWordStyle(snapshotBitmap, word, out var analyzedWord))
            {
                analyzedWords.Add(analyzedWord);
            }
        }

        if (analyzedWords.Count < 2)
        {
            return Array.Empty<InlineStyleSourceFragment>();
        }

        if (!TryDetermineBaselineWordStyle(analyzedWords, out var baselineColor, out var baselineInkRatio, out var medianHeight))
        {
            return Array.Empty<InlineStyleSourceFragment>();
        }

        var styledWords = analyzedWords
            .Select(word =>
            {
                var preserveColor = ColorDistance(word.TextColor, baselineColor) >= 64 && word.Contrast >= 46;
                var preserveBold = word.InkRatio >= Math.Max(0.08, baselineInkRatio * 1.22)
                                   && word.Word.Bounds.Height >= medianHeight * 0.84;
                return new StyledAnalyzedWord(word, preserveColor, preserveBold);
            })
            .Where(word => word.PreserveColor || word.PreserveBold)
            .ToArray();
        if (styledWords.Length == 0)
        {
            return Array.Empty<InlineStyleSourceFragment>();
        }

        var groups = new List<InlineStyleSourceFragment>();
        StyledAnalyzedWord? current = null;
        var currentWords = new List<StyledAnalyzedWord>();
        var nextId = 1;

        foreach (var styledWord in styledWords)
        {
            if (current is not null && CanExtendStyledWordGroup(current.Value, styledWord))
            {
                currentWords.Add(styledWord);
                current = styledWord;
                continue;
            }

            AddInlineStyleFragment(groups, currentWords, ref nextId);
            currentWords = [styledWord];
            current = styledWord;
        }

        AddInlineStyleFragment(groups, currentWords, ref nextId);
        return FilterInlineStyleFragments(
            groups,
            segmentWords.Count,
            segmentWords.Select(word => word.LineIndex).Distinct().Count());
    }

    private static bool TryAnalyzeWordStyle(
        System.Drawing.Bitmap snapshotBitmap,
        SegmentWord word,
        out AnalyzedSegmentWord analyzedWord)
    {
        analyzedWord = default;

        var background = SampleWordBackground(snapshotBitmap, word.Bounds);
        if (!TrySampleWordForeground(snapshotBitmap, word.Bounds, background, out var textColor, out var inkRatio, out var contrast))
        {
            return false;
        }

        analyzedWord = new AnalyzedSegmentWord(word, textColor, inkRatio, contrast);
        return true;
    }

    private static System.Drawing.Color SampleWordBackground(System.Drawing.Bitmap bitmap, ScreenRegion bounds)
    {
        var padX = Math.Clamp((int)Math.Round(bounds.Height * 0.55), 2, 10);
        var padY = Math.Clamp((int)Math.Round(bounds.Height * 0.35), 2, 8);
        var outerLeft = Math.Max(0, (int)Math.Floor(bounds.Left) - padX);
        var outerTop = Math.Max(0, (int)Math.Floor(bounds.Top) - padY);
        var outerRight = Math.Min(bitmap.Width, (int)Math.Ceiling(bounds.Right) + padX);
        var outerBottom = Math.Min(bitmap.Height, (int)Math.Ceiling(bounds.Bottom) + padY);

        if (outerRight <= outerLeft || outerBottom <= outerTop)
        {
            return System.Drawing.Color.White;
        }

        long r = 0;
        long g = 0;
        long b = 0;
        long count = 0;

        for (var y = outerTop; y < outerBottom; y++)
        {
            for (var x = outerLeft; x < outerRight; x++)
            {
                if (x >= bounds.Left && x <= bounds.Right && y >= bounds.Top && y <= bounds.Bottom)
                {
                    continue;
                }

                var pixel = bitmap.GetPixel(x, y);
                r += pixel.R;
                g += pixel.G;
                b += pixel.B;
                count++;
            }
        }

        if (count == 0)
        {
            return System.Drawing.Color.White;
        }

        return System.Drawing.Color.FromArgb(
            (int)(r / count),
            (int)(g / count),
            (int)(b / count));
    }

    private static bool TrySampleWordForeground(
        System.Drawing.Bitmap bitmap,
        ScreenRegion bounds,
        System.Drawing.Color background,
        out System.Drawing.Color textColor,
        out double inkRatio,
        out double contrast)
    {
        textColor = background;
        inkRatio = 0;
        contrast = 0;

        var x0 = Math.Clamp((int)Math.Floor(bounds.Left), 0, Math.Max(0, bitmap.Width - 1));
        var y0 = Math.Clamp((int)Math.Floor(bounds.Top), 0, Math.Max(0, bitmap.Height - 1));
        var x1 = Math.Clamp((int)Math.Ceiling(bounds.Right), 0, bitmap.Width);
        var y1 = Math.Clamp((int)Math.Ceiling(bounds.Bottom), 0, bitmap.Height);
        if (x1 <= x0 || y1 <= y0)
        {
            return false;
        }

        long weightedR = 0;
        long weightedG = 0;
        long weightedB = 0;
        long weightSum = 0;
        var total = 0;
        var changed = 0;

        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                total++;
                var pixel = bitmap.GetPixel(x, y);
                var difference = ColorDistance(pixel, background);
                if (difference < 32)
                {
                    continue;
                }

                changed++;
                var weight = difference * difference;
                weightedR += pixel.R * weight;
                weightedG += pixel.G * weight;
                weightedB += pixel.B * weight;
                weightSum += weight;
            }
        }

        if (weightSum <= 0 || total == 0 || changed < 4)
        {
            return false;
        }

        inkRatio = (double)changed / total;
        textColor = System.Drawing.Color.FromArgb(
            (int)(weightedR / weightSum),
            (int)(weightedG / weightSum),
            (int)(weightedB / weightSum));
        contrast = ColorDistance(textColor, background);
        return contrast >= 28 && inkRatio >= 0.04;
    }

    private static bool TryDetermineBaselineWordStyle(
        IReadOnlyList<AnalyzedSegmentWord> analyzedWords,
        out System.Drawing.Color baselineColor,
        out double baselineInkRatio,
        out double medianHeight)
    {
        baselineColor = System.Drawing.Color.White;
        baselineInkRatio = 0;
        medianHeight = 0;

        IReadOnlyList<AnalyzedSegmentWord>? bestCluster = null;
        foreach (var candidate in analyzedWords)
        {
            var cluster = analyzedWords
                .Where(word => ColorDistance(word.TextColor, candidate.TextColor) <= 64)
                .ToArray();

            if (bestCluster is null || cluster.Length > bestCluster.Count)
            {
                bestCluster = cluster;
            }
        }

        if (bestCluster is null || bestCluster.Count == 0)
        {
            return false;
        }

        baselineColor = AverageColor(bestCluster.Select(word => word.TextColor));
        var inkRatios = bestCluster.Select(word => word.InkRatio).OrderBy(value => value).ToArray();
        baselineInkRatio = inkRatios[inkRatios.Length / 2];
        var heights = analyzedWords.Select(word => word.Word.Bounds.Height).OrderBy(value => value).ToArray();
        medianHeight = heights[heights.Length / 2];
        return true;
    }

    private static bool CanExtendStyledWordGroup(StyledAnalyzedWord current, StyledAnalyzedWord candidate)
    {
        if (candidate.Word.Word.Index != current.Word.Word.Index + 1)
        {
            return false;
        }

        if (candidate.Word.Word.LineIndex != current.Word.Word.LineIndex)
        {
            return false;
        }

        if (candidate.PreserveColor != current.PreserveColor || candidate.PreserveBold != current.PreserveBold)
        {
            return false;
        }

        if (candidate.PreserveColor && ColorDistance(candidate.Word.TextColor, current.Word.TextColor) > 72)
        {
            return false;
        }

        return true;
    }

    private static void AddInlineStyleFragment(
        List<InlineStyleSourceFragment> fragments,
        IReadOnlyList<StyledAnalyzedWord> styledWords,
        ref int nextId)
    {
        if (styledWords.Count == 0)
        {
            return;
        }

        var start = styledWords.Min(word => word.Word.Word.Index);
        var end = styledWords.Max(word => word.Word.Word.Index);
        var bounds = CombineBounds(styledWords.Select(word => word.Word.Word.Bounds).ToArray());
        var preserveColor = styledWords.Any(word => word.PreserveColor);
        var preserveBold = styledWords.Any(word => word.PreserveBold);
        fragments.Add(new InlineStyleSourceFragment(nextId, start, end, bounds, preserveColor, preserveBold));
        nextId++;
    }

    private static IReadOnlyList<InlineStyleSourceFragment> FilterInlineStyleFragments(
        IReadOnlyList<InlineStyleSourceFragment> fragments,
        int totalWordCount,
        int lineCount)
    {
        if (fragments.Count == 0)
        {
            return Array.Empty<InlineStyleSourceFragment>();
        }

        IEnumerable<InlineStyleSourceFragment> filtered = fragments;
        if (totalWordCount >= 8 || lineCount > 1)
        {
            filtered = filtered.Where(fragment =>
            {
                var fragmentWordCount = fragment.EndWordIndex - fragment.StartWordIndex + 1;
                if (fragment.PreserveSourceColor)
                {
                    return fragmentWordCount <= Math.Max(1, totalWordCount / 2);
                }

                return fragment.PreserveBold
                       && lineCount == 1
                       && fragmentWordCount <= 3;
            });
        }

        var result = filtered
            .OrderBy(fragment => fragment.StartWordIndex)
            .ToList();
        if (result.Count == 0)
        {
            return Array.Empty<InlineStyleSourceFragment>();
        }

        var styledWordCount = result.Sum(fragment => fragment.EndWordIndex - fragment.StartWordIndex + 1);
        var maxCoverage = totalWordCount >= 10 || lineCount > 1 ? 0.4 : 0.7;
        if (styledWordCount > Math.Max(4, (int)Math.Ceiling(totalWordCount * maxCoverage)))
        {
            result = result
                .Where(fragment => fragment.PreserveSourceColor)
                .OrderBy(fragment => fragment.StartWordIndex)
                .ToList();

            if (result.Count == 0)
            {
                return Array.Empty<InlineStyleSourceFragment>();
            }

            styledWordCount = result.Sum(fragment => fragment.EndWordIndex - fragment.StartWordIndex + 1);
            if (styledWordCount > Math.Max(4, (int)Math.Ceiling(totalWordCount * 0.35)))
            {
                return Array.Empty<InlineStyleSourceFragment>();
            }
        }

        if (result.Count > 4)
        {
            result = result
                .OrderByDescending(fragment => fragment.PreserveSourceColor)
                .ThenByDescending(fragment => fragment.EndWordIndex - fragment.StartWordIndex + 1)
                .Take(4)
                .OrderBy(fragment => fragment.StartWordIndex)
                .ToList();
        }

        return result;
    }

    private static string BuildMarkedSourceText(
        IReadOnlyList<SegmentWord> segmentWords,
        IReadOnlyList<InlineStyleSourceFragment> styleFragments,
        out string plainSourceText)
    {
        var startTags = styleFragments.ToDictionary(fragment => fragment.StartWordIndex, fragment => fragment);
        var endTags = styleFragments.ToDictionary(fragment => fragment.EndWordIndex, fragment => fragment);
        var markedBuilder = new StringBuilder();
        var plainBuilder = new StringBuilder();

        for (var i = 0; i < segmentWords.Count; i++)
        {
            var word = segmentWords[i];
            if (i > 0)
            {
                var separator = NeedSpaceBetweenTokens(segmentWords[i - 1].Text, word.Text) ? " " : string.Empty;
                markedBuilder.Append(separator);
                plainBuilder.Append(separator);
            }

            if (startTags.TryGetValue(word.Index, out var startFragment))
            {
                markedBuilder.Append($"<ts{startFragment.Id}>");
            }

            markedBuilder.Append(word.Text);
            plainBuilder.Append(word.Text);

            if (endTags.TryGetValue(word.Index, out var endFragment))
            {
                markedBuilder.Append($"</ts{endFragment.Id}>");
            }
        }

        plainSourceText = plainBuilder.ToString();
        return markedBuilder.ToString();
    }

    private static bool TryParseStyledTranslatedRuns(
        string translatedText,
        IReadOnlyList<InlineStyleSourceFragment> styleFragments,
        out string plainTranslatedText,
        out IReadOnlyList<TranslatedInlineRun> inlineRuns)
    {
        plainTranslatedText = string.Empty;
        inlineRuns = Array.Empty<TranslatedInlineRun>();

        if (string.IsNullOrWhiteSpace(translatedText))
        {
            return false;
        }

        var fragmentMap = styleFragments.ToDictionary(fragment => fragment.Id);
        var matches = StyledMarkerRegex.Matches(translatedText);
        if (matches.Count == 0)
        {
            return false;
        }

        var runs = new List<TranslatedInlineRun>();
        var plainBuilder = new StringBuilder();
        var position = 0;
        InlineStyleSourceFragment? activeFragment = null;

        foreach (Match match in matches)
        {
            if (match.Index > position)
            {
                AppendInlineRun(
                    runs,
                    plainBuilder,
                    translatedText[position..match.Index],
                    activeFragment);
            }

            var id = int.Parse(match.Groups["id"].Value);
            if (!fragmentMap.TryGetValue(id, out var fragment))
            {
                return false;
            }

            var isClosing = string.Equals(match.Groups[1].Value, "/", StringComparison.Ordinal);
            if (!isClosing)
            {
                if (activeFragment is not null)
                {
                    return false;
                }

                activeFragment = fragment;
            }
            else
            {
                if (activeFragment is null || activeFragment.Value.Id != id)
                {
                    return false;
                }

                activeFragment = null;
            }

            position = match.Index + match.Length;
        }

        if (position < translatedText.Length)
        {
            AppendInlineRun(
                runs,
                plainBuilder,
                translatedText[position..],
                activeFragment);
        }

        if (activeFragment is not null)
        {
            return false;
        }

        var mergedRuns = MergeAdjacentInlineRuns(runs);
        if (!mergedRuns.Any(run => run.PreserveSourceColor || run.PreserveBold))
        {
            return false;
        }

        plainTranslatedText = string.Concat(mergedRuns.Select(run => run.Text));
        inlineRuns = mergedRuns;
        return !string.IsNullOrWhiteSpace(plainTranslatedText);
    }

    private static void AppendInlineRun(
        List<TranslatedInlineRun> runs,
        StringBuilder plainBuilder,
        string text,
        InlineStyleSourceFragment? activeFragment)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        plainBuilder.Append(text);
        if (activeFragment is null)
        {
            runs.Add(new TranslatedInlineRun(text));
            return;
        }

        runs.Add(new TranslatedInlineRun(
            text,
            activeFragment.Value.Bounds,
            activeFragment.Value.PreserveSourceColor,
            activeFragment.Value.PreserveBold));
    }

    private static IReadOnlyList<TranslatedInlineRun> MergeAdjacentInlineRuns(IReadOnlyList<TranslatedInlineRun> runs)
    {
        if (runs.Count == 0)
        {
            return Array.Empty<TranslatedInlineRun>();
        }

        var merged = new List<TranslatedInlineRun> { runs[0] };
        foreach (var run in runs.Skip(1))
        {
            var last = merged[^1];
            if (last.PreserveSourceColor == run.PreserveSourceColor
                && last.PreserveBold == run.PreserveBold
                && Nullable.Equals(last.SourceBounds, run.SourceBounds))
            {
                merged[^1] = last with { Text = last.Text + run.Text };
                continue;
            }

            merged.Add(run);
        }

        return merged;
    }

    private static System.Drawing.Color AverageColor(IEnumerable<System.Drawing.Color> colors)
    {
        long r = 0;
        long g = 0;
        long b = 0;
        long count = 0;

        foreach (var color in colors)
        {
            r += color.R;
            g += color.G;
            b += color.B;
            count++;
        }

        if (count == 0)
        {
            return System.Drawing.Color.White;
        }

        return System.Drawing.Color.FromArgb(
            (int)(r / count),
            (int)(g / count),
            (int)(b / count));
    }

    private static int ColorDistance(System.Drawing.Color left, System.Drawing.Color right)
    {
        return Math.Abs(left.R - right.R)
               + Math.Abs(left.G - right.G)
               + Math.Abs(left.B - right.B);
    }

    private static bool NeedSpaceBetweenTokens(string previous, string current)
    {
        if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(current))
        {
            return false;
        }

        if (previous.EndsWith('-') && char.IsLetterOrDigit(current[0]))
        {
            return false;
        }

        if (current[0] is '.' or ',' or ':' or ';' or '!' or '?' or ')' or ']' or '"' or '\'')
        {
            return false;
        }

        if (previous[^1] is '(' or '[' or '"' or '\'')
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyList<TranslatedOcrLine> LayoutTranslatedSegment(
        LayoutTextSegment segment,
        SegmentTranslationResult translation)
    {
        var preferredFontSize = EstimatePreferredFontSize(segment.SourceLines);

        if (segment.SourceLines.Count == 1)
        {
            var sourceLine = segment.SourceLines[0];
            return
            [
                new TranslatedOcrLine(
                    sourceLine.Text.Trim(),
                    translation.TranslatedText.Trim(),
                    sourceLine.Bounds,
                    1,
                    preferredFontSize,
                    translation.InlineRuns,
                    segment.Kind)
            ];
        }

        return
        [
            new TranslatedOcrLine(
                segment.Text,
                translation.TranslatedText.Trim(),
                segment.Bounds,
                segment.SourceLines.Count,
                preferredFontSize,
                translation.InlineRuns,
                segment.Kind)
        ];
    }

    private async Task<string> TranslateSourceTextAsync(
        string sourceText,
        string? sourceLanguage,
        string targetLanguage,
        CapturePerformanceTrace performanceTrace,
        CancellationToken cancellationToken)
    {
        var normalized = TextRegionIntelligence.NormalizeWhitespace(sourceText);
        if (TryTranslateCommonUiLabel(normalized, out var commonUiTranslation))
        {
            return ApplySourceStyle(sourceText, commonUiTranslation);
        }

        var effectiveSourceLanguage = ResolveSourceLanguage(sourceLanguage, normalized);
        if (string.Equals(effectiveSourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return ApplySourceStyle(sourceText, normalized);
        }

        if (_translationTextCache.TryGet(effectiveSourceLanguage, targetLanguage, normalized, out var cachedTranslatedText))
        {
            return PostProcessTranslatedText(sourceText, cachedTranslatedText);
        }

        performanceTrace.Counters.IncrementTranslationRequests();
        TranslationResponse response;
        using (performanceTrace.Measure(CapturePerformancePhase.TranslationBatch))
        {
            response = await _translationService.TranslateAsync(
                normalized,
                effectiveSourceLanguage,
                targetLanguage,
                cancellationToken);
        }
        var translatedText = response.TranslatedText;
        _translationTextCache.Set(effectiveSourceLanguage, targetLanguage, normalized, translatedText);

        return PostProcessTranslatedText(sourceText, translatedText);
    }

    private async Task<Dictionary<string, string>> PretranslatePlainSourceTextsAsync(
        IReadOnlyList<LayoutTextSegment> segments,
        string? sourceLanguage,
        string targetLanguage,
        CapturePerformanceTrace performanceTrace,
        CancellationToken cancellationToken)
    {
        var translatedBySource = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingByLanguage = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var segment in segments)
        {
            var sourceText = TextRegionIntelligence.NormalizeWhitespace(segment.Text);
            if (string.IsNullOrWhiteSpace(sourceText) || translatedBySource.ContainsKey(sourceText))
            {
                continue;
            }

            if (TryTranslateCommonUiLabel(sourceText, out var commonUiTranslation))
            {
                translatedBySource[sourceText] = ApplySourceStyle(sourceText, commonUiTranslation);
                continue;
            }

            var effectiveSourceLanguage = ResolveSourceLanguage(sourceLanguage, sourceText);
            if (string.Equals(effectiveSourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                translatedBySource[sourceText] = ApplySourceStyle(sourceText, sourceText);
                continue;
            }

            if (_translationTextCache.TryGet(effectiveSourceLanguage, targetLanguage, sourceText, out var cachedTranslatedText))
            {
                translatedBySource[sourceText] = PostProcessTranslatedText(sourceText, cachedTranslatedText);
                continue;
            }

            var languageKey = effectiveSourceLanguage ?? string.Empty;
            if (!pendingByLanguage.TryGetValue(languageKey, out var texts))
            {
                texts = [];
                pendingByLanguage[languageKey] = texts;
            }

            texts.Add(sourceText);
        }

        foreach (var pendingGroup in pendingByLanguage)
        {
            var effectiveSourceLanguage = string.IsNullOrWhiteSpace(pendingGroup.Key)
                ? null
                : pendingGroup.Key;
            performanceTrace.Counters.IncrementTranslationRequests();
            IReadOnlyList<TranslationResponse> responses;
            using (performanceTrace.Measure(CapturePerformancePhase.TranslationBatch))
            {
                responses = await TranslationBatchCoordinator.TranslateAsync(
                    _translationService,
                    pendingGroup.Value,
                    effectiveSourceLanguage,
                    targetLanguage,
                    cancellationToken);
            }

            for (var index = 0; index < pendingGroup.Value.Count; index++)
            {
                var sourceText = pendingGroup.Value[index];
                var translatedText = index < responses.Count
                    ? responses[index].TranslatedText
                    : string.Empty;
                _translationTextCache.Set(effectiveSourceLanguage, targetLanguage, sourceText, translatedText);
                translatedBySource[sourceText] = PostProcessTranslatedText(sourceText, translatedText);
            }
        }

        return translatedBySource;
    }

    private async Task<TranslationResponse> TranslateWholeTextAsync(
        string sourceText,
        string? sourceLanguage,
        string targetLanguage,
        CapturePerformanceTrace performanceTrace,
        CancellationToken cancellationToken)
    {
        var normalized = TextRegionIntelligence.NormalizeWhitespace(sourceText);
        var effectiveSourceLanguage = ResolveSourceLanguage(sourceLanguage, normalized);
        if (string.Equals(effectiveSourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return new TranslationResponse(
                ApplySourceStyle(sourceText, normalized),
                _translationService.DisplayName,
                "Перевод не требовался: язык источника совпал с целевым.");
        }

        if (_translationTextCache.TryGet(effectiveSourceLanguage, targetLanguage, normalized, out var cachedTranslatedText))
        {
            return new TranslationResponse(
                PostProcessTranslatedText(sourceText, cachedTranslatedText),
                _translationService.DisplayName,
                null);
        }

        performanceTrace.Counters.IncrementTranslationRequests();
        TranslationResponse response;
        using (performanceTrace.Measure(CapturePerformancePhase.TranslationBatch))
        {
            response = await _translationService.TranslateAsync(
                normalized,
                effectiveSourceLanguage,
                targetLanguage,
                cancellationToken);
        }
        _translationTextCache.Set(effectiveSourceLanguage, targetLanguage, normalized, response.TranslatedText);

        return new TranslationResponse(
            PostProcessTranslatedText(sourceText, response.TranslatedText),
            response.ProviderName,
            response.Note);
    }

    private static bool TryTranslateCommonUiLabel(string sourceText, out string translation)
    {
        var normalizedSource = TextRegionIntelligence.NormalizeWhitespace(sourceText);
        var exactMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Back"] = "Назад",
            ["Next"] = "Далее",
            ["Cancel"] = "Отмена",
            ["OK"] = "ОК",
            ["Yes"] = "Да",
            ["No"] = "Нет",
            ["Continue"] = "Продолжить",
            ["Finish"] = "Завершить",
            ["Install"] = "Установить",
            ["Browse"] = "Обзор",
            ["Apply"] = "Применить",
            ["Retry"] = "Повторить",
            ["Skip"] = "Пропустить",
            ["Accept"] = "Принять",
            ["Decline"] = "Отклонить",
            ["Update"] = "Обновить",
            ["Previous"] = "Назад",
            ["Sales"] = "Продажи",
            ["Archives"] = "Архивы",
            ["Attributes"] = "Атрибуты",
            ["Types"] = "Типы",
            ["Parts"] = "Части",
            ["Charts"] = "Графики",
            ["Calculator"] = "Калькулятор",
            ["Calendar"] = "Календарь",
            ["MediaInfo"] = "МедиаИнфо",
            ["Patches"] = "Патчи",
            ["Tags"] = "Теги",
            ["Filter"] = "Фильтр",
            ["List"] = "Список",
            ["Files"] = "Файлы",
            ["File"] = "Файл",
            ["Path"] = "Путь",
            ["Size"] = "Размер",
            ["Load"] = "Загрузить",
            ["Clear"] = "Очистить",
            ["Rename"] = "Переименование",
            ["Episodes"] = "Эпизоды",
            ["Subtitles"] = "Субтитры",
            ["SFV"] = "SFV",
            ["Extract All"] = "Извлечь все",
            ["Support"] = "Поддержка",
            ["Support us"] = "Поддержите нас",
            ["Settings"] = "Настройки",
            ["Search"] = "Поиск",
            ["Home"] = "Главная",
            ["Profile"] = "Профиль",
            ["Account"] = "Аккаунт",
            ["Reports"] = "Отчеты",
            ["Analytics"] = "Аналитика",
            ["Dashboard"] = "Панель",
            ["Notifications"] = "Уведомления",
            ["Help"] = "Помощь",
            ["About"] = "О программе",
            ["Menu"] = "Меню",
            ["Open"] = "Открыть",
            ["Close"] = "Закрыть",
            ["Save"] = "Сохранить",
            ["Delete"] = "Удалить",
            ["Edit"] = "Изменить",
            ["View"] = "Вид",
            ["Download"] = "Скачать",
            ["Upload"] = "Загрузить"
        };

        if (TryTranslateCommonUiCommand(normalizedSource, exactMap, out translation))
        {
            return true;
        }

        if (TextRegionIntelligence.TryParseCommonUiLabel(normalizedSource, out var prefix, out var canonicalCore, out var suffix)
            && exactMap.TryGetValue(canonicalCore, out var translatedCore))
        {
            translation = TextRegionIntelligence.ComposeUiLabel(prefix, translatedCore, suffix);
            return true;
        }

        var tokens = normalizedSource
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length >= 2)
        {
            var translatedSequence = new List<string>(tokens.Length);
            for (var index = 0; index < tokens.Length;)
            {
                if (index + 1 < tokens.Length
                    && TextRegionIntelligence.TryParseCommonUiLabel(
                        $"{tokens[index]} {tokens[index + 1]}",
                        out _,
                        out var pairCore,
                        out _)
                    && exactMap.TryGetValue(pairCore, out var pairTranslation))
                {
                    translatedSequence.Add(pairTranslation);
                    index += 2;
                    continue;
                }

                if (TextRegionIntelligence.TryParseCommonUiLabel(tokens[index], out _, out var singleCore, out _)
                    && exactMap.TryGetValue(singleCore, out var singleTranslation))
                {
                    translatedSequence.Add(singleTranslation);
                    index++;
                    continue;
                }

                translatedSequence.Clear();
                break;
            }

            if (translatedSequence.Count >= 2)
            {
                translation = string.Join(" ", translatedSequence);
                return true;
            }
        }

        translation = string.Empty;
        return false;
    }

    private static bool TryTranslateCommonUiCommand(
        string sourceText,
        IReadOnlyDictionary<string, string> exactMap,
        out string translation)
    {
        translation = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return false;
        }

        var exactPhraseMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Keyboard shortcuts"] = "Горячие клавиши",
            ["Switch Monitor"] = "Переключить монитор",
            ["Reset to Defaults"] = "Сбросить до значений по умолчанию"
        };
        if (exactPhraseMap.TryGetValue(sourceText, out var exactPhraseTranslation))
        {
            translation = exactPhraseTranslation;
            return true;
        }

        if (TryTranslateVerbPhrase(sourceText, "Toggle ", "Переключить ", exactMap, out translation)
            || TryTranslateVerbPhrase(sourceText, "Enable ", "Включить ", exactMap, out translation)
            || TryTranslateVerbPhrase(sourceText, "Disable ", "Отключить ", exactMap, out translation)
            || TryTranslateVerbPhrase(sourceText, "Switch ", "Переключить ", exactMap, out translation))
        {
            return true;
        }

        if (sourceText.StartsWith("Reset to ", StringComparison.OrdinalIgnoreCase))
        {
            var operand = sourceText["Reset to ".Length..].Trim();
            if (TryTranslateUiOperand(operand, exactMap, out var translatedOperand))
            {
                translation = $"Сбросить до {translatedOperand}";
                return true;
            }
        }

        return false;
    }

    private static bool TryTranslateVerbPhrase(
        string sourceText,
        string sourcePrefix,
        string translatedPrefix,
        IReadOnlyDictionary<string, string> exactMap,
        out string translation)
    {
        translation = string.Empty;
        if (!sourceText.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var operand = sourceText[sourcePrefix.Length..].Trim();
        if (!TryTranslateUiOperand(operand, exactMap, out var translatedOperand))
        {
            return false;
        }

        translation = translatedPrefix + translatedOperand;
        return true;
    }

    private static bool TryTranslateUiOperand(
        string sourceText,
        IReadOnlyDictionary<string, string> exactMap,
        out string translation)
    {
        translation = string.Empty;
        var normalizedSource = TextRegionIntelligence.NormalizeWhitespace(sourceText);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            return false;
        }

        var phraseMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Monitor"] = "монитор",
            ["Keyboard shortcuts"] = "горячие клавиши",
            ["VR Mode"] = "режим VR",
            ["VR Passthrough"] = "режим сквозного обзора VR",
            ["Hand Passthrough"] = "сквозной обзор рук",
            ["Desk Passthrough"] = "сквозной обзор стола",
            ["Performance Overlay"] = "оверлей производительности",
            ["Foveated Streaming"] = "фовеативный стриминг",
            ["Defaults"] = "значений по умолчанию"
        };

        if (phraseMap.TryGetValue(normalizedSource, out var phraseTranslation))
        {
            translation = phraseTranslation;
            return true;
        }

        if (TextRegionIntelligence.TryParseCommonUiLabel(normalizedSource, out var prefix, out var canonicalCore, out var suffix)
            && exactMap.TryGetValue(canonicalCore, out var translatedCore))
        {
            translation = TextRegionIntelligence.ComposeUiLabel(prefix, translatedCore, suffix);
            return true;
        }

        var tokens = normalizedSource
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
        {
            return false;
        }

        var translatedSequence = new List<string>(tokens.Length);
        for (var index = 0; index < tokens.Length;)
        {
            if (index + 1 < tokens.Length
                && phraseMap.TryGetValue($"{tokens[index]} {tokens[index + 1]}", out var pairPhraseTranslation))
            {
                translatedSequence.Add(pairPhraseTranslation);
                index += 2;
                continue;
            }

            if (TextRegionIntelligence.TryParseCommonUiLabel(tokens[index], out _, out var singleCore, out _)
                && exactMap.TryGetValue(singleCore, out var singleTranslation))
            {
                translatedSequence.Add(singleTranslation.ToLowerInvariant());
                index++;
                continue;
            }

            translatedSequence.Clear();
            break;
        }

        if (translatedSequence.Count == 0)
        {
            return false;
        }

        translation = string.Join(" ", translatedSequence);
        return true;
    }

    private static string? ResolveSourceLanguage(string? sourceLanguage, string sourceText)
    {
        if (!string.IsNullOrWhiteSpace(sourceLanguage))
        {
            return sourceLanguage;
        }

        return TextRegionIntelligence.GuessSourceLanguage(sourceText);
    }

    private byte[] BuildPreparedBackground(
        byte[] snapshotPngBytes,
        int width,
        int height,
        IReadOnlyList<TranslatedOcrLine> overlayLines)
    {
        var maskPngBytes = TextRegionIntelligence.BuildInpaintMask(width, height, overlayLines);
        return _imageInpaintingService.Inpaint(snapshotPngBytes, maskPngBytes);
    }

    private static string PostProcessTranslatedText(string sourceText, string translatedText)
    {
        var result = TextRegionIntelligence.NormalizeWhitespace(translatedText);
        if (string.IsNullOrWhiteSpace(result))
        {
            return result;
        }

        return ApplySourceStyle(sourceText, result);
    }

    private static string ApplySourceStyle(string sourceText, string translatedText)
    {
        var trimmedSource = sourceText.Trim();
        var trimmedTranslated = translatedText.Trim();

        if (string.IsNullOrWhiteSpace(trimmedTranslated))
        {
            return trimmedTranslated;
        }

        if (IsAllCaps(trimmedSource))
        {
            return trimmedTranslated.ToUpperInvariant();
        }

        if (IsTitleLike(trimmedSource))
        {
            return CapitalizeFirst(trimmedTranslated);
        }

        return trimmedTranslated;
    }

    private void CloseExistingPresentation()
    {
        _translationOverlayWindow?.Hide();
        _translationOverlayWindow?.Close();
        _translationOverlayWindow = null;

        _translationResultWindow?.Hide();
        _translationResultWindow?.Close();
        _translationResultWindow = null;
    }

    private async Task CloseSelectionOverlayAsync()
    {
        if (_selectionOverlayHost is null)
        {
            return;
        }

        var selector = _selectionOverlayHost;
        _selectionOverlayHost = null;

        selector.Closed -= SelectionOverlayHost_Closed;
        selector.LanguageSelectionChanged -= SelectionOverlayHost_LanguageSelectionChanged;
        selector.SelectAreaRequested -= SelectionOverlayHost_SelectAreaRequested;
        await selector.CloseAsync();
        selector.Dispose();
    }

    private async Task PrepareForFreshCaptureAsync(bool preserveSelectionOverlay = false)
    {
        if (!preserveSelectionOverlay)
        {
            await CloseSelectionOverlayAsync();
        }

        CloseExistingPresentation();
        await WaitForWindowsToDisappearAsync();
    }

    private void ClearDisplayedTexts()
    {
        RecognizedTextBox.Text = string.Empty;
        TranslatedTextBox.Text = string.Empty;
    }

    private async Task WaitForWindowsToDisappearAsync()
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(80);
    }

    private static bool IsAllCaps(string text)
    {
        var letters = text.Where(char.IsLetter).ToArray();
        return letters.Length > 0 && letters.All(char.IsUpper);
    }

    private static bool IsTitleLike(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var firstLetter = text.FirstOrDefault(char.IsLetter);
        return firstLetter != default && char.IsUpper(firstLetter);
    }

    private static string CapitalizeFirst(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLetter(text[i]))
            {
                continue;
            }

            return string.Concat(
                text[..i],
                char.ToUpperInvariant(text[i]),
                text[(i + 1)..]);
        }

        return text;
    }

    private static double EstimatePreferredFontSize(IReadOnlyList<OcrLineRegion> sourceLines)
    {
        var heights = sourceLines
            .Select(line => line.Bounds.Height)
            .Where(height => height > 0)
            .OrderBy(height => height)
            .ToArray();

        if (heights.Length == 0)
        {
            return 11.5;
        }

        var median = heights[heights.Length / 2];
        return Math.Round(Math.Clamp(median * 0.94, 9, 24), 1);
    }

    private async Task WarmUpTranslatorAsync()
    {
        if (_translationService is not IWarmableTranslationService warmable)
        {
            return;
        }

        try
        {
            await warmable.WarmUpAsync(CancellationToken.None);
        }
        catch
        {
        }

        await PreloadCurrentTranslationPairAsync();
    }

    private async Task PreloadCurrentTranslationPairAsync()
    {
        if (_translationService is not ITranslationPairWarmupService pairWarmupService)
        {
            return;
        }

        var sourceLanguage = GetSelectedLanguageCode(SourceLanguageCombo);
        var targetLanguage = GetSelectedLanguageCode(TargetLanguageCombo) ?? "ru";
        if (string.Equals(sourceLanguage, _lastWarmedSourceLanguage, StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetLanguage, _lastWarmedTargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastWarmedSourceLanguage = sourceLanguage;
        _lastWarmedTargetLanguage = targetLanguage;

        try
        {
            await pairWarmupService.WarmUpPairAsync(sourceLanguage, targetLanguage, CancellationToken.None);
        }
        catch
        {
        }
    }

    private void SwapTranslationService(ITranslationService newService)
    {
        DisposeTranslationService();
        _translationService = newService;
        _lastWarmedSourceLanguage = null;
        _lastWarmedTargetLanguage = null;
        _ = WarmUpTranslatorAsync();
    }

    private void DisposeTranslationService()
    {
        if (_translationService is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static byte[] EncodeSnapshotPng(System.Drawing.Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private readonly record struct SegmentTranslationResult(
        string TranslatedText,
        IReadOnlyList<TranslatedInlineRun>? InlineRuns = null);

    private readonly record struct SegmentWord(
        int Index,
        int LineIndex,
        string Text,
        ScreenRegion Bounds);

    private readonly record struct AnalyzedSegmentWord(
        SegmentWord Word,
        System.Drawing.Color TextColor,
        double InkRatio,
        double Contrast);

    private readonly record struct StyledAnalyzedWord(
        AnalyzedSegmentWord Word,
        bool PreserveColor,
        bool PreserveBold);

    private readonly record struct InlineStyleSourceFragment(
        int Id,
        int StartWordIndex,
        int EndWordIndex,
        ScreenRegion Bounds,
        bool PreserveSourceColor,
        bool PreserveBold);

    private readonly record struct OverlayBuildResult(
        IReadOnlyList<TranslatedOcrLine> Lines,
        string RecognizedPreview);

    private readonly record struct ProcessedCaptureData(
        string RecognizedPreview,
        TranslationResponse Translation,
        IReadOnlyList<TranslatedOcrLine> OverlayLines,
        byte[] PreparedBackgroundPngBytes);

}
