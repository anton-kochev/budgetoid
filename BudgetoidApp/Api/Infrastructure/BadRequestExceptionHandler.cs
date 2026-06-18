using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Api.Infrastructure;

// Malformed request bodies (e.g. invalid JSON) make minimal-API model binding throw
// BadHttpRequestException, which already carries the right status code (400). Without this,
// the catch-all GlobalExceptionHandler would turn transport-level client errors into 500s.
public sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequestException)
        {
            return false;
        }

        httpContext.Response.StatusCode = badRequestException.StatusCode;

        ProblemDetails problemDetails = new()
        {
            Status = badRequestException.StatusCode,
            Title = "The request could not be read."
        };

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        });
    }
}
