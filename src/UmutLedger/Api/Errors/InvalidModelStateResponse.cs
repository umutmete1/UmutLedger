using Microsoft.AspNetCore.Mvc;
using UmutLedger.Domain.Exceptions;

namespace UmutLedger.Api.Errors;

public static class InvalidModelStateResponse
{
    private const string MessageSeparator = " ";
    private const string Summary = "The request is not valid.";

    public static IActionResult Create(ActionContext context)
    {
        var sentences = context.ModelState.Values
            .SelectMany(entry => entry.Errors)
            .Select(error => error.ErrorMessage)
            .Prepend(Summary);
        var problem = LedgerProblem.Create(
            context.HttpContext,
            StatusCodes.Status400BadRequest,
            ErrorCodes.ValidationFailed,
            string.Join(MessageSeparator, sentences));

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { LedgerProblem.ContentType }
        };
    }
}
