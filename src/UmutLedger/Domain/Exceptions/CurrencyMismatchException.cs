using UmutLedger.Domain.Enums;

namespace UmutLedger.Domain.Exceptions;

public sealed class CurrencyMismatchException(Currency expected, Currency actual)
    : LedgerException(ErrorKind.RuleViolation, ErrorCodes.CurrencyMismatch, $"Expected currency {expected} but received {actual}.");
