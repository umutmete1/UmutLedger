using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Domain.Entities;

public sealed class Entry
{
    public Entry(Guid id, Guid transferId, Guid accountId, long amount, DateTimeOffset createdAt)
    {
        if (amount == 0)
        {
            throw new ValidationFailedException("Entry amount must not be zero.");
        }

        Id = Require.NotEmpty(id, "Entry id");
        TransferId = Require.NotEmpty(transferId, "Entry transfer id");
        AccountId = Require.NotEmpty(accountId, "Entry account id");
        Amount = amount;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid TransferId { get; }

    public Guid AccountId { get; }

    public long Amount { get; }

    public DateTimeOffset CreatedAt { get; }
}
