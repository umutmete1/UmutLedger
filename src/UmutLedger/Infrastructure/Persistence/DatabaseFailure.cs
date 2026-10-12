using Npgsql;

namespace UmutLedger.Infrastructure.Persistence;

public static class DatabaseFailure
{
    public static bool IsUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or NpgsqlException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }
}
