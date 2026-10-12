using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Support;

// Stands in for a metrics recorder with a bug, to prove that telemetry cannot change a money result.
public sealed class ThrowingLedgerMetrics : ILedgerMetrics
{
    public void RecordMovement(MovementKind kind, Currency currency, string outcome) => throw new TelemetryBugException();

    public void RecordAmount(MovementKind kind, Money amount) => throw new TelemetryBugException();

    public void RecordLockWait(MovementKind kind, Currency currency, TimeSpan wait) => throw new TelemetryBugException();
}

public sealed class TelemetryBugException : Exception;
