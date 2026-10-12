using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Tests.Infrastructure;

public class DatabaseFailureTests
{
    [Fact]
    public void IsUnavailable_ConnectionFailure_IsTrue()
    {
        Assert.True(DatabaseFailure.IsUnavailable(new NpgsqlException("Failed to connect.", new SocketException())));
    }

    [Fact]
    public void IsUnavailable_Timeout_IsTrue()
    {
        Assert.True(DatabaseFailure.IsUnavailable(new TimeoutException()));
    }

    [Fact]
    public void IsUnavailable_ConnectionFailureWrappedByAnotherException_IsTrue()
    {
        var connectionFailure = new NpgsqlException("Failed to connect.", new IOException());

        Assert.True(DatabaseFailure.IsUnavailable(new InvalidOperationException("Query failed.", connectionFailure)));
    }

    [Fact]
    public void IsUnavailable_ConstraintViolation_IsFalse()
    {
        var violation = new PostgresException("check violated", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation);

        Assert.False(DatabaseFailure.IsUnavailable(new DbUpdateException("Save failed.", violation)));
    }

    [Fact]
    public void IsUnavailable_UnrelatedException_IsFalse()
    {
        Assert.False(DatabaseFailure.IsUnavailable(new InvalidOperationException()));
    }
}
