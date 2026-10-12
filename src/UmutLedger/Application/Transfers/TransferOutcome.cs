using UmutLedger.Domain.Entities;

namespace UmutLedger.Application.Transfers;

public sealed class TransferOutcome
{
    public TransferOutcome(Transfer transfer, bool wasReplayed)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        Transfer = transfer;
        WasReplayed = wasReplayed;
    }

    public Transfer Transfer { get; }

    public bool WasReplayed { get; }
}
