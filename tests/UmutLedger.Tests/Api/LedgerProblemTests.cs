using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UmutLedger.Api.Errors;

namespace UmutLedger.Tests.Api;

public class LedgerProblemTests
{
    private const string RequestIdentifier = "request-1";

    private static ProblemDetails CreateProblem() => LedgerProblem.Create(
        new DefaultHttpContext { TraceIdentifier = RequestIdentifier },
        StatusCodes.Status400BadRequest,
        "validation_failed",
        "detail");

    [Fact]
    public void WithAnActiveTrace_TraceIdIsThatTracesId()
    {
        using var span = new Activity("request").Start();

        Assert.Equal(span.TraceId.ToString(), CreateProblem().Extensions["traceId"]);
    }

    [Fact]
    public void WithoutAnActiveTrace_TraceIdIsTheRequestIdentifier()
    {
        Activity.Current = null;

        Assert.Equal(RequestIdentifier, CreateProblem().Extensions["traceId"]);
    }
}
