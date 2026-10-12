using Npgsql;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Tests.Infrastructure;

public class LedgerConnectionPoolTests
{
    private const string WithoutPoolSize = "Host=db.example;Port=5432;Database=ledger;Username=ledger;Password=secret";

    private static NpgsqlConnectionStringBuilder Limited(string? connectionString) =>
        new(LedgerConnectionPool.ApplyDefaultLimit(connectionString));

    [Fact]
    public void ApplyDefaultLimit_WhenNoPoolSizeIsSet_LimitsThePoolToTheDefault() =>
        Assert.Equal(LedgerConnectionPool.DefaultMaximumSize, Limited(WithoutPoolSize).MaxPoolSize);

    [Fact]
    public void DefaultMaximumSize_IsHalfOfPostgresDefaultConnectionLimit() =>
        Assert.Equal(50, LedgerConnectionPool.DefaultMaximumSize);

    [Theory]
    [InlineData(20)]
    [InlineData(200)]
    public void ApplyDefaultLimit_WhenAPoolSizeIsSet_KeepsIt(int configuredSize) =>
        Assert.Equal(configuredSize, Limited($"{WithoutPoolSize};Maximum Pool Size={configuredSize}").MaxPoolSize);

    [Theory]
    [InlineData("MaxPoolSize=20")]
    [InlineData("maximum pool size=20")]
    public void ApplyDefaultLimit_WhenAPoolSizeIsSetUnderAnotherSpelling_KeepsIt(string poolSetting) =>
        Assert.Equal(20, Limited($"{WithoutPoolSize};{poolSetting}").MaxPoolSize);

    [Fact]
    public void ApplyDefaultLimit_KeepsEveryOtherSetting()
    {
        var limited = Limited(WithoutPoolSize);

        Assert.Equal("db.example", limited.Host);
        Assert.Equal(5432, limited.Port);
        Assert.Equal("ledger", limited.Database);
        Assert.Equal("ledger", limited.Username);
        Assert.Equal("secret", limited.Password);
    }

    [Fact]
    public void ApplyDefaultLimit_WithoutAConnectionString_StillLimitsThePool() =>
        Assert.Equal(LedgerConnectionPool.DefaultMaximumSize, Limited(null).MaxPoolSize);
}
