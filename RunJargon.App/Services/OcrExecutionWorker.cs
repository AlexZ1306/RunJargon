using System.Windows.Threading;

namespace RunJargon.App.Services;

public sealed class OcrExecutionWorker : IOcrExecutionWorker
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _dispatcherTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public OcrExecutionWorker()
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "RunJargon OCR Worker"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var dispatcher = await _dispatcherTcs.Task.ConfigureAwait(false);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        await dispatcher.InvokeAsync(async () =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                var result = await operation(cancellationToken).ConfigureAwait(true);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException oce)
            {
                completion.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return await completion.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_dispatcherTcs.Task.IsCompletedSuccessfully)
        {
            _dispatcherTcs.Task.Result.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    private void ThreadMain()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        _dispatcherTcs.TrySetResult(dispatcher);
        Dispatcher.Run();
    }
}
