using System.Windows.Threading;
using RunJargon.App.Models;
using RunJargon.App.Utilities;
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
    private readonly TaskCompletionSource<SelectionOverlayWindow> _windowTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ScreenRegion?> _selectionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _shutdownTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Dispatcher? _dispatcher;
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

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _thread.Start();
        return _windowTcs.Task;
    }

    public async Task<ScreenRegion?> WaitForSelectionAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _windowTcs.Task.ConfigureAwait(false);
        return await _selectionTcs.Task.ConfigureAwait(false);
    }

    public async Task PrepareForCleanCaptureAsync()
    {
        await InvokeOnWindowAsync(window => window.PrepareForCleanCaptureAsync()).ConfigureAwait(false);
    }

    public async Task ShowToolbarOnlyAsync(bool isBusy)
    {
        await InvokeOnWindowAsync(window =>
        {
            window.ShowToolbarOnly(isBusy);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    public async Task SetBusyAsync(bool isBusy)
    {
        await InvokeOnWindowAsync(window =>
        {
            window.SetBusy(isBusy);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    public async Task UpdateCopyTextsAsync(string recognizedTextToCopy, string translatedTextToCopy)
    {
        await InvokeOnWindowAsync(window =>
        {
            window.UpdateCopyTexts(recognizedTextToCopy, translatedTextToCopy);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    public async Task CloseAsync()
    {
        if (_disposed)
        {
            return;
        }

        SelectionOverlayWindow? window = null;
        try
        {
            window = await _windowTcs.Task.ConfigureAwait(false);
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

            if (window.IsVisible)
            {
                window.Close();
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

    private async Task InvokeOnWindowAsync(Func<SelectionOverlayWindow, Task> operation)
    {
        if (_disposed)
        {
            return;
        }

        SelectionOverlayWindow window;
        try
        {
            window = await _windowTcs.Task.ConfigureAwait(false);
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

        await dispatcher.InvokeAsync(() => operation(window)).Task.Unwrap().ConfigureAwait(false);
    }

    private void ThreadMain()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            var window = new SelectionOverlayWindow(
                _sourceLanguages,
                _targetLanguages,
                _selectedSourceLanguageCode,
                _selectedTargetLanguageCode,
                _recognizedTextToCopy,
                _translatedTextToCopy);
            UpdateSelectedLanguages(window);

            window.LanguageSelectionChanged += Window_LanguageSelectionChanged;
            window.Closed += Window_Closed;

            var selectionTask = window.WaitForSelectionAsync();
            selectionTask.ContinueWith(
                task =>
                {
                    if (task.IsCanceled)
                    {
                        _selectionTcs.TrySetCanceled();
                    }
                    else if (task.IsFaulted)
                    {
                        _selectionTcs.TrySetException(task.Exception!.InnerExceptions);
                    }
                    else
                    {
                        _selectionTcs.TrySetResult(task.Result);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            _windowTcs.TrySetResult(window);
            window.Show();
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _windowTcs.TrySetException(ex);
            _selectionTcs.TrySetException(ex);
        }
        finally
        {
            _shutdownTcs.TrySetResult(null);
        }
    }

    private void Window_LanguageSelectionChanged(object? sender, EventArgs e)
    {
        if (sender is not SelectionOverlayWindow window)
        {
            return;
        }

        UpdateSelectedLanguages(window);
        LanguageSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (sender is SelectionOverlayWindow window)
        {
            UpdateSelectedLanguages(window);
            window.LanguageSelectionChanged -= Window_LanguageSelectionChanged;
            window.Closed -= Window_Closed;
        }

        _isClosed = true;
        Closed?.Invoke(this, EventArgs.Empty);

        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    private void UpdateSelectedLanguages(SelectionOverlayWindow window)
    {
        SelectedSourceLanguageCode = window.SelectedSourceLanguageCode;
        SelectedTargetLanguageCode = window.SelectedTargetLanguageCode;
    }
}
