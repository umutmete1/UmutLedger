using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;
using UmutLedger.Infrastructure.Telemetry;

namespace UmutLedger.Tests.Infrastructure;

public sealed class LedgerMetricsTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly IMeterFactory _meterFactory;
    private readonly LedgerMetrics _metrics;

    public LedgerMetricsTests()
    {
        _meterFactory = _services.GetRequiredService<IMeterFactory>();
        _metrics = new LedgerMetrics(_meterFactory);
    }

    public void Dispose() => _services.Dispose();

    private MetricCollector<T> Collect<T>(string instrumentName) where T : struct =>
        new(_meterFactory, LedgerMetrics.MeterName, instrumentName);

    [Fact]
    public void Constructor_WithoutAMeterFactory_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new LedgerMetrics(null!));

    [Fact]
    public void RecordMovement_AddsOneWithKindCurrencyAndOutcome()
    {
        using var collector = Collect<long>(LedgerMetrics.MovementsCounter);

        _metrics.RecordMovement(MovementKind.Withdrawal, Currency.EUR, "insufficient_funds");

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1, measurement.Value);
        Assert.Equal(3, measurement.Tags.Count);
        Assert.Equal("withdrawal", measurement.Tags[LedgerMetrics.KindLabel]);
        Assert.Equal("EUR", measurement.Tags[LedgerMetrics.CurrencyLabel]);
        Assert.Equal("insufficient_funds", measurement.Tags[LedgerMetrics.OutcomeLabel]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void RecordMovement_WithoutAnOutcome_Throws(string? outcome) =>
        Assert.ThrowsAny<ArgumentException>(() => _metrics.RecordMovement(MovementKind.Deposit, Currency.USD, outcome!));

    [Fact]
    public void RecordAmount_AddsTheMinorUnitsWithKindAndCurrency()
    {
        using var collector = Collect<long>(LedgerMetrics.AmountCounter);

        _metrics.RecordAmount(MovementKind.Transfer, new Money(2500, Currency.GBP));

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(2500, measurement.Value);
        Assert.Equal(2, measurement.Tags.Count);
        Assert.Equal("transfer", measurement.Tags[LedgerMetrics.KindLabel]);
        Assert.Equal("GBP", measurement.Tags[LedgerMetrics.CurrencyLabel]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RecordAmount_ThatIsNotPositive_Throws(long amount) =>
        Assert.Throws<ValidationFailedException>(() => _metrics.RecordAmount(MovementKind.Deposit, new Money(amount, Currency.USD)));

    [Fact]
    public void RecordAmount_WithoutAnAmount_Throws() =>
        Assert.Throws<ArgumentNullException>(() => _metrics.RecordAmount(MovementKind.Deposit, null!));

    [Fact]
    public void RecordLockWait_RecordsSecondsWithKindAndCurrency()
    {
        using var collector = Collect<double>(LedgerMetrics.LockWaitHistogram);

        _metrics.RecordLockWait(MovementKind.Deposit, Currency.USD, TimeSpan.FromMilliseconds(250));

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(0.25, measurement.Value);
        Assert.Equal(2, measurement.Tags.Count);
        Assert.Equal("deposit", measurement.Tags[LedgerMetrics.KindLabel]);
        Assert.Equal("USD", measurement.Tags[LedgerMetrics.CurrencyLabel]);
    }

    [Fact]
    public void RecordLockWait_ThatIsNegative_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _metrics.RecordLockWait(MovementKind.Deposit, Currency.USD, TimeSpan.FromTicks(-1)));

    // The unit decides the Prometheus name: "s" becomes the _seconds suffix the dashboard queries.
    [Fact]
    public void LockWaitHistogram_IsInSecondsWithBucketsFromOneMillisecondToTenSeconds()
    {
        using var collector = Collect<double>(LedgerMetrics.LockWaitHistogram);

        var histogram = Assert.IsType<Histogram<double>>(collector.Instrument);
        var buckets = histogram.Advice!.HistogramBucketBoundaries!;

        Assert.Equal("s", histogram.Unit);
        Assert.Equal(0.001, buckets[0]);
        Assert.Equal(10, buckets[^1]);
    }
}
