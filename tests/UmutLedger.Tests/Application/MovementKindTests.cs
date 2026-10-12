using UmutLedger.Application.Transfers;

namespace UmutLedger.Tests.Application;

public class MovementKindTests
{
    [Theory]
    [InlineData(MovementKind.Deposit, "deposit")]
    [InlineData(MovementKind.Withdrawal, "withdrawal")]
    [InlineData(MovementKind.Transfer, "transfer")]
    public void ToLabel_IsTheLowerCaseName(MovementKind kind, string expected) =>
        Assert.Equal(expected, kind.ToLabel());
}
