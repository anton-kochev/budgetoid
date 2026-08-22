using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.Passkeys;
using Application.Registration;
using Domain.Budgets;
using Domain.Users;
using Infrastructure.Persistence.Provisioning;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TestSupport;
using CodeSubmission = IntegrationTests.RegistrationCodeSubmission;
using RegisteredAccount = IntegrationTests.RegistrationCeremonyResult;

namespace IntegrationTests;

/// <summary>
/// Registration is one consented act and one transaction: a caller holding a provider token and no
/// account runs a WebAuthn ceremony, hands over the recovery card the client minted, and the server
/// writes the whole account — or writes nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every test here drives the real least-privilege connection</b>, the way the rest of this suite
/// does, and that is what makes the row counts below evidence rather than description. A write the
/// grant matrix does not cover fails with <c>42501</c>; a policed statement issued on a connection that
/// has not yet named a user fails with <c>22P02</c>. Neither is reachable from a handler tested in
/// isolation, and the second is the specific failure a transaction opened too early produces.
/// </para>
/// <para>
/// <b>Both routes are authenticated by the identity provider's scheme and by nothing else</b>, so the
/// factory here repoints that scheme at <see cref="TestAuthHandler" />. Without it
/// <c>AuthorizationMiddleware</c> re-authenticates against the real Google <c>JwtBearer</c> handler,
/// which sees no <c>Authorization</c> header and refuses — see the remarks on
/// <see cref="ApiFactory" /> for why naming the default scheme cannot fix that.
/// </para>
/// <para>
/// <b>What is here and what is next door.</b> This file holds the happy path, the provider gate, the
/// marker-position pin and the refusal battery — the prf gate in all four of its shapes, the set's size,
/// the four conflicts, the eleventh factor, the claim minimisation, both directions of the nonce-pool
/// separation and both directions of the session-cookie bridge. It also holds the three claims a
/// mutation pass found nothing holding: that the options leg names the account <em>its own</em> challenge
/// derives, that the device signs in with the handle it was given rather than one a test derived, and that
/// the session stands on the passkey credential rather than the card. The route table's own census lives in
/// <see cref="RegistrationRouteTests" />, because a policy naming a scheme is invisible from the outside;
/// the ordering of the handler's ladder lives in <c>UnitTests.RegisterAccountHandlerTests</c>, because
/// two adjacent rungs swapped produce the same status code here.
/// </para>
/// <para>
/// <b>Every refusal below also asserts that nothing was written, and the sweep is over the relations an
/// account owns rather than over the whole database.</b> That distinction is doing work on exactly one
/// relation. A refused finish leg legitimately loses a <c>webauthn_challenges</c> row — the nonce is
/// burnt at rung 4, before the response is verified, deliberately, so that a caller cannot grind
/// responses against one issued challenge — and every refusal here happens at or after that rung.
/// <see cref="AssertNoAccountRowAnywhereAsync" /> already handles it without a special case: it demands
/// <b>zero</b> of every discovered relation but the two nobody owns, and a spent challenge is zero. The
/// tests that begin with an account already in place cannot use it at all and compare a whole census of
/// row counts taken before and after instead — see <see cref="AssertNothingChangedAsync" />.
/// </para>
/// </remarks>
public sealed class AccountRegistrationTests
{
    /// <summary>The Google subject registration is driven as.</summary>
    private const string Subject = "google-registering";

    /// <summary>
    /// The address the provider asserts for <see cref="Subject" />.
    /// </summary>
    /// <remarks>
    /// Named rather than left to the factory's default because the options leg is required to answer
    /// with it as both <c>user.name</c> and <c>user.displayName</c>, and a test comparing the response
    /// against a value it did not choose would compare the fixture with itself.
    /// </remarks>
    private const string Email = "registering@budgetoid.test";

    /// <summary>
    /// A second principal, whose address the provider does vouch for.
    /// </summary>
    /// <remarks>
    /// Only the unverified-email test needs it, for its provable-fail control: that test's own subject
    /// has already been refused on this host, and reusing it would leave the control measuring whether
    /// a refused subject can register rather than whether a verified one can.
    /// </remarks>
    private const string VerifiedSubject = "google-registering-verified";

    private const string VerifiedEmail = "registering-verified@budgetoid.test";

    /// <summary>
    /// A second principal with an account of its own to collide with, or none at all.
    /// </summary>
    /// <remarks>
    /// Every conflict test below needs exactly one thing to collide, and the way to arrange that is a
    /// second principal that differs in everything else. A test reusing <see cref="Subject" /> and
    /// <see cref="Email" /> together would breach two unique rules at once — which is the ambiguity the
    /// handler's disambiguating re-read exists to settle, and therefore the one arrangement that cannot
    /// tell the two refusals apart.
    /// </remarks>
    private const string OtherSubject = "google-registering-other";

    private const string OtherEmail = "registering-other@budgetoid.test";

    private const string OptionsPath = RegistrationCeremony.OptionsPath;
    private const string RegistrationPath = RegistrationCeremony.RegistrationPath;

    private const string AssertionOptionsPath = "/api/passkeys/assertion/options";
    private const string AssertionPath = "/api/passkeys/assertion";

    /// <summary>
    /// The three other ceremonies' legs, reached by the two nonce-pool tests below.
    /// </summary>
    /// <remarks>
    /// Written out rather than read off the endpoint classes, the choice <see cref="CookieName" /> makes:
    /// they are wire contract, and a test reading a production constant stays green through a rename that
    /// breaks every client.
    /// </remarks>
    private const string PasskeyRegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string PasskeyRegistrationPath = "/api/passkeys/registration";
    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RecoveryCodeGenerationPath = "/api/me/recovery-codes";
    private const string RedemptionPath = "/api/recovery-codes/redemption";

    /// <summary>
    /// The clause that separates the four 409s from one another, one per conflict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>All four answer the same status and the same title</b> — <c>ConflictExceptionHandler</c> writes
    /// one title for every conflict in the product — so the <c>detail</c> is the whole of what a caller
    /// is told, and a status-only assertion cannot tell "this Google account is registered" from "that
    /// factor identifier is spoken for". It is also the only thing separating the subject conflict from
    /// the email one after the handler's disambiguating re-read has run.
    /// </para>
    /// <para>
    /// A clause rather than the whole sentence, and restated here rather than read off the handler: those
    /// are private constants in a ring this project may reference but should not depend on the wording
    /// of, and a test taking its expectation from the type under test agrees with whatever that type
    /// later decides to say.
    /// </para>
    /// </remarks>
    private const string SubjectConflictClause = "Google account is already registered";

    private const string EmailConflictClause = "already linked to a different Google account";

    /// <summary>
    /// The whole sentence a caller whose provider identity already has an account is answered with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A departure from the clause convention above, and the two conflicts it covers are the reason.</b>
    /// The subject conflict and the email conflict are one status, one title and one repository outcome
    /// apart — <c>RegisterAccountHandler.RefusalFor</c> re-reads the credential to tell them apart — so the
    /// sentence is the entire difference a caller can see, and half of it is the part that says what to do
    /// next. A clause check passes a refusal that named the right problem and then told somebody to go and
    /// fix the wrong one.
    /// </para>
    /// <para>
    /// Still transcribed rather than read off the handler, which declares both privately in a ring this
    /// project may reference but must not take its expectations from. Change the handler's wording and
    /// this line changes with it, on purpose: a sentence a person is asked to act on is contract, not an
    /// implementation detail.
    /// </para>
    /// </remarks>
    private const string SubjectConflictSentence =
        "This Google account is already registered. Sign in with the passkey it holds, or redeem a "
        + "recovery code.";

    /// <summary>The whole sentence an address already linked elsewhere is answered with.</summary>
    /// <remarks>See <see cref="SubjectConflictSentence" />.</remarks>
    private const string EmailConflictSentence =
        "This email address is already linked to a different Google account.";

    private const string AuthenticatorConflictClause = "authenticator is already registered";

    private const string FactorConflictClause = "factor identifier is already registered";

    /// <summary>
    /// The member every refusal about the ceremony's own response is keyed under.
    /// </summary>
    /// <remarks>
    /// Restated rather than read off <c>RegisterAccountHandler</c>, which declares it privately. It is
    /// what a client is told to correct, so it is half of the refusal rather than an implementation
    /// detail of it.
    /// </remarks>
    private const string ResponseField = "Response";

    /// <summary>The member the card is refused under — the command's own spelling of it.</summary>
    /// <remarks>
    /// It is <c>Codes</c> and not <c>RecoveryCodes</c>, for the reason
    /// <see cref="PostRegistrationAsync" /> gives about the wire member it keys.
    /// </remarks>
    private const string CodesField = "Codes";

    /// <summary>
    /// The member the card travels on, as the wire spells it.
    /// </summary>
    /// <remarks>
    /// Named once because six places send it or take it away, and a card sent under a member the route
    /// does not read is refused as a set of the wrong size — which is a green for four of the tests below
    /// and an unreadable red for the rest. See <see cref="PostRegistrationAsync" /> for why the word is
    /// <c>codes</c>.
    /// </remarks>
    private const string CodesMember = RegistrationCeremony.CodesMember;

    /// <summary>The member the passkey's own factor identifier is refused under.</summary>
    /// <remarks>
    /// Restated rather than read off <c>RegisterAccountCommand</c>, for <see cref="CodesField" />'s
    /// reason. It is the key that distinguishes the eleventh-factor refusal from a malformed identifier:
    /// both are keyed here, and the sentence is what tells them apart.
    /// </remarks>
    private const string FactorIdField = "FactorId";

    /// <summary>
    /// The name a request presents its session handle under, written out as a literal.
    /// </summary>
    /// <remarks>
    /// The argument is <see cref="SessionCookieIssuanceTests" />': it is wire contract, a browser sends
    /// the bytes rather than the symbol, and a test reading the production constant stays green through
    /// a rename that signs out every account already holding one.
    /// </remarks>
    private const string CookieName = "__Host-budgetoid-session";

    /// <summary>How many codes a card holds. Restated rather than read off the handler.</summary>
    private const int RequiredCodeCount = RegistrationCeremony.RequiredCodeCount;

    /// <summary>The exact width of a verifier, decoded — <c>RecoveryCodeHash.VerifierLength</c>.</summary>
    private const int VerifierLength = RegistrationCeremony.VerifierLength;

    /// <summary>
    /// The exact width of a WebAuthn user handle, decoded: the sixteen raw bytes of a uuid.
    /// </summary>
    /// <remarks>
    /// Written out rather than measured from <c>PasskeyEncoding.ToUserHandle</c> of some value the test
    /// already holds. A comparison against a width the code under test produced agrees with whatever width
    /// that code later produces, and the whole point of asserting it here is that a substituted handle is
    /// very likely the thirty-two bytes a challenge is minted at.
    /// </remarks>
    private const int UserHandleLength = 16;

    /// <summary>
    /// The three spellings <c>credentials.type</c> holds, as the column stores them.
    /// </summary>
    /// <remarks>
    /// Restated rather than read off <c>CredentialTypeSpelling</c>, which is the definition under test
    /// wherever these appear: a test taking its expectation from that type agrees with whatever it later
    /// decides a passkey is called, and these words are also the vocabulary
    /// <c>CK_sessions_kind_matches_credential</c> is written in.
    /// </remarks>
    private const string PasskeyType = "passkey";

    private const string FederatedType = "federated";

    private const string RecoveryCodesType = "recovery_codes";

    /// <summary>
    /// The two relations no account owns: reference data the migration seeds, and EF's own bookkeeping.
    /// </summary>
    /// <remarks>
    /// They are what tells a live measurement from an empty one. Every "nothing was written" assertion
    /// below reads every discovered relation, and a discovery that came back with nothing would satisfy
    /// such an assertion perfectly — so these two are asserted <b>non-zero</b> in the same breath.
    /// </remarks>
    private static readonly string[] TablesNoAccountOwns =
    [
        "currencies",
        "__EFMigrationsHistory",
    ];

    /// <summary>
    /// What one completed registration leaves behind, table by table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Eleven <c>wrapped_account_keys</c> rows is the number a reader will try to correct to two.</b>
    /// A factor is not a credential: the passkey is one factor and the card is <b>ten</b>, because each
    /// code derives its own key-encryption key and a person redeems whichever one they still hold. A
    /// single envelope pair for the whole card would seal the account under one code and leave the other
    /// nine unlocking nothing — with a session handed over either way, so nothing goes red until a
    /// browser months later. See <c>docs/business-logic/account-keys.md</c> and ADR 0018.
    /// </para>
    /// <para>
    /// Three <c>credentials</c> for the same reason read the other way: the federated one the provider
    /// gated on, the passkey, and one row for the whole card.
    /// </para>
    /// <para>
    /// <c>webauthn_challenges</c> is here at zero rather than left out. The nonce the ceremony opened on
    /// is single-use, and a finish leg that answered without spending it is a registration anybody
    /// holding the transcript can run again — under the same derived account id, which is the one value
    /// a replay must not be able to reach.
    /// </para>
    /// </remarks>
    private static readonly KeyValuePair<string, long>[] RowsOneRegistrationWrites =
    [
        new("users", 1),
        new("budgets", 1),
        new("credentials", 3),
        new("passkey_public_keys", 1),
        new("passkey_signature_counters", 1),
        new("recovery_code_hashes", RequiredCodeCount),
        new("wrapped_account_keys", RequiredCodeCount + 1),
        new("sessions", 1),
        new("session_tokens", 1),
        new("webauthn_challenges", 0),
    ];

    /// <summary>
    /// CON-012, FR-102 on the options leg: no provider token, no ceremony.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cheapest half of the gate and the one worth stating separately. The options leg mints the
    /// challenge the account identifier is derived from, so a leg reachable without a token hands an
    /// anonymous caller the ability to choose which account identifiers exist — before any of the
    /// finish leg's own checks are in play.
    /// </para>
    /// <para>
    /// <b>The provable-fail control beside it is not optional here, it is the whole test.</b> This
    /// application answers <b>401</b> for a path that does not exist: the fallback policy carries
    /// <c>RequireAuthenticatedUser</c> and <c>AuthorizationMiddleware</c> applies it to a request that
    /// matched no endpoint at all. So "the anonymous caller was refused" is satisfied perfectly by a
    /// route nobody ever wrote, and without the second half this test is green against an empty
    /// <c>Program.cs</c>. The control is what says the leg exists and answers somebody.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RegistrationOptions_WithNoProviderToken_IsRefused()
    {
        // Arrange — one client carrying the first-party header the factory adds and no credential of
        // any kind, which is what a browser that never signed in presents, and one carrying a provider
        // token for an account that does not exist yet.
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient anonymous = factory.CreateClient();
        HttpClient bearer = factory.CreateAuthenticatedClient(Subject, Email);

        // Act
        HttpResponseMessage refused = await anonymous.PostAsync(OptionsPath, content: null);
        HttpResponseMessage admitted = await bearer.PostAsync(OptionsPath, content: null);

        // Assert
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// CON-012, FR-102 on the finish leg: no provider token, no account, and not one row anywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The status alone is not the claim.</b> The finish leg writes roughly thirty rows across nine
    /// relations, and a handler that refused the response while having already committed part of that —
    /// the user, the budget, the challenge's consumption — would answer exactly this 401. So the whole
    /// database is counted afterwards, discovered rather than listed, and every relation an account can
    /// own has to read zero.
    /// </para>
    /// <para>
    /// The body is a genuine attestation over a challenge this server never issued. That is the shape a
    /// caller who obtained a transcript would send, and it means the refusal below cannot be the
    /// framework declining to bind a malformed payload.
    /// </para>
    /// <para>
    /// <b>The control at the end is what keeps this from being green against a route that does not
    /// exist.</b> A path nobody wrote answers 401 here — see
    /// <see cref="RegistrationOptions_WithNoProviderToken_IsRefused" /> for why — and writes nothing,
    /// which satisfies both halves above on its own. It runs <em>after</em> the count, so the count is
    /// taken against a database the arrangement has not touched.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WithNoProviderToken_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient anonymous = factory.CreateClient();
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        HttpResponseMessage options = await anonymous.PostAsync(OptionsPath, content: null);
        HttpResponseMessage finish = await PostRegistrationAsync(
            anonymous,
            device.Register(UnissuedChallenge(), ApiFactory.PasskeyOrigin, prfEnabled: true),
            WrappedKeyFixture.Mint(),
            CardOf(Verifiers()));

        // Assert
        await Assert.That(options.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(finish.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await AssertNoAccountRowAnywhereAsync(host);

        // Act, again — the same ceremony with a provider token on it, which is the only thing that
        // differs between this request and the one refused above.
        RegisteredAccount admitted = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(Subject, Email),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        // Assert — the leg exists, and the token is what it turned on.
        await Assert.That(admitted.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    /// <summary>
    /// FR-108: one completed registration, thirty-odd rows, one <c>SaveChanges</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The counts are exact and the enumeration is whole.</b> Nine relations are named with the
    /// number each must hold, and every other discovered relation has to read zero — so a table added
    /// later and written here without being thought about fails this test rather than sliding past it.
    /// </para>
    /// <para>
    /// <b>Driven over the real least-privilege connection, which is where the two silent failures
    /// live.</b> This is the test that dies with <c>22P02</c> if anybody wraps the write in a
    /// transaction — a transaction opened before the identity is published configures the connection
    /// while <c>app.current_user_id</c> is still empty, and every policed statement inside it then
    /// meets <c>''::uuid</c> — and with <c>42501</c> if a relation is missing its grant.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WhenComplete_WritesEveryRowInOneTransaction()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        RegisteredAccount registered = await RegisterAccountAsync(client, device);

        // Assert — the status first, so counts that came back short read as the refusal they are
        // rather than as a write that half happened.
        await Assert.That(registered.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        IReadOnlyDictionary<string, long> counts = await CountEveryRelationAsync(host);

        // The named relations, each by value.
        await Assert.That(DescribeDrift(RowsOneRegistrationWrites, counts)).IsEmpty();

        // And nothing anywhere else. Written this way round so a relation added later is covered by
        // this test on the day it appears rather than on the day somebody remembers it.
        await Assert.That(NamesOf(counts.Where(relation =>
                relation.Value != 0
                && !TablesNoAccountOwns.Contains(relation.Key, StringComparer.Ordinal)
                && !RowsOneRegistrationWrites.Any(expected =>
                    string.Equals(expected.Key, relation.Key, StringComparison.Ordinal)))))
            .IsEmpty();

        // The non-vacuity guard: the discovery found relations at all, and the two nobody owns are
        // still populated. Without it an enumeration that stopped matching would make every claim
        // above true of an empty dictionary.
        await Assert.That(counts).IsNotEmpty();
        await Assert.That(NamesOf(TablesNoAccountOwns
                .Select(name => KeyValuePair.Create(name, counts.GetValueOrDefault(name)))
                .Where(relation => relation.Value == 0)))
            .IsEmpty();
    }

    /// <summary>
    /// FR-001: the one budget registration writes belongs to the account it just created, and it is
    /// nameless and currency-less.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shape is the subject, and <see cref="RowsOneRegistrationWrites" /> cannot say a word about
    /// it.</b> That census pins <c>budgets</c> at one row; a row filed under a stranger's
    /// <c>user_id</c>, or one arriving pre-named in a currency nobody chose, satisfies it exactly. Two
    /// nulls are the whole of what a person never has to answer for: a user never encounters "budget" as
    /// something to create, so a default name or a guessed base currency would be the product deciding
    /// something on their behalf and then showing it to them as a fact.
    /// </para>
    /// <para>
    /// <b>It arrived here from a deleted <c>BudgetProvisioningTests</c>, and the premise had to move
    /// with it.</b> There, the budget was written by the first authenticated request a subject ever
    /// made — the provisioning middleware's find-or-create — and the test drove
    /// <c>GET /api/accounts</c> to trigger it. That path is gone: an account and its budget now come
    /// into existence on one route, in one <c>SaveChanges</c>, and no request mints anything. Its
    /// sibling asserting that repeated sign-ins add no further budgets went with the middleware, because
    /// nothing re-runs provisioning to add one.
    /// </para>
    /// <para>
    /// Read on the container superuser like every other read in this file: <c>user_isolation</c> is
    /// <c>FOR ALL</c>, and a policed connection reports another account's row exactly as it reports a
    /// missing one — which would turn "the budget belongs to somebody else" into "there is no budget".
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_CreatesExactlyOneBudget()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        RegisteredAccount registered = await RegisterAccountAsync(client, device);

        // Assert — the status first, so a budget that is missing reads as the refusal it is rather than
        // as a write that half happened.
        await Assert.That(registered.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        BudgetRow budget = await SoleBudgetAsync(host);
        await Assert.That(budget.UserId).IsEqualTo(registered.AccountId);
        await Assert.That(budget.Name).IsNull();
        await Assert.That(budget.BaseCurrencyCode).IsNull();
    }

    /// <summary>
    /// FR-087, FR-088: registration signs the person in, on a full session, in the cookie the next
    /// request presents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The cookie and the row are asserted against each other rather than each on its own.</b> A
    /// handle minted correctly and written into the cookie in a form the reading half cannot use — the
    /// wrong encoding, the wrong width, an escaped character — leaves a perfectly good
    /// <c>session_tokens</c> row and a browser that is signed out on its very next request. The digest
    /// of the cookie's value has to be the digest the row stores, and it is computed here with
    /// <see cref="SHA256" /> rather than through the production hasher, so both spellings moving
    /// together is a failure rather than an agreement.
    /// </para>
    /// <para>
    /// <b><c>full</c>, and never <c>locked</c>.</b> The account was reached by a passkey the caller
    /// proved possession of moments ago; a locked session here would hand somebody a brand-new account
    /// they cannot read the contents of.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_EstablishesOneFullSessionAndSetsItsCookie()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        RegisteredAccount registered = await RegisterAccountAsync(client, device);

        // Assert
        await Assert.That(registered.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        JsonObject body = await ReadJsonObjectAsync(registered.Response);
        await Assert.That(body["session"]!["kind"]!.GetValue<string>()).IsEqualTo("full");

        string cookieValue = SessionCookieValueOf(registered.Response);
        await Assert.That(cookieValue).DoesNotContain("=");

        SessionRow session = await SoleSessionAsync(host);
        await Assert.That(session.Kind).IsEqualTo("full");
        await Assert.That(session.UserId).IsEqualTo(registered.AccountId);

        SessionTokenRow handle = await SoleSessionTokenAsync(host);
        await Assert.That(handle.SessionId).IsEqualTo(session.Id);
        await Assert.That(handle.UserId).IsEqualTo(registered.AccountId);
        await Assert.That(handle.TokenHash).IsEqualTo(DigestOf(cookieValue));
    }

    /// <summary>
    /// The response says a session was opened and hands over no name for anything it created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two questions, and neither answers the other.</b> The member set is compared whole against a
    /// written-out expectation, because a containment check over member names can never fail — every
    /// widening leaves the expected members present. The payload search beside it is the second
    /// question: what must not appear is a <em>value</em>, which no member list can rule out.
    /// </para>
    /// <para>
    /// <b>The factor identifiers are in the search and they are the easiest to leak back.</b> The client
    /// minted them and sent them, so echoing them looks like a courtesy — and it publishes, to anything
    /// that can read this response, the associated data both envelopes of every one of eleven factors
    /// were sealed with.
    /// </para>
    /// <para>
    /// The email is searched for too. It arrived on a provider token this response is answering, so it
    /// discloses nothing to the caller — but a registration response that carries it is one more copy of
    /// an address in a log, a proxy cache and a browser's network panel, for a member no client needs.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_AnswersNoAccountIdCredentialIdSessionIdOrFactorId()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        RegisteredAccount registered = await RegisterAccountAsync(client, device);

        // Assert
        await Assert.That(registered.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // No Location either: it would name the account resource, which is the identifier this whole
        // test is about, in a header a body census walks straight past.
        await Assert.That(registered.Response.Headers.Location?.ToString() ?? "<none>").IsEqualTo("<none>");

        string payload = await registered.Response.Content.ReadAsStringAsync();
        JsonObject body = JsonNode.Parse(payload)!.AsObject();

        // Ordered before joining, so a member added later produces the same message whichever order the
        // serializer emitted it in.
        await Assert.That(MembersOf(body)).IsEqualTo("session");
        await Assert.That(MembersOf(body["session"]!.AsObject())).IsEqualTo("expiresAtUtc, kind");

        // Every identifier this request brought into existence, in both spellings a Guid renders in,
        // plus the two values that were not the server's to give back.
        List<string> forbidden =
        [
            .. IdentifiersOf(registered.AccountId),
            .. IdentifiersOf((await SoleSessionAsync(host)).Id),
            .. (await CredentialIdsAsync(host)).SelectMany(IdentifiersOf),
            .. (await FactorIdsAsync(host)).SelectMany(IdentifiersOf),
            registered.PasskeyKeys.FactorId,
            SessionCookieValueOf(registered.Response),
            Email,
        ];

        await Assert.That(forbidden.Where(value => payload.Contains(value, StringComparison.OrdinalIgnoreCase)))
            .IsEmpty();
    }

    /// <summary>
    /// The whole reason the account identifier is derived: the passkey registered here signs in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the test that fails when the FINISH leg draws <c>users.id</c> fresh instead of deriving
    /// it.</b> <c>CompleteAssertionHandler</c> compares the handle a device presents byte for byte against
    /// <c>PasskeyEncoding.ToUserHandle(userId)</c> on every later sign-in, so an account row written under
    /// an identifier the ceremony did not derive answers no assertion this test presents. That failure is
    /// silent and permanent in production, which is why it is driven end to end rather than asserted as an
    /// equality between two values inside one process.
    /// </para>
    /// <para>
    /// <b>The handle is derived from the challenge here rather than read back out of the database</b>,
    /// and that is the discrimination it does draw. Reading <c>users.id</c> and signing over it would
    /// agree with whatever identifier the finish leg happened to write, including a fresh one — the
    /// assertion would verify and this test would pass while every real device was locked out.
    /// </para>
    /// <para>
    /// <b>And this test says nothing whatever about the OPTIONS leg, which is where the same defect is
    /// likelier to land.</b> Deriving the handle here means it never travels from the options response to
    /// the sign-in, so <c>BeginAccountRegistrationHandler</c> may hand the authenticator any bytes at all
    /// and this test stays green — measured, by substituting thirty-two random bytes for the derived
    /// <c>user.id</c>: every test in the suite passed, this one included. A real device would have been
    /// locked out of the account from its first sign-in. That half is held by
    /// <see cref="RegistrationOptions_NameTheAccountTheirOwnChallengeDerives" /> and by
    /// <see cref="Registration_ThenAssertion_PresentsTheHandleTheDeviceWasGiven" />, which is this
    /// ceremony run again with the device presenting the handle it was actually issued.
    /// </para>
    /// <para>
    /// The sign-in runs on a client carrying no provider token at all, which is the state a later
    /// sign-in actually arrives in.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_ThenAssertion_SignsInWithTheSamePasskey()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        RegisteredAccount registered = await RegisterAccountAsync(client, device);
        await Assert.That(registered.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Act — the ordinary sign-in path, anonymous, presenting the handle the ceremony was run under.
        HttpClient anonymous = factory.CreateClient();
        byte[] challenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(registered.AccountId),

            // The device registered reporting zero, which is what an authenticator backing a synced
            // passkey does; signing at zero again keeps the clone detector out of this test.
            signCount: 0);
        HttpResponseMessage signIn = await anonymous.PostAsJsonAsync(AssertionPath, new
        {
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });

        // Assert
        await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await ReadJsonObjectAsync(signIn))["kind"]!.GetValue<string>()).IsEqualTo("full");

        // Two sessions on one account: the one registration opened, and the one this sign-in did.
        await Assert.That(await CountAsync(host, "select count(*) from sessions")).IsEqualTo(2L);
        await Assert.That(await CountAsync(host, "select count(*) from users")).IsEqualTo(1L);
    }

    /// <summary>
    /// The options leg names the account its <b>own</b> challenge derives, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the cheap half of a binding nothing held.</b> The finish leg derives the account
    /// identifier from the challenge and writes the row under it; the options leg derives the same value
    /// and hands it to the authenticator as <c>user.id</c>. Only the second is under test here, and it was
    /// the one nothing looked at: replacing <see cref="BeginAccountRegistrationHandler" />'s derived
    /// <c>user.id</c> with thirty-two random bytes left the entire suite green, this file's own end-to-end
    /// sign-in included.
    /// </para>
    /// <para>
    /// <b>The challenge is taken from the response under test</b>, not from a second call. A test that
    /// began one ceremony to learn the challenge and another to read the handle would compare two
    /// unrelated nonces and could only ever fail. The derivation runs here rather than being read back
    /// from anywhere, so the claim is "this response's handle is this response's challenge derived",
    /// which is a fact about one request.
    /// </para>
    /// <para>
    /// <b>The width is asserted separately from the value</b>, because it is the thing a failure explains.
    /// A user handle is the sixteen raw bytes of a uuid; a mutation that hands out fresh random material
    /// almost certainly hands out the thirty-two bytes a challenge is minted at, and "the handle is 32
    /// bytes wide" says what happened where "the bytes differ" only says that they do.
    /// </para>
    /// <para>
    /// <b>The second ceremony is the discrimination, not decoration.</b> Everything above holds for a
    /// handle derived from the account's address, or from any other value that is constant across the two
    /// calls. Two options legs on one client issue two nonces and must therefore name two different
    /// accounts — an account identifier that repeated across ceremonies would collide on <c>PK_users</c>
    /// the second time anybody registered.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RegistrationOptions_NameTheAccountTheirOwnChallengeDerives()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);

        // Act
        IssuedRegistrationOptions first = await BeginRegistrationCeremonyAsync(client);
        IssuedRegistrationOptions second = await BeginRegistrationCeremonyAsync(client);

        // Assert — the width first, so a handle of the wrong size reads as the substitution it is.
        await Assert.That(first.UserHandle.Length).IsEqualTo(UserHandleLength);

        await Assert.That(Base64UrlText.Encode(first.UserHandle))
            .IsEqualTo(Base64UrlText.Encode(
                PasskeyEncoding.ToUserHandle(RegistrationAccountId.For(first.Challenge))));

        // And a second ceremony names a second account, so nothing constant can satisfy the line above.
        await Assert.That(Base64UrlText.Encode(second.Challenge))
            .IsNotEqualTo(Base64UrlText.Encode(first.Challenge));
        await Assert.That(Base64UrlText.Encode(second.UserHandle))
            .IsNotEqualTo(Base64UrlText.Encode(first.UserHandle));
        await Assert.That(Base64UrlText.Encode(second.UserHandle))
            .IsEqualTo(Base64UrlText.Encode(
                PasskeyEncoding.ToUserHandle(RegistrationAccountId.For(second.Challenge))));
    }

    /// <summary>
    /// The device signs in with the handle <b>it was given</b>, which is the binding the derivation
    /// exists for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read this beside
    /// <see cref="Registration_ThenAssertion_SignsInWithTheSamePasskey" />, which cannot see what this
    /// sees.</b> That test derives the handle itself, from the challenge, and hands it to the
    /// authenticator at sign-in time. Both legs of the ceremony derive from the same challenge, so the
    /// value it presents agrees with the account row whatever the options leg put in <c>user.id</c> — and
    /// a <see cref="BeginAccountRegistrationHandler" /> handing out random bytes leaves it perfectly
    /// green. The handle is never carried from the options response to the assertion, so the one link
    /// this product's silent-lockout failure lives on is not in that test at all.
    /// </para>
    /// <para>
    /// <b>What closes it is that the device keeps the handle.</b>
    /// <see cref="SyntheticAuthenticator.StoredUserHandle" /> holds <c>publicKey.user.id</c> exactly as
    /// enrolled, the way a platform authenticator does, and the sign-in below presents that value and no
    /// other. An options leg that named a different account therefore produces an assertion
    /// <c>CompleteAssertionHandler.RequireMatchingUserHandle</c> refuses — which is what a real device
    /// would produce, permanently, on every sign-in it ever attempted.
    /// </para>
    /// <para>
    /// <b>The presented handle is asserted to be present before the status is.</b> That comparison is
    /// skipped entirely when a response carries no handle — a conforming authenticator may omit one, so
    /// absence is tolerated by design — which means an assertion that quietly stopped carrying one would
    /// answer 200 while exercising nothing. This is the assertion that keeps the 200 below meaning
    /// something.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_ThenAssertion_PresentsTheHandleTheDeviceWasGiven()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        RegisteredAccount registered = await RegisterAccountAsync(client, device);
        await Assert.That(registered.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Act — an ordinary anonymous sign-in presenting the handle this device kept at enrolment, and
        // no value this test computed.
        HttpClient anonymous = factory.CreateClient();
        byte[] challenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            device.StoredUserHandle,
            signCount: 0);
        HttpResponseMessage signIn = await anonymous.PostAsJsonAsync(AssertionPath, new
        {
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });

        // Assert — the request carried a handle at all, first, because the comparison this test exists
        // for is skipped entirely when it does not.
        await Assert.That(assertion.UserHandleBase64Url ?? "<none>").IsNotEqualTo("<none>");

        // Then the consequence, which is what a person would meet: the device signs in.
        await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await ReadJsonObjectAsync(signIn))["kind"]!.GetValue<string>()).IsEqualTo("full");

        // Then the cause, as a diagnostic: the handle the device kept names the account that was written.
        await Assert.That(assertion.UserHandleBase64Url)
            .IsEqualTo(Base64UrlText.Encode(PasskeyEncoding.ToUserHandle(registered.AccountId)));
    }

    /// <summary>
    /// The session registration opens stands on the <b>passkey</b> credential, never the card.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both credentials open a <c>full</c> session, which is why nothing else here can see this.</b>
    /// <c>CK_sessions_kind_matches_credential</c> admits <c>passkey</c> and <c>recovery_codes</c> alike,
    /// the composite foreign key is satisfied either way because both rows belong to this account, and the
    /// response says only <c>kind</c>. Establishing the session over the recovery-codes credential
    /// therefore answers 201, writes the same thirty-odd rows, sets the same cookie and passes every other
    /// test in this file.
    /// </para>
    /// <para>
    /// <b>What it changes is which credential a later revocation sweeps.</b> Revoking the passkey would
    /// leave the session standing — the person is told the device no longer opens the account and their
    /// browser goes on reading it — and replacing the card would sign somebody out of a session their
    /// passkey opened. <c>RegisterAccountHandler</c>'s own remarks state the rule; until now nothing held
    /// it.
    /// </para>
    /// <para>
    /// <b>The credential's own <c>type</c> is read, and it is read through the id the session names.</b>
    /// Not the ordering of <c>credentials.id</c>, which is a <see cref="Guid" /> nothing sorts
    /// meaningfully, and not the <c>credential_type</c> copy on <c>sessions</c> alone: that copy is
    /// written by the same line that chooses the credential, so a session filed against the wrong row
    /// carries a copy that agrees with it perfectly. Joining to <c>credentials</c> is what makes this an
    /// assertion about which row was chosen.
    /// </para>
    /// <para>
    /// <b>The account really does hold all three credentials.</b> Without that line the claim is satisfied
    /// by a registration that never wrote a recovery-codes credential at all — there would be nothing to
    /// have got wrong — and the count is the provable-fail control that says the wrong answer was
    /// available.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_OpensTheSessionOverThePasskeyCredential()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        RegisteredAccount registered = await RegisterAccountAsync(client, device);

        // Assert
        await Assert.That(registered.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        SessionRow session = await SoleSessionAsync(host);
        await Assert.That(await CredentialTypeOfAsync(host, session.CredentialId)).IsEqualTo(PasskeyType);

        // The copy the CHECK constraint reads agrees with its source, which is the other half of the
        // claim: a session whose two columns disagreed would name the passkey while being checked as
        // something else.
        await Assert.That(session.CredentialType).IsEqualTo(PasskeyType);

        // The control: the account holds a recovery-codes credential and a federated one, so the wrong
        // answer was there to be given.
        await Assert.That(await CredentialTypesAsync(host))
            .IsEquivalentTo(new[] { FederatedType, PasskeyType, RecoveryCodesType });
    }

    /// <summary>
    /// An address the provider will not vouch for registers nothing — and this is where the claim gate's
    /// <b>reach</b> over these two routes is pinned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read this before moving <see cref="RegistrationClaimGate" /> off the registration group.</b>
    /// The gate used to be an arm inside a provisioning middleware, and what this test pinned was that
    /// arm's <em>position</em> — below the <c>sub</c>/<c>email</c> and <c>email_verified</c> gates,
    /// above the resolve — because grouping all three metadata arms together, which reads like the
    /// tidier arrangement, took a registration route out from under them entirely. There is no
    /// middleware and no marker now, so there is no ordering left to get wrong; what is left to get
    /// wrong is the <em>attachment</em>. The gate is an endpoint filter registered on the group beside
    /// its <c>RequireAuthorization</c>, and a group that stops calling <c>AddEndpointFilter</c> creates
    /// this account for a caller whose address the provider explicitly declines to assert, with nothing
    /// else in the pipeline left to stop it. The options leg is asserted first for exactly that reason:
    /// it is the leg a lost gate makes reachable, and it goes red on its own.
    /// </para>
    /// <para>
    /// The refusal is checked by <b>title</b> and not by status. Every gate on this path answers 401, so a
    /// status comparison cannot tell "the provider did not verify this address" from "the route's policy
    /// declined the principal" — and the second is what a fixture whose scheme repointing stopped working
    /// produces, which would leave this test green while measuring nothing about the claim.
    /// </para>
    /// <para>
    /// The finish leg answers over a challenge this server never issued, because the options leg it would
    /// otherwise have come from is refused. It is driven anyway: what is asserted after it is that the
    /// database is untouched, and a leg never called cannot show that.
    /// </para>
    /// <para>
    /// <b>The control at the end is the same one every refusal test here carries</b>, and it is what
    /// keeps the two assertions above from being satisfied by a route that does not work for anybody. A
    /// fixture whose scheme repointing has stopped working answers 401 on both legs for every principal
    /// alike; so does a group that has lost its policy, or a ceremony driver that has drifted from the
    /// wire format. Each of those makes this test green while proving nothing about the claim. It runs
    /// after the count, on a second subject, so the count is taken on an untouched database.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WithAnUnverifiedEmail_IsRefusedAndWritesNothing()
    {
        // Arrange — a principal the provider names and whose address it reports as not verified.
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClientWithUnverifiedEmail(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        HttpResponseMessage options = await client.PostAsync(OptionsPath, content: null);
        HttpResponseMessage finish = await PostRegistrationAsync(
            client,
            device.Register(UnissuedChallenge(), ApiFactory.PasskeyOrigin, prfEnabled: true),
            WrappedKeyFixture.Mint(),
            CardOf(Verifiers()));

        // Assert
        await Assert.That(options.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await TitleOfAsync(options))
            .IsEqualTo(RegistrationClaimGate.UnverifiedEmailTitle);
        await Assert.That(finish.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await TitleOfAsync(finish))
            .IsEqualTo(RegistrationClaimGate.UnverifiedEmailTitle);
        await AssertNoAccountRowAnywhereAsync(host);

        // Act, again — the same ceremony for a principal whose address the provider does vouch for.
        RegisteredAccount admitted = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(VerifiedSubject, VerifiedEmail),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        // Assert — the two legs exist, and the verified claim is what they turned on.
        await Assert.That(admitted.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    /// <summary>
    /// FR-105, the first of four shapes of silence: the client reported no extension results at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Four arrangements and not one parameterised test, because the four are four different
    /// claims.</b> No <c>clientExtensionResults</c> member is a client that never asked the authenticator
    /// for PRF; a present object with no <c>prf</c> is a client that asked for something else; a
    /// <c>prf</c> of <c>null</c> is a client that asked and reported nothing back; and
    /// <c>prf.enabled: false</c> is a device that answered <b>no</b>. A single case would prove the gate
    /// refuses one of them, and the gate is a null-and-false pattern — <c>is not { Enabled: true }</c> —
    /// where any one of the four could be admitted by a rewrite that still refused the other three.
    /// </para>
    /// <para>
    /// <b>The nonce is live and this request spends it</b>, which is what puts the refusal at the prf gate
    /// rather than at the challenge. A body posted over a challenge this server never issued would be
    /// refused at rung 4 and would say nothing about the extension at all.
    /// </para>
    /// <para>
    /// The refusal is read by its <b>key and its sentence</b>, not by its status: every rung of this
    /// ladder from 1 to 11 answers 400, so a status comparison cannot tell the prf gate from a malformed
    /// envelope, and the ladder's order is exactly what an integration test cannot otherwise see.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WhenTheClientReportsNoExtensionResults_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act — the member is removed rather than set to null: an absent member and a present null are
        // two different things a client can say, and this is the first.
        HttpResponseMessage refused = await PostAmendedRegistrationAsync(
            client,
            device,
            body => body.Remove("clientExtensionResults"));

        // Assert
        await AssertRefusedByThePrfGateAsync(refused);
        await AssertNoAccountRowAnywhereAsync(host);
    }

    /// <summary>
    /// FR-105, the second shape: extension results arrived and carry no <c>prf</c> member.
    /// </summary>
    /// <remarks>See <see cref="Registration_WhenTheClientReportsNoExtensionResults_IsRefusedAndWritesNothing" />.</remarks>
    [Test]
    public async Task Registration_WhenTheExtensionResultsCarryNoPrf_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act — an empty object, which is what a client that ran the ceremony asking for some other
        // extension reports.
        HttpResponseMessage refused = await PostAmendedRegistrationAsync(
            client,
            device,
            body => body["clientExtensionResults"] = new { });

        // Assert
        await AssertRefusedByThePrfGateAsync(refused);
        await AssertNoAccountRowAnywhereAsync(host);
    }

    /// <summary>
    /// FR-105, the third shape: <c>prf</c> is present and null.
    /// </summary>
    /// <remarks>See <see cref="Registration_WhenTheClientReportsNoExtensionResults_IsRefusedAndWritesNothing" />.</remarks>
    [Test]
    public async Task Registration_WhenPrfIsNull_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act — a dictionary rather than an anonymous type, because a member has to be written as a JSON
        // null and an anonymous type carrying a null reference would serialize the same way only by
        // accident of the serializer's settings.
        HttpResponseMessage refused = await PostAmendedRegistrationAsync(
            client,
            device,
            body => body["clientExtensionResults"] =
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["prf"] = null });

        // Assert
        await AssertRefusedByThePrfGateAsync(refused);
        await AssertNoAccountRowAnywhereAsync(host);
    }

    /// <summary>
    /// FR-105, the fourth shape and the only one that is an answer rather than a silence: the device
    /// reported <c>prf.enabled: false</c>.
    /// </summary>
    /// <remarks>
    /// The sentence the gate answers with says the device "did not report an enabled prf extension
    /// result" rather than that it "returned no prf extension result", because this case <b>is</b> a
    /// returned result — one that means no. That wording is a literal transcription of the predicate and
    /// is therefore true of all four shapes rather than of three of them.
    /// </remarks>
    [Test]
    public async Task Registration_WhenTheDeviceReportsPrfDisabled_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        HttpResponseMessage refused = await PostAmendedRegistrationAsync(
            client,
            device,
            body => body["clientExtensionResults"] = new { prf = new { enabled = false } });

        // Assert
        await AssertRefusedByThePrfGateAsync(refused);
        await AssertNoAccountRowAnywhereAsync(host);
    }

    /// <summary>
    /// FR-107: a card of nine codes is not a card, and the account is not created for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Too few is a person left with fewer ways back into their account than the screen told them they
    /// had — and on this route the card is the only way back that survives losing the device the passkey
    /// lives on, so a short card accepted here is an account one lost laptop away from being unreachable.
    /// </para>
    /// <para>
    /// <b>The refusal is read under the member's own key.</b> The route carries the card on <c>codes</c>
    /// and the command names it <c>Codes</c>, which is the member a client has to correct; a test reading
    /// only "some sentence arrived" would stay green through a refusal keyed on a member this request
    /// does not have.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WithNineCodes_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        HttpResponseMessage refused = await PostAmendedRegistrationAsync(
            client,
            device,
            body => body[CodesMember] = SubmissionsOf(CardOf(Verifiers(RequiredCodeCount - 1))));

        // Assert
        await AssertRefusedForTheCardsSizeAsync(refused);
        await AssertNoAccountRowAnywhereAsync(host);
    }

    /// <summary>
    /// FR-107: eleven codes is a client this server no longer agrees with about what a card is.
    /// </summary>
    /// <remarks>
    /// The mirror of the nine, and it is not the same test twice. Too many is the direction a reader is
    /// tempted to wave through — an extra code is one more way back, which sounds harmless — and it is a
    /// person holding a printed card whose entries outnumber the codes the account will ever accept.
    /// </remarks>
    [Test]
    public async Task Registration_WithElevenCodes_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        HttpResponseMessage refused = await PostAmendedRegistrationAsync(
            client,
            device,
            body => body[CodesMember] = SubmissionsOf(CardOf(Verifiers(RequiredCodeCount + 1))));

        // Assert
        await AssertRefusedForTheCardsSizeAsync(refused);
        await AssertNoAccountRowAnywhereAsync(host);
    }

    /// <summary>
    /// FR-107: an empty array is the shape a handler is likeliest to read as "nothing to do".
    /// </summary>
    /// <remarks>
    /// Stated separately from the nine and the eleven because it is the one a count check written as
    /// <c>Count &gt; RequiredCodeCount</c>, or as a loop over what arrived, lets through — and what it
    /// would let through here is an account created with a passkey and no way back at all.
    /// </remarks>
    [Test]
    public async Task Registration_WithAnEmptyCard_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        HttpResponseMessage refused = await PostAmendedRegistrationAsync(
            client,
            device,
            body => body[CodesMember] = Array.Empty<object>());

        // Assert
        await AssertRefusedForTheCardsSizeAsync(refused);
        await AssertNoAccountRowAnywhereAsync(host);
    }

    /// <summary>
    /// FR-107: no <c>codes</c> member at all, which is a set of the wrong size rather than a fault.
    /// </summary>
    /// <remarks>
    /// <b>This is the case the request record's non-<c>required</c> members exist for.</b> An absent
    /// member binds to <see langword="null" /> despite the non-nullable declaration and reaches the
    /// handler's own refusal, worded for a person holding a device; declared <c>required</c> it would
    /// earn a framework 400 raised before anything signed had been judged, and — worse on this route —
    /// before the prf gate, so a client whose authenticator genuinely cannot do PRF would be told its
    /// payload was malformed.
    /// </remarks>
    [Test]
    public async Task Registration_WithNoCodesMember_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        HttpResponseMessage refused = await PostAmendedRegistrationAsync(
            client,
            device,
            body => body.Remove(CodesMember));

        // Assert
        await AssertRefusedForTheCardsSizeAsync(refused);
        await AssertNoAccountRowAnywhereAsync(host);
    }

    /// <summary>
    /// FR-104, ASM-013: one provider identity, one account, and a second attempt changes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same subject with a different address, so the subject is the only thing that collides.</b>
    /// Re-sending the first registration whole breaches <c>IX_credentials_provider_subject</c> and
    /// <c>IX_users_email</c> at once, PostgreSQL names one of them by write order, and the answer is the
    /// same sentence either way — which makes it exactly the arrangement that cannot show the subject
    /// rule is being enforced at all.
    /// </para>
    /// <para>
    /// <b>The nonce is taken as a stranger and spent as the registered subject, and that is the
    /// arrangement rather than a way around one.</b> The options leg now refuses a subject that already
    /// has an account — see
    /// <see cref="SecondOptionsRequest_ForASubjectThatAlreadyHasAnAccount_IsRefused" /> — so driving both
    /// legs as that subject never reaches the thing this test is about. Beginning as
    /// <see cref="OtherSubject" /> and finishing as <see cref="Subject" /> is literally the
    /// begin-then-finish race the finish leg's check exists to catch: options and finish are two
    /// requests, and an account can be created in the gap by another tab, another device or a retry
    /// already in flight. <b>Do not delete this test as covered by the options leg.</b> Since that leg
    /// refuses the common path, this test and its two neighbours below are the <em>only</em> things
    /// exercising the finish leg's subject conflict at all; the <c>OtherSubject</c> challenge is the
    /// point, not an accident.
    /// </para>
    /// <para>
    /// <b>The sentence is the assertion, not the status.</b> All four conflicts on this route answer 409
    /// under one title, so a status comparison cannot tell this from the email conflict next door — and
    /// the two are told apart by a re-read the handler performs, which is the piece with no other cover.
    /// </para>
    /// <para>
    /// <b>And no cookie.</b> The endpoint writes the session cookie only after the handler returns, so a
    /// refused registration must leave nothing on the client. A cookie written before the call would name
    /// a session that was never inserted, on the browser of whoever was guessing.
    /// </para>
    /// <para>
    /// <b>The re-read must not run, and that is the half no response can show.</b> The repository names
    /// <c>IX_credentials_provider_subject</c> and answers <c>SubjectTaken</c>, which is unambiguous, so
    /// <c>RegisterAccountHandler.RefusalFor</c> returns without asking anything. Widen the <c>EmailTaken</c>
    /// catch to claim that index as well and this conflict arrives as <c>EmailTaken</c> instead — whereupon
    /// the disambiguating re-read finds the winning credential and answers <b>this same sentence</b>. Every
    /// status, every body and every row count in this file is identical, which is exactly why the
    /// attribution had nothing holding it. The count is what separates "the repository knew" from "the
    /// caller worked it out", and the email conflict next door asserts the opposite number, so neither test
    /// can be satisfied by a counter that never moves.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WhenTheSubjectAlreadyHasAnAccount_Returns409AndChangesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        FederatedCredentialReadCounter reads = new();
        await using ApiFactory factory = CreateApiFactory(host, reads);
        RegisteredAccount first = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(Subject, Email),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await Assert.That(first.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Taken before the nonce is minted, so the challenge row the options leg writes and the finish
        // leg spends nets to nothing across the census.
        IReadOnlyDictionary<string, long> before = await CountEveryRelationAsync(host);

        // A ceremony opened by a subject nobody has registered, because the registered one is refused a
        // leg earlier now. This is the begin-then-finish race, not a detour around the conflict.
        IssuedRegistrationOptions ceremony = await BeginRegistrationCeremonyAsync(
            factory.CreateAuthenticatedClient(OtherSubject, OtherEmail));

        // A delta rather than an absolute, and read after the options leg: that leg asks the same port
        // the same question, so counting from any earlier point would measure two legs and pin neither.
        int readsBefore = reads.Count;

        // Act — the registered provider subject finishes the ceremony a stranger opened, on a fresh
        // device, eleven fresh factors and a fresh address, so the subject alone can collide.
        HttpResponseMessage refused = await RegisterOverAsync(
            factory.CreateAuthenticatedClient(Subject, OtherEmail),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId),
            ceremony.Challenge);

        // Assert
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        string detail = await DetailOfAsync(refused);
        await Assert.That(detail).IsEqualTo(SubjectConflictSentence);
        await Assert.That(detail).DoesNotContain(EmailConflictClause);
        await Assert.That(refused.Headers.Contains("Set-Cookie")).IsFalse();

        // The subject collision is reported as itself, so nothing had to be worked out.
        await Assert.That(reads.Count - readsBefore).IsEqualTo(0);
        await AssertNothingChangedAsync(host, before);
    }

    /// <summary>
    /// FR-103: an address already linked to another provider identity, and the sentence says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the test of the handler's disambiguating re-read.</b> A losing insert can breach the
    /// credential's <c>(provider, subject)</c> and the email at once and PostgreSQL names only one, so
    /// the repository's <c>EmailTaken</c> outcome is ambiguous: the credential index being named means the
    /// email did not collide, while the email index being named says nothing about the subject. The
    /// handler settles it by re-reading the federated credential, and finding none proves the email alone
    /// collided.
    /// </para>
    /// <para>
    /// The arrangement is therefore <b>a different subject with an already-taken address</b>, which is the
    /// only shape that reaches the "found none" branch — and the assertion is that the answer is the email
    /// sentence and not the subject one, because a re-read that was deleted, inverted or run against the
    /// wrong provider would answer the subject sentence to somebody who has no account.
    /// </para>
    /// <para>
    /// <b>The re-read is counted here, and it is the positive half of the pair.</b>
    /// <see cref="Registration_WhenTheSubjectAlreadyHasAnAccount_Returns409AndChangesNothing" /> asserts
    /// zero for a collision the repository can attribute on its own; this asserts exactly one, for the
    /// collision only the caller can settle. Neither number is interesting alone — a counter wired to
    /// nothing satisfies the zero, and a handler that re-read on every refusal satisfies the one — and
    /// together they say the two conflicts travel different paths, which no response body can.
    /// </para>
    /// <para>
    /// <b>The ceremony is driven a leg at a time so the counter can be read between them.</b> The options
    /// leg now asks the same port the same question before it mints a nonce, so a count taken before it
    /// sees two reads where only one belongs to the leg under test. Raising the expected number to two is
    /// the wrong repair: it would pin "two reads happen somewhere on this route" instead of "the finish
    /// leg re-reads", and it goes stale the moment either leg changes. Nothing else about the arrangement
    /// moved — one client still drives both legs, and it is a subject with no account, which is the
    /// caller the options leg still serves.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WhenTheEmailBelongsToAnotherAccount_Returns409()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        FederatedCredentialReadCounter reads = new();
        await using ApiFactory factory = CreateApiFactory(host, reads);
        RegisteredAccount first = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(Subject, Email),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await Assert.That(first.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        IReadOnlyDictionary<string, long> before = await CountEveryRelationAsync(host);

        // A provider identity nobody has registered, asserting an address somebody has. It opens the
        // ceremony itself — the options leg has nothing to refuse it for, since the address is not what
        // that leg may judge.
        HttpClient stranger = factory.CreateAuthenticatedClient(OtherSubject, Email);
        IssuedRegistrationOptions ceremony = await BeginRegistrationCeremonyAsync(stranger);

        // After the options leg, which now reads the same port: the delta must measure the finish leg's
        // disambiguating re-read and nothing else.
        int readsBefore = reads.Count;

        // Act
        HttpResponseMessage refused = await RegisterOverAsync(
            stranger,
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId),
            ceremony.Challenge);

        // Assert
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        string detail = await DetailOfAsync(refused);
        await Assert.That(detail).IsEqualTo(EmailConflictSentence);
        await Assert.That(detail).DoesNotContain(SubjectConflictClause);
        await Assert.That(refused.Headers.Contains("Set-Cookie")).IsFalse();

        // The ambiguous outcome was settled by asking, once.
        await Assert.That(reads.Count - readsBefore).IsEqualTo(1);
        await AssertNothingChangedAsync(host, before);
    }

    /// <summary>
    /// FR-103, FR-104: when the address <b>and</b> the subject both collide, the re-read finds the winner
    /// and the caller is told to sign in rather than sent looking for somebody else's Google account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it holds: the branch <c>RegisterAccountHandler.RefusalFor</c> spends a paragraph on, and
    /// the two conflict tests above cannot reach it.</b> Each of those stages exactly one collision —
    /// the subject alone, the address alone — and the first never reaches the <c>EmailTaken</c> arm at
    /// all while the second reaches it and finds nothing. This is the third arrangement, the one where
    /// the arm is reached <em>and</em> the read comes back with a winner: the same provider identity and
    /// the same address, so both <c>IX_users_email</c> and <c>IX_credentials_provider_subject</c> would
    /// be breached, and EF writes <c>users</c> before <c>credentials</c>, so PostgreSQL names the email
    /// index. The repository reports <c>EmailTaken</c> — honestly, since the email did collide — and
    /// only the re-read can say that the subject collided too.
    /// </para>
    /// <para>
    /// <b>What it costs when it fires: somebody who already has an account is told the address belongs to
    /// a different Google account.</b> They go looking for an account that is not theirs, and the one
    /// sentence that would have helped — sign in with the passkey it holds, or redeem a recovery code —
    /// is the one they were not shown. Delete the re-read and return
    /// <c>EmailAlreadyLinkedMessage</c> unconditionally and every other test in this file stays green;
    /// this one reddens on the <c>detail</c>.
    /// </para>
    /// <para>
    /// <b>The class remarks call this arrangement the one that cannot tell the two refusals apart, and
    /// that is right about a different question.</b> It cannot say <em>which index</em> PostgreSQL
    /// named, which is why the subject test above uses a fresh address. What it can say — and what no
    /// other arrangement can — is what the handler does once the ambiguous outcome has arrived.
    /// </para>
    /// <para>
    /// <b>The counter is asserted at one for a second reason here.</b> A handler that answered the
    /// subject sentence for every <c>EmailTaken</c> without asking would satisfy the <c>detail</c>
    /// assertion below and break
    /// <see cref="Registration_WhenTheEmailBelongsToAnotherAccount_Returns409" /> instead; the pair is
    /// what makes each of them a statement about the re-read rather than about a constant.
    /// </para>
    /// <para>
    /// <b>The nonce is opened by a stranger, because the registered subject is now refused a leg
    /// earlier.</b> Both collisions still have to arrive on one finish leg, and that leg is reached the
    /// only way it can be: <see cref="OtherSubject" /> asks for the ceremony,
    /// <see cref="Subject" /> finishes it asserting <see cref="Email" />. That is the begin-then-finish
    /// race the finish leg's check is written for — two requests with a gap, and an account created in
    /// the gap — so the arrangement states the rule more sharply than re-running the whole ceremony did.
    /// <b>Do not delete this test as covered by the options leg's refusal:</b> that leg never reaches the
    /// <c>EmailTaken</c> arm, and this is one of the three arrangements left that can.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WhenBothTheEmailAndTheSubjectCollide_SaysTheSubjectIsRegistered()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        FederatedCredentialReadCounter reads = new();
        await using ApiFactory factory = CreateApiFactory(host, reads);
        RegisteredAccount first = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(Subject, Email),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await Assert.That(first.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        // Before the nonce is minted, so the challenge row the options leg writes and the finish leg
        // spends nets to nothing across the census.
        IReadOnlyDictionary<string, long> before = await CountEveryRelationAsync(host);

        // A ceremony opened by a subject with no account, which is the only caller the options leg will
        // serve now — the registered subject is refused there before a nonce exists.
        IssuedRegistrationOptions ceremony = await BeginRegistrationCeremonyAsync(
            factory.CreateAuthenticatedClient(OtherSubject, OtherEmail));

        // After the options leg, which reads the same port: the delta is about the finish leg alone.
        int readsBefore = reads.Count;

        // Act — the whole of the first registration again, finished over a stranger's nonce: the same
        // provider identity asserting the same address, on a fresh device and eleven fresh factors, so
        // the two identity rules are the only things that can collide and both of them do.
        HttpResponseMessage refused = await RegisterOverAsync(
            factory.CreateAuthenticatedClient(Subject, Email),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId),
            ceremony.Challenge);

        // Assert
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        string detail = await DetailOfAsync(refused);
        await Assert.That(detail).IsEqualTo(SubjectConflictSentence);
        await Assert.That(detail).DoesNotContain(EmailConflictClause);
        await Assert.That(refused.Headers.Contains("Set-Cookie")).IsFalse();

        // The ambiguous outcome really was ambiguous — the arm that re-reads is the arm that ran.
        await Assert.That(reads.Count - readsBefore).IsEqualTo(1);
        await AssertNothingChangedAsync(host, before);
    }

    /// <summary>
    /// One authenticator, one account: a WebAuthn handle already enrolled cannot be enrolled again.
    /// </summary>
    /// <remarks>
    /// A new provider identity and a new address, so the device's handle is the only thing that collides —
    /// and the handle is a value the caller cannot choose, since it is minted by the authenticator. What
    /// this refuses is a second account reached by a device that already opens one, which would leave two
    /// accounts whose sign-ins are indistinguishable to the person holding the laptop.
    /// </remarks>
    [Test]
    public async Task Registration_WhenTheAuthenticatorIsAlreadyRegistered_Returns409()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        RegisteredAccount first = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(Subject, Email),
            device);
        await Assert.That(first.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        IReadOnlyDictionary<string, long> before = await CountEveryRelationAsync(host);

        // Act — a second provider identity, a second address, and the same physical device.
        RegisteredAccount second = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(OtherSubject, OtherEmail),
            device);

        // Assert
        await Assert.That(second.Response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await DetailOfAsync(second.Response)).Contains(AuthenticatorConflictClause);
        await Assert.That(second.Response.Headers.Contains("Set-Cookie")).IsFalse();
        await AssertNothingChangedAsync(host, before);
    }

    /// <summary>
    /// A client-minted factor identifier is unique across the whole table, not per account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>factor_id</c> is <c>PK_wrapped_account_keys</c>, so a value already standing in the table
    /// belongs to somebody — possibly to somebody else. The sentence says to mint a fresh one and wrap the
    /// keys under it, because re-sending the same envelopes cannot work: the identifier is the associated
    /// data they were sealed with.
    /// </para>
    /// <para>
    /// The claimed identifier is the <b>first account's passkey factor</b>, and every other member of the
    /// second request is fresh, so this is the only rule the save can lose to. It is deliberately not one
    /// of the ten codes' factors from the same request, which is a different rule refused one rung
    /// earlier and by the application rather than by the database — see
    /// <see cref="Registration_WhenThePasskeyFactorIdRepeatsACodes_IsRefused" />, which is what tells the
    /// two apart.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WhenAFactorIdIsAlreadyRegistered_Returns409()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RegisteredAccount first = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(Subject, Email),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await Assert.That(first.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        IReadOnlyDictionary<string, long> before = await CountEveryRelationAsync(host);

        // Act — fresh envelopes under an identifier the first registration already filed.
        RegisteredAccount second = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(OtherSubject, OtherEmail),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId),
            WrappedKeyFixture.MintFor(first.PasskeyKeys.Factor));

        // Assert
        await Assert.That(second.Response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await DetailOfAsync(second.Response)).Contains(FactorConflictClause);
        await Assert.That(second.Response.Headers.Contains("Set-Cookie")).IsFalse();
        await AssertNothingChangedAsync(host, before);
    }

    /// <summary>
    /// The eleventh factor against the ten: the passkey's identifier must differ from every code's, and
    /// the application says so rather than the primary key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one rule on the route with no database backstop that says the right thing.</b> Left
    /// to <c>PK_wrapped_account_keys</c> the collision arrives mid-save, and the sentence a reader would
    /// then get back tells them an identifier is <em>already registered</em> — naming a factor nobody has
    /// registered, on a request that was merely wrong, and sending them to look for a request they never
    /// made. So the assertion is that this is the application's own <b>400</b> keyed on the passkey's
    /// member, and explicitly <b>not</b> the 409 the four conflicts above answer.
    /// </para>
    /// <para>
    /// <b>Not rung 10's distinctness check either.</b> That one compares the ten codes among themselves;
    /// this compares the passkey's identifier against them, and a handler holding only the first would be
    /// green on every card whose ten are distinct — which is every card a real client mints.
    /// </para>
    /// <para>
    /// The repeated factor is the <b>last</b> code's, not the first: a check that looked at
    /// <c>codes[0]</c> and trusted the other nine would pass an arrangement built on the first.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WhenThePasskeyFactorIdRepeatsACodes_IsRefused()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        byte[] challenge = await BeginCeremonyAsync(client, OptionsPath);
        AttestationResult attestation = device.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true);
        IReadOnlyList<CodeSubmission> card = CardOf(Verifiers());

        // Act — the passkey claims the tenth code's identifier, with envelopes of its own so nothing else
        // about the request is wrong.
        HttpResponseMessage refused = await PostRegistrationAsync(
            client,
            attestation,
            WrappedKeyFixture.MintFor(card[^1].Keys.Factor),
            card);

        // Assert — the application's refusal, keyed on the member the caller can correct.
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(refused.StatusCode).IsNotEqualTo(HttpStatusCode.Conflict);

        string message = await ReadValidationErrorAsync(refused, FactorIdField);
        await Assert.That(message).Contains("must differ from every recovery code's");

        // And it is not the database's sentence, which names a factor nobody registered.
        await Assert.That(message).DoesNotContain(FactorConflictClause);
        await AssertNoAccountRowAnywhereAsync(host);
    }

    /// <summary>
    /// FR-003: the address the provider asserted is stored, and no other claim it carried is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The principal has to carry the claims for this test to say anything.</b> A real provider token
    /// carries <c>name</c>, <c>picture</c> and <c>locale</c> beside the three this product reads, and a
    /// test driven by a principal that never carried them searches the database for values nothing sent —
    /// a search that passes against every implementation there is, including one that stores whatever it
    /// is handed. <see cref="TestAuthHandler.ExtraClaimHeader" /> exists for exactly this and for nothing
    /// else.
    /// </para>
    /// <para>
    /// <b>The search is over whole rows cast to text</b>, not over columns somebody named. A claim stashed
    /// in a column added later, in a JSON payload, or beside the value it was supposed to replace is found
    /// by the same query — which is the only form of this assertion that survives the schema moving.
    /// </para>
    /// <para>
    /// <b>The email is the control</b>, and it is not decoration: a scan that reached no relation, or a
    /// pattern that matched nothing, reports an empty answer for a value that is stored exactly as it does
    /// for one that is not. Finding the address in <c>users</c> is what says the scan works.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_StoresTheAssertedEmailAndNoOtherClaim()
    {
        // Arrange — a principal carrying the three claims a real provider token has beside the ones this
        // product reads. None of the three carries a `%` or a `_`, which the search would read as a
        // wildcard and which would turn "found nothing" into "matched everything".
        const string DisplayName = "Ada Lovelace";
        const string PictureUrl = "https://provider.test/avatar/ada.png";
        const string Locale = "uk-UA";

        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(
            Subject,
            Email,
            extraClaims: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = DisplayName,
                ["picture"] = PictureUrl,
                ["locale"] = Locale,
            });
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        RegisteredAccount registered = await RegisterAccountAsync(client, device);

        // Assert — the status first, so a search over an empty database does not read as minimisation.
        await Assert.That(registered.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Named rather than counted, so a failure says which relation grew a copy of which claim.
        await Assert.That(string.Join(", ", await RelationsHoldingAsync(host, DisplayName)))
            .IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", await RelationsHoldingAsync(host, PictureUrl)))
            .IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", await RelationsHoldingAsync(host, Locale)))
            .IsEqualTo(string.Empty);

        // The control: the one claim that IS stored is found by the same scan, in the one relation that
        // may hold it.
        await Assert.That(await RelationsHoldingAsync(host, Email)).IsEquivalentTo(new[] { "users" });
    }

    /// <summary>
    /// The account-registration pool is not reachable from the other three: a nonce minted for any of
    /// them registers nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The account identifier is derived from the challenge</b>, so a nonce from another pool spent
    /// here is an account identifier chosen by whoever holds that nonce. Each of the three is worth its
    /// own arrangement: an <c>Authentication</c> nonce is minted <b>anonymously</b>, so anybody at all can
    /// obtain one; a <c>Registration</c> nonce is minted for somebody already signed in who is adding a
    /// device; and a <c>Reauthentication</c> nonce is the one that authorizes destroying an account.
    /// </para>
    /// <para>
    /// All three are minted by an account that exists and spent by a <b>second</b> principal, which is the
    /// shape the attack actually has: the value being carried across the boundary is the nonce, not the
    /// session.
    /// </para>
    /// <para>
    /// The first registration is the provable-fail control. Without it every assertion here is satisfied
    /// by a route that refuses everything.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_BuiltOnEveryOtherPoolsNonce_IsRefused()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient owner = factory.CreateAuthenticatedClient(Subject, Email);
        RegisteredAccount first = await RegisterAccountAsync(
            owner,
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await Assert.That(first.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        HttpClient stranger = factory.CreateAuthenticatedClient(OtherSubject, OtherEmail);
        SyntheticAuthenticator strangersDevice =
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // The owner's own session, because the two pools below are minted on routes a provider token no
        // longer reaches: adding a device and re-authenticating both authenticate from the cookie.
        using HttpClient ownerSession = SessionClientFor(factory, first);
        IReadOnlyDictionary<string, long> before = await CountEveryRelationAsync(host);

        // Act — one live, unspent nonce from each of the other three pools, each answered by a genuine
        // ceremony and posted to the finish leg that creates accounts.
        List<(string Pool, HttpResponseMessage Response)> refusals =
        [
            ("authentication", await RegisterOverAsync(
                stranger,
                strangersDevice,
                await BeginCeremonyAsync(factory.CreateClient(), AssertionOptionsPath))),
            ("registration", await RegisterOverAsync(
                stranger,
                strangersDevice,
                await BeginCeremonyAsync(ownerSession, PasskeyRegistrationOptionsPath))),
            ("reauthentication", await RegisterOverAsync(
                stranger,
                strangersDevice,
                await BeginCeremonyAsync(ownerSession, ReauthenticationOptionsPath))),
        ];

        // Assert — one undifferentiated refusal per pool, keyed on the response, and nothing written.
        foreach ((string pool, HttpResponseMessage response) in refusals)
        {
            await Assert.That(response.StatusCode)
                .IsEqualTo(HttpStatusCode.BadRequest)
                .Because($"a {pool} nonce must not register an account");
            await Assert.That(await ReadValidationErrorAsync(response, ResponseField))
                .IsEqualTo("The challenge is not a live account-registration challenge.");
        }

        await AssertNothingChangedAsync(host, before);
    }

    /// <summary>
    /// The other direction: an account-registration nonce is refused by every other finish leg.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the new pool is a one-way guard — the registration leg refuses the older three while
    /// the older three accept the new one, which is the asymmetry
    /// <c>PasskeyCeremonyTests</c> completes as a 3×3 for the pools that came before it.
    /// </para>
    /// <para>
    /// The nonce is minted by a client the options leg will serve — any principal holding a provider token
    /// and no account — and spent against an account that exists. The three legs answer in their own
    /// vocabularies: sign-in and the re-authentication gate answer <b>401</b> with the deliberately
    /// uninformative passkey title, and the add-a-device leg answers <b>400</b>, because it is
    /// authenticated throughout and may say what was wrong. Asserting each in its own words is the point:
    /// a single expected status would have to be wrong about two of the three.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AnAccountRegistrationNonce_IsRefusedByEveryOtherCeremony()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient owner = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        RegisteredAccount registered = await RegisterAccountAsync(owner, device);
        await Assert.That(registered.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        HttpClient anonymous = factory.CreateClient();
        HttpClient stranger = factory.CreateAuthenticatedClient(OtherSubject, OtherEmail);

        // The owner's own session: both legs spent against below — adding a device and issuing a card —
        // authenticate from the cookie and are unreachable with a provider token.
        using HttpClient ownerSession = SessionClientFor(factory, registered);
        byte[] userHandle = PasskeyEncoding.ToUserHandle(registered.AccountId);
        IReadOnlyDictionary<string, long> before = await CountEveryRelationAsync(host);

        // Act — three live account-registration nonces, one per leg.
        AssertionResult signIn = device.Authenticate(
            await BeginCeremonyAsync(stranger, OptionsPath),
            ApiFactory.PasskeyOrigin,
            userHandle,
            signCount: 0);
        HttpResponseMessage refusedSignIn = await anonymous.PostAsJsonAsync(AssertionPath, new
        {
            credentialId = signIn.CredentialIdBase64Url,
            clientDataJson = signIn.ClientDataJsonBase64Url,
            authenticatorData = signIn.AuthenticatorDataBase64Url,
            signature = signIn.SignatureBase64Url,
            userHandle = signIn.UserHandleBase64Url,
        });

        SyntheticAuthenticator secondDevice =
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        AttestationResult addDevice = secondDevice.Register(
            await BeginCeremonyAsync(stranger, OptionsPath),
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true);
        WrappedKeyFixture addedDeviceKeys = WrappedKeyFixture.Mint();
        HttpResponseMessage refusedAddDevice = await ownerSession.PostAsJsonAsync(PasskeyRegistrationPath, new
        {
            clientDataJson = addDevice.ClientDataJsonBase64Url,
            attestationObject = addDevice.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
            factorId = addedDeviceKeys.FactorId,
            wrappedContentKey = addedDeviceKeys.WrappedContentKey,
            wrappedIndexKey = addedDeviceKeys.WrappedIndexKey,
        });

        AssertionResult reauthentication = device.Authenticate(
            await BeginCeremonyAsync(stranger, OptionsPath),
            ApiFactory.PasskeyOrigin,
            userHandle,
            signCount: 0);
        HttpResponseMessage refusedGeneration = await ownerSession.PostAsJsonAsync(RecoveryCodeGenerationPath, new
        {
            codes = SubmissionsOf(CardOf(Verifiers())),
            credentialId = reauthentication.CredentialIdBase64Url,
            clientDataJson = reauthentication.ClientDataJsonBase64Url,
            authenticatorData = reauthentication.AuthenticatorDataBase64Url,
            signature = reauthentication.SignatureBase64Url,
            userHandle = reauthentication.UserHandleBase64Url,
        });

        // Assert
        await Assert.That(refusedSignIn.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await TitleOfAsync(refusedSignIn))
            .IsEqualTo(PasskeyVerificationExceptionHandler.Title);

        await Assert.That(refusedAddDevice.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        await Assert.That(refusedGeneration.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await TitleOfAsync(refusedGeneration))
            .IsEqualTo(PasskeyVerificationExceptionHandler.Title);

        await AssertNothingChangedAsync(host, before);
    }

    /// <summary>
    /// A browser holding a live session and nothing the provider vouched for registers nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what naming the scheme on the route's policy buys.</b> The default scheme is a policy
    /// scheme that forwards a cookie-bearing request to the session handler, so without
    /// <c>AddAuthenticationSchemes</c> a request presenting only the cookie would authenticate perfectly
    /// well and reach the finish leg — and the account it created is one no provider ever vouched for.
    /// <c>AuthorizationMiddleware</c> re-authenticates against the scheme a policy names and replaces the
    /// principal with the result, which is why the cookie is not enough here and would be anywhere else.
    /// </para>
    /// <para>
    /// <b>The control at the end is what stops this being green against a route that does not exist.</b> A
    /// path nobody wrote answers 401 in this application, and so does every gate on this path, so the two
    /// refusals above hold against nothing at all. The last act sends the same cookie <em>and</em> a
    /// provider token and gets a 200, which says the leg is there and the missing token is what refused
    /// it.
    /// </para>
    /// <para>
    /// Under this fixture the provider's scheme is answered by <see cref="TestAuthHandler" />, so "carries
    /// a bearer" means "carries the subject header". The mechanism under test is unchanged: the route's
    /// policy names a scheme, and that scheme decides.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WithASessionCookieAndNoBearer_IsRefused()
    {
        // Arrange — a real session, minted by a real registration, presented by a client carrying nothing
        // else.
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RegisteredAccount registered = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(Subject, Email),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await Assert.That(registered.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        string cookie = $"{CookieName}={SessionCookieValueOf(registered.Response)}";
        HttpClient sessionOnly = factory.CreateClient();
        sessionOnly.DefaultRequestHeaders.Add("Cookie", cookie);
        IReadOnlyDictionary<string, long> before = await CountEveryRelationAsync(host);

        // Act
        HttpResponseMessage options = await sessionOnly.PostAsync(OptionsPath, content: null);
        HttpResponseMessage finish = await PostRegistrationAsync(
            sessionOnly,
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId)
                .Register(UnissuedChallenge(), ApiFactory.PasskeyOrigin, prfEnabled: true),
            WrappedKeyFixture.Mint(),
            CardOf(Verifiers()));

        // Assert
        await Assert.That(options.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(finish.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await AssertNothingChangedAsync(host, before);

        // The control: the same cookie beside a provider token reaches the leg. It runs after the census,
        // so the census is taken on a database this arrangement has not touched.
        //
        // The token names a subject with no account, and that moved: the options leg now refuses a
        // subject that already has one, so the registered pair this session belongs to would answer 409
        // and the control would be measuring the conflict instead of the scheme. What it controls for is
        // unchanged — a 200 says the leg exists and the missing token, not a missing route, is what
        // refused the two acts above.
        HttpClient sessionAndBearer = factory.CreateAuthenticatedClient(OtherSubject, OtherEmail);
        sessionAndBearer.DefaultRequestHeaders.Add("Cookie", cookie);
        HttpResponseMessage admitted = await sessionAndBearer.PostAsync(OptionsPath, content: null);
        await Assert.That(admitted.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// A browser holding a live session <b>and</b> a provider token is judged by the provider token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of the bridge, and it fails closed in the opposite direction. If the route's policy
    /// were dropped, this request would be authenticated by the session handler and the endpoint would
    /// read a principal carrying no <c>sub</c> claim at all — which is an empty subject reaching
    /// <c>Credential.CreateFederated</c>, not a refusal. Answering <b>409, subject taken</b> is the
    /// observable proof that the endpoint saw the <em>provider</em> principal and resolved it to the
    /// account that principal already owns.
    /// </para>
    /// <para>
    /// It is the companion control of
    /// <see cref="Registration_WithASessionCookieAndNoBearer_IsRefused" />: one shows the cookie alone is
    /// not enough, the other shows the cookie does not displace what is enough.
    /// </para>
    /// <para>
    /// <b>It stays on the finish leg, and that is a decision rather than an accident of how it was
    /// written.</b> The options leg now answers the same 409 for the same subject, so the cheap rewrite
    /// is to point this at that leg and delete the ceremony. It is the wrong one. The property this test
    /// holds is <em>which scheme decides a request carrying both credentials</em>, and the paragraph
    /// above prices losing it on this leg specifically: a finish leg authenticated by the session handler
    /// reads a principal with no <c>sub</c> and no <c>email</c>, and <b>creates an account</b> no
    /// provider ever vouched for. On the options leg the same slip mints a nonce and creates nothing. A
    /// property is worth holding where its failure costs most, so the request under test remains the one
    /// that writes rows.
    /// </para>
    /// <para>
    /// <b>The nonce therefore comes from a stranger.</b> The registered subject can no longer open a
    /// ceremony, so <see cref="OtherSubject" /> asks — carrying no cookie, so the request under test is
    /// still the only one presenting both credentials — and the cookie-and-bearer client finishes it.
    /// This is the begin-then-finish race, and reaching the finish leg through it is what keeps the
    /// answer a fact about the principal the endpoint read rather than about the leg it arrived on.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WithASessionCookieAndABearer_Returns409SubjectTaken()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RegisteredAccount registered = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(Subject, Email),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await Assert.That(registered.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        HttpClient sessionAndBearer = factory.CreateAuthenticatedClient(Subject, OtherEmail);
        sessionAndBearer.DefaultRequestHeaders.Add(
            "Cookie",
            $"{CookieName}={SessionCookieValueOf(registered.Response)}");

        // Before the nonce is minted, so the challenge row the options leg writes and the finish leg
        // spends nets to nothing across the census.
        IReadOnlyDictionary<string, long> before = await CountEveryRelationAsync(host);

        // A stranger opens the ceremony, and carries no cookie: the request under test is the one below,
        // and it must be the only one presenting both credentials.
        IssuedRegistrationOptions ceremony = await BeginRegistrationCeremonyAsync(
            factory.CreateAuthenticatedClient(OtherSubject, OtherEmail));

        // Act — one request carrying a live session cookie and a provider token for the account that
        // session belongs to. The provider token is what the endpoint must read.
        HttpResponseMessage refused = await RegisterOverAsync(
            sessionAndBearer,
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId),
            ceremony.Challenge);

        // Assert
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await DetailOfAsync(refused)).Contains(SubjectConflictClause);
        await AssertNothingChangedAsync(host, before);
    }

    /// <summary>
    /// The card registration issued is a card: two of its codes redeem, and the second is not the first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the only test that would catch one envelope pair filed for the whole card.</b> The row
    /// counts elsewhere in this file say eleven <c>wrapped_account_keys</c> rows were written, and the
    /// hash counts say ten <c>recovery_code_hashes</c> rows were — but neither says the ten hashes were
    /// filed against the set's own credential, nor that a code other than the first can be redeemed. A
    /// registration that hashed the ten and filed a single share satisfies every constraint the database
    /// holds, answers 201, and is discovered by somebody months later who redeems the code they still
    /// hold and finds the account locked.
    /// </para>
    /// <para>
    /// <b>Two redemptions, and the tenth code first.</b> One redemption is satisfied by a write that filed
    /// whichever code the test happened to pick; taking the last and then the first says two distinct
    /// codes of the set are live, and the falling <c>remaining</c> says they were counted as one set
    /// rather than two.
    /// </para>
    /// <para>
    /// Driven on an anonymous client, which is the state somebody who lost their authenticator actually
    /// arrives in — and the state the redemption route is written for.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_ThenRedemption_SignsIn()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RegisteredAccount registered = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(Subject, Email),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await Assert.That(registered.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        HttpClient anonymous = factory.CreateClient();

        // Act — the tenth code, then the first.
        HttpResponseMessage last = await anonymous.PostAsJsonAsync(
            RedemptionPath,
            new { verifier = registered.Verifiers[^1] });
        HttpResponseMessage first = await anonymous.PostAsJsonAsync(
            RedemptionPath,
            new { verifier = registered.Verifiers[0] });

        // Assert
        await Assert.That(last.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonObject lastBody = await ReadJsonObjectAsync(last);
        await Assert.That(lastBody["kind"]!.GetValue<string>()).IsEqualTo("full");
        await Assert.That(lastBody["remaining"]!.GetValue<int>()).IsEqualTo(RequiredCodeCount - 1);

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonObject firstBody = await ReadJsonObjectAsync(first);
        await Assert.That(firstBody["kind"]!.GetValue<string>()).IsEqualTo("full");
        await Assert.That(firstBody["remaining"]!.GetValue<int>()).IsEqualTo(RequiredCodeCount - 2);

        // One account, its eleven shares of the account keys untouched — a redemption spends a code's
        // hash row and never the envelope pair that code opens the account with.
        await Assert.That(await CountAsync(host, "select count(*) from users")).IsEqualTo(1L);
        await Assert.That(await CountAsync(host, "select count(*) from wrapped_account_keys"))
            .IsEqualTo((long)RequiredCodeCount + 1);
        await Assert.That(await CountAsync(host, "select count(*) from recovery_code_hashes"))
            .IsEqualTo((long)RequiredCodeCount - 2);
    }

    /// <summary>
    /// FR-063, ADR 0018: each of the eleven factors gets the account's keys sealed under <b>its own</b>
    /// key-encryption key, filed under <b>its own</b> factor identifier, against the credential that
    /// factor belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it holds: the story's central claim, which every count in this file leaves untouched.</b>
    /// Two one-line edits to the projection in <c>RegisterAccountHandler</c> write eleven perfectly legal
    /// rows and pass the whole suite. Shift the card's envelopes by one across the ten codes —
    /// <c>presented[(i + 1) % presented.Count]</c> — and row <c>F_i</c> holds an envelope sealed under
    /// <c>AAD(F_i+1)</c>, which no reader can rebuild, so <b>not one of the ten codes opens anything,
    /// ever</b>. File all eleven against <c>passkey</c> instead and the composite foreign key, both check
    /// constraints and <see cref="RowsOneRegistrationWrites" /> are all satisfied — and revoking the
    /// passkey then cascades ten recovery factors' envelopes away and answers 204.
    /// </para>
    /// <para>
    /// <b>What it costs when it fires: an account nobody can ever open again, discovered months later.</b>
    /// Both mutations hand over a 201 and a session. The first is found by somebody who lost their
    /// authenticator, typed the code they wrote down, was signed in, and found the account unreadable; the
    /// second by somebody who revoked a laptop.
    /// </para>
    /// <para>
    /// <b>Real envelopes, and that is the whole of what makes the first half catchable.</b>
    /// <see cref="WrappedKeyFixture" /> mints well-formed random bytes, which is right for every other
    /// test here and says nothing about which factor an envelope belongs to. This test seals the same two
    /// account keys eleven times under eleven different keys, each bound to its own factor identifier as
    /// associated data, and then opens what the database handed back — see
    /// <see cref="ClientKeyCustody" /> for what a second implementation of the client's format does and
    /// does not buy. The ten codes are real too, so the verifier the server hashed and the key that
    /// unwraps the account are sibling branches of one secret, exactly as they are in a browser.
    /// </para>
    /// <para>
    /// <b>The passkey's key-encryption key is drawn at random rather than derived.</b> The client derives
    /// it from the authenticator's PRF output, and the synthetic device reports the extension as enabled
    /// without producing one. Nothing here turns on where that key came from: what is asserted is that
    /// the envelope filed under the passkey's factor is the envelope sealed under the passkey's key, and
    /// a key the test holds says that as well as a derived one would.
    /// </para>
    /// <para>
    /// <b>The cross-open at the end is what stops the whole test being vacuous.</b> Every assertion above
    /// it would also pass if <see cref="ClientKeyCustody.TryOpen" /> ignored its associated data, or if
    /// the eleven key-encryption keys had silently collapsed to one. Opening a row under its
    /// <em>neighbour's</em> key has to fail, and that failure is the tag doing the work the shift
    /// mutation would defeat.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_SealsEachFactorsEnvelopesUnderThatFactorsOwnKey()
    {
        // Arrange — one pair of account keys, drawn once, wrapped eleven times. Fresh per factor would
        // pass every round trip below and lose the account's history the first time a second factor was
        // used, which is the mistake ADR 0018 is written against.
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient client = factory.CreateAuthenticatedClient(Subject, Email);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        byte[] contentKey = RandomNumberGenerator.GetBytes(ClientKeyCustody.KeyBytes);
        byte[] indexKey = RandomNumberGenerator.GetBytes(ClientKeyCustody.KeyBytes);

        SealedFactor passkeyFactor = SealAccountKeysFor(
            RandomNumberGenerator.GetBytes(ClientKeyCustody.KeyBytes),
            contentKey,
            indexKey);

        string[] codes =
            [.. Enumerable.Range(0, RequiredCodeCount).Select(_ => ClientKeyCustody.MintRecoveryCode())];
        SealedFactor[] codeFactors =
        [
            .. codes.Select(code => SealAccountKeysFor(
                ClientKeyCustody.KeyEncryptionKeyFromRecoveryCode(code),
                contentKey,
                indexKey)),
        ];

        // One submission per code, built in one scope from one code, so a verifier and a pair of
        // envelopes cannot come apart in the fixture and read as the handler's doing.
        IReadOnlyList<CodeSubmission> card =
        [
            .. codes.Select((code, index) =>
                new CodeSubmission(ClientKeyCustody.VerifierOf(code), codeFactors[index].Keys)),
        ];

        Dictionary<Guid, SealedFactor> minted = codeFactors
            .Append(passkeyFactor)
            .ToDictionary(factor => factor.Factor);

        // Act — the real ceremony, with the card and the passkey's envelopes this test sealed.
        IssuedRegistrationOptions options = await BeginRegistrationCeremonyAsync(client);
        HttpResponseMessage response = await PostRegistrationAsync(
            client,
            device.Register(
                options.Challenge,
                ApiFactory.PasskeyOrigin,
                signCount: 0,
                prfEnabled: true,
                userHandle: options.UserHandle),
            passkeyFactor.Keys,
            card);

        // Assert — the status first, so rows that are missing read as the refusal they are rather than
        // as a write that half happened.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        IReadOnlyList<WrappedAccountKeysRow> rows = await WrappedAccountKeyRowsAsync(host);
        await Assert.That(rows.Count).IsEqualTo(RequiredCodeCount + 1);

        // The eleven identifiers are the eleven this test minted, neither more nor fewer.
        await Assert.That(string.Join(", ", rows.Select(row => row.FactorId).Order()))
            .IsEqualTo(string.Join(", ", minted.Keys.Order()));

        // The passkey's row stands alone against the card's ten, and the credential each names is read
        // back out of `credentials` rather than trusted from the copy on the row: that copy is written by
        // whichever line chose the credential, so it always agrees with a wrong choice.
        WrappedAccountKeysRow passkeyRow = rows.Single(row => row.FactorId == passkeyFactor.Factor);
        IReadOnlyList<WrappedAccountKeysRow> cardRows =
            [.. rows.Where(row => row.FactorId != passkeyFactor.Factor)];

        await Assert.That(passkeyRow.CredentialType).IsEqualTo(PasskeyType);
        await Assert.That(await CredentialTypeOfAsync(host, passkeyRow.CredentialId)).IsEqualTo(PasskeyType);

        await Assert.That(string.Join(", ", cardRows.Select(row => row.CredentialType).Distinct()))
            .IsEqualTo(RecoveryCodesType);
        await Assert.That(cardRows.Select(row => row.CredentialId).Distinct().Count()).IsEqualTo(1);
        await Assert.That(await CredentialTypeOfAsync(host, cardRows[0].CredentialId))
            .IsEqualTo(RecoveryCodesType);
        await Assert.That(cardRows[0].CredentialId).IsNotEqualTo(passkeyRow.CredentialId);

        // And the half no count can reach: every row's two envelopes open under the key of the factor
        // that row names, rebuilding the associated data from the identifier the DATABASE handed back
        // rather than from the one this test sent.
        string expectedContent = Convert.ToHexString(contentKey);
        string expectedIndex = Convert.ToHexString(indexKey);

        foreach (WrappedAccountKeysRow row in rows)
        {
            SealedFactor factor = minted[row.FactorId];

            await Assert.That(OpenedUnder(factor.KeyEncryptionKey, row, ClientKeyCustody.ContentPurpose))
                .IsEqualTo(expectedContent);
            await Assert.That(OpenedUnder(factor.KeyEncryptionKey, row, ClientKeyCustody.IndexPurpose))
                .IsEqualTo(expectedIndex);
        }

        // The non-vacuity control: each of the card's rows refuses the next code's key. Without it every
        // assertion above is satisfied by eleven identical keys and by an open that ignores its
        // associated data — which is the state a shifted projection would be indistinguishable from.
        for (int index = 0; index < cardRows.Count; index++)
        {
            SealedFactor neighbour = minted[cardRows[(index + 1) % cardRows.Count].FactorId];

            await Assert.That(
                    OpenedUnder(neighbour.KeyEncryptionKey, cardRows[index], ClientKeyCustody.ContentPurpose))
                .IsEqualTo(Unopenable);
        }
    }

    /// <summary>
    /// FR-104 read one leg earlier: a provider identity that already has an account is refused by the
    /// <b>options</b> leg, before any authenticator is asked to do anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the finish leg's 409 cannot undo is a passkey.</b> By the time that leg answers, the
    /// browser has already run <c>navigator.credentials.create()</c> and the authenticator has saved a
    /// credential permanently — WebAuthn gives a relying party no way to delete one it caused to be
    /// enrolled. So a second registration attempt from the same Google account leaves a stray passkey on
    /// somebody's phone or laptop, for an account that was never created and never will be, and every
    /// later credential list on that device shows it. Refusing here is the only place the refusal costs
    /// nothing.
    /// </para>
    /// <para>
    /// <b>The sentence is the assertion, not the status.</b> Every conflict in this product answers 409
    /// under one title, so the detail is the whole of what a caller is told — and this leg must say the
    /// same thing the finish leg says for the same cause, or somebody who reaches the two on consecutive
    /// visits is told two different stories about one fact. It is transcribed rather than read off a
    /// symbol, for <see cref="SubjectConflictSentence" />'s reason.
    /// </para>
    /// <para>
    /// The refusal is asserted against the <em>same</em> subject and a <em>fresh</em> address, so the
    /// provider identity is the only thing that collides. The address is not what this leg may judge:
    /// <c>IX_users_email</c> is the finish leg's business and answers a different sentence.
    /// </para>
    /// <para>
    /// The positive control lives next door in
    /// <see cref="RegistrationOptions_ForASubjectWithNoAccount_IssuesOptions" />, because a leg that
    /// refused every caller alike satisfies this test and the one after it perfectly.
    /// </para>
    /// </remarks>
    [Test]
    public async Task SecondOptionsRequest_ForASubjectThatAlreadyHasAnAccount_IsRefused()
    {
        // Arrange — one whole account, created by the ceremony a browser really runs.
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RegisteredAccount first = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(Subject, Email),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await Assert.That(first.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Act — the same provider subject asks to open a second ceremony.
        HttpResponseMessage refused = await factory
            .CreateAuthenticatedClient(Subject, OtherEmail)
            .PostAsync(OptionsPath, content: null);

        // Assert
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        string detail = await DetailOfAsync(refused);
        await Assert.That(detail).IsEqualTo(SubjectConflictSentence);
        await Assert.That(detail).DoesNotContain(EmailConflictClause);
    }

    /// <summary>
    /// The half that actually protects the device: the refused options leg mints no nonce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not the previous test restated.</b> A handler that issues the challenge and
    /// <em>then</em> discovers the account answers the identical 409 with the identical sentence, and
    /// every assertion next door stays green — while a row sits in <c>webauthn_challenges</c> that
    /// nothing will ever spend, and the ordering rule the fix exists for has not been written. The count
    /// is the only thing that can see the difference.
    /// </para>
    /// <para>
    /// <b>A delta, never an absolute.</b> The arrangement's own ceremony issues a nonce and spends it, so
    /// the absolute number here happens to be zero today — and a test asserting zero would be pinning
    /// what the harness leaves behind rather than what the refused request did. Read before, read after,
    /// compare.
    /// </para>
    /// <para>
    /// <b>The control at the end is what makes the delta evidence.</b> A route that answered 500 for
    /// everybody, a pool renamed out from under the query, or a counting helper reading the wrong
    /// relation all produce a delta of zero and would leave this test green having measured nothing. So
    /// an unregistered subject runs the same leg afterwards and the same count must move by exactly one.
    /// </para>
    /// </remarks>
    [Test]
    public async Task SecondOptionsRequest_ForASubjectThatAlreadyHasAnAccount_IssuesNoChallenge()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RegisteredAccount first = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(Subject, Email),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await Assert.That(first.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        long before = await AccountRegistrationChallengeCountAsync(host);

        // Act
        HttpResponseMessage refused = await factory
            .CreateAuthenticatedClient(Subject, OtherEmail)
            .PostAsync(OptionsPath, content: null);

        // Assert — nothing was minted for a ceremony that may not start.
        long after = await AccountRegistrationChallengeCountAsync(host);
        await Assert.That(after - before).IsEqualTo(0L);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        // Act, again — an unregistered subject on the same leg, which is what says the count is live.
        HttpResponseMessage issued = await factory
            .CreateAuthenticatedClient(OtherSubject, OtherEmail)
            .PostAsync(OptionsPath, content: null);

        // Assert
        await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await AccountRegistrationChallengeCountAsync(host) - after).IsEqualTo(1L);
    }

    /// <summary>
    /// The positive control the two tests above are worthless without: a caller with no account still
    /// gets a ceremony.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A leg that refused everybody satisfies both refusal tests exactly.</b> So does one that threw
    /// on every request, and so does one whose new lookup asks the wrong question and finds an account
    /// for every subject alike — which is the specific way this fix goes wrong, because the read it adds
    /// is keyed on two values and a wrong one of them is invisible from a refusal.
    /// </para>
    /// <para>
    /// It runs on a database holding <b>one</b> account already, and asks as a different subject: an
    /// empty database would let a lookup that always answers "no account" pass while proving nothing
    /// about the one that matters.
    /// </para>
    /// <para>
    /// Both binary members are read back, because they are what the browser is about to hand its
    /// authenticator: a response carrying a 409's shape under a 200 would satisfy a status-only
    /// assertion.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RegistrationOptions_ForASubjectWithNoAccount_IssuesOptions()
    {
        // Arrange — an account exists, and it is somebody else's.
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RegisteredAccount first = await RegisterAccountAsync(
            factory.CreateAuthenticatedClient(Subject, Email),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await Assert.That(first.Response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Act
        HttpResponseMessage issued = await factory
            .CreateAuthenticatedClient(OtherSubject, OtherEmail)
            .PostAsync(OptionsPath, content: null);

        // Assert
        await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadJsonObjectAsync(issued);
        await Assert.That(Base64UrlText.Decode(body["challenge"]!.GetValue<string>()).Length).IsEqualTo(32);
        await Assert.That(Base64UrlText.Decode(body["user"]!["id"]!.GetValue<string>()).Length)
            .IsEqualTo(UserHandleLength);
        await Assert.That(body["user"]!["name"]!.GetValue<string>()).IsEqualTo(OtherEmail);
    }

    /// <summary>
    /// How many live nonces the account-registration pool holds.
    /// </summary>
    /// <remarks>
    /// <b>Scoped to the pool rather than to the relation.</b> The four ceremonies share one table, and a
    /// whole-table count would be moved by any other leg a test happened to drive — which is the sort of
    /// coupling that turns a red about this route into a red about its neighbour. The spelling is
    /// transcribed rather than read off <c>WebAuthnChallengeConfiguration</c>: it is what
    /// <c>CK_webauthn_challenges_ceremony</c> enumerates, and a test taking it from the mapping agrees
    /// with whatever that mapping later decides the pool is called.
    /// </remarks>
    private static Task<long> AccountRegistrationChallengeCountAsync(PostgresTestHost host) =>
        CountAsync(
            host,
            "select count(*) from webauthn_challenges where ceremony = 'account_registration'");

    /// <summary>
    /// The prf gate's refusal, read by the member it is keyed under and by what it says.
    /// </summary>
    /// <remarks>
    /// Shared by the four shapes above because they are four inputs to <b>one</b> rule, and the sentence
    /// is deliberately identical for all of them: the person on the other end is holding a device, and
    /// which of the four ways their client failed to report an enabled result is not something they can
    /// act on. Four copies of these lines would invite one of them to drift into asserting a distinction
    /// this route does not draw.
    /// </remarks>
    private static async Task AssertRefusedByThePrfGateAsync(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string message = await ReadValidationErrorAsync(response, ResponseField);
        await Assert.That(message).Contains("did not report an enabled prf");
        await Assert.That(message).Contains("cannot hold the account's keys");
    }

    /// <summary>
    /// The card-size refusal, read by the member it is keyed under and by what it says.
    /// </summary>
    /// <remarks>
    /// One sentence for four arrangements, and it names the number rather than the fault: too few, too
    /// many, none and absent are all "a set of the wrong size", which is the whole of what a client can
    /// correct. The expected count is restated rather than read off the validation unit, for the reason
    /// <see cref="RequiredCodeCount" /> is restated: a test taking its expectation from the code under
    /// test agrees with whatever that code later decides.
    /// </remarks>
    private static async Task AssertRefusedForTheCardsSizeAsync(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ReadValidationErrorAsync(response, CodesField))
            .IsEqualTo($"Exactly {RequiredCodeCount} recovery codes are required.");
    }

    // Every member below is a thin forwarder onto RegistrationCeremony, which owns the driver. They
    // stay here, rather than being replaced by calls at ~50 sites, because this file is the one that
    // holds the claims about the route: a test reading `RegisterAccountAsync(client, device)` says what
    // it arranges, and `RegistrationCeremony.RegisterAsync(client, device)` says where the code lives.
    // The signatures are unchanged, so nothing about this file's behaviour or its auth path moved.

    /// <inheritdoc cref="RegistrationCeremony.RegisterOverAsync" />
    private static Task<HttpResponseMessage> RegisterOverAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        byte[] challenge) =>
        RegistrationCeremony.RegisterOverAsync(client, device, challenge);

    /// <inheritdoc cref="RegistrationCeremony.RegisterAsync" />
    private static Task<RegisteredAccount> RegisterAccountAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        WrappedKeyFixture? passkeyKeys = null) =>
        RegistrationCeremony.RegisterAsync(client, device, passkeyKeys);

    /// <inheritdoc cref="RegistrationCeremony.BeginAsync" />
    private static Task<IssuedRegistrationOptions> BeginRegistrationCeremonyAsync(HttpClient client) =>
        RegistrationCeremony.BeginAsync(client);

    /// <inheritdoc cref="RegistrationCeremony.PostAsync" />
    private static Task<HttpResponseMessage> PostRegistrationAsync(
        HttpClient client,
        AttestationResult attestation,
        WrappedKeyFixture passkeyKeys,
        IReadOnlyList<CodeSubmission> card) =>
        RegistrationCeremony.PostAsync(client, attestation, passkeyKeys, card);

    /// <inheritdoc cref="RegistrationCeremony.BodyOf" />
    private static Dictionary<string, object?> BodyOf(
        AttestationResult attestation,
        WrappedKeyFixture passkeyKeys,
        IReadOnlyList<CodeSubmission> card) =>
        RegistrationCeremony.BodyOf(attestation, passkeyKeys, card);

    /// <inheritdoc cref="RegistrationCeremony.PostAmendedAsync" />
    private static Task<HttpResponseMessage> PostAmendedRegistrationAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Action<Dictionary<string, object?>> amend) =>
        RegistrationCeremony.PostAmendedAsync(client, device, amend);

    /// <inheritdoc cref="RegistrationCeremony.CardOf" />
    private static IReadOnlyList<CodeSubmission> CardOf(IReadOnlyList<string> verifiers) =>
        RegistrationCeremony.CardOf(verifiers);

    /// <inheritdoc cref="RegistrationCeremony.SubmissionsOf" />
    private static object[] SubmissionsOf(IReadOnlyList<CodeSubmission> card) =>
        RegistrationCeremony.SubmissionsOf(card);

    /// <inheritdoc cref="RegistrationCeremony.Verifiers" />
    private static string[] Verifiers(int count = RequiredCodeCount) =>
        RegistrationCeremony.Verifiers(count);

    /// <summary>
    /// Thirty-two bytes of the width a registration challenge is minted at, which this server never
    /// issued.
    /// </summary>
    /// <remarks>
    /// Used by the two refusal tests, whose own options legs are refused and so hand back nothing to
    /// answer. The width matters: <see cref="RegistrationAccountId.For" /> refuses any other, so a
    /// shorter fixture would be turned away by a check that says nothing about the gate under test.
    /// </remarks>
    private static byte[] UnissuedChallenge() => RandomNumberGenerator.GetBytes(32);

    /// <inheritdoc cref="RegistrationCeremony.BeginCeremonyAsync" />
    private static Task<byte[]> BeginCeremonyAsync(HttpClient client, string path) =>
        RegistrationCeremony.BeginCeremonyAsync(client, path);

    /// <inheritdoc cref="RegistrationCeremony.EnsureOkAsync" />
    private static Task<HttpResponseMessage> EnsureOkAsync(HttpResponseMessage response) =>
        RegistrationCeremony.EnsureOkAsync(response);

    /// <summary>
    /// Every relation an account can own reads zero, and the two nobody owns still read something.
    /// </summary>
    /// <remarks>
    /// The second half is not decoration: "every owned relation is empty" is satisfied perfectly by a
    /// discovery that returned nothing at all, and a catalog query that changed shape would make every
    /// refusal test in this file vacuously green.
    /// </remarks>
    private static async Task AssertNoAccountRowAnywhereAsync(PostgresTestHost host)
    {
        IReadOnlyDictionary<string, long> counts = await CountEveryRelationAsync(host);

        await Assert.That(counts).IsNotEmpty();
        await Assert.That(NamesOf(counts.Where(relation =>
                relation.Value != 0
                && !TablesNoAccountOwns.Contains(relation.Key, StringComparer.Ordinal))))
            .IsEmpty();
        await Assert.That(NamesOf(TablesNoAccountOwns
                .Select(name => KeyValuePair.Create(name, counts.GetValueOrDefault(name)))
                .Where(relation => relation.Value == 0)))
            .IsEmpty();
    }

    /// <summary>
    /// Every relation whose row count differs from <paramref name="before" />, as one sentence each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart of <see cref="AssertNoAccountRowAnywhereAsync" /> for the tests that begin with an
    /// account already in place. "Nothing was written" cannot be stated as "everything is empty" there,
    /// and the obvious weaker form — counting only the relations the test thought about — is exactly the
    /// claim that goes quiet when a later change writes somewhere nobody listed. So the whole census is
    /// compared, both directions, and a relation that appeared or vanished between the two reads is
    /// reported as its own kind of drift.
    /// </para>
    /// <para>
    /// <b>The snapshot has to be taken before the options leg</b>, not merely before the finish leg. The
    /// options leg writes a <c>webauthn_challenges</c> row and the finish leg spends it, so a census taken
    /// between the two reports that relation as having lost a row — which is correct behaviour reported as
    /// a change. Every caller below takes it before the ceremony starts.
    /// </para>
    /// </remarks>
    private static async Task AssertNothingChangedAsync(
        PostgresTestHost host,
        IReadOnlyDictionary<string, long> before)
    {
        IReadOnlyDictionary<string, long> after = await CountEveryRelationAsync(host);

        IReadOnlyList<string> drifted =
        [
            .. before
                .Where(relation => after.GetValueOrDefault(relation.Key, -1) != relation.Value)
                .Select(relation =>
                    $"{relation.Key}: was {relation.Value}, is now "
                    + (after.TryGetValue(relation.Key, out long count)
                        ? count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "not discovered at all"))
                .Order(StringComparer.Ordinal),
        ];

        IReadOnlyList<string> appeared =
        [
            .. after.Keys
                .Where(name => !before.ContainsKey(name))
                .Order(StringComparer.Ordinal),
        ];

        await Assert.That(string.Join(", ", drifted)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", appeared)).IsEqualTo(string.Empty);

        // Non-vacuity: two empty censuses agree with each other perfectly, and an empty census is what a
        // catalog query that changed shape produces.
        await Assert.That(before).IsNotEmpty();
        await Assert.That(after).IsNotEmpty();
    }

    /// <summary>
    /// The one message a validation problem carries under <paramref name="key" />, or a failure naming
    /// every key it did carry.
    /// </summary>
    /// <remarks>
    /// The key is half of what a client is told — it names the member to correct — so reading it away and
    /// asserting only that "some sentence arrived" discards the half that says what to do. That matters
    /// most on this route, where the card's ten submissions are keyed individually.
    /// </remarks>
    private static async Task<string> ReadValidationErrorAsync(HttpResponseMessage response, string key)
    {
        JsonObject body = await ReadJsonObjectAsync(response);

        if (body["errors"] is not JsonObject errors || errors[key] is not JsonArray messages)
        {
            string present = body["errors"] is JsonObject bag
                ? string.Join(", ", bag.Select(field => field.Key))
                : "<no errors bag>";

            throw new InvalidOperationException($"The refusal carries no '{key}'. It carries: {present}.");
        }

        return messages[0]!.GetValue<string>();
    }

    /// <summary>
    /// The <c>detail</c> of a problem response, or a sentence naming what came back instead.
    /// </summary>
    /// <remarks>
    /// Returned as text rather than thrown on, for <see cref="TitleOfAsync" />' reason: the four conflicts
    /// on this route answer one status and one title, so the detail is how an assertion tells them apart,
    /// and a failure has to carry the body that arrived.
    /// </remarks>
    private static async Task<string> DetailOfAsync(HttpResponseMessage response)
    {
        string payload = await response.Content.ReadAsStringAsync();

        return JsonNode.Parse(payload) is JsonObject body && body["detail"] is { } detail
            ? detail.GetValue<string>()
            : $"<no detail; body was: {payload}>";
    }

    /// <summary>
    /// Every relation holding <paramref name="value" /> anywhere in any column of any row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The whole row is cast to text rather than named columns being read</b>, which is what makes this
    /// a search for a <em>value</em> instead of a search in the places somebody expected one. A composite
    /// cast renders every column of the row, whatever its type, so a claim stashed in a JSON blob, in a
    /// column added later, or beside the value it was supposed to replace is found by the same query.
    /// </para>
    /// <para>
    /// Case-insensitive, because a store that lower-cased an address on the way in has still stored it.
    /// The values the callers pass carry no <c>%</c> or <c>_</c>, which <c>ilike</c> would read as
    /// wildcards — a pattern that matched everything would turn this into a search that always succeeds,
    /// which is the wrong direction for the assertions that expect nothing back.
    /// </para>
    /// <para>
    /// Read on the container superuser connection, never on the application role: both isolation policies
    /// are <c>FOR ALL</c>, so a policed connection reports no row for a row that is there exactly as it
    /// does for one that is not — and half of what this is used for is proving a value <b>is</b> stored.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<string>> RelationsHoldingAsync(
        PostgresTestHost host,
        string value,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        IReadOnlyList<DiscoveredTable> discovered =
            await RowLevelSecurityCoverage.DiscoverAsync(connection, cancellationToken);
        List<string> holding = [];

        foreach (DiscoveredTable relation in discovered)
        {
            if (relation.Kind is RelationKind.View or RelationKind.PartitionedTable)
            {
                continue;
            }

            // The relation name comes from pg_class rather than from anything a caller supplies, and the
            // value travels as a parameter — the one thing here a test does choose.
            await using NpgsqlCommand command = new(
                $"select count(*) from public.\"{relation.Name}\" row_under_test "
                + "where row_under_test::text ilike @pattern",
                connection);
            command.Parameters.AddWithValue("pattern", $"%{value}%");

            if (await command.ExecuteScalarAsync(cancellationToken) is long count && count > 0)
            {
                holding.Add(relation.Name);
            }
        }

        return [.. holding.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Counts every relation in the database that can hold rows of its own, discovered rather than
    /// listed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RowLevelSecurityCoverage.DiscoverAsync" /> is the production catalog reader the deploy
    /// verifier uses, and it is what <see cref="ErasureAtomicityTests" /> counts through as well — so
    /// this is the same enumeration rather than a second list of tables written out here. Reusing the
    /// reader means a relation added tomorrow is counted tomorrow, with nothing in either file to
    /// remember to update.
    /// </para>
    /// <para>
    /// The two exclusions are double counting and nothing else. A <see cref="RelationKind.View" />
    /// reports rows that live in the tables beneath it, and a
    /// <see cref="RelationKind.PartitionedTable" /> parent reports the rows of its partitions, each of
    /// which is counted in its own right. Written as a refusal of two kinds rather than as a filter to
    /// one, so a kind added to <see cref="RelationKind" /> later is counted rather than skipped.
    /// </para>
    /// <para>
    /// Read on the container superuser connection, never on the application role. Both isolation
    /// policies are <c>FOR ALL</c>, so a policed connection reports zero rows for a row that is there
    /// exactly as it does for one that is not — and half of what is asserted here is that rows exist.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, long>> CountEveryRelationAsync(
        PostgresTestHost host,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        IReadOnlyList<DiscoveredTable> discovered =
            await RowLevelSecurityCoverage.DiscoverAsync(connection, cancellationToken);
        Dictionary<string, long> counts = new(StringComparer.Ordinal);

        foreach (DiscoveredTable relation in discovered)
        {
            if (relation.Kind is RelationKind.View or RelationKind.PartitionedTable)
            {
                continue;
            }

            // The name comes from pg_class rather than from anything a caller supplies, and it is
            // quoted because __EFMigrationsHistory is stored exactly as EF quoted it — unquoted, an
            // identifier folds to lower case and the relation is not found. Schema-qualified because
            // the discovery reads public and only public.
            counts[relation.Name] = await ScalarAsync(
                connection,
                $"select count(*) from public.\"{relation.Name}\"",
                cancellationToken);
        }

        return counts;
    }

    /// <summary>
    /// One sentence per relation whose count is not what was expected, so a failure says which one
    /// drifted and by how much rather than only that something did.
    /// </summary>
    private static IReadOnlyList<string> DescribeDrift(
        IReadOnlyList<KeyValuePair<string, long>> expected,
        IReadOnlyDictionary<string, long> actual) =>
    [
        .. expected
            .Where(relation => !actual.TryGetValue(relation.Key, out long count) || count != relation.Value)
            .Select(relation => actual.TryGetValue(relation.Key, out long count)
                ? $"{relation.Key}: expected {relation.Value}, found {count}"
                : $"{relation.Key}: expected {relation.Value}, the relation is not discovered at all"),
    ];

    /// <summary>
    /// The relation names out of a count sequence, so a failure names the offenders instead of
    /// reporting a number.
    /// </summary>
    private static IReadOnlyList<string> NamesOf(IEnumerable<KeyValuePair<string, long>> relations) =>
        [.. relations.Select(relation => $"{relation.Key} = {relation.Value}").Order(StringComparer.Ordinal)];

    /// <summary>Both spellings a <see cref="Guid" /> renders in, so a search cannot miss one.</summary>
    private static IEnumerable<string> IdentifiersOf(Guid value) => [value.ToString("D"), value.ToString("N")];

    /// <summary>
    /// One <c>sessions</c> row, in the columns this file asks about.
    /// </summary>
    /// <param name="CredentialId">The credential the session stands on — which credential, not which kind.</param>
    /// <param name="CredentialType">
    /// The copy of <c>credentials.type</c> the row carries so that
    /// <c>CK_sessions_kind_matches_credential</c> has something on the row to check. Read beside
    /// <paramref name="CredentialId" /> rather than instead of it, because the copy is written by whichever
    /// line chose the credential and therefore always agrees with a wrong choice.
    /// </param>
    private sealed record SessionRow(
        Guid Id,
        Guid UserId,
        string Kind,
        Guid CredentialId,
        string CredentialType);

    /// <summary>
    /// One <c>session_tokens</c> row: the digest as hex, and the two ids it pairs.
    /// </summary>
    /// <remarks>
    /// Hex rather than bytes, so a failure prints a value a reader can compare by eye against the digest
    /// the test computed from the cookie.
    /// </remarks>
    private sealed record SessionTokenRow(string TokenHash, Guid SessionId, Guid UserId);

    /// <summary>
    /// The three columns of <c>budgets</c> that carry a decision: who owns it, and the two a person
    /// makes later.
    /// </summary>
    private sealed record BudgetRow(Guid UserId, string? Name, string? BaseCurrencyCode);

    /// <summary>
    /// The one <c>budgets</c> row in the database, or a failure saying how many there were.
    /// </summary>
    /// <remarks>
    /// Sole rather than first, for the reason <see cref="SoleSessionAsync" /> gives: two budgets for one
    /// registration is a defect, and a test reading the first of them would report the perfectly
    /// plausible answer it happened to get back.
    /// </remarks>
    private static async Task<BudgetRow> SoleBudgetAsync(PostgresTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select user_id, name, base_currency_code from budgets",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<BudgetRow> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add(new BudgetRow(
                reader.GetGuid(0),
                await reader.IsDBNullAsync(1) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2) ? null : reader.GetString(2)));
        }

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException($"Expected exactly one budget, found {rows.Count}.");
    }

    /// <summary>
    /// The one <c>sessions</c> row in the database, or a failure saying how many there were.
    /// </summary>
    /// <remarks>
    /// Sole rather than first: two sessions for one registration is a defect a test reading the first of
    /// them would report as a perfectly plausible answer.
    /// </remarks>
    private static async Task<SessionRow> SoleSessionAsync(PostgresTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select id, user_id, kind, credential_id, credential_type from sessions",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<SessionRow> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add(new SessionRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetString(4)));
        }

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException($"Expected exactly one session, found {rows.Count}.");
    }

    /// <summary>
    /// The <c>type</c> of the credential <paramref name="credentialId" /> names, or a failure saying that
    /// no such row exists.
    /// </summary>
    /// <remarks>
    /// Read on the container superuser connection, for the reason every other read in this file is: both
    /// isolation policies are <c>FOR ALL</c>, so a policed connection reports no row for a row that is
    /// there exactly as it does for one that is not.
    /// </remarks>
    private static async Task<string> CredentialTypeOfAsync(PostgresTestHost host, Guid credentialId)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("select type from credentials where id = @id", connection);
        command.Parameters.AddWithValue("id", credentialId);

        return await command.ExecuteScalarAsync() switch
        {
            string type => type,
            _ => throw new InvalidOperationException(
                $"No credentials row is filed under '{credentialId}', which a session names."),
        };
    }

    /// <summary>Every <c>credentials.type</c> in the database, ordered so a failure reads the same way twice.</summary>
    private static async Task<IReadOnlyList<string>> CredentialTypesAsync(PostgresTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("select type from credentials", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<string> types = [];
        while (await reader.ReadAsync())
        {
            types.Add(reader.GetString(0));
        }

        return [.. types.Order(StringComparer.Ordinal)];
    }

    /// <summary>The one <c>session_tokens</c> row, or a failure saying how many there were.</summary>
    private static async Task<SessionTokenRow> SoleSessionTokenAsync(PostgresTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select encode(token_hash, 'hex'), session_id, user_id from session_tokens",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<SessionTokenRow> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add(new SessionTokenRow(
                reader.GetString(0).ToUpperInvariant(),
                reader.GetGuid(1),
                reader.GetGuid(2)));
        }

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException($"Expected exactly one session handle, found {rows.Count}.");
    }

    private static Task<IReadOnlyList<Guid>> CredentialIdsAsync(PostgresTestHost host) =>
        GuidsAsync(host, "select id from credentials");

    private static Task<IReadOnlyList<Guid>> FactorIdsAsync(PostgresTestHost host) =>
        GuidsAsync(host, "select factor_id from wrapped_account_keys");

    /// <summary>
    /// One <c>wrapped_account_keys</c> row, in the columns the key-custody test asks about.
    /// </summary>
    /// <param name="CredentialType">
    /// The copy the row carries so <c>CK_wrapped_account_keys_credential_type</c> has something on the row
    /// to check. Read beside <paramref name="CredentialId" /> rather than instead of it, for the reason
    /// <see cref="SessionRow" /> reads both: the copy is written by whichever line chose the credential and
    /// therefore always agrees with a wrong choice.
    /// </param>
    private sealed record WrappedAccountKeysRow(
        Guid FactorId,
        Guid CredentialId,
        string CredentialType,
        byte[] ContentEnvelope,
        byte[] IndexEnvelope);

    /// <summary>
    /// One recovery factor as a client mints it: the identifier, the key it wraps under, and the two
    /// envelopes that key produced.
    /// </summary>
    /// <remarks>
    /// The three travel together so a test cannot pair one factor's key with another's envelopes by
    /// accident — the fixture-side twin of the rule the handler's projection keeps.
    /// </remarks>
    private sealed record SealedFactor(Guid Factor, byte[] KeyEncryptionKey, WrappedKeyFixture Keys);

    /// <summary>
    /// What <see cref="OpenedUnder" /> reports when an envelope does not open, in place of bytes.
    /// </summary>
    /// <remarks>
    /// A sentence rather than <see cref="string.Empty" /> or a null, so the control's failure message says
    /// what happened instead of showing a blank beside a long hex string.
    /// </remarks>
    private const string Unopenable = "<the envelope did not open>";

    /// <summary>
    /// The two account keys sealed under <paramref name="keyEncryptionKey" />, bound to a factor
    /// identifier minted here.
    /// </summary>
    /// <remarks>
    /// The identifier is minted inside rather than passed in, because it is the value both envelopes are
    /// bound to and a caller holding one loose is a caller who can seal under a different one than it
    /// sends.
    /// </remarks>
    private static SealedFactor SealAccountKeysFor(
        byte[] keyEncryptionKey,
        byte[] contentKey,
        byte[] indexKey)
    {
        Guid factor = Guid.CreateVersion7();

        return new SealedFactor(
            factor,
            keyEncryptionKey,
            new WrappedKeyFixture(
                factor,
                ClientKeyCustody.Seal(
                    keyEncryptionKey,
                    contentKey,
                    ClientKeyCustody.AssociatedData(factor, ClientKeyCustody.ContentPurpose)),
                ClientKeyCustody.Seal(
                    keyEncryptionKey,
                    indexKey,
                    ClientKeyCustody.AssociatedData(factor, ClientKeyCustody.IndexPurpose))));
    }

    /// <summary>
    /// What one of <paramref name="row" />'s envelopes opens to under <paramref name="keyEncryptionKey" />,
    /// as hex, or <see cref="Unopenable" />.
    /// </summary>
    /// <remarks>
    /// <b>The associated data is rebuilt from the row's own <c>factor_id</c></b>, which is the whole point:
    /// that is what a real reader has — the envelope was found beside the identifier — and it is why an
    /// envelope filed under the wrong factor cannot be opened by anybody, including the person it belongs
    /// to.
    /// </remarks>
    private static string OpenedUnder(
        byte[] keyEncryptionKey,
        WrappedAccountKeysRow row,
        string purpose)
    {
        byte[] envelope = purpose == ClientKeyCustody.ContentPurpose
            ? row.ContentEnvelope
            : row.IndexEnvelope;

        return ClientKeyCustody.TryOpen(
            keyEncryptionKey,
            envelope,
            ClientKeyCustody.AssociatedData(row.FactorId, purpose),
            out byte[] opened)
            ? Convert.ToHexString(opened)
            : Unopenable;
    }

    /// <summary>
    /// Every <c>wrapped_account_keys</c> row with the credential it is filed against.
    /// </summary>
    /// <remarks>
    /// Read on the container superuser connection like every other read in this file:
    /// <c>user_isolation</c> is <c>FOR ALL</c>, so a policed connection reports a misfiled row exactly as
    /// it reports a missing one.
    /// </remarks>
    private static async Task<IReadOnlyList<WrappedAccountKeysRow>> WrappedAccountKeyRowsAsync(
        PostgresTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select factor_id, credential_id, credential_type, wrapped_content_key, wrapped_index_key "
            + "from wrapped_account_keys",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<WrappedAccountKeysRow> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add(new WrappedAccountKeysRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                (byte[])reader[3],
                (byte[])reader[4]));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<Guid>> GuidsAsync(PostgresTestHost host, string sql)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<Guid> values = [];
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetGuid(0));
        }

        return values;
    }

    private static async Task<long> CountAsync(PostgresTestHost host, string sql)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        return await ScalarAsync(connection, sql);
    }

    /// <summary>
    /// Reads a count, refusing anything else. Pattern-matched rather than cast-and-null-forgive: a null
    /// or unexpected scalar means the query changed shape, and that should fail here rather than at the
    /// assertion.
    /// </summary>
    private static async Task<long> ScalarAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = new(sql, connection);

        return await command.ExecuteScalarAsync(cancellationToken) switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{sql}', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>The SHA-256 of the value a cookie carries, as hex, computed without production code.</summary>
    private static string DigestOf(string cookieValue) =>
        Convert.ToHexString(SHA256.HashData(Base64UrlText.Decode(cookieValue)));

    /// <inheritdoc cref="RegistrationCeremony.SessionCookieValueOf" />
    private static string SessionCookieValueOf(HttpResponseMessage response) =>
        RegistrationCeremony.SessionCookieValueOf(response);

    /// <summary>
    /// An object's member names, ordered and joined, so a member added later produces the same message
    /// whichever order the serializer emitted it in.
    /// </summary>
    private static string MembersOf(JsonObject body) =>
        string.Join(", ", body.Select(member => member.Key).Order(StringComparer.Ordinal));

    /// <inheritdoc cref="RegistrationCeremony.ReadJsonObjectAsync" />
    private static Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response) =>
        RegistrationCeremony.ReadJsonObjectAsync(response);

    /// <summary>
    /// The <c>title</c> of a problem response, or a sentence naming what came back instead.
    /// </summary>
    /// <remarks>
    /// Returned as text rather than thrown on, so an assertion comparing titles fails carrying the body
    /// that arrived — which on this path is how one 401 is told from another.
    /// </remarks>
    private static async Task<string> TitleOfAsync(HttpResponseMessage response)
    {
        string payload = await response.Content.ReadAsStringAsync();

        return JsonNode.Parse(payload) is JsonObject body && body["title"] is { } title
            ? title.GetValue<string>()
            : $"<no title; body was: {payload}>";
    }

    /// <summary>
    /// Hosts the API over this host's database with the provider scheme answered by the test handler.
    /// </summary>
    /// <remarks>
    /// Built here rather than through <see cref="PostgresTestHost.CreateFactory" />, which exposes no
    /// way to ask for the repointing — and deliberately not added to that helper, because every other
    /// caller of it drives routes whose policies name no scheme and would gain a behaviour they were
    /// never written against.
    /// </remarks>
    /// <param name="reads">
    /// A counter to route <see cref="IUserRepository.FindUserIdByFederatedCredentialAsync" /> through, or
    /// null to leave the application's own registration standing. Declared last, and optional, for the
    /// reason every optional parameter on <see cref="ApiFactory" /> itself is.
    /// </param>
    private static ApiFactory CreateApiFactory(
        PostgresTestHost host,
        FederatedCredentialReadCounter? reads = null) =>
        new(
            host.AppConnectionString,
            adminConnectionString: host.ConnectionString,
            usesApplicationAuthentication: true,
            repointsProviderSchemeToTestHandler: true,
            configureServices: reads is null
                ? null
                : services => CountFederatedCredentialReads(services, reads));

    /// <summary>
    /// A client presenting the session <paramref name="registered" /> opened, and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>Needed because a provider token now reaches the registration group and nothing else.</b> Two
    /// tests here mint a nonce on, or spend one against, a route outside that group — adding a device,
    /// re-authenticating, issuing a card — and every one of those authenticates from the cookie. The
    /// header is built here rather than through <c>ApiFactory</c> because the value comes off a response
    /// this file already holds, and because the two tests written against the session-cookie bridge
    /// above build theirs the same way.
    /// </remarks>
    private static HttpClient SessionClientFor(ApiFactory factory, RegisteredAccount registered)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{CookieName}={SessionCookieValueOf(registered.Response)}");

        return client;
    }

    /// <summary>
    /// Wraps whatever <see cref="IUserRepository" /> the application registered so that one call can be
    /// counted, leaving every other member and the registration's lifetime alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Decoration rather than substitution.</b> The real repository still answers, on the real
    /// least-privilege connection, so the two conflict tests below stay end-to-end tests of the route: the
    /// counter observes a call it does not change. A stand-in returning a canned answer would make the
    /// disambiguating re-read's own correctness untested by the very tests that are about it.
    /// </para>
    /// <para>
    /// The concrete implementation type is never named here. It is reconstructed from the descriptor the
    /// application registered, so this helper survives the class being renamed, moved or replaced by a
    /// factory — and, more importantly, it cannot silently start decorating a <em>different</em>
    /// implementation than the one the application uses.
    /// </para>
    /// </remarks>
    private static void CountFederatedCredentialReads(
        IServiceCollection services,
        FederatedCredentialReadCounter reads)
    {
        // Last, not single: this runs in ConfigureTestServices, after everything the application
        // registered, and the last registration for a service type is the one that resolves.
        ServiceDescriptor registered =
            services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IUserRepository))
            ?? throw new InvalidOperationException(
                $"Nothing registered {nameof(IUserRepository)}, so there is nothing to count.");

        services.Remove(registered);
        services.Add(ServiceDescriptor.Describe(
            typeof(IUserRepository),
            provider => new CountingUserRepository(Undecorated(provider, registered), reads),
            registered.Lifetime));
    }

    /// <summary>Builds the registration that was there before, whichever of the three shapes it took.</summary>
    private static IUserRepository Undecorated(IServiceProvider provider, ServiceDescriptor registered) =>
        registered switch
        {
            { ImplementationType: { } type } =>
                (IUserRepository)ActivatorUtilities.CreateInstance(provider, type),
            { ImplementationFactory: { } factory } => (IUserRepository)factory(provider),
            { ImplementationInstance: IUserRepository instance } => instance,
            _ => throw new InvalidOperationException(
                $"The {nameof(IUserRepository)} registration has no shape this helper can rebuild."),
        };

    /// <summary>
    /// How many times the disambiguating re-read ran, across every request on one host.
    /// </summary>
    /// <remarks>
    /// A shared object rather than a field on the decorator: <see cref="IUserRepository" /> is scoped, so
    /// each request builds its own decorator and a per-instance count would always read one or zero for
    /// reasons having nothing to do with the handler. Interlocked because the host serves requests on the
    /// thread pool.
    /// </remarks>
    private sealed class FederatedCredentialReadCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    /// <summary>
    /// Counts the one call and forwards every member untouched.
    /// </summary>
    /// <remarks>
    /// Written out rather than generated, so that a member added to <see cref="IUserRepository" /> fails to
    /// compile here and gets a decision — a dynamic proxy would forward it silently, which on this
    /// interface means forwarding an account deletion.
    /// </remarks>
    private sealed class CountingUserRepository(IUserRepository inner, FederatedCredentialReadCounter reads)
        : IUserRepository
    {
        public Task<Guid?> FindUserIdByFederatedCredentialAsync(
            string provider,
            string subject,
            CancellationToken cancellationToken = default)
        {
            reads.Increment();

            return inner.FindUserIdByFederatedCredentialAsync(provider, subject, cancellationToken);
        }

        public Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(userId, cancellationToken);
    }

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();

        return host;
    }
}
