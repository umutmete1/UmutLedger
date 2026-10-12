using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UmutLedger.Infrastructure.Persistence;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Api;

public class DatabaseConnectionApiTests(LedgerApi api) : IClassFixture<LedgerApi>
{
    // The test host's connection string sets no pool size, so this is the limit the application itself applies.
    [Fact]
    public void LedgerDatabase_UsesTheDefaultPoolLimit()
    {
        using var scope = api.Services.CreateScope();
        var connectionString = scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.GetConnectionString();

        Assert.Equal(LedgerConnectionPool.DefaultMaximumSize, new NpgsqlConnectionStringBuilder(connectionString).MaxPoolSize);
    }
}
