using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Domain.ValueObjects;

public sealed record IdempotencyKey
{
    public const int MaxLength = 255;

    public IdempotencyKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new IdempotencyKeyRequiredException();
        }

        if (value.Length > MaxLength)
        {
            throw new ValidationFailedException($"The idempotency key must be at most {MaxLength} characters.");
        }

        Value = value;
    }

    public string Value { get; }
}
