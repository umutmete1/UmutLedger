using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Domain;

public class MoneyTests
{
    private const Currency UnsupportedCurrency = (Currency)99;

    [Fact]
    public void Constructor_UnsupportedCurrency_Throws()
    {
        Assert.Throws<ValidationFailedException>(() => new Money(100, UnsupportedCurrency));
    }

    [Fact]
    public void Constructor_ValidValues_ExposesThem()
    {
        var money = new Money(-250, Currency.EUR);

        Assert.Equal(-250, money.Amount);
        Assert.Equal(Currency.EUR, money.Currency);
    }

    [Fact]
    public void Equals_SameAmountAndCurrency_IsTrue()
    {
        Assert.Equal(new Money(100, Currency.USD), new Money(100, Currency.USD));
        Assert.NotEqual(new Money(100, Currency.USD), new Money(100, Currency.EUR));
    }

    [Fact]
    public void Add_SameCurrency_ReturnsSum()
    {
        Assert.Equal(new Money(150, Currency.USD), new Money(100, Currency.USD).Add(new Money(50, Currency.USD)));
    }

    [Fact]
    public void Add_DifferentCurrency_Throws()
    {
        Assert.Throws<CurrencyMismatchException>(() => new Money(100, Currency.USD).Add(new Money(50, Currency.EUR)));
    }

    [Fact]
    public void Add_ResultExceedsLongRange_ThrowsValidationFailed()
    {
        Assert.Throws<ValidationFailedException>(() => new Money(long.MaxValue, Currency.USD).Add(new Money(1, Currency.USD)));
    }

    [Fact]
    public void Subtract_SameCurrency_ReturnsDifference()
    {
        Assert.Equal(new Money(-50, Currency.USD), new Money(50, Currency.USD).Subtract(new Money(100, Currency.USD)));
    }

    [Fact]
    public void Subtract_DifferentCurrency_Throws()
    {
        Assert.Throws<CurrencyMismatchException>(() => new Money(100, Currency.USD).Subtract(new Money(50, Currency.GBP)));
    }

    [Fact]
    public void Subtract_ResultExceedsLongRange_ThrowsValidationFailed()
    {
        Assert.Throws<ValidationFailedException>(() => new Money(long.MinValue, Currency.USD).Subtract(new Money(1, Currency.USD)));
    }

    [Fact]
    public void IsLessThan_SameCurrency_ComparesAmounts()
    {
        Assert.True(new Money(99, Currency.USD).IsLessThan(new Money(100, Currency.USD)));
        Assert.False(new Money(100, Currency.USD).IsLessThan(new Money(100, Currency.USD)));
    }

    [Fact]
    public void IsLessThan_DifferentCurrency_Throws()
    {
        Assert.Throws<CurrencyMismatchException>(() => new Money(1, Currency.USD).IsLessThan(new Money(2, Currency.EUR)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EnsurePositive_ZeroOrNegative_Throws(long amount)
    {
        Assert.Throws<ValidationFailedException>(() => new Money(amount, Currency.USD).EnsurePositive());
    }

    [Fact]
    public void EnsurePositive_Positive_DoesNotThrow()
    {
        new Money(1, Currency.USD).EnsurePositive();
    }
}
