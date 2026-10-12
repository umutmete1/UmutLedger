namespace UmutLedger.Domain.Exceptions;

public sealed class TransferNotFoundException(Guid transferId)
    : LedgerException(ErrorKind.NotFound, ErrorCodes.TransferNotFound, $"Transfer {transferId} does not exist.");
