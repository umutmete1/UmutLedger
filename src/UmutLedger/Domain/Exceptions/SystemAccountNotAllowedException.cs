namespace UmutLedger.Domain.Exceptions;

public sealed class SystemAccountNotAllowedException(Guid accountId)
    : LedgerException(ErrorKind.RuleViolation, ErrorCodes.SystemAccountNotAllowed, $"Account {accountId} is a system account and cannot be used in this request.");
