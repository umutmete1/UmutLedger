using Microsoft.Extensions.Time.Testing;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class LockWaitTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static Money Eur(long amount) => new(amount, Currency.EUR);

    private static Task<TResult> RunAsync<TResult>(
        RecordingLedgerMetrics metrics, Func<ITransferService, Task<TResult>> action, TimeProvider? clock = null) =>
        TestLedger.WithTransferServiceAsync(action, clock, metrics);

    [Fact]
    public async Task Deposit_RecordsOneLockWaitWithItsKindAndCurrency()
    {
        var accountId = await TestLedger.CreateCustomerAsync(Currency.EUR);
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service => service.DepositAsync(TestLedger.NewKey(), accountId, Eur(500), None));

        var lockWait = Assert.Single(metrics.LockWaits);
        Assert.Equal(MovementKind.Deposit, lockWait.Kind);
        Assert.Equal(Currency.EUR, lockWait.Currency);
        Assert.True(lockWait.Wait >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Withdraw_RecordsOneLockWaitWithItsKindAndCurrency()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(500, Currency.EUR);
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service => service.WithdrawAsync(TestLedger.NewKey(), accountId, Eur(200), None));

        var lockWait = Assert.Single(metrics.LockWaits);
        Assert.Equal(MovementKind.Withdrawal, lockWait.Kind);
        Assert.Equal(Currency.EUR, lockWait.Currency);
    }

    [Fact]
    public async Task Transfer_RecordsOneLockWaitWithItsKindAndCurrency()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(500);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service =>
            service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, TestLedger.Usd(200), None));

        var lockWait = Assert.Single(metrics.LockWaits);
        Assert.Equal(MovementKind.Transfer, lockWait.Kind);
        Assert.Equal(Currency.USD, lockWait.Currency);
    }

    [Fact]
    public async Task ReplayFoundBeforeLocking_RecordsNoLockWait()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        await RunAsync(new RecordingLedgerMetrics(), service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None));
        var metrics = new RecordingLedgerMetrics();

        var replay = await RunAsync(metrics, service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None));

        Assert.True(replay.WasReplayed);
        Assert.Empty(metrics.LockWaits);
    }

    [Fact]
    public async Task RejectedAfterLocking_StillRecordsTheLockWait()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);
        var metrics = new RecordingLedgerMetrics();

        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            RunAsync(metrics, service => service.WithdrawAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(101), None)));

        Assert.Single(metrics.LockWaits);
    }

    [Fact]
    public async Task SameAccountTransfer_IsRefusedBeforeLocking_AndRecordsNoLockWait()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);
        var metrics = new RecordingLedgerMetrics();

        await Assert.ThrowsAsync<SameAccountTransferException>(() =>
            RunAsync(metrics, service => service.TransferAsync(TestLedger.NewKey(), accountId, accountId, TestLedger.Usd(10), None)));

        Assert.Empty(metrics.LockWaits);
    }

    [Fact]
    public async Task ZeroAmount_IsRefusedBeforeLocking_AndRecordsNoLockWait()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var metrics = new RecordingLedgerMetrics();

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(metrics, service => service.DepositAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(0), None)));

        Assert.Empty(metrics.LockWaits);
    }

    // A clock that never advances reports exactly zero, which the real clock never would.
    [Fact]
    public async Task LockWait_IsMeasuredWithTheInjectedClock()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var metrics = new RecordingLedgerMetrics();
        var frozenClock = new FakeTimeProvider(TestLedger.BaseTime);

        await RunAsync(metrics, service => service.DepositAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(500), None), frozenClock);

        Assert.Equal(TimeSpan.Zero, Assert.Single(metrics.LockWaits).Wait);
    }
}
