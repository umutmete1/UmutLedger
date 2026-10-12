using UmutLedger.Domain.Exceptions;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Domain;

public class IdempotencyKeyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_MissingValue_ThrowsKeyRequired(string? value)
    {
        Assert.Throws<IdempotencyKeyRequiredException>(() => new IdempotencyKey(value));
    }

    [Fact]
    public void Constructor_ValueAtMaxLength_IsAccepted()
    {
        var value = new string('k', IdempotencyKey.MaxLength);

        Assert.Equal(value, new IdempotencyKey(value).Value);
    }

    [Fact]
    public void Constructor_ValueOverMaxLength_ThrowsValidationFailed()
    {
        Assert.Throws<ValidationFailedException>(() => new IdempotencyKey(new string('k', IdempotencyKey.MaxLength + 1)));
    }

    [Fact]
    public void Equals_SameValue_IsTrue()
    {
        Assert.Equal(new IdempotencyKey("abc"), new IdempotencyKey("abc"));
    }
}
