using Microsoft.EntityFrameworkCore;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Application.Accounts;

public sealed class AccountService(LedgerDbContext db, TimeProvider timeProvider) : IAccountService
{
    private const int LookAheadForNextPage = 1;

    public async Task<Account> CreateAsync(string name, Currency currency, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var account = new Account(Guid.CreateVersion7(now), name, currency, AccountType.Customer, now);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return account;
    }

    public async Task<Account> GetAsync(Guid accountId, CancellationToken cancellationToken) =>
        await db.Accounts.AsNoTracking().SingleOrDefaultAsync(account => account.Id == accountId, cancellationToken)
        ?? throw new AccountNotFoundException(accountId);

    public async Task<EntryPage> GetEntriesAsync(Guid accountId, PageRequest page, CancellationToken cancellationToken)
    {
        if (!await db.Accounts.AnyAsync(account => account.Id == accountId, cancellationToken))
        {
            throw new AccountNotFoundException(accountId);
        }

        var fetched = await EntriesOlderThan(page.Cursor)
            .AsNoTracking()
            .Where(entry => entry.AccountId == accountId)
            .OrderByDescending(entry => entry.Id)
            .Take(page.Limit + LookAheadForNextPage)
            .ToListAsync(cancellationToken);
        return EntryPage.From(fetched, page.Limit);
    }

    // Entry ids are time-ordered UUIDs, compared by PostgreSQL so the cursor follows the same order as the sort.
    private IQueryable<Entry> EntriesOlderThan(Guid? cursor) =>
        cursor is null
            ? db.Entries
            : db.Entries.FromSql($"SELECT * FROM entries WHERE id < {cursor.Value}");
}
