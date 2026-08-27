using System.Security.Claims;
using Api.Infrastructure;
using Application.AccountKeys;
using Application.AccountKeys.GetAccountKeys;
using Application.Passkeys;

namespace Api.Endpoints;

public static class AccountKeyEndpoints
{
    public static IEndpointRouteBuilder MapAccountKeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // A fourth group over "/api/me", beside credentials, the erasure and the export, for the
        // reason DataExportEndpoints states: "/api/me" is the current principal's namespace, and the
        // wrapped copies of the account's keys are something that principal holds. Not under
        // "/api/passkeys" — a set of recovery codes files ten of these rows and runs no ceremony.
        RouteGroupBuilder group = endpoints.MapGroup("/api/me");

        // NO METADATA OF ANY KIND, and each absence is its own decision.
        //
        // No RequireAuthorization: the application's fallback policy covers every route declaring
        // nothing, and restating it here would make the one line that defines the anonymous surface
        // stop being the only one. Never AllowAnonymous — this is the one read that names an
        // account's key custody. No AcceptsEndedSession: a dead handle has no business here, and the
        // marked set is exactly the sign-out route. No AllowsLockedSession: a session opened by a
        // federated credential can derive no key-encryption key, so it has nothing to open and is
        // refused by the fallback policy's FullSessionRequirement. AnonymousSurfaceTests,
        // AcceptsEndedSessionTests and LockedSessionTests each read their marker off the whole route
        // table, so any of the three appearing here reddens one of them.
        //
        // It mints nothing: RegisterAccountHandler is the only code that brings an account into
        // existence, reachable only from POST /api/registration/registration.
        group.MapGet("/account-keys", async (
            HttpContext httpContext,
            GetAccountKeysHandler handler,
            CancellationToken cancellationToken) =>
        {
            // Off the claim this request's own authentication produced, never off a body, a route or
            // a query string — the shape SessionEndpoints uses for the same value. There is therefore
            // no session id on the wire for a caller to substitute, which is what makes "the
            // credential that opened THIS session" a property of the shape rather than of a check.
            //
            // A principal carrying no such claim authenticated some other way, and gets the same
            // empty array below rather than a refusal, for the reason that return states.
            if (!Guid.TryParse(
                    httpContext.User.FindFirstValue(SessionCookieAuthenticationHandler.SessionIdClaimType),
                    out Guid sessionId))
            {
                return TypedResults.Ok(Array.Empty<AccountKeyEntry>());
            }

            IReadOnlyList<FactorEnvelopes> factors = await handler.HandleAsync(
                new GetAccountKeysQuery(sessionId),
                cancellationToken);

            // 200 WITH AN EMPTY ARRAY, NEVER 404 — and this is the single most likely thing a later
            // reader "fixes", because "nothing found → 404" is the right instinct almost everywhere
            // else. An empty answer is what a request whose session was never established, whose
            // session has already ended, and whose session belongs to somebody else all receive,
            // indistinguishably. The moment "no rows" answers differently from those, a caller learns
            // which of them happened — and on the one route that names an account's key custody, that
            // is the whole of what an attacker wanted. GetAccountKeysHandler refuses to be that
            // oracle one ring down; a 404 minted here would rebuild it above the handler that
            // declined to.
            //
            // Base64url is applied at this edge and nowhere below it: the Application ring carries an
            // envelope as bytes and the wire spelling is the API's business, which is the mirror of
            // WrappedKeyEnvelope.TryDecode on the write side. PasskeyEncoding.Encode emits the
            // unpadded alphabet the client's decoder is stricter about than this one — it refuses
            // padding and refuses the standard alphabet outright, so a Convert.ToBase64String here
            // would hand a browser a value it will not decode at all, over stored bytes that are
            // perfectly correct.
            return TypedResults.Ok(factors
                .Select(factor => new AccountKeyEntry(
                    factor.FactorId,
                    PasskeyEncoding.Encode(factor.WrappedContentKey.Span),
                    PasskeyEncoding.Encode(factor.WrappedIndexKey.Span)))
                .ToArray());
        });

        return endpoints;
    }

    /// <summary>
    /// One factor's row on the wire: <c>factorId</c>, <c>wrappedContentKey</c>, <c>wrappedIndexKey</c>
    /// — and nothing else, in either direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A response record of its own rather than <see cref="FactorEnvelopes" /> serialized directly,
    /// because the two envelopes leave as base64url text and that ring holds them as bytes.
    /// </para>
    /// <para>
    /// <b>No fourth member may be added, and each obvious candidate is refused for its own reason.</b>
    /// <c>credentialId</c> is the id a revocation route addresses a credential by and the join
    /// <c>account-keys.md</c> deliberately does not give a client — a browser locates its pair by
    /// trying each in turn, so an id here is a capability handed over for no use. <c>userId</c> is the
    /// value every policy in the database is keyed on and the one identifier a response body may never
    /// carry into a client log. <c>createdAtUtc</c> is a usage record beside key material: it says when
    /// each of an account's ten codes was issued, which is a timeline of somebody's recovery history
    /// that the screen reading this endpoint has no use for.
    /// </para>
    /// <para>
    /// <see cref="FactorId" /> leaves as a <see cref="Guid" /> so the serializer renders the canonical
    /// lower-case hyphenated spelling. That spelling is the contract rather than a formatting habit:
    /// both of a factor's envelopes were sealed against these exact bytes, and a client that rebuilds
    /// the associated data from a different rendering of the same UUID opens neither of them —
    /// permanently, for that factor, with no error naming the cause.
    /// </para>
    /// </remarks>
    private sealed record AccountKeyEntry(
        Guid FactorId,
        string WrappedContentKey,
        string WrappedIndexKey);
}
