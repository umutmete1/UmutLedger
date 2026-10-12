using UmutLedger.Domain.Enums;

namespace UmutLedger.Api.Models;

public sealed class CreateTransferRequest
{
    public required Guid DebitAccountId { get; init; }

    public required Guid CreditAccountId { get; init; }

    public required long Amount { get; init; }

    public required Currency Currency { get; init; }
}
