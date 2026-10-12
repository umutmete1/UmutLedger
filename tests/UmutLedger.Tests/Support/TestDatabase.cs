using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Tests.Support;

public static class TestDatabase
{
    private const string PostgresImage = "postgres:17-alpine";

    private static readonly Lazy<Task<string>> MigratedDatabase = new(StartAndMigrateAsync);

    public static Task<string> GetConnectionStringAsync() => MigratedDatabase.Value;

    public static async Task<LedgerDbContext> CreateContextAsync() =>
        new(OptionsFor(await GetConnectionStringAsync()));

    private static async Task<string> StartAndMigrateAsync()
    {
        var container = new PostgreSqlBuilder(PostgresImage).Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();

        await using var db = new LedgerDbContext(OptionsFor(connectionString));
        await db.Database.MigrateAsync();
        return connectionString;
    }

    private static DbContextOptions<LedgerDbContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options;
}
