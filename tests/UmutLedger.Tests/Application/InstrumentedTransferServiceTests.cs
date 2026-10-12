using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;
using UmutLedger.Infrastructure.Telemetry;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class InstrumentedTransferServiceTests
{
    private const int ParallelRequests = 20;
    private static readonly CancellationToken None = CancellationToken.None;

    private static FailSafeLedgerMetrics FailSafeAroundThrowingRecorder() =>
        new(new ThrowingLedgerMetrics(), new FakeLogger<FailSafeLedgerMetrics>());

    private static async Task<TResult> RunAsync<TResult>(
        ILedgerMetrics metrics,
        Func<ITransferService, Task<TResult>> action,
        ILogger<InstrumentedTransferService>? logger = null)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        var service = new InstrumentedTransferService(
            new TransferService(db, TimeProvider.System, metrics),
            metrics,
            logger ?? new FakeLogger<InstrumentedTransferService>());
        return await action(service);
    }

    private static InstrumentedTransferService Around(
        StubTransferService inner, ILedgerMetrics metrics, ILogger<InstrumentedTransferService>? logger = null) =>
        new(inner, metrics, logger ?? new FakeLogger<InstrumentedTransferService>());

    private static async Task AssertRejectedAsync<TException>(
        Func<ITransferService, Task<TransferOutcome>> movement, MovementKind kind, string errorCode)
        where TException : LedgerException
    {
        var metrics = new RecordingLedgerMetrics();

        await Assert.ThrowsAsync<TException>(() => RunAsync(metrics, movement));

        Assert.Equal(new RecordedMovement(kind, Currency.USD, errorCode), Assert.Single(metrics.Movements));
        Assert.Empty(metrics.Amounts);
    }

    [Fact]
    public async Task Deposit_ThatCreatesATransfer_RecordsCreatedAndTheAmount()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service => service.DepositAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(500), None));

        Assert.Equal(new RecordedMovement(MovementKind.Deposit, Currency.USD, MovementOutcomes.Created), Assert.Single(metrics.Movements));
        Assert.Equal(new RecordedAmount(MovementKind.Deposit, TestLedger.Usd(500)), Assert.Single(metrics.Amounts));
    }

    [Fact]
    public async Task Withdrawal_ThatCreatesATransfer_RecordsCreatedAndTheAmount()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(500);
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service => service.WithdrawAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(200), None));

        Assert.Equal(new RecordedMovement(MovementKind.Withdrawal, Currency.USD, MovementOutcomes.Created), Assert.Single(metrics.Movements));
        Assert.Equal(new RecordedAmount(MovementKind.Withdrawal, TestLedger.Usd(200)), Assert.Single(metrics.Amounts));
    }

    [Fact]
    public async Task Transfer_ThatCreatesATransfer_RecordsCreatedAndTheAmount()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(500);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service =>
            service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, TestLedger.Usd(300), None));

        Assert.Equal(new RecordedMovement(MovementKind.Transfer, Currency.USD, MovementOutcomes.Created), Assert.Single(metrics.Movements));
        Assert.Equal(new RecordedAmount(MovementKind.Transfer, TestLedger.Usd(300)), Assert.Single(metrics.Amounts));
    }

    [Fact]
    public async Task Replay_RecordsReplayedAndNoAmount()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        await RunAsync(new RecordingLedgerMetrics(), service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None));
        var metrics = new RecordingLedgerMetrics();

        await RunAsync(metrics, service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None));

        Assert.Equal(new RecordedMovement(MovementKind.Deposit, Currency.USD, MovementOutcomes.Replayed), Assert.Single(metrics.Movements));
        Assert.Empty(metrics.Amounts);
    }

    [Fact]
    public async Task SameAccountTransfer_RecordsItsErrorCodeAndNoAmount()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        await AssertRejectedAsync<SameAccountTransferException>(
            service => service.TransferAsync(TestLedger.NewKey(), accountId, accountId, TestLedger.Usd(10), None),
            MovementKind.Transfer,
            ErrorCodes.SameAccountTransfer);
    }

    [Fact]
    public async Task ZeroAmount_RecordsValidationFailedAndNoAmount()
    {
        var accountId = await TestLedger.CreateCustomerAsync();

        await AssertRejectedAsync<ValidationFailedException>(
            service => service.DepositAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(0), None),
            MovementKind.Deposit,
            ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task UnknownAccount_RecordsAccountNotFoundAndNoAmount() =>
        await AssertRejectedAsync<AccountNotFoundException>(
            service => service.DepositAsync(TestLedger.NewKey(), Guid.CreateVersion7(), TestLedger.Usd(10), None),
            MovementKind.Deposit,
            ErrorCodes.AccountNotFound);

    [Fact]
    public async Task WrongCurrency_RecordsCurrencyMismatchAndNoAmount()
    {
        var euroAccountId = await TestLedger.CreateCustomerAsync(Currency.EUR);
        var dollarAccountId = await TestLedger.CreateFundedCustomerAsync(100);

        await AssertRejectedAsync<CurrencyMismatchException>(
            service => service.TransferAsync(TestLedger.NewKey(), dollarAccountId, euroAccountId, TestLedger.Usd(10), None),
            MovementKind.Transfer,
            ErrorCodes.CurrencyMismatch);
    }

    [Fact]
    public async Task Overdraft_RecordsInsufficientFundsAndNoAmount()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        await AssertRejectedAsync<InsufficientFundsException>(
            service => service.WithdrawAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(101), None),
            MovementKind.Withdrawal,
            ErrorCodes.InsufficientFunds);
    }

    [Fact]
    public async Task WorldAccountNamedByTheCaller_RecordsSystemAccountNotAllowedAndNoAmount()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);
        var accountId = await TestLedger.CreateCustomerAsync();

        await AssertRejectedAsync<SystemAccountNotAllowedException>(
            service => service.TransferAsync(TestLedger.NewKey(), worldAccountId, accountId, TestLedger.Usd(10), None),
            MovementKind.Transfer,
            ErrorCodes.SystemAccountNotAllowed);
    }

    [Fact]
    public async Task KeyReusedForADifferentRequest_RecordsIdempotencyKeyReusedAndNoAmount()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        await RunAsync(new RecordingLedgerMetrics(), service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None));

        await AssertRejectedAsync<IdempotencyKeyReusedException>(
            service => service.DepositAsync(key, accountId, TestLedger.Usd(501), None),
            MovementKind.Deposit,
            ErrorCodes.IdempotencyKeyReused);
    }

    [Fact]
    public async Task Cancellation_RecordsCancelledAndRethrowsTheSameException()
    {
        var cancellation = new OperationCanceledException();
        var metrics = new RecordingLedgerMetrics();
        var service = Around(StubTransferService.Throwing(cancellation), metrics);

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.DepositAsync(TestLedger.NewKey(), Guid.CreateVersion7(), TestLedger.Usd(10), None));

        Assert.Same(cancellation, thrown);
        Assert.Equal(new RecordedMovement(MovementKind.Deposit, Currency.USD, MovementOutcomes.Cancelled), Assert.Single(metrics.Movements));
        Assert.Empty(metrics.Amounts);
    }

    [Fact]
    public async Task UnexpectedFailure_RecordsErrorAndRethrowsTheSameException()
    {
        var failure = new InvalidOperationException("boom");
        var metrics = new RecordingLedgerMetrics();
        var service = Around(StubTransferService.Throwing(failure), metrics);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.WithdrawAsync(TestLedger.NewKey(), Guid.CreateVersion7(), TestLedger.Usd(10), None));

        Assert.Same(failure, thrown);
        Assert.Equal(new RecordedMovement(MovementKind.Withdrawal, Currency.USD, MovementOutcomes.Error), Assert.Single(metrics.Movements));
        Assert.Empty(metrics.Amounts);
    }

    [Fact]
    public async Task Result_IsReturnedUnchanged()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var transfer = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);
        var expected = new TransferOutcome(transfer, wasReplayed: false);
        var service = Around(StubTransferService.Returning(expected), new RecordingLedgerMetrics());

        var actual = await service.TransferAsync(TestLedger.NewKey(), Guid.CreateVersion7(), accountId, TestLedger.Usd(100), None);

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task Get_ReturnsTheTransferAndRecordsNothing()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var transfer = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);
        var metrics = new RecordingLedgerMetrics();

        var found = await RunAsync(metrics, service => service.GetAsync(transfer.Id, None));

        Assert.Equal(transfer.Id, found.Id);
        Assert.Empty(metrics.Movements);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequests_AddTheAmountExactlyOnce()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        var metrics = new RecordingLedgerMetrics();

        var requests = Enumerable.Range(0, ParallelRequests).Select(_ => Task.Run(() =>
            RunAsync(metrics, service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None))));
        await Task.WhenAll(requests);

        Assert.Equal(new RecordedAmount(MovementKind.Deposit, TestLedger.Usd(500)), Assert.Single(metrics.Amounts));
        Assert.Single(metrics.Movements, movement => movement.Outcome == MovementOutcomes.Created);
        Assert.Equal(ParallelRequests - 1, metrics.Movements.Count(movement => movement.Outcome == MovementOutcomes.Replayed));
        Assert.Equal(500, await TestLedger.BalanceOfAsync(accountId));
    }

    [Fact]
    public async Task CreatedMovement_TagsTheCurrentSpanWithKindOutcomeAndTransferId()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        using var span = new Activity("request").Start();

        var outcome = await RunAsync(new RecordingLedgerMetrics(), service =>
            service.DepositAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(500), None));

        Assert.Equal("deposit", span.GetTagItem(InstrumentedTransferService.KindAttribute));
        Assert.Equal(MovementOutcomes.Created, span.GetTagItem(InstrumentedTransferService.OutcomeAttribute));
        Assert.Equal(outcome.Transfer.Id.ToString(), span.GetTagItem(InstrumentedTransferService.TransferIdAttribute));
    }

    [Fact]
    public async Task RejectedMovement_TagsTheCurrentSpanWithoutATransferId()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);
        using var span = new Activity("request").Start();

        await Assert.ThrowsAsync<InsufficientFundsException>(() => RunAsync(new RecordingLedgerMetrics(), service =>
            service.WithdrawAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(101), None)));

        Assert.Equal("withdrawal", span.GetTagItem(InstrumentedTransferService.KindAttribute));
        Assert.Equal(ErrorCodes.InsufficientFunds, span.GetTagItem(InstrumentedTransferService.OutcomeAttribute));
        Assert.DoesNotContain(span.TagObjects, tag => tag.Key == InstrumentedTransferService.TransferIdAttribute);
    }

    [Fact]
    public async Task CreatedMovement_WritesOneInformationLogLineWithoutTheIdempotencyKey()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        var logger = new FakeLogger<InstrumentedTransferService>();

        var outcome = await RunAsync(
            new RecordingLedgerMetrics(), service => service.DepositAsync(key, accountId, TestLedger.Usd(500), None), logger);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Equal("deposit", record.GetStructuredStateValue("Kind"));
        Assert.Equal(MovementOutcomes.Created, record.GetStructuredStateValue("Outcome"));
        Assert.Equal("500", record.GetStructuredStateValue("Amount"));
        Assert.Equal("USD", record.GetStructuredStateValue("Currency"));
        Assert.Equal(outcome.Transfer.Id.ToString(), record.GetStructuredStateValue("TransferId"));
        Assert.Equal(accountId.ToString(), record.GetStructuredStateValue("AccountIds"));
        Assert.Contains(accountId.ToString(), record.Message);
        Assert.DoesNotContain(key.Value, record.Message);
        Assert.DoesNotContain(record.StructuredState!, field => field.Value == key.Value);
    }

    [Fact]
    public async Task Transfer_LogsBothAccountIdsAsOneTextFieldDebitFirst()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(500);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var logger = new FakeLogger<InstrumentedTransferService>();

        await RunAsync(
            new RecordingLedgerMetrics(),
            service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, TestLedger.Usd(300), None),
            logger);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal($"{debitAccountId}, {creditAccountId}", record.GetStructuredStateValue("AccountIds"));
    }

    [Fact]
    public async Task Deposit_InEuro_RecordsTheEuroCurrencyOnTheMovementAndTheAmount()
    {
        var accountId = await TestLedger.CreateCustomerAsync(Currency.EUR);
        var metrics = new RecordingLedgerMetrics();
        var logger = new FakeLogger<InstrumentedTransferService>();
        var euros = new Money(500, Currency.EUR);

        await RunAsync(metrics, service => service.DepositAsync(TestLedger.NewKey(), accountId, euros, None), logger);

        Assert.Equal(new RecordedMovement(MovementKind.Deposit, Currency.EUR, MovementOutcomes.Created), Assert.Single(metrics.Movements));
        Assert.Equal(new RecordedAmount(MovementKind.Deposit, euros), Assert.Single(metrics.Amounts));
        Assert.Equal("EUR", Assert.Single(logger.Collector.GetSnapshot()).GetStructuredStateValue("Currency"));
    }

    [Fact]
    public async Task Deposit_WithFailingMetrics_StillReturnsItsOutcomeAndChangesTheBalanceOnce()
    {
        var accountId = await TestLedger.CreateCustomerAsync();

        var outcome = await RunAsync(
            FailSafeAroundThrowingRecorder(),
            service => service.DepositAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(500), None));

        Assert.False(outcome.WasReplayed);
        Assert.Equal(500, await TestLedger.BalanceOfAsync(accountId));
    }

    [Fact]
    public async Task RejectedMovement_WithFailingMetrics_StillThrowsTheLedgerError()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        await Assert.ThrowsAsync<InsufficientFundsException>(() => RunAsync(
            FailSafeAroundThrowingRecorder(),
            service => service.WithdrawAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(101), None)));
    }

    [Fact]
    public async Task CreatedMovement_WithFailingLogger_StillReturnsItsOutcome()
    {
        var accountId = await TestLedger.CreateCustomerAsync();

        var outcome = await RunAsync(
            new RecordingLedgerMetrics(),
            service => service.DepositAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(500), None),
            new ThrowingLogger<InstrumentedTransferService>());

        Assert.False(outcome.WasReplayed);
        Assert.Equal(500, await TestLedger.BalanceOfAsync(accountId));
    }

    [Fact]
    public async Task RejectedMovement_WithFailingLogger_StillThrowsTheLedgerError()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        await Assert.ThrowsAsync<InsufficientFundsException>(() => RunAsync(
            new RecordingLedgerMetrics(),
            service => service.WithdrawAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(101), None),
            new ThrowingLogger<InstrumentedTransferService>()));
    }

    // The decorator is handed the recorder with no fail-safe wrapper, so only its own guard stands between a telemetry bug and the caller.
    [Fact]
    public async Task CreatedMovement_WithAnUnwrappedFailingRecorder_StillReturnsTheSameOutcomeAndWarns()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var transfer = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);
        var expected = new TransferOutcome(transfer, wasReplayed: false);
        var logger = new FakeLogger<InstrumentedTransferService>();
        var service = Around(StubTransferService.Returning(expected), new ThrowingLedgerMetrics(), logger);

        var actual = await service.DepositAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(100), None);

        Assert.Same(expected, actual);
        var warnings = logger.Collector.GetSnapshot().Where(record => record.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning => Assert.IsType<TelemetryBugException>(warning.Exception));
    }

    [Fact]
    public async Task RejectedMovement_WithAnUnwrappedFailingRecorder_StillThrowsTheSameLedgerError()
    {
        var rejection = new InsufficientFundsException(Guid.CreateVersion7());
        var service = Around(StubTransferService.Throwing(rejection), new ThrowingLedgerMetrics());

        var thrown = await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            service.WithdrawAsync(TestLedger.NewKey(), Guid.CreateVersion7(), TestLedger.Usd(10), None));

        Assert.Same(rejection, thrown);
    }

    [Fact]
    public async Task FailingLockWaitRecorder_DoesNotFailATransferRunDirectlyOnTheTransferService()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(500);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var outcome = await TestLedger.WithTransferServiceAsync(
            service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, TestLedger.Usd(300), None),
            metrics: FailSafeAroundThrowingRecorder());

        Assert.False(outcome.WasReplayed);
        Assert.Equal(200, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(300, await TestLedger.BalanceOfAsync(creditAccountId));
    }

    [Fact]
    public async Task RejectedMovement_WritesOneLogLineWithItsErrorCode()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);
        var logger = new FakeLogger<InstrumentedTransferService>();

        await Assert.ThrowsAsync<InsufficientFundsException>(() => RunAsync(
            new RecordingLedgerMetrics(), service => service.WithdrawAsync(TestLedger.NewKey(), accountId, TestLedger.Usd(101), None), logger));

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal("withdrawal", record.GetStructuredStateValue("Kind"));
        Assert.Equal(ErrorCodes.InsufficientFunds, record.GetStructuredStateValue("Outcome"));
    }
}
