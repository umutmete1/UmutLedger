using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Infrastructure.Telemetry;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Infrastructure;

public class FailSafeLedgerMetricsTests
{
    [Fact]
    public void Calls_AreForwardedToTheInnerRecorder()
    {
        var inner = new RecordingLedgerMetrics();
        var metrics = new FailSafeLedgerMetrics(inner, new FakeLogger<FailSafeLedgerMetrics>());

        metrics.RecordMovement(MovementKind.Deposit, Currency.EUR, "created");
        metrics.RecordAmount(MovementKind.Deposit, new(5, Currency.EUR));
        metrics.RecordLockWait(MovementKind.Deposit, Currency.EUR, TimeSpan.FromSeconds(1));

        Assert.Equal(new RecordedMovement(MovementKind.Deposit, Currency.EUR, "created"), Assert.Single(inner.Movements));
        Assert.Equal(new RecordedAmount(MovementKind.Deposit, new(5, Currency.EUR)), Assert.Single(inner.Amounts));
        Assert.Equal(new RecordedLockWait(MovementKind.Deposit, Currency.EUR, TimeSpan.FromSeconds(1)), Assert.Single(inner.LockWaits));
    }

    [Fact]
    public void FailingInnerRecorder_IsContainedAndReportedOnceAsAWarning()
    {
        var logger = new FakeLogger<FailSafeLedgerMetrics>();
        var metrics = new FailSafeLedgerMetrics(new ThrowingLedgerMetrics(), logger);

        metrics.RecordMovement(MovementKind.Deposit, Currency.USD, "created");

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.IsType<TelemetryBugException>(record.Exception);
        Assert.Equal(nameof(metrics.RecordMovement), record.GetStructuredStateValue("Operation"));
    }

    [Fact]
    public void EveryRecordingMethod_ContainsAFailingInnerRecorder()
    {
        var logger = new FakeLogger<FailSafeLedgerMetrics>();
        var metrics = new FailSafeLedgerMetrics(new ThrowingLedgerMetrics(), logger);

        metrics.RecordMovement(MovementKind.Deposit, Currency.USD, "created");
        metrics.RecordAmount(MovementKind.Deposit, new(5, Currency.USD));
        metrics.RecordLockWait(MovementKind.Deposit, Currency.USD, TimeSpan.Zero);

        Assert.Equal(3, logger.Collector.Count);
    }

    [Fact]
    public void FailingWarning_IsContainedToo()
    {
        var metrics = new FailSafeLedgerMetrics(new ThrowingLedgerMetrics(), new ThrowingLogger<FailSafeLedgerMetrics>());

        metrics.RecordLockWait(MovementKind.Transfer, Currency.USD, TimeSpan.Zero);
    }
}
