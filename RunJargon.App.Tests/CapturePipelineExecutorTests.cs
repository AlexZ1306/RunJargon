using RunJargon.App.Services;

namespace RunJargon.App.Tests;

public sealed class CapturePipelineExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_RunsOperationsSerially()
    {
        var executor = new CapturePipelineExecutor();
        var order = new List<int>();

        var first = executor.ExecuteAsync(async _ =>
        {
            order.Add(1);
            await Task.Delay(30);
            order.Add(2);
            return 1;
        }, CancellationToken.None);

        var second = executor.ExecuteAsync(async _ =>
        {
            order.Add(3);
            await Task.Delay(1);
            order.Add(4);
            return 2;
        }, CancellationToken.None);

        var results = await Task.WhenAll(first, second);

        Assert.Equal([1, 2], results);
        Assert.Equal([1, 2, 3, 4], order);
    }
}
