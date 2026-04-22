namespace RunJargon.App.Services;

public interface ICapturePipelineExecutor
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}
