using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;

namespace UmutLedger.Api.Models;

public sealed record AccountResponse(Guid Id, string Name, Currency Currency, AccountType Type, long Balance, DateTimeOffset CreatedAt)
{
    public static AccountResponse From(Account account) =>
        new(account.Id, account.Name, account.Currency, account.Type, account.Balance, account.CreatedAt);
}
