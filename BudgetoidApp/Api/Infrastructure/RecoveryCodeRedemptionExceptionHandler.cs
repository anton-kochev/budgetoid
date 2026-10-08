using Application.RecoveryCodes;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Api.Infrastructure;

/// <summary>
/// Turns every refused recovery-code redemption into one response, byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// The title is fixed and there is no detail, no extension, and no hint of which check refused — an
/// absent member, text that was not base64url, a verifier of the wrong width, one past the ceiling, one
/// no row answers to, one already spent, and one spent by a concurrent request all leave here
/// identical. Anything that varied would be an oracle over what is stored: a caller who can tell "no
/// such code" from "that code was already used" learns that a value they presented was once real, which
/// is exactly what somebody working through a partially-observed recovery card wants to know.
/// </para>
/// <para>
/// <b>Its own handler rather than a second registration of
/// <see cref="PasskeyVerificationExceptionHandler"/>, and the argument is the sentence.</b> "The
/// passkey could not be verified." on a recovery-code route is a <em>wrong</em> statement rather than
/// merely an uninformative one: it tells a person holding a card that the thing they do not have is the
/// thing that failed. A wrong sentence is worse than a distinguishable one.
/// </para>
/// <para>
/// <b>It writes its own problem document rather than letting the status code pick one.</b> ASP.NET Core
/// applies the fallback authorization policy to a request matching <em>no endpoint</em>, so an
/// anonymous POST to an unmapped path is also answered 401 — by <c>UseStatusCodePages</c>, with the
/// bare title the status code implies. A response indistinguishable from that would make "the route
/// refused you" and "the route does not exist" the same answer, and would let this endpoint pass its
/// own refusal tests while unmapped.
/// </para>
/// <para>
/// Registered before <see cref="GlobalExceptionHandler"/>, which would otherwise turn a refused
/// redemption into a 500 and log it as a fault. Handlers run in registration order and the first to
/// claim the exception wins.
/// </para>
/// </remarks>
public sealed partial class RecoveryCodeRedemptionExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<RecoveryCodeRedemptionExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>
    /// The one sentence a refused redemption produces. Public so a test can pin it: the moment this
    /// varies by cause, the endpoint starts answering questions it must not.
    /// </summary>
    /// <remarks>
    /// It says what happened and offers no reason, and it never mentions a collision: a refusal naming
    /// one would say that two stored values met, which is a fact about what is stored.
    /// </remarks>
    public const string Title = "The recovery code could not be redeemed.";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not RecoveryCodeRedemptionException redemptionException)
        {
            return false;
        }

        // The only place the reason exists, and it is a warning rather than an error because a refused
        // redemption is an expected outcome of a hostile or simply mistaken request, not a fault. The
        // reason names a check and never a verifier, a hash or an account: this route is reached by
        // people who cannot get in, so its log line is the one place a stored value could leak into a
        // sink with a different retention policy and a different audience.
        Log.RecoveryCodeRedemptionRefused(logger, redemptionException.Reason);

        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;

        ProblemDetails problemDetails = new()
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = Title,
        };

        // The exception is deliberately not passed on the context: the development branch of the
        // ProblemDetails pipeline would attach its message, and this response has to be the same in
        // every environment — a shape only reproducible in production is a shape nobody tests, and here
        // the message is the very distinction the response exists to withhold.
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        });
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Warning,
            Message = "A recovery code redemption was refused: {Reason}")]
        public static partial void RecoveryCodeRedemptionRefused(ILogger logger, string reason);
    }
}
