using Application.Abstractions;

namespace Application.AccountKeys.GetAccountKeys;

/// <summary>
/// Hands the browser the account's key custody at both of its levels: the manifest naming every recovery
/// factor's public key and the generation that manifest is in, and what <b>every factor the account
/// holds</b> stores — that factor's wrapped private key and the account's two keys encapsulated to its
/// public half, one row per registered passkey and ten per set of recovery codes.
/// </summary>
/// <remarks>
/// <para>
/// <b>One read, and its only input is <see cref="IUserContext.UserId" />.</b> The account is the unit the
/// keys belong to, so the account is what the read is keyed on; nothing about the request narrows it
/// further, and there is no second identifier for anything to disagree about.
/// </para>
/// <para>
/// <b>Two levels in one answer, and the handler adds nothing to either.</b> The manifest and the epoch
/// are facts about the <em>account</em> — true once, however many factors it holds — and the envelopes
/// are facts about a <em>factor</em>. <see cref="AccountKeyCustody" /> holds both because the client's
/// job is to compare them: it refuses a rotation when there is no manifest, and refuses one where the
/// served factor set and the set the manifest names disagree. Splitting them across two calls from here
/// would put a concurrent enrolment between two correct halves and report the result to a person as
/// tampering, so the handler makes one call and forwards what it gets.
/// </para>
/// <para>
/// <b>The read of <c>factor_manifests</c> lives in the read service, and this handler is not where a
/// second one may land.</b> The shipped read service takes both levels off one statement — the argument
/// for why one statement rather than two is written there and on <see cref="AccountKeyCustody" /> — and
/// every account still answers <see langword="null" /> at epoch <c>0</c>, because nothing writes a
/// manifest. A reader must not reach for the table here: the port already returns the pair, and a lookup
/// beside this call would reintroduce exactly the two-snapshot gap the single statement exists to close.
/// </para>
/// <para>
/// <b>It used to be narrowed by the credential that opened the session, and that was wrong.</b> The
/// narrowing rested on "the browser can only ever be holding a key-encryption key derived from the
/// factor its own session was opened with", and two things that exist today say otherwise. Re-authentication
/// looks a passkey up <em>by account</em> —
/// <c>PasskeyReauthentication</c> calls <c>IPasskeyRepository.FindByWebAuthnCredentialIdForUserAsync</c>
/// with <see cref="IUserContext.UserId" /> and never with the session's credential — and the assertion
/// options carry <b>no <c>allowCredentials</c></b>, which <c>PasskeyRequestOptions</c> and
/// <c>BeginReauthenticationHandler</c> each state as a decision. So the <em>authenticator</em> chooses
/// which of the account's credentials answers a ceremony, and the client cannot know in advance which one
/// it will be. A read keyed on anything narrower than the account therefore refuses a factor that was
/// just presented and just verified.
/// </para>
/// <para>
/// <b>The failure that follows is reachable and silent.</b> Somebody signs in by redeeming a recovery
/// code, so the session is opened over the recovery-codes credential —
/// <c>RedeemRecoveryCodeHandler</c> establishes it over the set. They then ask for a new set of codes,
/// which <c>GenerateRecoveryCodesHandler</c> gates on a fresh <em>passkey</em> assertion. The ceremony
/// yields the passkey's key-encryption key; a credential-narrowed read hands back the ten recovery-code
/// envelopes; every unwrap fails to authenticate, and the client tells the person to present another
/// factor having just been given a perfectly valid one. Nothing on the server sees any of it happen:
/// every row is correct, every status code is a 200, and the only symptom is an account that will not
/// open.
/// </para>
/// <para>
/// <b>What widening costs, stated rather than waved past.</b> A caller now receives envelopes it holds
/// nothing to open — ten of them where a passkey session used to get one. That is material travelling
/// further than the request needs it, which is a real cost and not one to pretend away. It is accepted
/// for two reasons. The operator already holds every one of these rows, so widening discloses nothing to
/// the party the design is defending against. And a factor's envelopes open <b>only</b> under a
/// key-encryption key derived from that factor — a secret this server has never seen, being a PRF output
/// inside an authenticator or a recovery code written on a card — so an entry the caller cannot open is
/// ciphertext bound to associated data it cannot reproduce. What is left is the <em>count</em>: the
/// answer now says how many factors the account holds. That is a fact about the caller's own account
/// which the same principal can already assemble from <c>GET /api/me/credentials</c> and
/// <c>GET /api/me/recovery-codes</c>, so it is not a capability this route introduces.
/// </para>
/// <para>
/// <b>The owner argument comes from <see cref="IUserContext.UserId" />, and that is the value
/// row-level security is keyed on.</b> <c>wrapped_account_keys</c> is policed by <c>user_isolation</c>,
/// which compares against the very id the context published, so naming it here keeps a single answer to
/// "who is this request" — and the explicit predicate stays even though the policy would scope the read
/// anyway, because a policy makes a wrong query answer <em>empty</em> rather than <em>correct</em>. See
/// <see cref="IAccountKeyReadService.ListForAccountAsync" />.
/// </para>
/// <para>
/// <b>It asks nothing about whether the session is live, and now there is nothing here to ask with.</b>
/// Whether a session is live is <c>Session.IsActiveAt</c>'s answer and the <b>authentication pipeline</b>
/// is what applies it, before any handler in this ring is reached. That used to be a restraint — this
/// handler held an <c>ISessionRepository</c> and had to be told not to use it for liveness — and it is
/// now structural: the dependency is gone, so a second copy of that rule cannot be written here without
/// first re-introducing a session. <c>AccountKeysEndpointTests</c> pins that the pipeline still refuses a
/// revoked session on this route, which nothing did while the rule was only a paragraph.
/// </para>
/// <para>
/// <b>An account holding no manifest answers epoch <c>0</c> and no manifest, not a refusal.</b>
/// <see cref="Domain.Users.FactorManifest.MinimumRotationEpoch" /> is 1 and the column refuses anything
/// below it, so <c>0</c> is a number no stored row can hold and therefore an unambiguous spelling of
/// "there is no row". That is the pre-registration state and the state of <em>every</em> account in
/// every database, because nothing writes a manifest — so the empty answer here is the only answer this
/// product currently produces, and a handler treating it as an error would refuse every request on the
/// route. It is also independent of the list below it: a full factor list beside a missing manifest is
/// the normal shape today, not a contradiction.
/// </para>
/// <para>
/// <b>An account holding no factor rows answers an empty list, not a throw</b>, for the reason
/// <c>ListCredentialsHandler</c> gives about its own: an empty collection is the honest shape of "nothing
/// came back", and this read is taken for display. There are four ways to reach it, and the fourth is one
/// this file used to deny.
/// </para>
/// <list type="number">
/// <item>The account holds no recovery factor at all. No path reaches that state today — registration
/// creates eleven factors in one act or creates nothing — but nothing in the schema forbids it.</item>
/// <item>The account was erased between this request authenticating and this read running.</item>
/// <item>Its factor-bearing credentials were revoked in that same window; each revocation takes its
/// wrapped rows with it by the cascade from <c>credentials</c>.</item>
/// <item><b>A factor exists whose wrapped row was never written.</b> The earlier text here called that
/// state unreachable, on the grounds that every path creating a factor writes its row in the same
/// <c>SaveChanges</c> as the credential. That is a property of the three write paths that exist, not a
/// fact the schema holds: the two <c>NOT NULL</c> columns make "a row carries both keys or neither" a
/// schema fact, and <c>CLAUDE.md</c> is explicit that <b>"every factor has a row" is not one</b> — a
/// fourth write path that skipped them would create a keyless factor and redden nothing. So a keyless
/// factor is a state this answer can be reporting, and a handler that treated an empty list as
/// impossible would be wrong about its own domain.</item>
/// </list>
/// <para>
/// <b>Empty is still answered as a <c>200</c> carrying an empty <c>factors</c> array and never a
/// <c>404</c>, but not for the reason it used to be.</b> The array moved down a level when the response
/// grew the account's manifest beside it; nothing about this argument moved with it, and an account
/// holding no factor and no manifest is still a <c>200</c>. The old argument was an enumeration oracle: a 404 would have told a caller that a guessed
/// session id named a real row. That argument does <em>not</em> survive the widening — nothing is
/// narrowed by an identifier a caller could guess, and an authenticated request can only ever ask about
/// its own account. What holds now is the client. <c>AccountKeyCustodyService</c> reads an empty list as
/// <c>unopened</c> — "present another factor" — and reads any failed read at all, a 404 included, as
/// <c>unreachable</c>, whose advice is "try the same factor again in a minute". So a 404 would hand
/// somebody whose account genuinely holds nothing openable the one instruction that can never work.
/// </para>
/// <para>
/// <b>The order is <c>FactorEnvelopes.FactorId</c> ascending, and what that buys is determinism rather
/// than a meaning.</b> The client tries each pair in turn and the associated data decides which one
/// opens, so no sequence is more useful to a caller than another; what a caller does need is that two
/// reads of unchanged rows agree, which an unordered read of an eleven-row set does not promise. The
/// primary key is the sort because it cannot tie, so no second key is needed. The <em>particular</em>
/// sequence is still not part of the contract, though the reason usually given for that is false:
/// <see cref="Guid.CompareTo(Guid)" /> <b>is</b> a byte comparison of the canonical RFC 4122 form —
/// over 200,000 random pairs on .NET 10 it disagreed with big-endian byte order zero times — so a
/// .NET sort and a PostgreSQL <c>order by</c> are expected to agree, granted that <c>uuid_cmp</c> is
/// a <c>memcmp</c> over those same sixteen bytes, which is not something anyone here has run against
/// a live server. The trap worth naming is <see cref="Guid.ToByteArray()" /> instead: its default
/// layout is little-endian across the first three fields, and over those same 200,000 pairs it
/// ordered differently from <see cref="Guid.CompareTo(Guid)" /> on close to half of them. So a
/// hand-rolled comparison, or an expected order built in a test out of those bytes, matches neither
/// side. Determinism is what a caller may rest on; which factor comes first is what none of them may.
/// </para>
/// <para>
/// <b>The answer is a list because a factor is not a credential, and it must never be written as "the"
/// pair.</b> A passkey is one credential and one factor: one row. A set of recovery codes is one
/// credential and <b>ten</b> factors, because each code derives its own key-encryption key and a person
/// redeems whichever one they still have. <c>SingleOrDefault</c>, <c>FirstOrDefault</c>, or a return type
/// of <c>FactorEnvelopes?</c> would each be correct for an account holding one passkey and nothing else,
/// and would drop nine of every ten recovery-code envelopes — the exact failure that moved
/// <c>wrapped_account_keys</c>' primary key from <c>credential_id</c> to <c>factor_id</c>.
/// </para>
/// <para>
/// <b>It takes no <c>ILogger</c>, and must never take one</b>, for the reason
/// <c>ListCredentialsHandler</c> and <c>GetSignedInUserHandler</c> give:
/// the identifiers on this path name an account's key custody, and a log line copies them into a sink
/// with a different retention policy and a different audience from the table they came from. It is
/// sharper here than on either of those — a factor id is the associated data both of a row's envelopes
/// were sealed with, so a log holding factor ids beside envelopes is a partial reconstruction of the
/// account's key material in a place nobody is guarding. No gate anywhere reads a log line from here, so
/// there is nothing to trade against.
/// </para>
/// <para>
/// <b>No <c>ITransactionalExecutor</c>, and this is not an oversight.</b> One read and no write: there is
/// no unit of work to make atomic. Opening a transaction here would also put the authentication path's
/// ordering trap back in play — a transaction configures its connection when it opens, and every policed
/// statement inside one opened before the identity was published meets <c>''::uuid</c> and raises
/// <c>22P02</c>. That is the rule <c>CLAUDE.md</c> states and <c>RegisterAccountHandler</c>,
/// <c>CompleteAssertionHandler</c> and <c>RedeemRecoveryCodeHandler</c> each restate inline; nothing here
/// is worth re-opening it for.
/// </para>
/// </remarks>
public sealed class GetAccountKeysHandler(
    IUserContext userContext,
    IAccountKeyReadService readService)
    : IQueryHandler<GetAccountKeysQuery, AccountKeyCustody>
{
    public Task<AccountKeyCustody> HandleAsync(
        GetAccountKeysQuery query,
        CancellationToken cancellationToken = default) =>
        // The owner from the context the policy is keyed on, and nothing else. Every factor the account
        // holds — one row per passkey, ten per set of codes — because a ceremony can present any of
        // them and the authenticator, not this request, decides which. The manifest and its epoch come
        // back on the same answer rather than from a second call, so the two levels describe one instant.
        readService.ListForAccountAsync(userContext.UserId, cancellationToken);
}
