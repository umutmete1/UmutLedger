using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Tests.Support;

namespace UmutLedger.Tests.Application;

public class MovementOutcomesTests
{
    private static async Task<TransferOutcome> OutcomeAsync(bool wasReplayed)
    {
        var accountId = await TestLedger.CreateCustomerAsync();
        var transfer = await TestLedger.DepositAsync(accountId, TestLedger.Usd(100), TestLedger.BaseTime);
        return new TransferOutcome(transfer, wasReplayed);
    }

    [Fact]
    public async Task Of_ANewTransfer_IsCreated() =>
        Assert.Equal("created", MovementOutcomes.Of(await OutcomeAsync(wasReplayed: false)));

    [Fact]
    public async Task Of_AReplayedTransfer_IsReplayed() =>
        Assert.Equal("replayed", MovementOutcomes.Of(await OutcomeAsync(wasReplayed: true)));

    [Fact]
    public void Of_ALedgerException_IsItsErrorCode() =>
        Assert.Equal(ErrorCodes.InsufficientFunds, MovementOutcomes.Of(new InsufficientFundsException(Guid.CreateVersion7())));

    [Fact]
    public void Of_ACancellation_IsCancelled() =>
        Assert.Equal("cancelled", MovementOutcomes.Of(new TaskCanceledException()));

    [Fact]
    public void Of_AnyOtherException_IsError() =>
        Assert.Equal("error", MovementOutcomes.Of(new InvalidOperationException("boom")));
}
