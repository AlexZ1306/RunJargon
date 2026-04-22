using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;

namespace RunJargon.App.Models;

public sealed class CapturePerformanceTrace
{
    private readonly object _gate = new();
    private readonly Dictionary<CapturePerformancePhase, TimeSpan> _durations = new();

    public CapturePerformanceTrace()
    {
        CreatedAt = DateTimeOffset.Now;
        Counters = new CapturePerformanceCounters();
    }

    public DateTimeOffset CreatedAt { get; }

    public CapturePerformanceCounters Counters { get; }

    public CaptureProcessingMode? ProcessingMode { get; private set; }

    public IReadOnlyDictionary<CapturePerformancePhase, TimeSpan> Durations
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyDictionary<CapturePerformancePhase, TimeSpan>(
                    new Dictionary<CapturePerformancePhase, TimeSpan>(_durations));
            }
        }
    }

    public void SetProcessingMode(CaptureProcessingMode mode)
    {
        ProcessingMode = mode;
    }

    public IDisposable Measure(CapturePerformancePhase phase)
    {
        return new MeasurementScope(this, phase);
    }

    public void AddDuration(CapturePerformancePhase phase, TimeSpan duration)
    {
        lock (_gate)
        {
            if (_durations.TryGetValue(phase, out var existing))
            {
                _durations[phase] = existing + duration;
                return;
            }

            _durations[phase] = duration;
        }
    }

    public string ToDiagnosticString()
    {
        var builder = new StringBuilder();
        builder.Append("[Perf] mode=");
        builder.Append(ProcessingMode?.ToString() ?? "Unknown");
        builder.Append(" ocr=");
        builder.Append(Counters.OcrRequests);
        builder.Append(" refine=");
        builder.Append(Counters.CropRefinements);
        builder.Append(" recovery=");
        builder.Append(Counters.RecoveryAttempts);
        builder.Append(" translate=");
        builder.Append(Counters.TranslationRequests);
        builder.Append(" inpaint=");
        builder.Append(Counters.InpaintCalls);

        foreach (var duration in Durations.OrderBy(item => item.Key))
        {
            builder.Append(' ');
            builder.Append(duration.Key);
            builder.Append('=');
            builder.Append(Math.Round(duration.Value.TotalMilliseconds));
            builder.Append("ms");
        }

        return builder.ToString();
    }

    private sealed class MeasurementScope : IDisposable
    {
        private readonly CapturePerformanceTrace _owner;
        private readonly CapturePerformancePhase _phase;
        private readonly Stopwatch _stopwatch;

        public MeasurementScope(CapturePerformanceTrace owner, CapturePerformancePhase phase)
        {
            _owner = owner;
            _phase = phase;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _owner.AddDuration(_phase, _stopwatch.Elapsed);
        }
    }
}
