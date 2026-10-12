using Microsoft.EntityFrameworkCore;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Tests.Support;

public static class LedgerInvariants
{
    private const string CurrenciesWhoseEntriesDoNotSumToZero = """
        SELECT count(*)::int AS "Value" FROM (
            SELECT a.currency
            FROM entries e JOIN accounts a ON a.id = e.account_id
            GROUP BY a.currency
            HAVING sum(e.amount) <> 0) unbalanced
        """;

    private const string AccountsWhoseBalanceDiffersFromTheirEntries = """
        SELECT count(*)::int AS "Value"
        FROM accounts a
        WHERE a.balance <> (SELECT coalesce(sum(e.amount), 0) FROM entries e WHERE e.account_id = a.id)
        """;

    private const string TransfersWithoutTwoBalancedEntries = """
        SELECT count(*)::int AS "Value"
        FROM transfers t
        WHERE (SELECT count(*) FROM entries e WHERE e.transfer_id = t.id) <> 2
           OR (SELECT coalesce(sum(e.amount), 0) FROM entries e WHERE e.transfer_id = t.id) <> 0
        """;

    public static async Task AssertHoldAsync()
    {
        await using var db = await TestDatabase.CreateContextAsync();
        Assert.Equal(0, await CountAsync(db, CurrenciesWhoseEntriesDoNotSumToZero));
        Assert.Equal(0, await CountAsync(db, AccountsWhoseBalanceDiffersFromTheirEntries));
        Assert.Equal(0, await CountAsync(db, TransfersWithoutTwoBalancedEntries));
    }

    private static Task<int> CountAsync(LedgerDbContext db, string sql) =>
        db.Database.SqlQueryRaw<int>(sql).SingleAsync();
}
