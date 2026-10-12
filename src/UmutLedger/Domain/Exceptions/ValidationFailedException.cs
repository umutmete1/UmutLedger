namespace UmutLedger.Domain.Exceptions;

public sealed class ValidationFailedException(string message)
    : LedgerException(ErrorKind.InvalidRequest, ErrorCodes.ValidationFailed, message);
