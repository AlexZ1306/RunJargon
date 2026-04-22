using System.Windows.Threading;
using Forms = System.Windows.Forms;
using RunJargon.App.Models;
using RunJargon.App.Windows;

namespace RunJargon.App.Services;

public sealed class SelectionOverlayHost : IDisposable
{
    private readonly IReadOnlyList<TranslationLanguageOption> _sourceLanguages;
    private readonly IReadOnlyList<TranslationLanguageOption> _targetLanguages;
    private readonly string? _selectedSourceLanguageCode;
    private readonly string? _selectedTargetLanguageCode;
    private readonly string _recognizedTextToCopy;
    private readonly string _translatedTextToCopy;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<SelectionToolbarWindow> _toolbarWindowTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _shutdownTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Dispatcher? _dispatcher;
    private SelectionOverlayWindow? _selectionSurfaceWindow;
    private Task<ScreenRegion?>? _currentSelectionTask;
    private bool _disposed;
    private bool _isClosed;

    public SelectionOverlayHost(
        IReadOnlyList<TranslationLanguageOption> sourceLanguages,
        IReadOnlyList<TranslationLanguageOption> targetLanguages,
        string? selectedSourceLanguageCode,
        string? selectedTargetLanguageCode,
        string recognizedTextToCopy,
        string translatedTextToCopy)
    {
        _sourceLanguages = sourceLanguages;
        _targetLanguages = targetLanguages;
        _selectedSourceLanguageCode = selectedSourceLanguageCode;
        _selectedTargetLanguageCode = selectedTargetLanguageCode;
        _recognizedTextToCopy = recognizedTextToCopy;
        _translatedTextToCopy = translatedTextToCopy;

        SelectedSourceLanguageCode = selectedSourceLanguageCode;
        SelectedTargetLanguageCode = selectedTargetLanguageCode;

        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "RunJargon Selection Overlay"
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public string? SelectedSourceLanguageCode { get; private set; }

    public string? SelectedTargetLanguageCode { get; private set; }

    public event EventHandler? Closed;
    public event EventHandler? LanguageSelectionChanged;
    public event EventHandler? SelectAreaRequested;

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _thread.Start();
        return _toolbarWindowTcs.Task;
    }

    public async Task BeginSelectionAsync()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || _isClosed)
        {
            throw new ObjectDisposedException(nameof(SelectionOverlayHost));
        }

        var toolbarWindow = await _toolbarWindowTcs.Task.ConfigureAwait(false);
        await dispatcher.InvokeAsync(() =>
        {
            toolbarWindow.SetBusy(false);
            toolbarWindow.SetSelectionActive(true);
            toolbarWindow.PositionOnScreen(Forms.Screen.FromPoint(Forms.Cursor.Position));

            StartSelectionSurfaceCore();

            if (!toolbarWindow.IsVisible)
            {
                toolbarWindow.Show();
            }

            toolbarWindow.BringToFront();
        }).Task.ConfigureAwait(false);
    }

    public async Task<ScreenRegion?> WaitForSelectionAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_currentSelectionTask is null)
        {
            return null;
        }

        return await _currentSelectionTask.ConfigureAwait(false);
    }

    public Task PrepareForCleanCaptureAsync()
    {
        return Task.CompletedTask;
    }

    public async Task ShowToolbarOnlyAsync(bool isBusy)
    {
        await InvokeOnToolbarAsync(toolbar =>
        {
            toolbar.SetSelectionActive(false);
            toolbar.SetBusy(isBusy);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    public async Task SetBusyAsync(bool isBusy)
    {
        await InvokeOnToolbarAsync(toolbar =>
        {
            toolbar.SetBusy(isBusy);
            if (!isBusy)
            {
                toolbar.SetSelectionActive(false);
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    public async Task UpdateCopyTextsAsync(string recognizedTextToCopy, string translatedTextToCopy)
    {
        await InvokeOnToolbarAsync(toolbar =>
        {
            toolbar.UpdateCopyTexts(recognizedTextToCopy, translatedTextToCopy);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    public async Task CloseAsync()
    {
        if (_disposed)
        {
            return;
        }

        SelectionToolbarWindow toolbarWindow;
        try
        {
            toolbarWindow = await _toolbarWindowTcs.Task.ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        await dispatcher.InvokeAsync(() =>
        {
            if (_isClosed)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                return;
            }

            if (_selectionSurfaceWindow is not null)
            {
                _selectionSurfaceWindow.Close();
                _selectionSurfaceWindow = null;
            }

            if (toolbarWindow.IsVisible)
            {
                toolbarWindow.Close();
            }
            else
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }).Task.ConfigureAwait(false);

        await _shutdownTcs.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CloseAsync().GetAwaiter().GetResult();
        _disposed = true;
    }

    private async Task InvokeOnToolbarAsync(Func<SelectionToolbarWindow, Task> operation)
    {
        if (_disposed)
        {
            return;
        }

        SelectionToolbarWindow toolbarWindow;
        try
        {
            toolbarWindow = await _toolbarWindowTcs.Task.ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        var dispatcher = _dispatcher;
        if (_isClosed || dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        await dispatcher.InvokeAsync(() => operation(toolbarWindow)).Task.Unwrap().ConfigureAwait(false);
    }

    private void ThreadMain()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            var toolbarWindow = new SelectionToolbarWindow(
                _sourceLanguages,
                _targetLanguages,
                _selectedSourceLanguageCode,
                _selectedTargetLanguageCode,
                _recognizedTextToCopy,
                _translatedTextToCopy);
            UpdateSelectedLanguages(toolbarWindow);

            toolbarWindow.LanguageSelectionChanged += ToolbarWindow_LanguageSelectionChanged;
            toolbarWindow.SelectAreaRequested += ToolbarWindow_SelectAreaRequested;
            toolbarWindow.SelectionCancelRequested += ToolbarWindow_SelectionCancelRequested;
            toolbarWindow.CloseRequested += ToolbarWindow_CloseRequested;
            toolbarWindow.Closed += ToolbarWindow_Closed;

            _toolbarWindowTcs.TrySetResult(toolbarWindow);
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _toolbarWindowTcs.TrySetException(ex);
        }
        finally
        {
            _shutdownTcs.TrySetResult(null);
        }
    }

    private void StartSelectionSurfaceCore()
    {
        if (_selectionSurfaceWindow is not null)
        {
            return;
        }

        var selectionSurface = new SelectionOverlayWindow(
            Array.Empty<TranslationLanguageOption>(),
            Array.Empty<TranslationLanguageOption>(),
            null,
            null,
            string.Empty,
            string.Empty,
            showToolbar: false);
        _selectionSurfaceWindow = selectionSurface;
        _currentSelectionTask = selectionSurface.WaitForSelectionAsync();
        _currentSelectionTask.ContinueWith(
            task =>
            {
                var dispatcher = _dispatcher;
                if (dispatcher is null)
                {
                    return;
                }

                dispatcher.Invoke(() =>
                {
                    _selectionSurfaceWindow = null;
                    var toolbarWindow = _toolbarWindowTcs.Task.Result;

                    if (task.IsCompletedSuccessfully && task.Result is ScreenRegion region)
                    {
                        toolbarWindow.PositionForRegion(region);
                        toolbarWindow.SetSelectionActive(false);
                        toolbarWindow.BringToFront();
                        return;
                    }

                    toolbarWindow.SetSelectionActive(false);
                    toolbarWindow.SetBusy(false);
                    toolbarWindow.BringToFront();
                });
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        selectionSurface.Show();
        _toolbarWindowTcs.Task.Result.BringToFront();
    }

    private void ToolbarWindow_LanguageSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is not SelectionToolbarWindow toolbarWindow)
        {
            return;
        }

        UpdateSelectedLanguages(toolbarWindow);
        LanguageSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToolbarWindow_SelectAreaRequested(object? sender, EventArgs e)
    {
        SelectAreaRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ToolbarWindow_CloseRequested(object? sender, EventArgs e)
    {
        _ = CloseAsync();
    }

    private void ToolbarWindow_SelectionCancelRequested(object? sender, EventArgs e)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || _isClosed)
        {
            return;
        }

        dispatcher.Invoke(() =>
        {
            if (_selectionSurfaceWindow is null)
            {
                return;
            }

            _selectionSurfaceWindow.Close();
            _selectionSurfaceWindow = null;
            _currentSelectionTask = null;
        });
    }

    private void ToolbarWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is SelectionToolbarWindow toolbarWindow)
        {
            UpdateSelectedLanguages(toolbarWindow);
            toolbarWindow.LanguageSelectionChanged -= ToolbarWindow_LanguageSelectionChanged;
            toolbarWindow.SelectAreaRequested -= ToolbarWindow_SelectAreaRequested;
            toolbarWindow.SelectionCancelRequested -= ToolbarWindow_SelectionCancelRequested;
            toolbarWindow.CloseRequested -= ToolbarWindow_CloseRequested;
            toolbarWindow.Closed -= ToolbarWindow_Closed;
        }

        _isClosed = true;
        Closed?.Invoke(this, EventArgs.Empty);

        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    private void UpdateSelectedLanguages(SelectionToolbarWindow toolbarWindow)
    {
        SelectedSourceLanguageCode = toolbarWindow.SelectedSourceLanguageCode;
        SelectedTargetLanguageCode = toolbarWindow.SelectedTargetLanguageCode;
    }
}
