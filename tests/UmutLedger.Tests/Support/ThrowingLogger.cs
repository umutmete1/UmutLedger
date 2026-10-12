using Microsoft.Extensions.Logging;

namespace UmutLedger.Tests.Support;

// Stands in for a logging provider that fails, to prove that logging cannot change a money result.
public sealed class ThrowingLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        throw new TelemetryBugException();
}
