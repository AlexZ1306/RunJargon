using System.Diagnostics;
using RunJargon.App.Models;

namespace RunJargon.App.Services;

public sealed class CapturePerformanceTraceStore
{
    private readonly object _gate = new();
    private readonly Queue<CapturePerformanceTrace> _recent = new();

    public void Add(CapturePerformanceTrace trace)
    {
        lock (_gate)
        {
            _recent.Enqueue(trace);
            while (_recent.Count > 20)
            {
                _recent.Dequeue();
            }
        }

        Debug.WriteLine(trace.ToDiagnosticString());
    }

    public IReadOnlyList<CapturePerformanceTrace> GetRecent()
    {
        lock (_gate)
        {
            return _recent.ToArray();
        }
    }
}
