namespace UmutLedger.Infrastructure.Persistence;

public static class LedgerConstraints
{
    public const string ConnectionStringName = "Ledger";
    public const string TransferIdempotencyKey = "ux_transfers_idempotency_key";
}
