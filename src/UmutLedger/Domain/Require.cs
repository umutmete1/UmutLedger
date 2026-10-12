using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Domain;

public static class Require
{
    public static Guid NotEmpty(Guid value, string description)
    {
        if (value == Guid.Empty)
        {
            throw new ValidationFailedException($"{description} is required.");
        }

        return value;
    }

    public static T NotNull<T>(T? value, string description) where T : class
    {
        if (value is null)
        {
            throw new ValidationFailedException($"{description} is required.");
        }

        return value;
    }

    public static TEnum Defined<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ValidationFailedException($"{typeof(TEnum).Name} '{value}' is not supported.");
        }

        return value;
    }
}