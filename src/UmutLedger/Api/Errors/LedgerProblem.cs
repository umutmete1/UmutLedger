using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace UmutLedger.Api.Errors;

public static class LedgerProblem
{
    public const string ContentType = "application/problem+json";
    private const string CodeExtension = "code";
    private const string TraceIdExtension = "traceId";

    public static ProblemDetails Create(HttpContext httpContext, int status, string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = ReasonPhrases.GetReasonPhrase(status),
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        problem.Extensions[CodeExtension] = code;
        problem.Extensions[TraceIdExtension] = TraceIdOf(httpContext);
        return problem;
    }

    public static Task WriteAsync(HttpContext httpContext, int status, string code, string detail, CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = status;
        return httpContext.Response.WriteAsJsonAsync(
            Create(httpContext, status, code, detail), options: null, contentType: ContentType, cancellationToken);
    }

    private static string TraceIdOf(HttpContext httpContext) =>
        Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
}
