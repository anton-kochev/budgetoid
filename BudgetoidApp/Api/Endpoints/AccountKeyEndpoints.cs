using Api.Infrastructure;
using Application.AccountKeys;
using Application.AccountKeys.GetAccountKeys;
using Application.Passkeys;

namespace Api.Endpoints;

/// <summary>
/// The route that hands a browser the account's key custody: the manifest naming every recovery
/// factor's public key, the generation it is in, and what each factor stores.
/// </summary>
/// <remarks>
/// <b>It is one of <em>two</em> routes in the product that return key material, and the header that
/// says so is no longer a private copy.</b> This route's response carries every factor's wrapped
/// private key; <c>GET /api/me/key-rotation</c> carries the staged seals of an interrupted run, which
/// are the only copies of the generation that run was rewriting the account under. Both write
/// <see cref="ResponseCaching.NoStore" /> themselves — the value has one owner, the decision stays with
/// each route, and <see cref="SecurityHeadersMiddleware" /> still refuses to write a blanket
/// <c>Cache-Control</c> for the reason it gives where it refuses.
/// </remarks>
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
            //
            // The VALUE is shared with GET /api/me/key-rotation and the DECISION is not: two private
            // copies of a security header is the shape that drifts, while a route inheriting one by
            // being written in the same file is the shape that spreads. ResponseCaching carries that
            // split, and the argument for no-store alone rather than the longer incantation.
            response.Headers.CacheControl = ResponseCaching.NoStore;

            AccountKeyCustody custody = await handler.HandleAsync(
                new GetAccountKeysQuery(),
                cancellationToken);

            // 200 WITH AN EMPTY factors ARRAY, NEVER 404 — and this is the single most likely thing a
            // later reader "fixes", because "nothing found → 404" is the right instinct almost
            // everywhere else. The reason is no longer the enumeration oracle the narrowed route
            // carried; it is the client. AccountKeyCustodyService reads an empty list as "present
            // another factor" and reads any failed read — a 404 included — as "try the same factor
            // again in a minute", so a 404 would give somebody whose account holds nothing openable the
            // one instruction that can never work. GetAccountKeysHandler enumerates the four ways an
            // empty answer is reached. THE ARRAY MOVING DOWN A LEVEL CHANGES NONE OF THAT, and neither
            // does the manifest arriving beside it: an account with no factors AND no manifest is a
            // 200 carrying manifest null, rotationEpoch 0 and an empty factors array. A 404 for the
            // missing manifest would be the same mistake wearing a newer member's clothes.
            //
            // Base64url is applied at this edge and nowhere below it: the Application ring carries an
            // envelope as bytes and the wire spelling is the API's business, which is the mirror of
            // WrappedPrivateKeyEnvelope.TryDecode and EncapsulatedAccountKeysEnvelope.TryDecode on
            // the write side. PasskeyEncoding.Encode emits the unpadded alphabet the client's decoder
            // is stricter about than this one — it refuses padding and refuses the standard alphabet
            // outright, so a Convert.ToBase64String here would hand a browser a value it will not
            // decode at all, over stored bytes that are perfectly correct.
            //
            // One encoder over both members and that is not the flattening the vocabulary warns
            // about: base64url is the transport, and it is the same transport for two framings the
            // decoders on the write side keep apart by naming two types. What must never be shared is
            // the judging, not the spelling. THE MANIFEST IS THE THIRD VALUE THAT TRANSPORT CARRIES,
            // through the same encoder for the same reason — it is the list of public keys sealed under
            // the account's content key, a third value this server cannot open, and the transport says
            // nothing about which of the three it is spelling.
            //
            // The absent manifest stays absent rather than becoming "": an empty string is a legal
            // base64url rendering of zero bytes, so encoding an absent value would hand the client a
            // manifest of length zero and make "there is no manifest" indistinguishable from "there is
            // one and it names nobody". null is the only spelling that keeps them apart on the wire.
            return TypedResults.Ok(new AccountKeysResponse(
                custody.Manifest is { } manifest ? PasskeyEncoding.Encode(manifest.Span) : null,
                custody.RotationEpoch,
                [
                    .. custody.Factors.Select(factor => new AccountKeyEntry(
                        factor.FactorId,
                        PasskeyEncoding.Encode(factor.WrappedPrivateKey.Span),
                        PasskeyEncoding.Encode(factor.EncapsulatedAccountKeys.Span))),
                ]));
        });

        return endpoints;
    }

    /// <summary>
    /// The whole answer on the wire: <c>manifest</c>, <c>rotationEpoch</c> and <c>factors</c> — the
    /// account's two facts, then one row per recovery factor.
    /// </summary>
    /// <param name="Manifest">
    /// The account's authenticated manifest of every factor's public key, unpadded base64url, or
    /// <see langword="null" /> when the account has no manifest row. <b>Which answer an account gets is
    /// decided by when it registered</b>: registration writes the first manifest at epoch 1 in the same
    /// save as the account, so an account created since that landed answers bytes, and one created
    /// before it answers <see langword="null" /> forever — nothing can backfill a blob sealed under a
    /// content key this server has never held.
    /// </param>
    /// <param name="RotationEpoch">
    /// Which generation of the manifest is in force, or <c>0</c> when there is no manifest row.
    /// </param>
    /// <param name="Factors">
    /// One <see cref="AccountKeyEntry" /> per recovery factor, ordered as the read returned them. Empty
    /// is a normal answer and never a <c>404</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The body used to be the bare array this member now holds, and the wrapper is what gives the
    /// account's own facts a place to live.</b> A manifest names the whole factor set and an epoch
    /// numbers the generation of that set; both are true once per account however many factors it holds,
    /// so both belong at the top level. The rejected alternative is the one a reader reaches for when the
    /// response is only an array: repeat them on every row. That is eleven copies of one value on an
    /// ordinary account, it invites the client to read the first row's copy and call it the account's,
    /// and it would widen <see cref="AccountKeyEntry" /> — which is refused there, member by member, and
    /// is refused harder now that there is a correct place to put an account-level fact.
    /// </para>
    /// <para>
    /// <b>Epoch <c>0</c> beside a <see langword="null" /> manifest is the pre-registration state, not an
    /// error and not a <c>404</c>.</b> A stored generation starts at 1 — <c>FactorManifest</c> refuses
    /// anything lower and the column's check constraint refuses it again — so <c>0</c> is a number no row
    /// can hold, which is what lets one integer say "there is nothing stored" without a second member to
    /// disambiguate it. A client cannot distinguish "no manifest" from "manifest of zero length" if the
    /// absent one is spelled <c>""</c>, which is why the encoder is skipped rather than fed an empty
    /// span.
    /// </para>
    /// <para>
    /// <b>Why one route rather than a second one beside it.</b> The client refuses a rotation when there
    /// is no manifest, and refuses one where the served factor set and the set the manifest names
    /// disagree — a comparison that is only sound if both sides came out of the same read. Two routes
    /// would let a factor be enrolled between them, and the client would report two correct answers as a
    /// mismatch. The cost is stated rather than waved past: every caller of this route now carries the
    /// manifest whether it is going to compare anything or not.
    /// </para>
    /// <para>
    /// <b>Base64url at this edge and nowhere below it</b>, the rule the route delegate argues for the two
    /// envelopes and this member follows unchanged: <see cref="AccountKeyCustody.Manifest" /> is bytes,
    /// and the wire spelling is the API's business. <c>Convert.ToBase64String</c> is the specific wrong
    /// answer — the client's decoder refuses padding and refuses the standard alphabet outright.
    /// </para>
    /// </remarks>
    private sealed record AccountKeysResponse(
        string? Manifest,
        int RotationEpoch,
        IReadOnlyList<AccountKeyEntry> Factors);

    /// <summary>
    /// One factor's row on the wire: <c>factorId</c>, <c>wrappedPrivateKey</c>,
    /// <c>encapsulatedAccountKeys</c> — and nothing else, in either direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A response record of its own rather than <see cref="FactorEnvelopes" /> serialized directly,
    /// because both payloads leave as base64url text and that ring holds them as bytes.
    /// </para>
    /// <para>
    /// <b>The response has two levels now, and this is the lower one.</b>
    /// <see cref="AccountKeysResponse" /> carries what is true of the <em>account</em> — the manifest
    /// naming the whole factor set, and the generation that manifest is in. This record carries what is
    /// true of <em>one factor</em>, and the boundary between them is the test a proposed member has to
    /// pass: a value that is the same on all eleven rows of an ordinary account is an account-level fact
    /// and goes up, a value that differs per factor belongs here, and a value that is neither is refused
    /// below by name. <b>The wrapper is not permission to widen this row — it is the reason the row can
    /// stay at three.</b> Every account-level member a reader used to have nowhere to put now has
    /// somewhere better, so a fourth member here has lost the only honest excuse it ever had.
    /// </para>
    /// <para>
    /// <b>The two member names are the contract, not a label.</b> <em>Wrapped under</em> names a key
    /// over another key and <em>encapsulated to</em> names a public key; a client reading
    /// <c>wrappedPrivateKey</c> knows to unwrap it with the key-encryption key it just derived, and a
    /// client reading <c>encapsulatedAccountKeys</c> knows to decapsulate with the private half that
    /// unwrapping produced. Renaming either to the other verb — or to a neutral one covering both —
    /// would describe two steps as one, and the client that ran them in the wrong order would get an
    /// authentication failure naming nothing.
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
    /// response body may never carry into a client log. <c>createdAtUtc</c> is refused for the dullest
    /// reason of the three: <b>the caller is already holding it.</b> Every one of these rows carries its
    /// credential's own instant — each of the three paths that writes one reads the clock once and
    /// stamps the credential and its factors from that single value in a single <c>SaveChanges</c>
    /// (<see cref="Application.Registration.RegisterAccountHandler" />,
    /// <see cref="Application.Passkeys.CompleteRegistration.CompleteRegistrationHandler" />,
    /// <see cref="Application.RecoveryCodes.GenerateRecoveryCodes.GenerateRecoveryCodesHandler" />) —
    /// and <c>GET /api/me/credentials</c>, which the same session reaches under the same fallback
    /// policy, returns exactly that instant as its own <c>createdAtUtc</c>. The member would therefore
    /// disclose no fact the client cannot already read, and widen a key-material response to say it.
    /// </para>
    /// <para>
    /// <b>Two more candidates are refused by where they already are, and they are the ones this change
    /// creates.</b> A <c>manifest</c> or a <c>rotationEpoch</c> on a row would be the account's single
    /// value copied once per factor: eleven copies on an ordinary account, kept consistent by nothing,
    /// and a client reading row zero's copy would be reading a per-account fact off whichever row the
    /// sort happened to put first. <em>Per-factor</em> spellings of the same idea are refused for a
    /// second reason on top of that one — the manifest is authenticated as a <b>set</b>, which is the
    /// whole argument for there being no per-row public key column beneath it either, so a row-level
    /// slice of it would be a fragment nothing can verify. Both belong on
    /// <see cref="AccountKeysResponse" />, both are there, and neither has an argument for being in two
    /// places.
    /// </para>
    /// <para>
    /// <b>It is not a recovery timeline, and the argument that used to stand here said it was.</b> A
    /// set's ten codes are written in one save from one clock read, so they carry one instant and not
    /// ten; there is no sequence of issuings for a member here to expose. Nor is it the <c>last_login</c>
    /// that <c>ProhibitedColumnVocabulary</c> refuses — that vocabulary bans records of <em>use</em> and
    /// permits <c>created_at_utc</c>, which is why the table this route reads has one.
    /// </para>
    /// <para>
    /// <see cref="FactorId" /> leaves as a <see cref="Guid" /> so the serializer renders the canonical
    /// lower-case hyphenated spelling. That spelling is the contract rather than a formatting habit:
    /// the wrapped private key was wrapped against these exact bytes as its associated data, and a
    /// client that rebuilds them from a different rendering of the same UUID unwraps nothing —
    /// permanently, for that factor, with no error naming the cause. The encapsulated value is then
    /// unreachable too, because the private half that opens it is what the unwrap was for.
    /// </para>
    /// </remarks>
    private sealed record AccountKeyEntry(
        Guid FactorId,
        string WrappedPrivateKey,
        string EncapsulatedAccountKeys);
}
