using Microsoft.EntityFrameworkCore;
using Npgsql;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Tests.Infrastructure;

public class IdempotencyKeyConflictTests
{
    private static DbUpdateException UpdateFailure(string sqlState, string constraintName) =>
        new("Save failed.", new PostgresException("failure", "ERROR", "ERROR", sqlState, constraintName: constraintName));

    [Fact]
    public void Matches_UniqueViolationOnTheIdempotencyKeyIndex_IsTrue()
    {
        Assert.True(IdempotencyKeyConflict.Matches(UpdateFailure(PostgresErrorCodes.UniqueViolation, LedgerConstraints.TransferIdempotencyKey)));
    }

    [Fact]
    public void Matches_UniqueViolationOnAnotherIndex_IsFalse()
    {
        Assert.False(IdempotencyKeyConflict.Matches(UpdateFailure(PostgresErrorCodes.UniqueViolation, "pk_transfers")));
    }

    [Fact]
    public void Matches_AnotherErrorOnTheIdempotencyKeyIndex_IsFalse()
    {
        Assert.False(IdempotencyKeyConflict.Matches(UpdateFailure(PostgresErrorCodes.CheckViolation, LedgerConstraints.TransferIdempotencyKey)));
    }

    [Fact]
    public void Matches_FailureNotFromPostgres_IsFalse()
    {
        Assert.False(IdempotencyKeyConflict.Matches(new DbUpdateException("Save failed.", new InvalidOperationException())));
    }
}
