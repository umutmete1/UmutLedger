using UmutLedger.Domain.Entities;
using UmutLedger.Domain.ValueObjects;

namespace UmutLedger.Application.Transfers;

public interface ITransferService
{
    Task<TransferOutcome> TransferAsync(IdempotencyKey key, Guid debitAccountId, Guid creditAccountId, Money amount, CancellationToken cancellationToken);

    Task<TransferOutcome> DepositAsync(IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken);

    Task<TransferOutcome> WithdrawAsync(IdempotencyKey key, Guid accountId, Money amount, CancellationToken cancellationToken);

    Task<Transfer> GetAsync(Guid transferId, CancellationToken cancellationToken);
}
