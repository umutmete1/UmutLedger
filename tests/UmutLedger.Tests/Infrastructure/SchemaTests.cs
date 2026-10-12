using Microsoft.EntityFrameworkCore;
using Npgsql;
using UmutLedger.Domain.Enums;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Infrastructure;

public class SchemaTests
{
    private const string AppendOnlyViolation = "23001";

    [Fact]
    public async Task Migrations_CreateOneSystemAccountPerCurrency()
    {
        await using var db = await TestDatabase.CreateContextAsync();

        var systemCurrencies = await db.Accounts
            .Where(account => account.Type == AccountType.System)
            .Select(account => account.Currency)
            .ToListAsync();

        Assert.Equal(Enum.GetValues<Currency>().Order(), systemCurrencies.Order());
    }

    [Fact]
    public async Task Accounts_SecondSystemAccountForACurrency_IsRefused()
    {
        await using var db = await TestDatabase.CreateContextAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO accounts (id, name, currency, type, balance, created_at) VALUES ({Guid.CreateVersion7()}, 'Second world', 'USD', 'System', 0, now())"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Theory]
    [InlineData("'Overdrawn', 'USD', 'Customer', -1")]
    [InlineData("'   ', 'USD', 'Customer', 0")]
    [InlineData("'Yen account', 'JPY', 'Customer', 0")]
    [InlineData("'Odd type', 'USD', 'Other', 0")]
    public async Task Accounts_InvalidRow_IsRefused(string nameCurrencyTypeBalance)
    {
        await using var db = await TestDatabase.CreateContextAsync();
        var sql = $"INSERT INTO accounts (id, name, currency, type, balance, created_at) VALUES ('{Guid.CreateVersion7()}', {nameCurrencyTypeBalance}, now())";

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Transfers_ZeroAmount_IsRefused()
    {
        var debitAccountId = await TestLedger.CreateCustomerAsync();
        var creditAccountId = await TestLedger.CreateCustomerAsync();
        await using var db = await TestDatabase.CreateContextAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO transfers (id, idempotency_key, debit_account_id, credit_account_id, amount, currency, created_at) VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid().ToString()}, {debitAccountId}, {creditAccountId}, 0, 'USD', now())"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Transfers_SameAccountOnBothSides_IsRefused()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        await using var db = await TestDatabase.CreateContextAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO transfers (id, idempotency_key, debit_account_id, credit_account_id, amount, currency, created_at) VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid().ToString()}, {accountId}, {accountId}, 10, 'USD', now())"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task Transfers_DuplicateIdempotencyKey_IsRefused()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var existing = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);
        await using var db = await TestDatabase.CreateContextAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO transfers (id, idempotency_key, debit_account_id, credit_account_id, amount, currency, created_at) VALUES ({Guid.CreateVersion7()}, {existing.IdempotencyKey.Value}, {existing.DebitAccountId}, {existing.CreditAccountId}, 10, 'USD', now())"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal("ux_transfers_idempotency_key", exception.ConstraintName);
    }

    [Fact]
    public async Task Entries_ZeroAmount_IsRefused()
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var transfer = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);
        await using var db = await TestDatabase.CreateContextAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO entries (id, transfer_id, account_id, amount, created_at) VALUES ({Guid.CreateVersion7()}, {transfer.Id}, {accountId}, 0, now())"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Theory]
    [InlineData("UPDATE transfers SET amount = amount + 1 WHERE id = {0}")]
    [InlineData("DELETE FROM transfers WHERE id = {0}")]
    [InlineData("UPDATE entries SET amount = amount + 1 WHERE transfer_id = {0}")]
    [InlineData("DELETE FROM entries WHERE transfer_id = {0}")]
    public async Task TransfersAndEntries_UpdateOrDelete_IsRefused(string sql)
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var transfer = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);
        await using var db = await TestDatabase.CreateContextAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, transfer.Id));

        Assert.Equal(AppendOnlyViolation, exception.SqlState);
        await LedgerInvariants.AssertHoldAsync();
    }
}
