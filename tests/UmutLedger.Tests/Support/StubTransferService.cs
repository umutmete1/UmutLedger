using UmutLedger.Application.Transfers;
using UmutLedger.Domain.Entities;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Tests.Support;

// Stands in for the inner transfer service where a test needs an outcome the real one cannot be made to produce on demand.
public sealed class StubTransferService(Func<Task<TransferOutcome>> result) : ITransferService
{
    public static StubTransferService Returning(TransferOutcome outcome) => new(() => Task.FromResult(outcome));

    public static StubTransferService Throwing(Exception exception) => new(() => Task.FromException<TransferOutcome>(exception));

    public Task<TransferOutcome> TransferAsync(
        IdempotencyKey key, Guid debitAccountId, Guid creditAccountId, Money amount, CancellationToken cancellationToken) => result();

    public Task<TransferOutcome> DepositAsync(IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken) => result();

    public Task<TransferOutcome> WithdrawAsync(IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken) => result();

    public Task<Transfer> GetAsync(Guid transferId, CancellationToken cancellationToken) => throw new NotSupportedException();
}
