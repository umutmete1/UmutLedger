using UmutLedger.Domain.Enums;

namespace UmutLedger.Api.Models;

public sealed class CreateAccountRequest
{
    public required string Name { get; init; }

    public required Currency Currency { get; init; }
}
