using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Application.Transfers;

public static class MovementOutcomes
{
    public const string Created = "created";
    public const string Replayed = "replayed";
    public const string Cancelled = "cancelled";
    public const string Error = "error";

    public static string Of(TransferOutcome outcome) => outcome.WasReplayed ? Replayed : Created;

    public static string Of(Exception exception) => exception switch
    {
        LedgerException ledgerException => ledgerException.Code,
        OperationCanceledException => Cancelled,
        _ => Error
    };
}
