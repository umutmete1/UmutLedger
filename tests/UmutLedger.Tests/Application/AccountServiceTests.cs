using Microsoft.Extensions.Time.Testing;
using UmutLedger.Application.Accounts;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class AccountServiceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<TResult> WithServiceAsync<TResult>(Func<IAccountService, Task<TResult>> action, TimeProvider? timeProvider = null)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        return await action(new AccountService(db, timeProvider ?? TimeProvider.System));
    }

    [Fact]
    public async Task CreateAsync_ValidRequest_StoresACustomerAccountWithZeroBalance()
    {
        var clock = new FakeTimeProvider(TestLedger.BaseTime);

        var created = await WithServiceAsync(service => service.CreateAsync("Alice", Currency.EUR, None), clock);

        var stored = await TestLedger.LoadAccountAsync(created.Id);
        Assert.Equal("Alice", stored.Name);
        Assert.Equal(Currency.EUR, stored.Currency);
        Assert.Equal(AccountType.Customer, stored.Type);
        Assert.Equal(0, stored.Balance);
        Assert.Equal(TestLedger.BaseTime, stored.CreatedAt);
    }

    [Fact]
    public async Task CreateAsync_BlankName_Throws()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() => WithServiceAsync(service => service.CreateAsync(" ", Currency.USD, None)));
    }

    [Fact]
    public async Task GetAsync_ExistingAccount_ReturnsIt()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(300);

        var account = await WithServiceAsync(service => service.GetAsync(accountId, None));

        Assert.Equal(accountId, account.Id);
        Assert.Equal(300, account.Balance);
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task GetAsync_UnknownAccount_Throws()
    {
        await Assert.ThrowsAsync<AccountNotFoundException>(() => WithServiceAsync(service => service.GetAsync(Guid.CreateVersion7(), None)));
    }

    [Fact]
    public async Task GetEntriesAsync_UnknownAccount_Throws()
    {
        await Assert.ThrowsAsync<AccountNotFoundException>(() =>
            WithServiceAsync(service => service.GetEntriesAsync(Guid.CreateVersion7(), new PageRequest(null, null), None)));
    }

    [Fact]
    public async Task GetEntriesAsync_AccountWithoutEntries_ReturnsAnEmptyPage()
    {
        var accountId = await TestLedger.CreateCustomerAsync();

        var page = await WithServiceAsync(service => service.GetEntriesAsync(accountId, new PageRequest(null, null), None));

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task GetEntriesAsync_SeveralEntries_PagesNewestFirst()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime.AddSeconds(1));
        await TestLedger.DepositAsync(accountId, TestLedger.Usd(200), TestLedger.BaseTime.AddSeconds(2));
        await TestLedger.DepositAsync(accountId, TestLedger.Usd(300), TestLedger.BaseTime.AddSeconds(3));

        var firstPage = await WithServiceAsync(service => service.GetEntriesAsync(accountId, new PageRequest(2, null), None));
        var secondPage = await WithServiceAsync(service => service.GetEntriesAsync(accountId, new PageRequest(2, firstPage.NextCursor), None));

        Assert.Equal([300, 200], firstPage.Items.Select(entry => entry.Amount));
        Assert.Equal(firstPage.Items[^1].Id, firstPage.NextCursor);
        Assert.Equal([100], secondPage.Items.Select(entry => entry.Amount));
        Assert.Null(secondPage.NextCursor);
        await LedgerInvariants.AssertHoldAsync();
    }

    [Fact]
    public async Task GetEntriesAsync_OnlyReturnsEntriesOfTheRequestedAccount()
    {
        var accountId = await TestLedger.CreateFundedCustomerAsync(100);
        await TestLedger.CreateFundedCustomerAsync(999);

        var page = await WithServiceAsync(service => service.GetEntriesAsync(accountId, new PageRequest(null, null), None));

        Assert.Single(page.Items);
        Assert.All(page.Items, entry => Assert.Equal(accountId, entry.AccountId));
        await LedgerInvariants.AssertHoldAsync();
    }
}
