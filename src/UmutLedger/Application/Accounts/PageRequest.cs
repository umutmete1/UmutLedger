using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Application.Accounts;

public sealed class PageRequest
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    private const int MinLimit = 1;

    public PageRequest(int? limit, Guid? cursor)
    {
        Limit = limit ?? DefaultLimit;
        if (Limit is < MinLimit or > MaxLimit)
        {
            throw new ValidationFailedException($"limit must be between {MinLimit} and {MaxLimit}.");
        }

        Cursor = cursor;
    }

    public int Limit { get; }

    public Guid? Cursor { get; }
}
