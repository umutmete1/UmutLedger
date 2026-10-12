using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Infrastructure.Telemetry;

public sealed class FailSafeLedgerMetrics(ILedgerMetrics inner, ILogger<FailSafeLedgerMetrics> logger) : ILedgerMetrics
{
    public void RecordMovement(MovementKind kind, Currency currency, string outcome) =>
        Contain(nameof(RecordMovement), () => inner.RecordMovement(kind, currency, outcome));

    public void RecordAmount(MovementKind kind, Money amount) =>
        Contain(nameof(RecordAmount), () => inner.RecordAmount(kind, amount));

    public void RecordLockWait(MovementKind kind, Currency currency, TimeSpan wait) =>
        Contain(nameof(RecordLockWait), () => inner.RecordLockWait(kind, currency, wait));

    private void Contain(string operation, Action record) =>
        TelemetryGuard.Contain(record, exception => logger.TelemetryFailed(exception, operation));
}
