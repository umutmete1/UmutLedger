using UmutLedger.Domain.Enums;

namespace UmutLedger.Api.Models;

public sealed class MoneyRequest
{
    public required long Amount { get; init; }

    public required Currency Currency { get; init; }
}
