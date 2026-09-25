using Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Api.Infrastructure;

/// <summary>
/// Renders every <see cref="ConflictException"/> in the product as one 409 problem document.
/// </summary>
/// <remarks>
/// <para>
/// <b>The status and the title are fixed for every conflict, and the extension member is the only thing
/// that varies with the rule.</b> Two conflicts whose remedies are opposites — adopt the row that already
/// exists, and mint a fresh identifier — used to be told apart by nothing but prose written for a person,
/// so a client had the status and no more. <see cref="ConflictKindName"/> is what a client branches on;
/// <c>Detail</c> is still the whole of what a person is shown, and the two are not substitutes.
/// </para>
/// <para>
/// <b>It ADDS a member and replaces nothing.</b> No <c>Detail</c> moves and no <c>errors</c> map appears:
/// a conflict's remedy is not a correction to a field, which is the argument for the status in the first
/// place — see <c>PayeeRepository.AddAsync</c>, where the same index answers 400 on the rename leg
/// precisely because that remedy <em>is</em> one.
/// </para>
/// </remarks>
public sealed class ConflictExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    /// <summary>
    /// The member a conflict's kind is written to, spelled here rather than derived: extension keys are
    /// serialized verbatim, so no naming policy in <c>Program.cs</c> reaches this string and a client
    /// reads exactly what is written.
    /// </summary>
    internal const string ConflictKindName = "conflictKind";

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
            Detail = conflictException.Message,

            // Read off the exception rather than resolved here, so a kind with no spelling has already
            // failed at the site that raised it. See ConflictException's constructor.
            Extensions = { [ConflictKindName] = conflictException.Spelling },
        };

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        });
    }
}
