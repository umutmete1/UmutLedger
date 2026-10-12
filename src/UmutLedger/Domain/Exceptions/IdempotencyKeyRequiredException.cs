namespace UmutLedger.Domain.Exceptions;

public sealed class IdempotencyKeyRequiredException()
    : LedgerException(ErrorKind.InvalidRequest, ErrorCodes.IdempotencyKeyRequired, "The Idempotency-Key header is required.");
