using Microsoft.EntityFrameworkCore;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Application.Transfers;

public sealed class TransferService(LedgerDbContext db, TimeProvider timeProvider, ILedgerMetrics metrics) : ITransferService
{
    public async Task<TransferOutcome> TransferAsync(
        IdempotencyKey key, Guid debitAccountId, Guid creditAccountId, Money amount, CancellationToken cancellationToken)
    {
        if (debitAccountId == creditAccountId)
        {
            throw new SameAccountTransferException();
        }

        var instruction = new Instruction(MovementKind.Transfer, key, debitAccountId, creditAccountId, amount, [debitAccountId, creditAccountId]);
        return await ExecuteAsync(instruction, cancellationToken);
    }

    public async Task<TransferOutcome> DepositAsync(
        IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken)
    {
        var worldAccountId = await FindWorldAccountIdAsync(amount.Currency, cancellationToken);
        return await ExecuteAsync(new Instruction(MovementKind.Deposit, key, worldAccountId, accountId, amount, [accountId]), cancellationToken);
    }

    public async Task<TransferOutcome> WithdrawAsync(
        IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken)
    {
        var worldAccountId = await FindWorldAccountIdAsync(amount.Currency, cancellationToken);
        return await ExecuteAsync(new Instruction(MovementKind.Withdrawal, key, accountId, worldAccountId, amount, [accountId]), cancellationToken);
    }

    public async Task<Transfer> GetAsync(Guid transferId, CancellationToken cancellationToken) =>
        await TransfersWithEntries().AsNoTracking().SingleOrDefaultAsync(transfer => transfer.Id == transferId, cancellationToken)
        ?? throw new TransferNotFoundException(transferId);

    private async Task<TransferOutcome> ExecuteAsync(Instruction instruction, CancellationToken cancellationToken)
    {
        instruction.Amount.EnsurePositive();

        // A stale tracked Account would be returned by the lock query instead of the freshly locked row.
        db.ChangeTracker.Clear();
        try
        {
            return await ApplyInOneTransactionAsync(instruction, cancellationToken);
        }
        catch (DbUpdateException exception) when (IdempotencyKeyConflict.Matches(exception))
        {
            // A concurrent request with the same key committed first; this one rolled back and now applies the replay rules to the winner.
            db.ChangeTracker.Clear();
            var winner = await TransfersWithEntries().SingleAsync(transfer => transfer.IdempotencyKey == instruction.Key, cancellationToken);
            return Replay(winner, instruction);
        }
    }

    private async Task<TransferOutcome> ApplyInOneTransactionAsync(Instruction instruction, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var replayWithoutLocks = await FindReplayAsync(instruction, cancellationToken);
        if (replayWithoutLocks is not null)
        {
            return replayWithoutLocks;
        }

        var lockedAccounts = await LockAccountsAsync(instruction, cancellationToken);

        // A request that waited for the locks may find the winner's transfer committed in the meantime.
        var replayAfterLocks = await FindReplayAsync(instruction, cancellationToken);
        if (replayAfterLocks is not null)
        {
            return replayAfterLocks;
        }

        var debitAccount = Find(lockedAccounts, instruction.DebitAccountId);
        var creditAccount = Find(lockedAccounts, instruction.CreditAccountId);
        RejectSystemAccountsNamedByTheCaller(lockedAccounts, instruction);

        var now = timeProvider.GetUtcNow();
        var transfer = Transfer.Execute(Guid.CreateVersion7(now), instruction.Key, debitAccount, creditAccount, instruction.Amount, now);
        db.Transfers.Add(transfer);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TransferOutcome(transfer, wasReplayed: false);
    }

    private async Task<TransferOutcome?> FindReplayAsync(Instruction instruction, CancellationToken cancellationToken)
    {
        var existing = await TransfersWithEntries().SingleOrDefaultAsync(transfer => transfer.IdempotencyKey == instruction.Key, cancellationToken);
        return existing is null ? null : Replay(existing, instruction);
    }

    // Locking in ascending id order means two opposing transfers always queue instead of deadlocking.
    private async Task<Dictionary<Guid, Account>> LockAccountsAsync(Instruction instruction, CancellationToken cancellationToken)
    {
        Guid[] accountIds = [instruction.DebitAccountId, instruction.CreditAccountId];
        var lockRequestedAt = timeProvider.GetTimestamp();
        var lockedAccounts = await db.Accounts
            .FromSql($"SELECT * FROM accounts WHERE id = ANY({accountIds}) ORDER BY id FOR UPDATE")
            .ToListAsync(cancellationToken);
        metrics.RecordLockWait(instruction.Kind, instruction.Amount.Currency, timeProvider.GetElapsedTime(lockRequestedAt));
        return lockedAccounts.ToDictionary(account => account.Id);
    }

    private Task<Guid> FindWorldAccountIdAsync(Currency currency, CancellationToken cancellationToken) =>
        db.Accounts
            .Where(account => account.Type == AccountType.System && account.Currency == currency)
            .Select(account => account.Id)
            .SingleAsync(cancellationToken);

    private IQueryable<Transfer> TransfersWithEntries() => db.Transfers.Include(transfer => transfer.Entries);

    private static Account Find(Dictionary<Guid, Account> lockedAccounts, Guid accountId) =>
        lockedAccounts.GetValueOrDefault(accountId) ?? throw new AccountNotFoundException(accountId);

    private static void RejectSystemAccountsNamedByTheCaller(Dictionary<Guid, Account> lockedAccounts, Instruction instruction)
    {
        foreach (var accountId in instruction.AccountIdsNamedByTheCaller)
        {
            if (lockedAccounts[accountId].Type == AccountType.System)
            {
                throw new SystemAccountNotAllowedException(accountId);
            }
        }
    }

    private static TransferOutcome Replay(Transfer existing, Instruction instruction)
    {
        if (!existing.IsSameRequest(instruction.DebitAccountId, instruction.CreditAccountId, instruction.Amount))
        {
            throw new IdempotencyKeyReusedException();
        }

        return new TransferOutcome(existing, wasReplayed: true);
    }

    private sealed record Instruction(
        MovementKind Kind,
        IdempotencyKey Key,
        Guid DebitAccountId,
        Guid CreditAccountId,
        Money Amount,
        IReadOnlyCollection<Guid> AccountIdsNamedByTheCaller);
}
