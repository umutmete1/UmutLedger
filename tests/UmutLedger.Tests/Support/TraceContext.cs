using System.Diagnostics;

namespace UmutLedger.Tests.Support;

public static class TraceContext
{
    public const string HeaderName = "traceparent";

    public static string NewTraceId() => ActivityTraceId.CreateRandom().ToString();

    public static string ParentHeaderFor(string traceId) => $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01";
}
