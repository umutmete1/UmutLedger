using Microsoft.EntityFrameworkCore;
using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Support;

public static class TestLedger
{
    public static readonly DateTimeOffset BaseTime = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public static Money Usd(long amount) => new(amount, Currency.USD);

    public static IdempotencyKey NewKey() => new(Guid.NewGuid().ToString());

    public static async Task<Guid> CreateCustomerAsync(Currency currency = Currency.USD)
    {
        var account = new Account(Guid.CreateVersion7(), "Test customer", currency, AccountType.Customer, BaseTime);
        await using var db = await TestDatabase.CreateContextAsync();
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    public static async Task<Guid> CreateFundedCustomerAsync(long balance, Currency currency = Currency.USD)
    {
        var accountId = await CreateCustomerAsync(currency);
        await DepositAsync(accountId, new Money(balance, currency), BaseTime);
        return accountId;
    }

    // Seeds a deposit without the service under test, holding both row locks so parallel tests never lose an update.
    public static async Task<Transfer> DepositAsync(Guid accountId, Money amount, DateTimeOffset at)
    {
        var worldAccountId = await WorldAccountIdAsync(amount.Currency);
        Guid[] accountIds = [worldAccountId, accountId];

        await using var db = await TestDatabase.CreateContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var accounts = await db.Accounts
            .FromSql($"SELECT * FROM accounts WHERE id = ANY({accountIds}) ORDER BY id FOR UPDATE")
            .ToListAsync();
        var transfer = Transfer.Execute(
            Guid.CreateVersion7(at),
            NewKey(),
            accounts.Single(account => account.Id == worldAccountId),
            accounts.Single(account => account.Id == accountId),
            amount,
            at);
        db.Transfers.Add(transfer);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return transfer;
    }

    public static async Task<TResult> WithTransferServiceAsync<TResult>(
        Func<ITransferService, Task<TResult>> action,
        TimeProvider? timeProvider = null,
        ILedgerMetrics? metrics = null)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        var service = new TransferService(db, timeProvider ?? TimeProvider.System, metrics ?? new RecordingLedgerMetrics());
        return await action(service);
    }

    public static async Task<Account> LoadAccountAsync(Guid accountId)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        return await db.Accounts.AsNoTracking().SingleAsync(account => account.Id == accountId);
    }

    public static async Task<long> BalanceOfAsync(Guid accountId) => (await LoadAccountAsync(accountId)).Balance;

    public static async Task<Guid> WorldAccountIdAsync(Currency currency)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        return await db.Accounts
            .Where(account => account.Type == AccountType.System && account.Currency == currency)
            .Select(account => account.Id)
            .SingleAsync();
    }
}
