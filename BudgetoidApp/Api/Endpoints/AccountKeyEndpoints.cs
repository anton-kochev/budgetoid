using Application.AccountKeys;
using Application.AccountKeys.GetAccountKeys;
using Application.Passkeys;

namespace Api.Endpoints;

public static class AccountKeyEndpoints
{
    /// <summary>
    /// The one response header this route states for itself, and the one endpoint in the application
    /// that has a reason to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Api.Infrastructure.SecurityHeadersMiddleware" /> refuses to write a blanket
    /// <c>Cache-Control</c> and says why: no endpoint here states its cacheability, so the question is
    /// open rather than delegated, and settling it globally would settle it in the one place that knows
    /// least about what was returned. This is the endpoint that closes that sentence for itself — it is
    /// the only route in the product that returns key material, so it is the only one whose body must
    /// not be written to a shared cache, a disk cache or a back-button restore.
    /// </para>
    /// <para>
    /// <b><c>no-store</c> alone, not the longer incantation.</b> <c>no-cache</c> permits storage and
    /// requires revalidation, which is the opposite of what is wanted; <c>private</c> permits a browser
    /// cache; <c>max-age=0</c> without <c>no-store</c> permits a stale-serving cache to keep the bytes.
    /// The four together are a superstition that reads as more careful and stores more.
    /// </para>
    /// </remarks>
    private const string NoStore = "no-store";

    public static IEndpointRouteBuilder MapAccountKeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // A fourth group over "/api/me", beside credentials, the erasure and the export, for the
        // reason DataExportEndpoints states: "/api/me" is the current principal's namespace, and the
        // wrapped copies of the account's keys are something that principal holds. Not under
        // "/api/passkeys" — a set of recovery codes files ten of these rows and runs no ceremony.
        RouteGroupBuilder group = endpoints.MapGroup("/api/me");

        // NO AUTHORIZATION METADATA OF ANY KIND, and each absence is its own decision.
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
            HttpResponse response,
            GetAccountKeysHandler handler,
            CancellationToken cancellationToken) =>
        {
            // NOTHING IS READ OFF THE REQUEST, AND THAT IS THE SHAPE RATHER THAN AN OMISSION. This
            // route used to read the session_id claim its own authentication produced and hand it
            // inward, because the answer was narrowed to the credential that opened that session.
            // The answer is now the account's, which IUserContext already resolves one ring down, so
            // there is no claim to read, no id to parse and no branch for a principal that carries
            // one shaped wrongly. GetAccountKeysHandler carries the argument for the widening.
            //
            // A ClaimsPrincipal still never crosses into the Application ring; there is simply
            // nothing left at this edge that would want one.
            //
            // Cache-Control is written HERE rather than in SecurityHeadersMiddleware, which argues at
            // length for owning no global value, and it is a DIRECT WRITE rather than a second
            // Response.OnStarting callback. That middleware registers the only OnStarting callback in
            // the application and says what a second one costs: Kestrel runs them LIFO and abandons
            // the whole stack on the first throw, so an added callback both overwrites this route's
            // header and puts the four security headers behind its own failure. A direct write is
            // safe here because that callback assigns only its own four names and touches nothing
            // else on the response. The one path where the header is lost is a 500 — the exception
            // handler's Response.Clear() takes it — and the body written there is a ProblemDetails
            // carrying no key material, which is the case a cache is welcome to keep.
            response.Headers.CacheControl = NoStore;

            IReadOnlyList<FactorEnvelopes> factors = await handler.HandleAsync(
                new GetAccountKeysQuery(),
                cancellationToken);

            // 200 WITH AN EMPTY ARRAY, NEVER 404 — and this is the single most likely thing a later
            // reader "fixes", because "nothing found → 404" is the right instinct almost everywhere
            // else. The reason is no longer the enumeration oracle the narrowed route carried; it is
            // the client. AccountKeyCustodyService reads an empty list as "present another factor"
            // and reads any failed read — a 404 included — as "try the same factor again in a
            // minute", so a 404 would give somebody whose account holds nothing openable the one
            // instruction that can never work. GetAccountKeysHandler enumerates the four ways an
            // empty answer is reached.
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
    /// trying each in turn, so an id here is a capability handed over for no use. It is also the
    /// member a reader will reach for <em>now</em> that the answer spans an account's credentials,
    /// on the grounds that a client could then skip the entries it cannot open; it could not, because
    /// which credential a ceremony answered with is exactly what the client does not know.
    /// <c>userId</c> is the value every policy in the database is keyed on and the one identifier a
    /// response body may never carry into a client log. <c>createdAtUtc</c> is a usage record beside
    /// key material: it says when each of an account's ten codes was issued, which is a timeline of
    /// somebody's recovery history that the screen reading this endpoint has no use for.
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
