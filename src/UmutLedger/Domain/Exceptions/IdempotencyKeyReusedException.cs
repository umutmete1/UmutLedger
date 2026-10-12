namespace UmutLedger.Domain.Exceptions;

public sealed class IdempotencyKeyReusedException()
    : LedgerException(ErrorKind.RuleViolation, ErrorCodes.IdempotencyKeyReused, "The idempotency key was already used for a different request.");
