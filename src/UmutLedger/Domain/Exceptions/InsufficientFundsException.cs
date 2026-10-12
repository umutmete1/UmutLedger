namespace UmutLedger.Domain.Exceptions;

public sealed class InsufficientFundsException(Guid accountId)
    : LedgerException(ErrorKind.RuleViolation, ErrorCodes.InsufficientFunds, $"Account {accountId} does not have enough funds.");
