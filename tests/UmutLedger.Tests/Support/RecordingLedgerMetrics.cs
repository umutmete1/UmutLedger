using System.Collections.Concurrent;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Support;

public sealed record RecordedMovement(MovementKind Kind, Currency Currency, string Outcome);

public sealed record RecordedAmount(MovementKind Kind, Money Amount);

public sealed record RecordedLockWait(MovementKind Kind, Currency Currency, TimeSpan Wait);

// Thread-safe because the concurrency tests share one instance across parallel requests.
public sealed class RecordingLedgerMetrics : ILedgerMetrics
{
    private readonly ConcurrentQueue<RecordedMovement> _movements = new();
    private readonly ConcurrentQueue<RecordedAmount> _amounts = new();
    private readonly ConcurrentQueue<RecordedLockWait> _lockWaits = new();

    public IReadOnlyCollection<RecordedMovement> Movements => _movements.ToArray();

    public IReadOnlyCollection<RecordedAmount> Amounts => _amounts.ToArray();

    public IReadOnlyCollection<RecordedLockWait> LockWaits => _lockWaits.ToArray();

    public void RecordMovement(MovementKind kind, Currency currency, string outcome) =>
        _movements.Enqueue(new RecordedMovement(kind, currency, outcome));

    public void RecordAmount(MovementKind kind, Money amount) => _amounts.Enqueue(new RecordedAmount(kind, amount));

    public void RecordLockWait(MovementKind kind, Currency currency, TimeSpan wait) =>
        _lockWaits.Enqueue(new RecordedLockWait(kind, currency, wait));
}
