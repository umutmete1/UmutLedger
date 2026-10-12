namespace UmutLedger.Domain.Exceptions;

public sealed class SameAccountTransferException()
    : LedgerException(ErrorKind.InvalidRequest, ErrorCodes.SameAccountTransfer, "The debit and credit accounts must be different.");
