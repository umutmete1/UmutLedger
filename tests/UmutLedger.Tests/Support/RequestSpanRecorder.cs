using System.Collections.Concurrent;
using System.Diagnostics;

namespace UmutLedger.Tests.Support;

// The listener is process-wide, so spans are looked up by the trace id the test itself sent.
public sealed class RequestSpanRecorder : IDisposable
{
    private const string AspNetCoreSource = "Microsoft.AspNetCore";

    private readonly ConcurrentQueue<Activity> _started = new();
    private readonly ActivityListener _listener;

    public RequestSpanRecorder()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AspNetCoreSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _started.Enqueue
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public Activity SpanOf(string traceId) => _started.Single(span => span.TraceId.ToString() == traceId);

    public void Dispose() => _listener.Dispose();
}
