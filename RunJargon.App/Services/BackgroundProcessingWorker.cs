using System.Threading.Channels;

namespace RunJargon.App.Services;

public sealed class BackgroundProcessingWorker : IDisposable
{
    private readonly Channel<IWorkItem> _queue = Channel.CreateUnbounded<IWorkItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Task _runner;
    private bool _disposed;

    public BackgroundProcessingWorker()
    {
        _runner = Task.Factory.StartNew(
                RunLoopAsync,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
    }

    public Task<T> ExecuteAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var workItem = new WorkItem<T>(operation, cancellationToken);
        if (!_queue.Writer.TryWrite(workItem))
        {
            throw new InvalidOperationException("Background processing queue is unavailable.");
        }

        return workItem.Task;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        try
        {
            _runner.GetAwaiter().GetResult();
        }
        catch
        {
        }
    }

    private async Task RunLoopAsync()
    {
        await foreach (var workItem in _queue.Reader.ReadAllAsync())
        {
            workItem.Execute();
        }
    }

    private interface IWorkItem
    {
        void Execute();
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<T> _operation;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(Func<T> operation, CancellationToken cancellationToken)
        {
            _operation = operation;
            _cancellationToken = cancellationToken;
        }

        public Task<T> Task => _completion.Task;

        public void Execute()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
                return;
            }

            try
            {
                _completion.TrySetResult(_operation());
            }
            catch (OperationCanceledException oce)
            {
                _completion.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }
    }
}
