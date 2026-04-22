namespace RunJargon.App.Services;

public sealed class CapturePipelineExecutor : ICapturePipelineExecutor
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var scheduled = _tail.ContinueWith(
                    _ => Task.Run(() => operation(cancellationToken), cancellationToken),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();

            _tail = scheduled.ContinueWith(
                _ => { },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

            return scheduled;
        }
    }
}
