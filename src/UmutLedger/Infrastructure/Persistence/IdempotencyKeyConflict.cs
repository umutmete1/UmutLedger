using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace UmutLedger.Infrastructure.Persistence;

public static class IdempotencyKeyConflict
{
    public static bool Matches(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: LedgerConstraints.TransferIdempotencyKey
        };
}
