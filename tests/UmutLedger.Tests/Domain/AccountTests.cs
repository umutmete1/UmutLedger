using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Domain;

public class AccountTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static Account NewAccount(AccountType type = AccountType.Customer, string name = "Alice") =>
        new(Guid.CreateVersion7(), name, Currency.USD, type, CreatedAt);

    private static Money Usd(long amount) => new(amount, Currency.USD);

    [Fact]
    public void Constructor_ValidValues_CreatesAccountWithZeroBalance()
    {
        var id = Guid.CreateVersion7();

        var account = new Account(id, "  Alice  ", Currency.GBP, AccountType.Customer, CreatedAt);

        Assert.Equal(id, account.Id);
        Assert.Equal("Alice", account.Name);
        Assert.Equal(Currency.GBP, account.Currency);
        Assert.Equal(AccountType.Customer, account.Type);
        Assert.Equal(0, account.Balance);
        Assert.Equal(CreatedAt, account.CreatedAt);
    }

    [Fact]
    public void Constructor_EmptyId_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Account(Guid.Empty, "Alice", Currency.USD, AccountType.Customer, CreatedAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankName_Throws(string name)
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount(name: name));
    }

    [Fact]
    public void Constructor_NameAtMaxLength_IsAccepted()
    {
        var account = NewAccount(name: new string('a', Account.MaxNameLength));

        Assert.Equal(Account.MaxNameLength, account.Name.Length);
    }

    [Fact]
    public void Constructor_NameOverMaxLength_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount(name: new string('a', Account.MaxNameLength + 1)));
    }

    [Fact]
    public void Constructor_UnsupportedCurrency_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Account(Guid.CreateVersion7(), "Alice", (Currency)99, AccountType.Customer, CreatedAt));
    }

    [Fact]
    public void Constructor_UnsupportedType_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount(type: (AccountType)99));
    }

    [Fact]
    public void Credit_PositiveAmount_IncreasesBalance()
    {
        var account = NewAccount();

        account.Credit(Usd(500));

        Assert.Equal(500, account.Balance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Credit_NonPositiveAmount_Throws(long amount)
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount().Credit(Usd(amount)));
    }

    [Fact]
    public void Credit_DifferentCurrency_Throws()
    {
        Assert.Throws<CurrencyMismatchException>(() => NewAccount().Credit(new Money(100, Currency.EUR)));
    }

    [Fact]
    public void Credit_BalanceWouldExceedLongRange_ThrowsValidationFailedAndKeepsBalance()
    {
        var account = NewAccount();
        account.Credit(Usd(long.MaxValue));

        Assert.Throws<ValidationFailedException>(() => account.Credit(Usd(1)));
        Assert.Equal(long.MaxValue, account.Balance);
    }

    [Fact]
    public void Credit_NullAmount_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount().Credit(null!));
    }

    [Fact]
    public void Debit_NullAmount_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount().Debit(null!));
    }

    [Fact]
    public void EnsureCanCredit_ValidAmount_DoesNotThrowAndKeepsBalance()
    {
        var account = NewAccount();
        account.Credit(Usd(500));

        account.EnsureCanCredit(Usd(100));

        Assert.Equal(500, account.Balance);
    }

    [Fact]
    public void EnsureCanCredit_NullAmount_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount().EnsureCanCredit(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EnsureCanCredit_NonPositiveAmount_Throws(long amount)
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount().EnsureCanCredit(Usd(amount)));
    }

    [Fact]
    public void EnsureCanCredit_DifferentCurrency_Throws()
    {
        Assert.Throws<CurrencyMismatchException>(() => NewAccount().EnsureCanCredit(new Money(100, Currency.EUR)));
    }

    [Fact]
    public void EnsureCanCredit_BalanceWouldExceedLongRange_Throws()
    {
        var account = NewAccount();
        account.Credit(Usd(long.MaxValue));

        Assert.Throws<ValidationFailedException>(() => account.EnsureCanCredit(Usd(1)));
        Assert.Equal(long.MaxValue, account.Balance);
    }

    [Fact]
    public void Debit_CustomerWithEnoughFunds_DecreasesBalance()
    {
        var account = NewAccount();
        account.Credit(Usd(500));

        account.Debit(Usd(500));

        Assert.Equal(0, account.Balance);
    }

    [Fact]
    public void Debit_CustomerWithoutEnoughFunds_ThrowsAndKeepsBalance()
    {
        var account = NewAccount();
        account.Credit(Usd(500));

        Assert.Throws<InsufficientFundsException>(() => account.Debit(Usd(501)));
        Assert.Equal(500, account.Balance);
    }

    [Fact]
    public void Debit_SystemAccount_MayGoNegative()
    {
        var account = NewAccount(AccountType.System);

        account.Debit(Usd(300));

        Assert.Equal(-300, account.Balance);
    }

    [Fact]
    public void Debit_SystemBalanceWouldExceedLongRange_ThrowsValidationFailed()
    {
        var account = NewAccount(AccountType.System);
        account.Debit(Usd(long.MaxValue));
        account.Debit(Usd(1));

        Assert.Throws<ValidationFailedException>(() => account.Debit(Usd(1)));
        Assert.Equal(long.MinValue, account.Balance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Debit_NonPositiveAmount_Throws(long amount)
    {
        Assert.Throws<ValidationFailedException>(() => NewAccount(AccountType.System).Debit(Usd(amount)));
    }

    [Fact]
    public void Debit_DifferentCurrency_Throws()
    {
        Assert.Throws<CurrencyMismatchException>(() => NewAccount(AccountType.System).Debit(new Money(100, Currency.EUR)));
    }
}
