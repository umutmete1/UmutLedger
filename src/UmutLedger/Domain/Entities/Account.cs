using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Domain.Entities;

public sealed class Account
{
    public const int MaxNameLength = 100;
    private const long OpeningBalance = 0;

    public Account(Guid id, string name, Currency currency, AccountType type, DateTimeOffset createdAt)
    {
        var openingBalance = new Money(OpeningBalance, currency);
        Id = Require.NotEmpty(id, "Account id");
        Name = ValidName(name);
        Currency = openingBalance.Currency;
        Type = Require.Defined(type);
        Balance = openingBalance.Amount;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Name { get; }

    public Currency Currency { get; }

    public AccountType Type { get; }

    public long Balance { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public void EnsureCanCredit(Money amount) => BalanceAfterCredit(amount);

    public void Credit(Money amount) => Balance = BalanceAfterCredit(amount);

    public void Debit(Money amount)
    {
        Require.NotNull(amount, "Amount").EnsurePositive();
        var balance = CurrentBalance();
        if (Type == AccountType.Customer && balance.IsLessThan(amount))
        {
            throw new InsufficientFundsException(Id);
        }

        Balance = balance.Subtract(amount).Amount;
    }

    private long BalanceAfterCredit(Money amount)
    {
        Require.NotNull(amount, "Amount").EnsurePositive();
        return CurrentBalance().Add(amount).Amount;
    }

    private Money CurrentBalance() => new(Balance, Currency);

    private static string ValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ValidationFailedException("Account name is required.");
        }

        var trimmedName = name.Trim();
        if (trimmedName.Length > MaxNameLength)
        {
            throw new ValidationFailedException($"Account name must be at most {MaxNameLength} characters.");
        }

        return trimmedName;
    }
}
