using Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Api.Infrastructure;

public sealed class ConflictExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ConflictException conflictException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        ProblemDetails problemDetails = new()
        {
            Status = StatusCodes.Status409Conflict,
            Title = "The request conflicts with the current state of the resource.",
            Detail = conflictException.Message
        };

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        });
    }
}
