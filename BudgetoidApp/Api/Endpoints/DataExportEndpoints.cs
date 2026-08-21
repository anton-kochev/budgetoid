using System.Globalization;
using Application.Users.ExportData;

namespace Api.Endpoints;

public static class DataExportEndpoints
{
    public static IEndpointRouteBuilder MapDataExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // A second group over "/api/me" rather than a member of the erasure one, which is the shape
        // PasskeyEndpoints already uses for one prefix and two groups. "/api/me" is the current
        // principal's namespace: this is a read of what that principal owns, and it hangs off the
        // principal for the same reason the erasure does.
        RouteGroupBuilder group = endpoints.MapGroup("/api/me");

        // AN EXPORT IS A READ AND IT MINTS NOTHING, which is now structural rather than declared.
        // RegisterAccountHandler is the only code in the application that brings an account into
        // existence — the only caller of the only factory the domain offers — and it is reachable only
        // from POST /api/registration/registration, behind the provider scheme, a verified passkey
        // attestation and a challenge drawn from the AccountRegistration pool. No metadata added here
        // could put minting back.
        //
        // The reason is worth keeping beside it: a route that minted to answer an export would hand a
        // provider token outliving an erasure by up to an hour a way to bring the account back as an
        // empty shell.
        //
        // No RequireAuthorization either: the application's fallback policy already covers every route
        // that declares nothing, and restating it here would make the one line that defines the
        // anonymous surface stop being the only one. And never AllowAnonymous.
        group.MapGet("/export", async (
            ExportDataHandler handler,
            HttpContext httpContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            ExportDocument document = await handler.HandleAsync(
                new ExportDataQuery(),
                cancellationToken);

            // The typed property rather than Headers.Append: it replaces, so no path through this
            // endpoint can leave two dispositions on one response — a client reading the first would
            // save a file nothing here named. Set before returning, which is safe because the lambda
            // returns before the result executes and writes the body.
            httpContext.Response.Headers.ContentDisposition =
                DispositionFor(timeProvider.GetUtcNow());

            // TypedResults.Ok rather than a file result or hand-serialized JSON: those write through
            // whatever JsonSerializerOptions the call site passes, bypassing ConfigureHttpJsonOptions
            // — so camelCase and the string enum converter would come from somewhere other than the
            // rest of the API, and the exported document would name its columns differently from every
            // response the same client already parses.
            return TypedResults.Ok(document);
        });

        return endpoints;
    }

    /// <summary>
    /// The <c>Content-Disposition</c> naming the export after the instant it was served, in UTC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It lives here rather than in the Application layer because a <c>Content-Disposition</c> is an
    /// HTTP header and nothing below the API knows what one is.
    /// </para>
    /// <para>
    /// <see cref="CultureInfo.InvariantCulture" /> is load-bearing, not decoration. A serving thread
    /// whose culture carries a non-Gregorian calendar — <c>th-TH</c> renders 2026 as 2569 — turns the
    /// same format string into a filename 543 years wrong that still looks like a timestamp. The
    /// instant is projected to UTC before formatting for the matching reason: a formatter reading a
    /// local offset lands in the wrong day, and at the turn of a year in the wrong year.
    /// </para>
    /// <para>
    /// No RFC 5987 <c>filename*</c> form: the name is ASCII by construction, so a second encoding of
    /// it would be surface nothing exercises.
    /// </para>
    /// </remarks>
    private static string DispositionFor(DateTimeOffset instant) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"attachment; filename=\"budgetoid-export-{instant.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.json\"");
}
