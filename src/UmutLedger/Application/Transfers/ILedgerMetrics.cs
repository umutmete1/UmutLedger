using UmutLedger.Domain.Enums;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Application.Transfers;

public interface ILedgerMetrics
{
    void RecordMovement(MovementKind kind, Currency currency, string outcome);

    void RecordAmount(MovementKind kind, Money amount);

    void RecordLockWait(MovementKind kind, Currency currency, TimeSpan wait);
}
