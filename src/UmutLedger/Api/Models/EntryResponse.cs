using UmutLedger.Domain.Entities;

namespace UmutLedger.Api.Models;

public sealed record EntryResponse(Guid Id, Guid TransferId, Guid AccountId, long Amount, DateTimeOffset CreatedAt)
{
    public static EntryResponse From(Entry entry) =>
        new(entry.Id, entry.TransferId, entry.AccountId, entry.Amount, entry.CreatedAt);
}
