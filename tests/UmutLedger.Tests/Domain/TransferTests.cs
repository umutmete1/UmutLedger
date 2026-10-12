using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Domain;

public class TransferTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly IdempotencyKey Key = new("key-1");

    private static Account NewAccount(AccountType type = AccountType.Customer, Currency currency = Currency.USD) =>
        new(Guid.CreateVersion7(), "Account", currency, type, CreatedAt);

    private static Account FundedAccount(long balance)
    {
        var account = NewAccount();
        account.Credit(Usd(balance));
        return account;
    }

    private static Money Usd(long amount) => new(amount, Currency.USD);

    [Fact]
    public void Execute_ValidTransfer_MovesMoneyAndRecordsIt()
    {
        var id = Guid.CreateVersion7();
        var debitAccount = FundedAccount(1000);
        var creditAccount = NewAccount();

        var transfer = Transfer.Execute(id, Key, debitAccount, creditAccount, Usd(400), CreatedAt);

        Assert.Equal(600, debitAccount.Balance);
        Assert.Equal(400, creditAccount.Balance);
        Assert.Equal(id, transfer.Id);
        Assert.Equal(Key, transfer.IdempotencyKey);
        Assert.Equal(debitAccount.Id, transfer.DebitAccountId);
        Assert.Equal(creditAccount.Id, transfer.CreditAccountId);
        Assert.Equal(400, transfer.Amount);
        Assert.Equal(Currency.USD, transfer.Currency);
        Assert.Equal(CreatedAt, transfer.CreatedAt);
    }

    [Fact]
    public void Execute_ValidTransfer_CreatesTwoEntriesThatSumToZero()
    {
        var debitAccount = FundedAccount(1000);
        var creditAccount = NewAccount();

        var transfer = Transfer.Execute(Guid.CreateVersion7(), Key, debitAccount, creditAccount, Usd(400), CreatedAt);

        Assert.Equal(2, transfer.Entries.Count);
        Assert.Equal(0, transfer.Entries.Sum(entry => entry.Amount));
        Assert.Equal(-400, transfer.Entries.Single(entry => entry.AccountId == debitAccount.Id).Amount);
        Assert.Equal(400, transfer.Entries.Single(entry => entry.AccountId == creditAccount.Id).Amount);
        Assert.All(transfer.Entries, entry =>
        {
            Assert.Equal(transfer.Id, entry.TransferId);
            Assert.Equal(CreatedAt, entry.CreatedAt);
            Assert.NotEqual(Guid.Empty, entry.Id);
        });
        Assert.Equal(2, transfer.Entries.Select(entry => entry.Id).Distinct().Count());
    }

    [Fact]
    public void Execute_EmptyId_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => Transfer.Execute(Guid.Empty, Key, FundedAccount(100), NewAccount(), Usd(10), CreatedAt));
    }

    [Fact]
    public void Execute_NullIdempotencyKey_ThrowsKeyRequired()
    {
        var debitAccount = FundedAccount(100);

        Assert.Throws<IdempotencyKeyRequiredException>(() => Transfer.Execute(Guid.CreateVersion7(), null!, debitAccount, NewAccount(), Usd(10), CreatedAt));
        Assert.Equal(100, debitAccount.Balance);
    }

    [Fact]
    public void Execute_NullDebitAccount_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, null!, NewAccount(), Usd(10), CreatedAt));
    }

    [Fact]
    public void Execute_NullCreditAccount_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, FundedAccount(100), null!, Usd(10), CreatedAt));
    }

    [Fact]
    public void Execute_SameAccountOnBothSides_Throws()
    {
        var account = FundedAccount(100);

        Assert.Throws<SameAccountTransferException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, account, account, Usd(10), CreatedAt));
        Assert.Equal(100, account.Balance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Execute_NonPositiveAmount_Throws(long amount)
    {
        Assert.Throws<ValidationFailedException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, FundedAccount(100), NewAccount(), Usd(amount), CreatedAt));
    }

    [Fact]
    public void Execute_InsufficientFunds_Throws()
    {
        Assert.Throws<InsufficientFundsException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, FundedAccount(100), NewAccount(), Usd(101), CreatedAt));
    }

    [Fact]
    public void Execute_DebitAccountInAnotherCurrency_Throws()
    {
        var debitAccount = NewAccount(AccountType.System, Currency.EUR);

        Assert.Throws<CurrencyMismatchException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, debitAccount, NewAccount(), Usd(10), CreatedAt));
    }

    [Fact]
    public void Execute_CreditAccountInAnotherCurrency_Throws()
    {
        var debitAccount = FundedAccount(100);
        var creditAccount = NewAccount(currency: Currency.EUR);

        Assert.Throws<CurrencyMismatchException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, debitAccount, creditAccount, Usd(10), CreatedAt));
        Assert.Equal(100, debitAccount.Balance);
        Assert.Equal(0, creditAccount.Balance);
    }

    [Fact]
    public void Execute_CreditWouldOverflow_ThrowsAndLeavesBothBalancesUnchanged()
    {
        var debitAccount = FundedAccount(100);
        var creditAccount = NewAccount();
        creditAccount.Credit(Usd(long.MaxValue));

        Assert.Throws<ValidationFailedException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, debitAccount, creditAccount, Usd(10), CreatedAt));
        Assert.Equal(100, debitAccount.Balance);
        Assert.Equal(long.MaxValue, creditAccount.Balance);
    }

    [Fact]
    public void Execute_NullAmount_ThrowsAndLeavesBothBalancesUnchanged()
    {
        var debitAccount = FundedAccount(100);
        var creditAccount = NewAccount();

        Assert.Throws<ValidationFailedException>(() => Transfer.Execute(Guid.CreateVersion7(), Key, debitAccount, creditAccount, null!, CreatedAt));
        Assert.Equal(100, debitAccount.Balance);
        Assert.Equal(0, creditAccount.Balance);
    }

    [Fact]
    public void IsSameRequest_IdenticalRequest_IsTrue()
    {
        var debitAccount = FundedAccount(100);
        var creditAccount = NewAccount();
        var transfer = Transfer.Execute(Guid.CreateVersion7(), Key, debitAccount, creditAccount, Usd(10), CreatedAt);

        Assert.True(transfer.IsSameRequest(debitAccount.Id, creditAccount.Id, Usd(10)));
    }

    [Fact]
    public void IsSameRequest_AnyDifference_IsFalse()
    {
        var debitAccount = FundedAccount(100);
        var creditAccount = NewAccount();
        var transfer = Transfer.Execute(Guid.CreateVersion7(), Key, debitAccount, creditAccount, Usd(10), CreatedAt);

        Assert.False(transfer.IsSameRequest(Guid.CreateVersion7(), creditAccount.Id, Usd(10)));
        Assert.False(transfer.IsSameRequest(debitAccount.Id, Guid.CreateVersion7(), Usd(10)));
        Assert.False(transfer.IsSameRequest(debitAccount.Id, creditAccount.Id, Usd(11)));
        Assert.False(transfer.IsSameRequest(debitAccount.Id, creditAccount.Id, new Money(10, Currency.EUR)));
    }
}
