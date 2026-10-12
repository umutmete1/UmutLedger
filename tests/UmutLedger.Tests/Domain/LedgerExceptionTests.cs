using UmutLedger.Domain.Enums;
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Tests.Domain;

public class LedgerExceptionTests
{
    private static readonly (LedgerException Exception, ErrorKind Kind, string Code)[] Cases =
    [
        (new ValidationFailedException("bad"), ErrorKind.InvalidRequest, "validation_failed"),
        (new IdempotencyKeyRequiredException(), ErrorKind.InvalidRequest, "idempotency_key_required"),
        (new SameAccountTransferException(), ErrorKind.InvalidRequest, "same_account_transfer"),
        (new AccountNotFoundException(Guid.NewGuid()), ErrorKind.NotFound, "account_not_found"),
        (new TransferNotFoundException(Guid.NewGuid()), ErrorKind.NotFound, "transfer_not_found"),
        (new CurrencyMismatchException(Currency.USD, Currency.EUR), ErrorKind.RuleViolation, "currency_mismatch"),
        (new InsufficientFundsException(Guid.NewGuid()), ErrorKind.RuleViolation, "insufficient_funds"),
        (new SystemAccountNotAllowedException(Guid.NewGuid()), ErrorKind.RuleViolation, "system_account_not_allowed"),
        (new IdempotencyKeyReusedException(), ErrorKind.RuleViolation, "idempotency_key_reused"),
    ];

    [Fact]
    public void EveryException_CarriesItsKindCodeAndMessage()
    {
        Assert.All(Cases, testCase =>
        {
            Assert.Equal(testCase.Kind, testCase.Exception.Kind);
            Assert.Equal(testCase.Code, testCase.Exception.Code);
            Assert.False(string.IsNullOrWhiteSpace(testCase.Exception.Message));
        });
    }

    [Fact]
    public void ErrorCodes_InfrastructureCodes_AreStable()
    {
        Assert.Equal("service_unavailable", ErrorCodes.ServiceUnavailable);
        Assert.Equal("internal_error", ErrorCodes.InternalError);
    }
}
