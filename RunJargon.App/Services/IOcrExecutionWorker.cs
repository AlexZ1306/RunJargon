namespace RunJargon.App.Services;

public interface IOcrExecutionWorker : IDisposable
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}
