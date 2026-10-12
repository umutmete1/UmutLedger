using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class TransferServiceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static Money Usd(long amount) => TestLedger.Usd(amount);

    private static async Task<int> CountTransfersAsync(IdempotencyKey key)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        return await db.Transfers.CountAsync(transfer => transfer.IdempotencyKey == key);
    }

    [Fact]
    public async Task DepositAsync_CreditsTheAccountFromTheWorldAccount()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);
        var clock = new FakeTimeProvider(TestLedger.BaseTime);

        var outcome = await TestLedger.WithTransferServiceAsync(service => service.DepositAsync(TestLedger.NewKey(), accountId, Usd(500), None), clock);

        Assert.False(outcome.WasReplayed);
        Assert.Equal(worldAccountId, outcome.Transfer.DebitAccountId);
        Assert.Equal(accountId, outcome.Transfer.CreditAccountId);
        Assert.Equal(TestLedger.BaseTime, outcome.Transfer.CreatedAt);
        Assert.Equal(2, outcome.Transfer.Entries.Count);
        Assert.Equal(500, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task WithdrawAsync_DebitsTheAccountToTheWorldAccount()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(500);
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);

        var outcome = await TestLedger.WithTransferServiceAsync(service => service.WithdrawAsync(TestLedger.NewKey(), accountId, Usd(200), None));

        Assert.Equal(accountId, outcome.Transfer.DebitAccountId);
        Assert.Equal(worldAccountId, outcome.Transfer.CreditAccountId);
        Assert.Equal(300, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task WithdrawAsync_MoreThanTheBalance_ThrowsAndWritesNothing()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);
        var key = TestLedger.NewKey();

        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.WithdrawAsync(key, accountId, Usd(101), None)));

        Assert.Equal(100, await TestLedger.BalanceOfAsync(accountId));
        Assert.Equal(0, await CountTransfersAsync(key));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_MovesMoneyBetweenTwoCustomers()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(1000);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var outcome = await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, Usd(400), None));

        Assert.False(outcome.WasReplayed);
        Assert.Equal(600, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(400, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_SameAccountOnBothSides_Throws()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);

        await Assert.ThrowsAsync<SameAccountTransferException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), accountId, accountId, Usd(10), None)));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task TransferAsync_NonPositiveAmount_Throws(long amount)
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, Usd(amount), None)));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_UnknownDebitAccount_Throws()
    {
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        await Assert.ThrowsAsync<AccountNotFoundException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), Guid.CreateVersion7(), creditAccountId, Usd(10), None)));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_UnknownCreditAccount_ThrowsAndWritesNothing()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);

        await Assert.ThrowsAsync<AccountNotFoundException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, Guid.CreateVersion7(), Usd(10), None)));

        Assert.Equal(100, await TestLedger.BalanceOfAsync(debitAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_RequestCurrencyDiffersFromTheAccounts_ThrowsAndWritesNothing()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100, Currency.EUR);
        var creditAccountId = await TestLedger.CreateCustomerAsync(Currency.EUR);

        await Assert.ThrowsAsync<CurrencyMismatchException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, Usd(10), None)));

        Assert.Equal(100, await TestLedger.BalanceOfAsync(debitAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_CreditAccountInAnotherCurrency_ThrowsAndWritesNothing()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);
        var creditAccountId = await TestLedger.CreateCustomerAsync(Currency.GBP);

        await Assert.ThrowsAsync<CurrencyMismatchException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, Usd(10), None)));

        Assert.Equal(100, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(0, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_AfterAFailedTransferOnTheSameContext_DoesNotPersistTheFailedDebit()
    {
        const long fundedBalance = 100;
        var failedDebitAccountId = await TestLedger.CreateFundedCustomerAsync(fundedBalance);
        var gbpCreditAccountId = await TestLedger.CreateCustomerAsync(Currency.GBP);
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(fundedBalance);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        await using var db = await TestDatabase.CreateContextAsync();
        var service = new TransferService(db, TimeProvider.System, new RecordingLedgerMetrics());
        await Assert.ThrowsAsync<CurrencyMismatchException>(() =>
            service.TransferAsync(TestLedger.NewKey(), failedDebitAccountId, gbpCreditAccountId, Usd(10), None));

        await service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, Usd(40), None);

        Assert.Equal(fundedBalance, await TestLedger.BalanceOfAsync(failedDebitAccountId));
        Assert.Equal(fundedBalance - 40, await TestLedger.BalanceOfAsync(debitAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task DepositAsync_AccountInAnotherCurrency_Throws()
    {
        var accountId = await TestLedger.CreateCustomerAsync(Currency.EUR);

        await Assert.ThrowsAsync<CurrencyMismatchException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.DepositAsync(TestLedger.NewKey(), accountId, Usd(10), None)));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task DepositAsync_UnknownAccount_Throws()
    {
        await Assert.ThrowsAsync<AccountNotFoundException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.DepositAsync(TestLedger.NewKey(), Guid.CreateVersion7(), Usd(10), None)));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_NamesASystemAccount_Throws()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        await Assert.ThrowsAsync<SystemAccountNotAllowedException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(TestLedger.NewKey(), worldAccountId, creditAccountId, Usd(10), None)));

        Assert.Equal(0, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task DepositAsync_IntoTheWorldAccountItself_ThrowsSystemAccountNotAllowed()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);

        await Assert.ThrowsAsync<SystemAccountNotAllowedException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.DepositAsync(TestLedger.NewKey(), worldAccountId, Usd(10), None)));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task WithdrawAsync_FromTheWorldAccountItself_ThrowsSystemAccountNotAllowed()
    {
        var worldAccountId = await TestLedger.WorldAccountIdAsync(Currency.USD);

        await Assert.ThrowsAsync<SystemAccountNotAllowedException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.WithdrawAsync(TestLedger.NewKey(), worldAccountId, Usd(10), None)));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_SameKeyAndSameRequest_ReplaysTheOriginalWithoutWriting()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(1000);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();

        var original = await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(400), None));
        var replay = await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(400), None));

        Assert.True(replay.WasReplayed);
        Assert.Equal(original.Transfer.Id, replay.Transfer.Id);
        Assert.Equal(2, replay.Transfer.Entries.Count);
        Assert.Equal(600, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(1, await CountTransfersAsync(key));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_SameKeyButDifferentAmount_ThrowsKeyReused()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(1000);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(400), None));

        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(401), None)));

        Assert.Equal(600, await TestLedger.BalanceOfAsync(debitAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_KeyAlreadyUsedByADeposit_ThrowsKeyReused()
    {
        var debitAccountId = await TestLedger.CreateCustomerAsync();
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        await TestLedger.WithTransferServiceAsync(service => service.DepositAsync(key, debitAccountId, Usd(500), None));

        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(500), None)));

        Assert.Equal(500, await TestLedger.BalanceOfAsync(debitAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task DepositAsync_SameKeyAndSameRequest_ReplaysTheOriginal()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();

        var original = await TestLedger.WithTransferServiceAsync(service => service.DepositAsync(key, accountId, Usd(500), None));
        var replay = await TestLedger.WithTransferServiceAsync(service => service.DepositAsync(key, accountId, Usd(500), None));

        Assert.True(replay.WasReplayed);
        Assert.Equal(original.Transfer.Id, replay.Transfer.Id);
        Assert.Equal(500, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_RejectedRequest_IsNotRememberedAndCanSucceedLater()
    {
        var debitAccountId = await TestLedger.CreateCustomerAsync();
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        await Assert.ThrowsAsync<InsufficientFundsException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(50), None)));
        await TestLedger.DepositAsync(debitAccountId, Usd(50), TestLedger.BaseTime);

        var outcome = await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(50), None));

        Assert.False(outcome.WasReplayed);
        Assert.Equal(50, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task TransferAsync_ReplayAfterTheFundsAreGone_StillReturnsTheOriginal()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(100);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();
        var original = await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(100), None));

        var replay = await TestLedger.WithTransferServiceAsync(service => service.TransferAsync(key, debitAccountId, creditAccountId, Usd(100), None));

        Assert.True(replay.WasReplayed);
        Assert.Equal(original.Transfer.Id, replay.Transfer.Id);
        Assert.Equal(0, await TestLedger.BalanceOfAsync(debitAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task GetAsync_ExistingTransfer_ReturnsItWithEntries()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var created = await TestLedger.DepositAsync(accountId, Usd(100), TestLedger.BaseTime);

        var transfer = await TestLedger.WithTransferServiceAsync(service => service.GetAsync(created.Id, None));

        Assert.Equal(created.Id, transfer.Id);
        Assert.Equal(2, transfer.Entries.Count);
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task GetAsync_UnknownTransfer_Throws()
    {
        await Assert.ThrowsAsync<TransferNotFoundException>(() =>
            TestLedger.WithTransferServiceAsync(service => service.GetAsync(Guid.CreateVersion7(), None)));
    }
}
