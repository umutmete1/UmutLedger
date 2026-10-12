namespace UmutLedger.Domain.Exceptions;

public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string IdempotencyKeyRequired = "idempotency_key_required";
    public const string SameAccountTransfer = "same_account_transfer";
    public const string AccountNotFound = "account_not_found";
    public const string TransferNotFound = "transfer_not_found";
    public const string CurrencyMismatch = "currency_mismatch";
    public const string InsufficientFunds = "insufficient_funds";
    public const string SystemAccountNotAllowed = "system_account_not_allowed";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string ServiceUnavailable = "service_unavailable";
    public const string InternalError = "internal_error";
}