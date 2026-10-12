using Microsoft.AspNetCore.Diagnostics;
using UmutLedger.Domain.Exceptions;
using UmutLedger.Infrastructure.Persistence;

namespace UmutLedger.Api.Errors;

public sealed class LedgerExceptionHandler(ILogger<LedgerExceptionHandler> logger) : IExceptionHandler
{
    private const string UnavailableDetail = "The service is temporarily unavailable. Retry the request with the same idempotency key.";
    private const string UnexpectedDetail = "An unexpected error occurred.";

    private static readonly IReadOnlyDictionary<ErrorKind, int> StatusByKind = new Dictionary<ErrorKind, int>
    {
        [ErrorKind.InvalidRequest] = StatusCodes.Status400BadRequest,
        [ErrorKind.NotFound] = StatusCodes.Status404NotFound,
        [ErrorKind.RuleViolation] = StatusCodes.Status422UnprocessableEntity
    };

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, detail) = Describe(exception);
        await LedgerProblem.WriteAsync(httpContext, status, code, detail, cancellationToken);
        return true;
    }

    private (int Status, string Code, string Detail) Describe(Exception exception)
    {
        if (exception is LedgerException ledgerException)
        {
            return (StatusByKind[ledgerException.Kind], ledgerException.Code, ledgerException.Message);
        }

        if (DatabaseFailure.IsUnavailable(exception))
        {
            logger.LogError(exception, "The database is unavailable.");
            return (StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServiceUnavailable, UnavailableDetail);
        }

        logger.LogError(exception, "An unexpected error occurred.");
        return (StatusCodes.Status500InternalServerError, ErrorCodes.InternalError, UnexpectedDetail);
    }
}
