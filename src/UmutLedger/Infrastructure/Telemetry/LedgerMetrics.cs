using System.Diagnostics.Metrics;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Infrastructure.Telemetry;

public sealed class LedgerMetrics : ILedgerMetrics
{
    public const string MeterName = "UmutLedger";
    public const string MovementsCounter = "ledger.movements";
    public const string AmountCounter = "ledger.movements.amount";
    public const string LockWaitHistogram = "ledger.account_lock.wait.duration";
    public const string KindLabel = "kind";
    public const string CurrencyLabel = "currency";
    public const string OutcomeLabel = "outcome";

    private const string MovementUnit = "{movement}";
    private const string MinorUnit = "{minor_unit}";
    private const string SecondsUnit = "s";
    private const long OneMovement = 1;

    private static readonly IReadOnlyList<double> LockWaitBucketsInSeconds =
        [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private readonly Counter<long> _movements;
    private readonly Counter<long> _amount;
    private readonly Histogram<double> _lockWait;

    public LedgerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _movements = meter.CreateCounter<long>(
            MovementsCounter, MovementUnit, "Money movement requests that reached the transfer service.");
        _amount = meter.CreateCounter<long>(
            AmountCounter, MinorUnit, "Money moved by newly created transfers, in minor units.");
        _lockWait = meter.CreateHistogram<double>(
            LockWaitHistogram,
            unit: SecondsUnit,
            description: "Time taken to acquire the account row locks.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LockWaitBucketsInSeconds });
    }

    public void RecordMovement(MovementKind kind, Currency currency, string outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        _movements.Add(OneMovement, Kind(kind), CurrencyOf(currency), Label(OutcomeLabel, outcome));
    }

    public void RecordAmount(MovementKind kind, Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);
        amount.EnsurePositive();
        _amount.Add(amount.Amount, Kind(kind), CurrencyOf(amount.Currency));
    }

    public void RecordLockWait(MovementKind kind, Currency currency, TimeSpan wait)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero);
        _lockWait.Record(wait.TotalSeconds, Kind(kind), CurrencyOf(currency));
    }

    private static KeyValuePair<string, object?> Kind(MovementKind kind) => Label(KindLabel, kind.ToLabel());

    private static KeyValuePair<string, object?> CurrencyOf(Currency currency) => Label(CurrencyLabel, currency.ToString());

    private static KeyValuePair<string, object?> Label(string name, string value) => new(name, value);
}
