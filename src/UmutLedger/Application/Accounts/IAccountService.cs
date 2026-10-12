using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;

namespace UmutLedger.Application.Accounts;

public interface IAccountService
{
    Task<Account> CreateAsync(string name, Currency currency, CancellationToken cancellationToken);

    Task<Account> GetAsync(Guid accountId, CancellationToken cancellationToken);

    Task<EntryPage> GetEntriesAsync(Guid accountId, PageRequest page, CancellationToken cancellationToken);
}
