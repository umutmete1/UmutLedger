using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;

namespace UmutLedger.Api.Models;

public sealed record TransferResponse(
    Guid Id,
    string IdempotencyKey,
    Guid DebitAccountId,
    Guid CreditAccountId,
    long Amount,
    Currency Currency,
    DateTimeOffset CreatedAt,
    IReadOnlyList<EntryResponse> Entries)
{
    public static TransferResponse From(Transfer transfer) =>
        new(
            transfer.Id,
            transfer.IdempotencyKey.Value,
            transfer.DebitAccountId,
            transfer.CreditAccountId,
            transfer.Amount,
            transfer.Currency,
            transfer.CreatedAt,
            transfer.Entries.OrderBy(entry => entry.Amount).Select(EntryResponse.From).ToList());
}
