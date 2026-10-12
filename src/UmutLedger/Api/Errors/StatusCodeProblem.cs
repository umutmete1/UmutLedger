using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.WebUtilities;

namespace UmutLedger.Api.Errors;

public static class StatusCodeProblem
{
    private const char ReasonPhraseSeparator = ' ';
    private const char CodeSeparator = '_';

    public static Task WriteAsync(StatusCodeContext context)
    {
        var httpContext = context.HttpContext;
        var status = httpContext.Response.StatusCode;
        var reasonPhrase = ReasonPhrases.GetReasonPhrase(status);
        var code = reasonPhrase.ToLowerInvariant().Replace(ReasonPhraseSeparator, CodeSeparator);

        return LedgerProblem.WriteAsync(
            httpContext, status, code, $"The request could not be processed: {reasonPhrase}.", httpContext.RequestAborted);
    }
}
