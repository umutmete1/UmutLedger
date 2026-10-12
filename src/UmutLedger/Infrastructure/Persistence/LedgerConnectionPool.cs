using Npgsql;

namespace UmutLedger.Infrastructure.Persistence;

public static class LedgerConnectionPool
{
    // Half of PostgreSQL's default max_connections (100): the driver's own default of 100 lets one
    // API instance ask for every connection the server has, and be refused once anything else is connected.
    public const int DefaultMaximumSize = 50;

    private const string MaximumPoolSizeKeyword = "Maximum Pool Size";

    public static string ApplyDefaultLimit(string? connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (!settings.Keys.Contains(MaximumPoolSizeKeyword))
        {
            settings.MaxPoolSize = DefaultMaximumSize;
        }

        return settings.ConnectionString;
    }
}
