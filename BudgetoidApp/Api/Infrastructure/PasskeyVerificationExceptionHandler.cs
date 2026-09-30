using Application.Passkeys;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Api.Infrastructure;

/// <summary>
/// Turns every refused sign-in into one response, byte for byte.
/// </summary>
/// <remarks>
/// The title is fixed and there is no detail and no hint of which check refused — unknown credential,
/// bad signature, untrusted origin, spent or expired challenge, counter regression, user-handle mismatch
/// all leave here identical. Anything that varied would be a credential-enumeration oracle: a caller
/// able to tell "no such credential" from "wrong signature" can discover which handles are registered
/// without ever holding one.
/// <para>
/// <b>One extension, and it is a constant.</b> <c>refusal: "assertion"</c> says which <em>proof</em>
/// failed, never why — on a route that also takes the identity provider's token, a client has to tell
/// "retry the passkey" from "sign in to Google again" (see <see cref="ProviderAuthorizationGate" />).
/// Written here, once, for every passkey refusal in the product, so every one of them stays the same
/// bytes as every other.
/// </para>
/// <para>
/// Registered before <see cref="GlobalExceptionHandler"/>, because the catch-all would otherwise turn
/// a refusal into a 500 and log it as a fault. Registration failures do not come through here — they
/// happen on an authenticated request and surface as an ordinary 400 or 409 with a real sentence.
/// </para>
/// </remarks>
public sealed partial class PasskeyVerificationExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<PasskeyVerificationExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>
    /// The one sentence a refused sign-in produces. Public so a test can pin it: the moment this
    /// varies by cause, the endpoint starts answering questions it must not.
    /// </summary>
    public const string Title = "The passkey could not be verified.";

    /// <summary>
    /// The <c>refusal</c> word every passkey refusal carries. Fixed for the same reason as
    /// <see cref="Title" />: it names the proof, not the check.
    /// </summary>
    public const string Refusal = "assertion";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not PasskeyVerificationException verificationException)
        {
            return false;
        }

        // The only place the reason exists. It is a warning rather than an error because a refused
        // sign-in is an expected outcome of a hostile or simply mistaken request, not a fault.
        Log.PasskeyVerificationRefused(logger, verificationException.Reason);

        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;

        ProblemDetails problemDetails = new()
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = Title,
            Extensions = { [RefusalMember.Name] = Refusal },
        };

        // The exception is deliberately not passed on the context: the development branch of the
        // ProblemDetails pipeline would attach its message, and this response has to be the same in
        // every environment — a shape only reproducible in production is a shape nobody tests.
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
            Message = "A passkey assertion was refused: {Reason}")]
        public static partial void PasskeyVerificationRefused(ILogger logger, string reason);
    }
}
