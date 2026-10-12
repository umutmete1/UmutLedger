using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;
using UmutLedger.Infrastructure.Persistence;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class TransferConcurrencyTests
{
    private const int ParallelRequests = 20;
    private const long TransferAmount = 100;
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan BlockedRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BlockedRequestPollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan LockHeldFor = TimeSpan.FromMilliseconds(300);

    private static Task<TransferOutcome> TransferAsync(IdempotencyKey key, Guid debitAccountId, Guid creditAccountId, long amount) =>
        Task.Run(() => TestLedger.WithTransferServiceAsync(service =>
            service.TransferAsync(key, debitAccountId, creditAccountId, TestLedger.Usd(amount), None)));

    private static async Task<bool> SucceededAsync(Task<TransferOutcome> transfer)
    {
        try
        {
            await transfer;
            return true;
        }
        catch (InsufficientFundsException)
        {
            return false;
        }
    }

    [Fact]
    public async Task OverdraftRace_OnlyTheAffordableTransfersSucceed()
    {
        const int affordableTransfers = 5;
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(affordableTransfers * TransferAmount);
        var creditAccountId = await TestLedger.CreateCustomerAsync();

        var attempts = Enumerable.Range(0, ParallelRequests)
            .Select(_ => SucceededAsync(TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, TransferAmount)));
        var results = await Task.WhenAll(attempts);

        Assert.Equal(affordableTransfers, results.Count(succeeded => succeeded));
        Assert.Equal(0, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(affordableTransfers * TransferAmount, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task OpposingTransfers_AllCompleteWithoutDeadlock()
    {
        const long openingBalance = ParallelRequests * TransferAmount;
        var firstAccountId = await TestLedger.CreateFundedCustomerAsync(openingBalance);
        var secondAccountId = await TestLedger.CreateFundedCustomerAsync(openingBalance);

        var transfers = Enumerable.Range(0, ParallelRequests).SelectMany(_ => new[]
        {
            TransferAsync(TestLedger.NewKey(), firstAccountId, secondAccountId, TransferAmount),
            TransferAsync(TestLedger.NewKey(), secondAccountId, firstAccountId, TransferAmount)
        });
        var outcomes = await Task.WhenAll(transfers);

        Assert.Equal(ParallelRequests * 2, outcomes.Length);
        Assert.Equal(openingBalance, await TestLedger.BalanceOfAsync(firstAccountId));
        Assert.Equal(openingBalance, await TestLedger.BalanceOfAsync(secondAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task DuplicateKeyRace_WritesExactlyOneTransfer()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(ParallelRequests * TransferAmount);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();

        var attempts = Enumerable.Range(0, ParallelRequests)
            .Select(_ => TransferAsync(key, debitAccountId, creditAccountId, TransferAmount));
        var outcomes = await Task.WhenAll(attempts);

        Assert.Single(outcomes, outcome => !outcome.WasReplayed);
        Assert.Single(outcomes.Select(outcome => outcome.Transfer.Id).Distinct());
        Assert.Equal(TransferAmount, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    // The winner drains the account, so the loser can only succeed by replaying: re-executing would fail for lack of funds.
    [Fact]
    public async Task DuplicateKeyRace_LoserBlockedBehindAWinnerThatDrainsTheAccount_ReplaysTheWinner()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(TransferAmount);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();

        await using var winnerDb = await TestDatabase.CreateContextAsync();
        await using var winnerTransaction = await winnerDb.Database.BeginTransactionAsync();
        var winnerProcessId = await BackendProcessIdAsync(winnerDb);
        var winner = await AddUncommittedTransferAsync(winnerDb, key, debitAccountId, creditAccountId);

        var loser = TransferAsync(key, debitAccountId, creditAccountId, TransferAmount);
        await WaitUntilARequestIsBlockedByAsync(winnerProcessId);
        await winnerTransaction.CommitAsync();
        var outcome = await loser;

        Assert.True(outcome.WasReplayed);
        Assert.Equal(winner.Id, outcome.Transfer.Id);
        Assert.Equal(0, await TestLedger.BalanceOfAsync(debitAccountId));
        Assert.Equal(TransferAmount, await TestLedger.BalanceOfAsync(creditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    // Two requests with one key but no account in common never wait on each other's row locks,
    // so only the unique constraint on the key can stop the second one.
    [Fact]
    public async Task DuplicateKeyRace_SameKeyOnUnrelatedAccounts_LoserIsStoppedByTheUniqueConstraint()
    {
        var winnerDebitAccountId = await TestLedger.CreateFundedCustomerAsync(TransferAmount);
        var winnerCreditAccountId = await TestLedger.CreateCustomerAsync();
        var loserDebitAccountId = await TestLedger.CreateFundedCustomerAsync(TransferAmount);
        var loserCreditAccountId = await TestLedger.CreateCustomerAsync();
        var key = TestLedger.NewKey();

        await using var winnerDb = await TestDatabase.CreateContextAsync();
        await using var winnerTransaction = await winnerDb.Database.BeginTransactionAsync();
        var winnerProcessId = await BackendProcessIdAsync(winnerDb);
        await AddUncommittedTransferAsync(winnerDb, key, winnerDebitAccountId, winnerCreditAccountId);

        var loser = TransferAsync(key, loserDebitAccountId, loserCreditAccountId, TransferAmount);
        await WaitUntilARequestIsBlockedByAsync(winnerProcessId);
        await winnerTransaction.CommitAsync();

        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() => loser);
        Assert.Equal(TransferAmount, await TestLedger.BalanceOfAsync(loserDebitAccountId));
        Assert.Equal(0, await TestLedger.BalanceOfAsync(loserCreditAccountId));
        Assert.Equal(TransferAmount, await TestLedger.BalanceOfAsync(winnerCreditAccountId));
        await LedgerInvariants.AssertHoldAsync();
    }

    // The measured wait started before the request was seen blocked and ended after the commit, so it contains the held interval.
    [Fact]
    public async Task LockWait_BehindAnotherTransaction_IsAtLeastAsLongAsTheLockWasHeld()
    {
        var debitAccountId = await TestLedger.CreateFundedCustomerAsync(TransferAmount);
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        var metrics = new RecordingLedgerMetrics();

        await using var holderDb = await TestDatabase.CreateContextAsync();
        await using var holderTransaction = await holderDb.Database.BeginTransactionAsync();
        var holderProcessId = await BackendProcessIdAsync(holderDb);
        await LockAccountsAsync(holderDb, debitAccountId, creditAccountId);

        var blocked = Task.Run(() => TestLedger.WithTransferServiceAsync(
            service => service.TransferAsync(TestLedger.NewKey(), debitAccountId, creditAccountId, TestLedger.Usd(TransferAmount), None),
            metrics: metrics));
        await WaitUntilARequestIsBlockedByAsync(holderProcessId);
        var heldSince = Stopwatch.GetTimestamp();
        await Task.Delay(LockHeldFor);
        var heldFor = Stopwatch.GetElapsedTime(heldSince);
        await holderTransaction.CommitAsync();
        await blocked;

        var lockWait = Assert.Single(metrics.LockWaits);
        Assert.True(lockWait.Wait >= heldFor, $"Recorded {lockWait.Wait} but the lock was held for {heldFor}.");
    }

    private static async Task<Transfer> AddUncommittedTransferAsync(
        LedgerDbContext db, IdempotencyKey key, Guid debitAccountId, Guid creditAccountId)
    {
        var lockedAccounts = await LockAccountsAsync(db, debitAccountId, creditAccountId);
        var transfer = Transfer.Execute(
            Guid.CreateVersion7(),
            key,
            lockedAccounts.Single(account => account.Id == debitAccountId),
            lockedAccounts.Single(account => account.Id == creditAccountId),
            TestLedger.Usd(TransferAmount),
            TestLedger.BaseTime);
        db.Transfers.Add(transfer);
        await db.SaveChangesAsync();
        return transfer;
    }

    private static Task<List<Account>> LockAccountsAsync(LedgerDbContext db, params Guid[] accountIds) =>
        db.Accounts
            .FromSql($"SELECT * FROM accounts WHERE id = ANY({accountIds}) ORDER BY id FOR UPDATE")
            .ToListAsync();

    private static Task<int> BackendProcessIdAsync(LedgerDbContext db) =>
        db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

    private static async Task WaitUntilARequestIsBlockedByAsync(int blockingProcessId)
    {
        using var timeout = new CancellationTokenSource(BlockedRequestTimeout);
        await using var probe = await TestDatabase.CreateContextAsync();
        while (await CountRequestsBlockedByAsync(probe, blockingProcessId) == 0)
        {
            await Task.Delay(BlockedRequestPollInterval, timeout.Token);
        }
    }

    private static Task<int> CountRequestsBlockedByAsync(LedgerDbContext probe, int blockingProcessId) =>
        probe.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE {blockingProcessId} = ANY(pg_blocking_pids(pid))")
            .SingleAsync();
}
