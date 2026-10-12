namespace UmutLedger.Domain.Exceptions;

public sealed class AccountNotFoundException(Guid accountId)
    : LedgerException(ErrorKind.NotFound, ErrorCodes.AccountNotFound, $"Account {accountId} does not exist.");
