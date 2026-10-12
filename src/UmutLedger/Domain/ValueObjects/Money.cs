using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Domain.ValueObjects;

public sealed record Money
{
    public Money(long amount, Currency currency)
    {
        Amount = amount;
        Currency = Require.Defined(currency);
    }

    public long Amount { get; }

    public Currency Currency { get; }

    public Money Add(Money other) => Combine(other, static (left, right) => checked(left + right));

    public Money Subtract(Money other) => Combine(other, static (left, right) => checked(left - right));

    public bool IsLessThan(Money other)
    {
        EnsureSameCurrency(other);
        return Amount < other.Amount;
    }

    public void EnsurePositive()
    {
        if (Amount <= 0)
        {
            throw new ValidationFailedException("Amount must be greater than zero.");
        }
    }

    private Money Combine(Money other, Func<long, long, long> operation)
    {
        EnsureSameCurrency(other);
        try
        {
            return new Money(operation(Amount, other.Amount), Currency);
        }
        catch (OverflowException)
        {
            throw new ValidationFailedException("Amount is outside the supported range.");
        }
    }

    private void EnsureSameCurrency(Money other)
    {
        if (other.Currency != Currency)
        {
            throw new CurrencyMismatchException(Currency, other.Currency);
        }
    }
}