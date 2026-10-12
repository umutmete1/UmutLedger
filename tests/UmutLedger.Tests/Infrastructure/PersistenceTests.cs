using Microsoft.EntityFrameworkCore;
using UmutLedger.Domain.Enums;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Infrastructure;

public class PersistenceTests
{
    [Fact]
    public async Task Account_SavedAndReloaded_KeepsItsValues()
    {
        var accountId = await TestLedger.CreateCustomerAsync(Currency.GBP);

        var reloaded = await TestLedger.LoadAccountAsync(accountId);

        Assert.Equal("Test customer", reloaded.Name);
        Assert.Equal(Currency.GBP, reloaded.Currency);
        Assert.Equal(AccountType.Customer, reloaded.Type);
        Assert.Equal(0, reloaded.Balance);
        Assert.Equal(TestLedger.BaseTime, reloaded.CreatedAt);
    }

    [Fact]
    public async Task Transfer_SavedAndReloaded_KeepsItsValuesAndEntries()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var saved = await TestLedger.DepositAsync(accountId, TestLedger.Usd(250), TestLedger.BaseTime);

        await using var db = await TestDatabase.CreateContextAsync();
        var reloaded = await db.Transfers.AsNoTracking()
            .Include(transfer => transfer.Entries)
            .SingleAsync(transfer => transfer.IdempotencyKey == saved.IdempotencyKey);

        Assert.Equal(saved.Id, reloaded.Id);
        Assert.Equal(saved.DebitAccountId, reloaded.DebitAccountId);
        Assert.Equal(accountId, reloaded.CreditAccountId);
        Assert.Equal(250, reloaded.Amount);
        Assert.Equal(Currency.USD, reloaded.Currency);
        Assert.Equal(TestLedger.BaseTime, reloaded.CreatedAt);
        Assert.Equal(2, reloaded.Entries.Count);
        Assert.Equal(0, reloaded.Entries.Sum(entry => entry.Amount));
    }

    [Fact]
    public async Task Deposit_UpdatesTheStoredBalance()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(750);

        Assert.Equal(750, await TestLedger.BalanceOfAsync(accountId));
        await LedgerInvariants.AssertHoldAsync();
    }
}
