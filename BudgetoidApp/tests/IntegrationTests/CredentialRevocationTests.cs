using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.Passkeys;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Revoking one passkey of an account, driven over real HTTP with a real authenticator.
/// </summary>
/// <remarks>
/// <para>
/// Revocation is gated by the same fresh re-authentication erasure is — a <c>reauthentication</c>
/// nonce, answered by a passkey this account owns — because it is the other action a stolen bearer
/// token would otherwise be enough to perform: taking somebody's remaining sign-in method away is a
/// lockout, and taking the attacker's own away after they add one is how a compromise is made
/// permanent. Most of these tests are about what the route does once the gate has let it through;
/// <see cref="EveryReachableRevocationRefusal_ProducesTheIdenticalResponse" /> is the one that says
/// the gate is in front of <b>this</b> route at all. It is not redundant with
/// <see cref="ErasureReauthenticationTests" />: that file proves the gate refuses what it should, on
/// a different endpoint, and every one of its tests stays green while this route calls the gate not
/// at all — which was confirmed by mutation, and is the whole reason the family below exists here.
/// </para>
/// <para>
/// <b><see cref="Revocation_OfAnotherAccountsCredential_IsRefusedAndRemovesNeitherAccountsRows" /> is
/// the most important test in this file, and the only one of its kind in the codebase.</b>
/// <c>credentials</c> is exempt from row-level security, so the <c>DELETE</c> a revocation issues is
/// scoped by the application's owner predicate and by nothing beneath it — no policy, and no grant
/// narrower than the whole table. Everything else here would stay green with that predicate gone.
/// </para>
/// <para>
/// <b>Two id spaces share one word in this request, and they are never compared.</b> The
/// <c>{credentialId}</c> in the route is a <c>credentials.id</c> GUID — <i>what is being removed</i>.
/// The <c>credentialId</c> in the body is the WebAuthn credential <b>handle</b> of the authenticator
/// that signed the assertion — <i>what proves presence</i>. A person may legitimately prove with the
/// very passkey they are removing, which is what the second test drives, so a route that "checked"
/// the two against each other would be refusing a correct request.
/// </para>
/// <para>
/// <b>What the response says is pinned in two places, and neither covers the other.</b>
/// <see cref="Revocation_EndsEverySessionTheCredentialEstablishedAndReportsHowMany" /> asks whether
/// <c>sessionsEnded</c> is right; <see cref="Revocation_ResponseCarriesTheSessionCountAndNothingElse" />
/// asks whether anything <em>else</em> arrived beside it. A widened record — an echoed credential id
/// being the obvious one — leaves the first test green.
/// </para>
/// <para>
/// Every row is counted on <see cref="PostgresTestHost.ConnectionString" /> — the container
/// superuser — and never on the application role. <c>passkey_signature_counters</c> carries
/// <c>user_isolation</c>, which is <c>FOR ALL</c>, so a policed connection reports zero rows for a
/// counter that is still there exactly as it does for one that is gone: read on the app role, "the
/// counter left with its credential" could not fail. Every count asserted zero after the act is
/// asserted one before it, on the same connection and the same predicate.
/// </para>
/// </remarks>
public sealed class CredentialRevocationTests
{
    private const string Subject = "google-revoking";

    /// <summary>
    /// The bystander account: the one whose credential another account tries to revoke, and the one
    /// whose mere existence makes every owner predicate on this route measurable.
    /// </summary>
    private const string OtherSubject = "google-revoking-bystander";

    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";
    private const string AssertionOptionsPath = "/api/passkeys/assertion/options";
    private const string AssertionPath = "/api/passkeys/assertion";

    /// <summary>
    /// The one member of the revocation response, named once so the test asserting it is present and
    /// the test reading its value cannot drift apart from each other.
    /// </summary>
    private const string SessionsEndedMember = "sessionsEnded";

    /// <summary>
    /// The <c>ceremony</c> value the re-authentication pool is filed under, as the column stores it.
    /// </summary>
    private const string ReauthenticationCeremony = "reauthentication";

    /// <summary>
    /// The member of a problem-details body that is a new value on every request rather than on every
    /// cause, and therefore the one member a comparison of two refusals must not compare.
    /// </summary>
    private const string TraceIdMember = "traceId";

    /// <summary>
    /// How many refusals <see cref="EveryReachableRevocationRefusal_ProducesTheIdenticalResponse" />
    /// drives. Named so that deleting one from the list is a failing test rather than a shorter and
    /// still perfectly green one.
    /// </summary>
    private const int ReachableRevocationRefusals = 8;

    /// <summary>
    /// The one member of the revocation response, joined exactly as
    /// <see cref="Revocation_ResponseCarriesTheSessionCountAndNothingElse" /> builds it. It is
    /// <see cref="SessionsEndedMember" /> today because the record has one member; the constant exists
    /// so that a second member arriving is a comparison of two strings rather than of two numbers.
    /// </summary>
    private const string RevocationMembers = SessionsEndedMember;

    /// <summary>
    /// The happy path, and the account holds <b>two</b> passkeys rather than one on purpose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A later step of this story refuses to revoke an account's last passkey — that is what keeps a
    /// revocation from being a lockout — so a happy-path test that revoked the only credential an
    /// account had would start failing the day that rule lands and would read as a regression in the
    /// route rather than as a test that arranged the wrong account. Proving with one and revoking the
    /// other keeps this test measuring removal and nothing else.
    /// </para>
    /// <para>
    /// The surviving passkey's three rows are the control, and without it this test cannot tell "the
    /// route removed the row it was named" from "the route emptied the tables". The two child rows go
    /// by the database's own <c>ON DELETE CASCADE</c> from <c>credentials</c>, which runs with the
    /// privileges of the referencing table's owner — the app role holds no <c>DELETE</c> on either,
    /// and <c>AppRoleGrantsTests</c> pins that absence deliberately.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_AfterAFreshReauthentication_RemovesTheCredentialAndItsKeyAndCounter()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator proving = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator revoked = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, proving);
        await RegisterPasskeyAsync(client, revoked);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid provingCredentialId = await ResolveCredentialIdAsync(admin, proving);
        Guid revokedCredentialId = await ResolveCredentialIdAsync(admin, revoked);

        // Both passkeys are whole before the act, or every zero below is a zero the arrangement
        // produced rather than one the route did.
        await AssertPasskeyIsWholeAsync(admin, provingCredentialId);
        await AssertPasskeyIsWholeAsync(admin, revokedCredentialId);

        // Act — the assertion is signed by the passkey that stays, and the route names the one that
        // goes. The body's credentialId is the proving device's WebAuthn handle; the route's is the
        // other credential's primary key.
        HttpResponseMessage response = await RevokeAsync(client, proving, userId, revokedCredentialId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        PasskeyRowCounts afterRevoked = await CountPasskeyRowsAsync(admin, revokedCredentialId);
        await Assert.That(afterRevoked.Credentials).IsEqualTo(0L);
        await Assert.That(afterRevoked.PublicKeys).IsEqualTo(0L);
        await Assert.That(afterRevoked.Counters).IsEqualTo(0L);

        // The control: the passkey that proved the request is untouched.
        await AssertPasskeyIsWholeAsync(admin, provingCredentialId);
    }

    /// <summary>
    /// Proving with the very passkey being removed succeeds — the request a person makes on their last
    /// working device, and the one shape of this route where the proof and the target are one row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this test proves is exactly its name and nothing more.</b> The two id spaces the class
    /// remarks describe are never compared, so a route that "checked" the body's WebAuthn handle
    /// against the route's <c>credentials.id</c> would refuse this correct request — that refusal is
    /// what this test is here to catch, and it is a near-duplicate of
    /// <see cref="Revocation_AfterAFreshReauthentication_RemovesTheCredentialAndItsKeyAndCounter" />
    /// in every other respect. A near-duplicate documenting an honest case is worth keeping.
    /// </para>
    /// <para>
    /// <b>It does not guard the tracked-entity mechanism, and an earlier version of this docstring
    /// claimed it did.</b> The claim was that verifying the assertion materialises the proving
    /// credential's <c>PasskeyPublicKey</c> and <c>PasskeySignatureCounter</c> into the change tracker,
    /// so removing the <c>Credential</c> with those dependents still tracked makes EF cascade into the
    /// copies it can see, emit its own <c>DELETE FROM passkey_public_keys</c> and
    /// <c>DELETE FROM passkey_signature_counters</c>, and die with <c>42501</c> and a 500 on a
    /// privilege the app role deliberately does not hold. The mechanism is real; this test does not
    /// observe it. Measured: removing the <b>first</b> <c>DiscardTrackedEntities()</c> from
    /// <c>RevokePasskeyHandler</c> — the one that clears exactly those two entities — turns nothing in
    /// either suite red, because the <b>second</b> discard sits between it and the only statement that
    /// can cascade and absorbs it whole. This test reddens only when both discards go, and then it
    /// reddens alongside the test named below rather than instead of it.
    /// </para>
    /// <para>
    /// <b>The <c>42501</c> mechanism is guarded by
    /// <see cref="Revocation_WhenTheCredentialHasLiveSessions_DoesNotFailOnAMissingSessionDeleteGrant" />,</b>
    /// which was confirmed to redden under removal of the second discard and is the test to read — and
    /// to keep — if that is the failure being looked for. Do not answer such a 500 with a grant: the
    /// SQLSTATE names a privilege, the cause is the change tracker, and <c>EraseAccountHandler</c>
    /// documents the identical mechanism for <c>budgets</c>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_OfTheCredentialThatProvedIt_Succeeds()
    {
        // Arrange — two passkeys again, so the later "never the last one" rule cannot turn this into a
        // refusal about something other than what it measures.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator proving = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator spare = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, proving);
        await RegisterPasskeyAsync(client, spare);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid provingCredentialId = await ResolveCredentialIdAsync(admin, proving);
        await AssertPasskeyIsWholeAsync(admin, provingCredentialId);

        // Act — one device, both roles: it signs the assertion and it is what the route names.
        HttpResponseMessage response = await RevokeAsync(client, proving, userId, provingCredentialId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        PasskeyRowCounts after = await CountPasskeyRowsAsync(admin, provingCredentialId);
        await Assert.That(after.Credentials).IsEqualTo(0L);
        await Assert.That(after.PublicKeys).IsEqualTo(0L);
        await Assert.That(after.Counters).IsEqualTo(0L);
    }

    /// <summary>
    /// Alice's own passkey, freshly and correctly proving presence, over Bob's <c>credentials.id</c> —
    /// and <b>neither</b> account loses a row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the single most important test in this story.</b> <c>credentials</c> is exempt from
    /// row-level security (ADR 0011), so the <c>DELETE</c> this route issues is scoped by the
    /// application's owner predicate and by <b>nothing beneath it</b>: no policy narrows it, no query
    /// filter narrows it, and the grant is on the whole table. This is the only test in the codebase
    /// that would notice the <c>userId</c> argument leaving the
    /// <c>FindPasskeyCredentialAsync</c> call in <c>RevokePasskeyHandler</c>.
    /// <c>docs/decisions/0014-scope-the-credential-delete-in-the-application.md</c> names it in the
    /// present tense as the one thing standing between a refactor and a cross-tenant delete.
    /// </para>
    /// <para>
    /// What it catches, stated as the failure: with the owner predicate gone, Alice's proof passes the
    /// gate exactly as it does here, Bob's credential resolves on its id alone, and <b>Bob loses a
    /// passkey to a request Alice made</b> — his public key and signature counter leaving with it by
    /// the cascade, and his sessions with them.
    /// </para>
    /// <para>
    /// <b>The status must be 404 and not 401, and that distinction is the whole test.</b> Alice proves
    /// with her <b>own</b> registered passkey over a nonce her own options call issued, so the
    /// re-authentication gate has no reason to refuse her: a 401 would mean the request died at the
    /// gate, which would make this test green for a reason that has nothing to do with the owner
    /// filter and would keep it green over a lookup with no filter left. The 404 is positive evidence
    /// that the gate <b>passed</b> and the scoped lookup then declined to resolve another account's
    /// row — the handler's own "an absent credential is a 404 here" branch.
    /// </para>
    /// <para>
    /// Both sides are counted before and after. Bob's rows surviving is the direct claim; Alice's
    /// surviving rules out the other half of the wrong design — a handler that answered correctly and
    /// removed the caller's own credential instead, which asserting Bob alone could not see.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_OfAnotherAccountsCredential_IsRefusedAndRemovesNeitherAccountsRows()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient alice, Guid aliceId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        (HttpClient bob, _, _) = await host.Factory.CreateSignedInClientAsync(OtherSubject);
        SyntheticAuthenticator alicesDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator bobsDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(alice, alicesDevice);
        await RegisterPasskeyAsync(bob, bobsDevice);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid alicesCredentialId = await ResolveCredentialIdAsync(admin, alicesDevice);
        Guid bobsCredentialId = await ResolveCredentialIdAsync(admin, bobsDevice);

        // Both passkeys are whole before the act, or "nothing moved" below is a claim about rows the
        // arrangement never wrote.
        await AssertPasskeyIsWholeAsync(admin, alicesCredentialId);
        await AssertPasskeyIsWholeAsync(admin, bobsCredentialId);

        // Act — Alice's session, Alice's own authenticator answering a nonce issued to her, and
        // her own user handle. Nothing about the proof is stale, foreign or malformed; the only thing
        // that belongs to somebody else is the credentials.id in the route.
        HttpResponseMessage response = await RevokeAsync(alice, alicesDevice, aliceId, bobsCredentialId);

        // Assert — 404, never 401: see the remarks. A 401 here is not a stricter pass, it is this test
        // measuring the gate instead of the owner filter.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await AssertPasskeyIsWholeAsync(admin, bobsCredentialId);
        await AssertPasskeyIsWholeAsync(admin, alicesCredentialId);
    }

    /// <summary>
    /// The account's own federated Google credential, named on a route that removes passkeys, proved
    /// with a genuine passkey of the same account — and it is a 404 the caller cannot tell from the one
    /// an id belonging to nobody produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The consequence of losing the type predicate is the worst outcome in this story, and it is
    /// silent.</b> The federated credential is the row <c>UserProvisioningMiddleware</c> resolves the
    /// Google <c>sub</c> on. Let this route delete it and the very next request authenticates fine at
    /// the provider, resolves to no account, and is answered 401 — every authenticated route, including
    /// <c>/api/me/erasure</c>. The person is left with an account that holds their money, cannot be
    /// reached, and cannot even be erased. Nothing in the database is corrupt and no error is logged;
    /// the account simply becomes unreachable.
    /// </para>
    /// <para>
    /// <b>Two passkeys, because the floor must not be what answers.</b> An account standing on one
    /// passkey reads a count of one and is refused with 409 before the type predicate has decided
    /// anything — and this test would then be green over a lookup with no type filter left, which is
    /// precisely the mutation it exists to catch.
    /// </para>
    /// <para>
    /// <b>The body is compared whole against the unknown-id 404, not merely asserted to be 404.</b> One
    /// lookup with three predicates answers both requests, so the two are indistinguishable <i>by
    /// construction</i> — and that is worth pinning rather than assuming, because a later reader who
    /// wanted a friendlier message would naturally give the federated case its own sentence
    /// ("credential is not a passkey") and hand a caller holding a stolen bearer token a way to
    /// identify which of an account's credential ids is the federated one. The unknown-id request is
    /// driven with its own fresh proof, since the gate spends a nonce whether it passes or not.
    /// </para>
    /// <para>
    /// The federated row is counted before and after. The proving passkey is counted too, for the
    /// reason the cross-account test gives about Alice's rows: a handler that answered correctly and
    /// removed the caller's own credential instead would pass on the federated count alone.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_OfTheFederatedCredential_IsRefusedAndRemovesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator proving = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator spare = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, proving);
        await RegisterPasskeyAsync(client, spare);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid provingCredentialId = await ResolveCredentialIdAsync(admin, proving);
        Guid federatedCredentialId = await ResolveFederatedCredentialIdAsync(admin, Subject);

        // Whole before the act, or "nothing moved" below is a claim about rows the arrangement never
        // wrote. The federated credential carries no public key and no counter, which is what makes it
        // a different kind of row rather than merely a different one.
        await AssertPasskeyIsWholeAsync(admin, provingCredentialId);
        PasskeyRowCounts federatedBefore = await CountPasskeyRowsAsync(admin, federatedCredentialId);
        await Assert.That(federatedBefore.Credentials).IsEqualTo(1L);
        await Assert.That(federatedBefore.PublicKeys).IsEqualTo(0L);

        // Act — the same account, the same fresh kind of proof, twice: once naming its own federated
        // credential and once naming an id no row in the table carries.
        //
        // The second proof comes from the OTHER registered passkey, not from `proving` again. A
        // successful gate advances that device's stored counter to the 1 the synthetic authenticator
        // reports, so proving twice with one device is a counter regression — a 401 from the gate,
        // which would compare two bodies neither of which is the 404 this test is about.
        HttpResponseMessage federated = await RevokeAsync(client, proving, userId, federatedCredentialId);
        HttpResponseMessage unknown = await RevokeAsync(client, spare, userId, Guid.CreateVersion7());

        // Assert — 404 and never 200, and never 400 either: the lookup declined to resolve the row, and
        // that is the same branch an id belonging to nobody takes.
        await Assert.That(federated.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await ReadComparableBodyAsync(federated))
            .IsEqualTo(await ReadComparableBodyAsync(unknown));

        // And the identity the account signs in with is still there.
        PasskeyRowCounts federatedAfter = await CountPasskeyRowsAsync(admin, federatedCredentialId);
        await Assert.That(federatedAfter.Credentials).IsEqualTo(1L);
        await AssertPasskeyIsWholeAsync(admin, provingCredentialId);
    }

    /// <summary>
    /// The floor: an account holding exactly one passkey cannot revoke it, and the refused request
    /// leaves all three of its rows where they were.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The floor is one <i>passkey</i>, not one credential, and this account is the case that tells
    /// the two apart.</b> It also holds the federated Google credential provisioning minted for it, so
    /// a count over <c>credentials</c> with no type predicate reads <b>two</b> here, lets the last
    /// passkey go, and this test goes red on the status alone. What the account would be left with is
    /// not a lesser account: a passkey is the only credential type that opens a session reaching budget
    /// content, so the person could still sign in with Google, still could not reach their own money,
    /// and could not even prove presence for an erasure.
    /// </para>
    /// <para>
    /// <b>409 and not 400, and not 404.</b> The body is well-formed, the proof is fresh and genuine,
    /// and the id names a passkey this account really owns — the request would succeed unchanged the
    /// moment a second passkey exists, which is a conflict with the state of the resource rather than a
    /// fault in the request or a missing one. A 400 would tell the client to fix the request, which is
    /// not what is wrong; a 404 would say the passkey is not theirs, which it is.
    /// </para>
    /// <para>
    /// <b>The second half of the assertion is the one a plausible implementation fails.</b> A handler
    /// that ends the credential's sessions — or discards, deletes, and then counts — before the count is
    /// consulted answers 409 exactly as this test demands while having already signed the person out of
    /// the very passkey it is telling them they still hold. That is why the three rows are counted after
    /// the refusal rather than the status being trusted on its own, and why the check has to sit after
    /// the re-authentication gate and the owner-scoped lookup but <b>before</b> anything is removed.
    /// </para>
    /// <para>
    /// <b>The count is scoped to an owner as well as to a type, and the bystander account below is the
    /// only thing in either suite that says so.</b> With one account seeded, "every passkey in the
    /// table" and "this account's passkeys" are the same set, and dropping <c>user_id</c> from the
    /// count changes no answer anywhere — measured, not supposed. In production the two sets are never
    /// the same: two accounts holding one passkey each would both read a count of two, the floor would
    /// never fire for anybody, and the first person to revoke their only passkey would be locked out
    /// permanently, which is the exact outcome this rule exists to prevent.
    /// </para>
    /// <para>
    /// Its control is
    /// <see cref="Revocation_AfterAFreshReauthentication_RemovesTheCredentialAndItsKeyAndCounter" />,
    /// which registers two passkeys and revokes one: a count check that is too eager — off by one, or
    /// refusing whenever any count is read — turns that test red while leaving this one green, so the
    /// pair pins the rule from both sides and no separate "the second of two may go" test is needed.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_OfTheOnlyRemainingPasskey_IsRefusedWithConflictAndRemovesNothing()
    {
        // Arrange — one passkey, and one is the whole arrangement. Registration also establishes the
        // account, so the federated Google credential is there beside it; that second credential is
        // what makes an untyped count read two.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator onlyDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, onlyDevice);

        // A second account holding one passkey of its own, seeded before the act and never touched by
        // it. This is not spare scenery: it is what makes the OWNER predicate on the passkey count
        // load-bearing. Without it this account's only passkey is also the only passkey in the table,
        // so a count that lost `user_id` reads the same one and this test stays green over a floor that
        // could never fire for anyone. With it, that count reads two, the floor says nothing, and the
        // route deletes — a status assertion away. Do not remove it as unused setup.
        await RegisterPasskeyAsync(
            host.Factory.CreateAuthenticatedClient(OtherSubject),
            SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        Guid userId = await ResolveUserIdAsync(host, Subject);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid onlyCredentialId = await ResolveCredentialIdAsync(admin, onlyDevice);

        // Whole before the act, or "nothing moved" below is a claim about rows the arrangement never
        // wrote — and here that would make the test pass for an account that held no passkey at all.
        await AssertPasskeyIsWholeAsync(admin, onlyCredentialId);

        // Act — the one device both proves presence and is what the route names, which is the shape of
        // the request a person makes when they are about to lock themselves out.
        HttpResponseMessage response = await RevokeAsync(client, onlyDevice, userId, onlyCredentialId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        // And nothing was taken on the way to saying no.
        await AssertPasskeyIsWholeAsync(admin, onlyCredentialId);
    }

    // ==================================================================================
    // THE TEST THAT IS DELIBERATELY NOT IN THIS FILE, because a reader will look for it:
    //
    //     "after a revocation the credential has no active session"
    //
    // sessions.credential_id is ON DELETE CASCADE, so deleting the credentials row removes its
    // session rows whether or not anything revoked them first. That test is green with the explicit
    // revocation call deleted and green with the revocation moved after the delete — it proves
    // nothing. The schema after the operation is identical either way, which is exactly why the
    // evidence has to leave in the RESPONSE instead, as sessionsEnded, and why the test below
    // asserts on a number rather than on rows. docs/business-logic/sessions.md names it under "The
    // test nobody should write". Do not add it.
    // ==================================================================================

    /// <summary>
    /// One passkey signed in <b>twice</b>, that passkey revoked, and the response says <b>2</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test catches two different defects at once, which is the whole reason it is shaped
    /// this way.</b> Delete the explicit revocation call from the handler and the count comes back
    /// <c>0</c>. Move the delete <i>ahead</i> of the revocation and the cascade removes the session
    /// rows first, so the revocation's predicate — <c>credential_id = X and revoked_at_utc is
    /// null</c> — matches nothing and the count is <c>0</c> again. Both defects leave the database in
    /// byte-for-byte the state a correct implementation leaves it in, so no row count anywhere can
    /// tell them from the real thing.
    /// </para>
    /// <para>
    /// <b>The member's presence is asserted separately from its value</b>, and that is not
    /// belt-and-braces: an absent JSON member deserializes to <c>0</c> on an <see langword="int" />,
    /// which is the identical value both defects above produce. Asserting only the number would call
    /// a response that never carried the member correct.
    /// </para>
    /// <para>
    /// Two sessions rather than one, because a count that reported "some sessions ended" as a
    /// constant <c>1</c> — or a predicate that stopped at the first match — passes a single-session
    /// arrangement.
    /// </para>
    /// <para>
    /// Both sign-ins report a signature counter of <b>zero</b>, which is what a synced passkey does:
    /// <c>PasskeySignatureCounter.Accept</c> treats a repeated zero as no movement rather than as a
    /// clone, so the second sign-in is accepted without the test having to keep a counter ledger of
    /// its own. It also leaves the counter where the re-authentication below expects it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_EndsEverySessionTheCredentialEstablishedAndReportsHowMany()
    {
        // Arrange — two passkeys, so the "never the last one" floor cannot turn this into a refusal,
        // and the sessions all hang off the one that goes.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator proving = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator revoked = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, proving);
        await RegisterPasskeyAsync(client, revoked);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid revokedCredentialId = await ResolveCredentialIdAsync(admin, revoked);

        // Two real sign-ins on the passkey that is about to go. Sessions are written by a verified
        // assertion and by nothing else, so this is the only way to arrange them.
        await SignInAsync(host, revoked, userId);
        await SignInAsync(host, revoked, userId);

        // Asserted before the act, or the 2 below is a number the arrangement failed to produce
        // rather than one the route counted.
        await Assert.That(await CountLiveSessionsAsync(admin, revokedCredentialId)).IsEqualTo(2L);

        // Act
        HttpResponseMessage response = await RevokeAsync(client, proving, userId, revokedCredentialId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadJsonObjectAsync(response);
        await Assert.That(body.ContainsKey(SessionsEndedMember)).IsTrue();
        await Assert.That(body[SessionsEndedMember]!.GetValue<int>()).IsEqualTo(2);
    }

    /// <summary>
    /// That the revocation response carries exactly <c>sessionsEnded</c>, and no second member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its own test rather than another assertion on the one above, because the two fail for
    /// unrelated reasons.</b> That test is about the <em>number</em> and pays for it with two real
    /// sign-ins; this one is about the <em>shape</em> and needs no session at all — a revocation that
    /// ended nothing reports zero and carries the same members. Folding them together would give one
    /// test two names and make a widened record read as a failure of the session count.
    /// </para>
    /// <para>
    /// <b>Green the day it is written, and that is the point rather than an apology</b> — the same
    /// argument <c>CredentialListEndpointTests.Credentials_EntryCarriesTheIdTheTypeAndTheDateAndNothingElse</c>
    /// makes for the GET. Nothing else in either suite goes red when a second member starts arriving
    /// here: every other test on this route reads the status, the rows behind it, or
    /// <c>sessionsEnded</c> alone, and all of them keep passing beside a <c>credentialId</c>. The defect
    /// this exists to catch is one a later reader adds — <c>PasskeyRevocation(int SessionsEnded, Guid
    /// CredentialId)</c> is the natural next step the day a client wants to refresh its list from the
    /// response — and <c>PasskeyRevocation</c>'s own remarks say why it must not be: the caller
    /// supplied the id it asked about, so echoing one back adds nothing, and an id in a response body is
    /// an id in a client log.
    /// </para>
    /// <para>
    /// <b>Never <c>ContainsKey</c>, and that is the whole shape of the assertion.</b> A containment
    /// check over member names can never fail: every widening leaves <c>sessionsEnded</c> present and
    /// the check green — which is exactly what the test above does, deliberately, because it is asking a
    /// different question. The members are joined and compared whole, joined rather than counted for the
    /// reason the GET's version gives: a count says "1 != 2" and leaves the reader to work out which
    /// member arrived, while the joined string names it in the failure message.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_ResponseCarriesTheSessionCountAndNothingElse()
    {
        // Arrange — two passkeys, so the "never the last one" floor cannot turn this into a refusal
        // whose problem-details body would satisfy no member comparison at all.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator proving = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator revoked = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, proving);
        await RegisterPasskeyAsync(client, revoked);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid revokedCredentialId = await ResolveCredentialIdAsync(admin, revoked);

        // Act
        HttpResponseMessage response = await RevokeAsync(client, proving, userId, revokedCredentialId);

        // Assert — the status first, so a body that is missing because the request was refused reads as
        // the refusal it is rather than as a member list nobody would recognise as a 401.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Ordered before joining, so a second member produces the same message whichever order the
        // serializer emitted it in — a red that reads differently between runs is a red people stop
        // trusting.
        JsonObject body = await ReadJsonObjectAsync(response);
        string members = string.Join(", ", body.Select(member => member.Key).Order(StringComparer.Ordinal));

        await Assert.That(members).IsEqualTo(RevocationMembers);
    }

    /// <summary>
    /// Two passkeys, a live session on each, one revoked — and the other account's-own session is
    /// still <b>unrevoked</b>, not merely still present.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the failure <c>docs/business-logic/sessions.md</c> calls "the one to watch": a
    /// revocation predicate keyed on <c>user_id</c> instead of <c>credential_id</c>. Every session in
    /// a single-credential account has the same owner, so every other test here passes under it —
    /// including the one above, whose two sessions belong to one credential and one person alike.
    /// Under that predicate, revoking the passkey on a lost phone signs the person out of the laptop
    /// they are holding.
    /// </para>
    /// <para>
    /// The surviving row is read on the container superuser connection and the assertion is on
    /// <c>revoked_at_utc is null</c>, because "the row is still there" is the weaker claim: the
    /// cascade only reaches the revoked credential's rows, so the survivor's mere existence says
    /// nothing about whether a sweep stamped it on the way past. Its total is counted too, so a
    /// vanished row cannot pass as an unrevoked one by making both numbers zero.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_LeavesTheAccountsOtherSessionsAlive()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator proving = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator revoked = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, proving);
        await RegisterPasskeyAsync(client, revoked);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid provingCredentialId = await ResolveCredentialIdAsync(admin, proving);
        Guid revokedCredentialId = await ResolveCredentialIdAsync(admin, revoked);

        // One sign-in on each passkey: the same account, reached two ways, exactly as a phone and a
        // laptop reach it.
        await SignInAsync(host, proving, userId);
        await SignInAsync(host, revoked, userId);

        await Assert.That(await CountLiveSessionsAsync(admin, provingCredentialId)).IsEqualTo(1L);
        await Assert.That(await CountLiveSessionsAsync(admin, revokedCredentialId)).IsEqualTo(1L);

        // Act
        HttpResponseMessage response = await RevokeAsync(client, proving, userId, revokedCredentialId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await CountSessionsAsync(admin, provingCredentialId)).IsEqualTo(1L);
        await Assert.That(await CountLiveSessionsAsync(admin, provingCredentialId)).IsEqualTo(1L);
    }

    /// <summary>
    /// Two passkeys, each holding its own share of the account keys, one revoked — and the survivor's
    /// share is not merely still there, it is <b>byte for byte</b> what was written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both halves are load-bearing and only one of them is obvious.</b> The revoked factor's row
    /// must leave, and it leaves by the database's own <c>ON DELETE CASCADE</c> from
    /// <c>credentials</c> — the application role holds no <c>DELETE</c> on
    /// <c>wrapped_account_keys</c>, so any other route out of the table is a <c>42501</c>. The half a
    /// reader will drop is the second: that the surviving factor's envelopes were not <em>rewritten</em>
    /// on the way past. A handler that re-filed the account's keys under the remaining factor — the
    /// natural shape the day somebody decides revocation should "re-key" — leaves the same one row
    /// standing, so a count cannot see it, and the person's remaining authenticator then derives a
    /// key-encryption key that opens envelopes nothing sealed for it.
    /// </para>
    /// <para>
    /// Each passkey is registered with a fixture of its own, so the two shares are distinguishable and
    /// the row that survived can be named by the factor identifier it was posted with rather than by
    /// being the only one left.
    /// </para>
    /// <para>
    /// Read on the container superuser connection, like every row in this file:
    /// <c>wrapped_account_keys</c> carries <c>user_isolation</c>, so a policed connection reports no row
    /// for one that is still there exactly as it does for one that is gone — and "the survivor is still
    /// there" is half of what this test claims.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RevokingOneOfTwoPasskeys_LeavesTheOtherFactorsWrappedKeys_AndRewritesNone()
    {
        // Arrange — two passkeys, and two distinguishable shares of the same two account keys.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator proving = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator revoked = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        WrappedKeyFixture survivingKeys = WrappedKeyFixture.Mint();
        WrappedKeyFixture revokedKeys = WrappedKeyFixture.Mint();
        await RegisterPasskeyAsync(client, proving, survivingKeys);
        await RegisterPasskeyAsync(client, revoked, revokedKeys);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid provingCredentialId = await ResolveCredentialIdAsync(admin, proving);
        Guid revokedCredentialId = await ResolveCredentialIdAsync(admin, revoked);

        // Both shares are filed before the act, or "one left" is a claim about a row that was never
        // there and "one survived" a claim about a row the arrangement never wrote.
        WrappedAccountKeysRow[] before = await WrappedAccountKeysAsync(admin, userId);
        await Assert.That(before.Any(row => row.FactorId == survivingKeys.Factor)).IsTrue();
        await Assert.That(before.Any(row => row.FactorId == revokedKeys.Factor)).IsTrue();

        // Act — the assertion is signed by the passkey that stays, and the route names the one that
        // goes.
        HttpResponseMessage response = await RevokeAsync(client, proving, userId, revokedCredentialId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        WrappedAccountKeysRow[] rows = await WrappedAccountKeysAsync(admin, userId);

        // The revoked factor's share left with its credential, and it is gone from the whole account
        // rather than merely gone from that credential.
        await Assert.That(rows.Any(row => row.CredentialId == revokedCredentialId)).IsFalse();
        await Assert.That(rows.Any(row => row.FactorId == revokedKeys.Factor)).IsFalse();

        // And the surviving factor holds exactly the bytes it was registered with, in the columns they
        // were filed in.
        WrappedAccountKeysRow[] surviving = [.. rows.Where(row => row.CredentialId == provingCredentialId)];
        await Assert.That(surviving.Length).IsEqualTo(1);
        await Assert.That(surviving[0].FactorId).IsEqualTo(survivingKeys.Factor);
        await Assert.That(Base64UrlText.Encode(surviving[0].WrappedContentKey))
            .IsEqualTo(survivingKeys.WrappedContentKey);
        await Assert.That(Base64UrlText.Encode(surviving[0].WrappedIndexKey))
            .IsEqualTo(survivingKeys.WrappedIndexKey);
    }

    /// <summary>
    /// A passkey holding a live session is revoked, and the request answers <b>200 and not 500</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Named as an outcome on purpose, because the failure names a permission and the cause is the
    /// change tracker.</b> Revoking loads every unrevoked <c>Session</c> of the credential into EF's
    /// change tracker. Remove the <c>Credential</c> with those dependents still tracked and EF
    /// cascades into the copies it can see and emits its own <c>DELETE FROM sessions</c> — on a table
    /// granted <c>SELECT, INSERT, UPDATE (revoked_at_utc)</c> and deliberately <b>no</b>
    /// <c>DELETE</c> — so the request dies with <c>42501</c> and a 500 before it removes anything.
    /// </para>
    /// <para>
    /// <b>Do not answer that 500 with a grant on <c>sessions</c>.</b> The absent <c>DELETE</c> is
    /// argued for in <c>docs/business-logic/sessions.md</c> and pinned by
    /// <c>AppRoleGrantsTests.Database_RefusesToDeleteASession</c>; the session rows are meant to leave
    /// by the database's own <c>ON DELETE CASCADE</c> from <c>credentials</c>, which runs with the
    /// referencing table owner's privileges rather than this role's. The fix is a second
    /// <c>DiscardTrackedEntities()</c> between the revocation and the delete.
    /// <c>EraseAccountHandler</c> documents the identical mechanism for <c>budgets</c>, and
    /// <see cref="Revocation_OfTheCredentialThatProvedIt_Succeeds" /> is the same shape of test for
    /// the tracked public key and signature counter.
    /// </para>
    /// <para>
    /// The proving passkey is the <b>other</b> one deliberately, so the only dependents in the
    /// tracker when the delete runs are the sessions this test arranged. Proving with the revoked
    /// passkey would drag its key and counter in as well and the 500 would no longer say which
    /// tracked collection caused it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_WhenTheCredentialHasLiveSessions_DoesNotFailOnAMissingSessionDeleteGrant()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator proving = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator revoked = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, proving);
        await RegisterPasskeyAsync(client, revoked);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid revokedCredentialId = await ResolveCredentialIdAsync(admin, revoked);

        // At least one live session, because a passkey that never signed in loads nothing into the
        // tracker and this test would then be measuring the plain happy path.
        await SignInAsync(host, revoked, userId);
        await Assert.That(await CountLiveSessionsAsync(admin, revokedCredentialId)).IsEqualTo(1L);

        // Act
        HttpResponseMessage response = await RevokeAsync(client, proving, userId, revokedCredentialId);

        // Assert — both halves stated, because 500 is the specific answer this test exists to refuse
        // and a bare equality check would report it as "not OK".
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// Every refusal the re-authentication gate in front of this route can produce, driven end to end
    /// and compared whole — status and body together, and all of them against each other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this test the gate has no coverage on this route at all.</b> Measured, not supposed:
    /// replacing the handler's <c>await reauthentication.VerifyAsync(…)</c> with
    /// <c>_ = reauthentication;</c> turned nothing red in either suite. With the gate gone a stolen
    /// bearer token on its own revokes any of the victim's passkeys — the single most valuable thing an
    /// attacker can do short of erasure, because taking somebody's remaining sign-in method away is a
    /// lockout, and taking the attacker's <i>own</i> away after they add one is how a compromise is
    /// made permanent.
    /// </para>
    /// <para>
    /// <b>Why this catches that mutation, stated as the failure.</b> Every entry below but one names a
    /// credential this account really owns and could really revoke. With the gate absent the first
    /// entry — an empty body, no proof whatsoever — succeeds with a 200 carrying
    /// <c>sessionsEnded</c>, and every entry after it is answered 404 for a row that is now gone. The
    /// comparison reports that as several distinct responses; the two assertions naming a 401 and the
    /// verification title report it as the wrong response even if some future refactor made them agree
    /// with each other again.
    /// </para>
    /// <para>
    /// <b>The one entry that names no row is what makes the gate's <em>position</em> measurable, and
    /// that is a separate mutation from the gate's absence.</b> Every other entry is a real, revocable
    /// credential, so a handler that ran its owner-scoped lookup <em>before</em> the gate would still
    /// find every one of those rows, still be refused by the gate afterwards, and still answer seven
    /// identical 401s — the reordering is invisible to a list where every lookup succeeds. Against an id
    /// no row carries, lookup-first answers 404 while the seven real ones answer 401, and the body
    /// comparison breaks. The order matters because the 409 this route can raise — "this is the
    /// account's only passkey" — is the one answer here that is not byte-identical to the 401, and an
    /// unproven request must do no work against the database before it earns that sentence.
    /// </para>
    /// <para>
    /// <b>Compared to each other rather than to a literal, because the property is
    /// indistinguishability.</b> A caller able to tell "that challenge was for another ceremony" from
    /// "that passkey is not yours" is mapping which handles exist and which pool a nonce came from
    /// while holding a stolen token. Two reasons compared against each other prove only that those two
    /// agree, so this drives all of them and asserts they collapse to one value.
    /// </para>
    /// <para>
    /// <b>Deliberately shorter than
    /// <see cref="ErasureReauthenticationTests.EveryReachableErasureRefusal_ProducesTheIdenticalResponse" />.</b>
    /// Both endpoints call the same <c>PasskeyReauthentication</c>, one implementation with one list of
    /// steps, so the deeper entries that file drives — untrusted origin, user-handle mismatch, a
    /// <c>webauthn.create</c> client-data type, a counter regression — are pinned there and would be
    /// re-running the same code here. What is <i>not</i> covered anywhere else is that this route
    /// invokes that gate at all, and reaching it does not take an exotic entry. The entries below are
    /// the reachable ones that cost nothing to arrange; adding more is welcome, removing one is not,
    /// which is what <see cref="ReachableRevocationRefusals" /> is for.
    /// </para>
    /// <para>
    /// <b>What this list cannot show is how deep an entry got.</b> Nothing here can observe which step
    /// refused a request — that indistinguishability is the property being asserted — so an entry that
    /// quietly began failing at an earlier step than the one it was written for stays green and stops
    /// covering the step it was added for. Read every step attribution below as what the entry was
    /// built to reach, not as something this comparison measures.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryReachableRevocationRefusal_ProducesTheIdenticalResponse()
    {
        // Arrange — two passkeys on this account, so nothing here is refused by the last-passkey floor,
        // and the one named in the route is the one the proofs do not come from.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        HttpClient anonymous = host.Factory.CreateClient();
        (HttpClient bob, _, _) = await host.Factory.CreateSignedInClientAsync(OtherSubject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator target = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator bobsDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        await RegisterPasskeyAsync(client, target);
        await RegisterPasskeyAsync(bob, bobsDevice);
        byte[] userHandle = PasskeyEncoding.ToUserHandle(userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid targetCredentialId = await ResolveCredentialIdAsync(admin, target);

        // Whole before the act, or the "nothing was taken" assertion at the end is a claim about rows
        // the arrangement never wrote.
        await AssertPasskeyIsWholeAsync(admin, targetCredentialId);

        // Act — every entry names the same real, revocable credential in the route, so the only reason
        // any of them is refused is the proof in the body.
        List<(string Reason, HttpResponseMessage Response)> refusals = [];

        // Every member absent. The request record declares none of them required and nothing registers
        // model validation, so {} binds them all to null and the gate's own decode is the first thing
        // to see it — the same 401 a wrong proof gets, rather than a framework 400 telling a caller
        // that its proof was the thing found wanting.
        refusals.Add((
            "no assertion members",
            await client.PostAsJsonAsync(RevocationPath(targetCredentialId), new { })));

        // The same empty body over an id no row carries, and the only entry here whose route names
        // nothing. It is what pins the ORDER of the gate and the lookup rather than the presence of the
        // gate: under the shipped order both this entry and the seven around it are the gate's 401,
        // because an unproven request reaches no query at all. Move the owner-scoped lookup above the
        // gate and this entry alone becomes a 404 — the row genuinely is not there — while the seven
        // real credentials still answer 401, and the whole-body comparison below names this line as the
        // one that drifted. See the remarks for why the order is a rule and not a preference.
        refusals.Add((
            "unknown credential id",
            await client.PostAsJsonAsync(RevocationPath(Guid.CreateVersion7()), new { })));

        // The sign-in pool, minted from an ANONYMOUS endpoint: anyone who can walk a person through one
        // WebAuthn prompt for this relying party holds a valid response over a nonce they chose the
        // moment for. A gate checking ConsumeAsync for a non-null answer rather than for
        // Reauthentication passes everything else and fails here.
        byte[] assertionChallenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        refusals.Add((
            "assertion challenge",
            await PostRevocationAsync(
                client,
                targetCredentialId,
                device.Authenticate(assertionChallenge, ApiFactory.PasskeyOrigin, userHandle))));

        // The other stale pool. A registration nonce is issued to a signed-in person, which is exactly
        // the stolen-session adversary this gate exists to stop.
        byte[] registrationChallenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        refusals.Add((
            "registration challenge",
            await PostRevocationAsync(
                client,
                targetCredentialId,
                device.Authenticate(registrationChallenge, ApiFactory.PasskeyOrigin, userHandle))));

        // Bob's registered passkey answering a live nonce issued to this account. No user handle, so
        // the gate's owner-scoped credential lookup is the only thing that can refuse it — with a
        // handle present, a lookup that had lost its owner filter would still be turned down by the
        // check below it and this entry would stay green over a gate with no binding left.
        byte[] strangerChallenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        refusals.Add((
            "another account's passkey",
            await PostRevocationAsync(
                client,
                targetCredentialId,
                bobsDevice.Authenticate(strangerChallenge, ApiFactory.PasskeyOrigin, userHandle: null))));

        // One challenge answered twice: the tampered attempt burns it, so the second post is a
        // faultless response refused for nothing but the nonce already being spent. A failed attempt
        // that did not consume would make one issued challenge something an attacker grinds responses
        // against, in front of a lockout.
        byte[] spentChallenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult answered = device.Authenticate(spentChallenge, ApiFactory.PasskeyOrigin, userHandle);
        refusals.Add((
            "invalid signature",
            await PostRevocationAsync(client, targetCredentialId, WithFlippedSignature(answered))));
        refusals.Add(("consumed challenge", await PostRevocationAsync(client, targetCredentialId, answered)));

        // Last, and the ordering is load-bearing: the store sweeps expired rows on every issue, so an
        // expired challenge inserted before any of the options calls above would be collected by one of
        // them and this entry would be refused for a nonce nobody ever issued instead.
        byte[] expiredChallenge = await InsertChallengeAsync(host, ReauthenticationCeremony, expiresInMinutes: -5);
        refusals.Add((
            "expired challenge",
            await PostRevocationAsync(
                client,
                targetCredentialId,
                device.Authenticate(expiredChallenge, ApiFactory.PasskeyOrigin, userHandle))));

        List<(string Reason, string Response)> observed = [];
        foreach ((string reason, HttpResponseMessage response) in refusals)
        {
            observed.Add((reason, $"{(int)response.StatusCode} {await ReadComparableBodyAsync(response)}"));
        }

        // Assert — each refusal against the first, with its own name on both sides of the comparison so
        // a failure says which one drifted rather than only that something did.
        string first = observed[0].Response;
        foreach ((string reason, string response) in observed)
        {
            await Assert.That($"{reason} => {response}").IsEqualTo($"{reason} => {first}");
        }

        await Assert.That(observed.Count).IsEqualTo(ReachableRevocationRefusals);
        await Assert.That(observed.Select(entry => entry.Response).Distinct().Count()).IsEqualTo(1);

        // The one value they collapse to is the gate's 401, and not some other response they happen to
        // share: a route that answered every one of these identically for a reason unrelated to the
        // gate would satisfy the comparison above and nothing else here.
        await Assert.That(refusals[0].Response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(first.Contains(PasskeyVerificationExceptionHandler.Title, StringComparison.Ordinal))
            .IsTrue();

        // And no refusal took anything on the way to saying no.
        await AssertPasskeyIsWholeAsync(admin, targetCredentialId);
    }

    /// <summary>
    /// A caller carrying no token at all is refused by the fallback policy, and the title says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three refusals answer 401 on this route and the status cannot tell them apart: the fallback
    /// authorization policy turning an anonymous caller away before the route is reached, the
    /// provisioning middleware finding no account for an authenticated principal, and the
    /// re-authentication gate refusing a proof. Only the middleware's carries
    /// <see cref="UserProvisioningMiddleware.NoAccountTitle" /> and only the gate's carries
    /// <see cref="PasskeyVerificationExceptionHandler.Title" />; the anonymous one is titled
    /// <c>"Unauthorized"</c> from the status code alone, because <c>UseStatusCodePages</c> writes it
    /// with no title of its own.
    /// </para>
    /// <para>
    /// Both inequalities, because each rules out a different way the door could be standing open.
    /// Without the middleware one, a route that had lost its authorization entirely still passes here —
    /// an anonymous request would walk on to the provisioning middleware, find no account for a
    /// principal it cannot even name, and be answered that middleware's 401. Without the gate one, a
    /// route reached anonymously and refused only by the ceremony would pass, and that would mean an
    /// unauthenticated caller reaching a handler at all.
    /// </para>
    /// <para>
    /// This is <c>SignedInUserEndpointTests</c>'s shape, for the reason it gives. The route names an id
    /// no row carries, since nothing about this request is supposed to reach a lookup.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_ByAnUnauthenticatedCaller_IsRefusedWithATitleTheGateNeverWrites()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();

        // Act — no subject header, so nothing authenticates and the fallback policy decides.
        HttpResponseMessage response = await host.Factory
            .CreateClient()
            .PostAsJsonAsync(RevocationPath(Guid.CreateVersion7()), new { });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        string title = await ReadTitleAsync(response);
        await Assert.That(title).IsNotEqualTo(UserProvisioningMiddleware.NoAccountTitle);
        await Assert.That(title).IsNotEqualTo(PasskeyVerificationExceptionHandler.Title);
    }

    /// <summary>
    /// The three rows one registered passkey owns, counted by the <c>credentials.id</c> they all hang
    /// off.
    /// </summary>
    private readonly record struct PasskeyRowCounts(long Credentials, long PublicKeys, long Counters);

    /// <summary>
    /// One <c>wrapped_account_keys</c> row, every column of it.
    /// </summary>
    /// <remarks>
    /// <c>credential_type</c> as the column spells it rather than as the enum member it parses to, so a
    /// reader of a failure sees the token the database actually holds. <c>created_at_utc</c> is absent:
    /// nothing here asks when a factor's share was filed, only which factor holds which bytes.
    /// </remarks>
    private readonly record struct WrappedAccountKeysRow(
        Guid CredentialId,
        Guid FactorId,
        Guid UserId,
        string CredentialType,
        byte[] WrappedContentKey,
        byte[] WrappedIndexKey);

    /// <summary>
    /// The route the revocation is posted to. The <c>{credentialId}</c> segment is a
    /// <c>credentials.id</c>, which is a different id space from the WebAuthn handle the body carries
    /// under the same word — see the class remarks.
    /// </summary>
    private static string RevocationPath(Guid credentialId) =>
        $"/api/me/credentials/{credentialId}/revocation";

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because every
    /// authenticated request below authenticates from a session cookie rather than from a provider
    /// bearer.
    /// </summary>
    /// <remarks>
    /// Kept beside <see cref="StartHostAsync" /> rather than replacing it, for two tests that cannot
    /// use it. <see cref="Revocation_OfTheOnlyRemainingPasskey_IsRefusedWithConflictAndRemovesNothing" />
    /// needs an account holding exactly one passkey and the sign-in harness seeds one of its own, so
    /// the floor it measures would never fire; and
    /// <see cref="Revocation_ByAnUnauthenticatedCaller_IsRefusedWithATitleTheGateNeverWrites" /> is
    /// about which refusal answers a caller carrying nothing.
    /// </remarks>
    private static async Task<PostgresTestHost> StartSignedInHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// Runs both authenticated legs of a registration, so the account really holds a passkey a
    /// signature answers to rather than material seeded out of band.
    /// </summary>
    /// <remarks>
    /// The establishing call is kept for the one test still driven by a provider bearer —
    /// <see cref="Revocation_OfTheOnlyRemainingPasskey_IsRefusedWithConflictAndRemovesNothing" /> —
    /// whose account has to hold exactly one passkey and therefore cannot come from
    /// <see cref="ApiFactory.CreateSignedInClientAsync" />, which seeds one of its own. On a
    /// cookie-carrying client the account already exists and the call is a read that changes nothing.
    /// </remarks>
    /// <param name="wrappedKeys">
    /// The share of the account keys this factor is to hold. Null mints a fresh one, which is what
    /// every test that is not about the wrapped keys wants — and it has to be fresh, because
    /// <c>factor_id</c> is the table's primary key — <c>PK_wrapped_account_keys</c> — so it is unique
    /// table-wide, and most tests here register two passkeys onto one account.
    /// </param>
    private static async Task RegisterPasskeyAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        WrappedKeyFixture? wrappedKeys = null)
    {
        await ApiFactory.EstablishAccountAsync(client);

        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true);
        WrappedKeyFixture keys = wrappedKeys ?? WrappedKeyFixture.Mint();
        HttpResponseMessage response = await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
            factorId = keys.FactorId,
            wrappedContentKey = keys.WrappedContentKey,
            wrappedIndexKey = keys.WrappedIndexKey,
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Runs the whole revocation ceremony: the re-authentication options leg,
    /// <paramref name="device" /> answering the nonce it issued, and the revocation post.
    /// </summary>
    /// <param name="device">The authenticator that proves presence — not necessarily the one removed.</param>
    /// <param name="credentialId">The <c>credentials.id</c> of the passkey to remove.</param>
    private static async Task<HttpResponseMessage> RevokeAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId,
        Guid credentialId)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId));

        return await PostRevocationAsync(client, credentialId, assertion);
    }

    /// <summary>
    /// Posts one revocation with an assertion the caller built, for the tests that need a proof the
    /// ceremony above would never produce — a stale pool, a foreign device, a flipped signature.
    /// </summary>
    /// <remarks>
    /// The body shape is the erasure request's, members and all, so a caller holding a stolen bearer
    /// token learns nothing from the difference between the two gates.
    /// </remarks>
    private static Task<HttpResponseMessage> PostRevocationAsync(
        HttpClient client,
        Guid credentialId,
        AssertionResult assertion) =>
        client.PostAsJsonAsync(RevocationPath(credentialId), new
        {
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });

    /// <summary>
    /// The same ceremony with one bit of the signature moved, so the response is genuine in every
    /// respect except the one under test.
    /// </summary>
    private static AssertionResult WithFlippedSignature(AssertionResult result)
    {
        byte[] signature = [.. result.Signature];
        signature[^1] ^= 0xFF;

        return result with { Signature = signature };
    }

    /// <summary>
    /// Writes a challenge row of a named ceremony directly and hands back its bytes, so an
    /// authenticator can sign a nonce whose age this test chose.
    /// </summary>
    /// <remarks>
    /// The bytes are returned because signing them is the whole point: everything about the resulting
    /// request is genuine except how old the nonce is. <c>created_at_utc</c> is always ten minutes
    /// back, because <c>CK_webauthn_challenges_lifetime</c> refuses a row whose expiry is at or before
    /// its creation. Written on the container superuser, like every other out-of-band statement here.
    /// </remarks>
    private static async Task<byte[]> InsertChallengeAsync(
        PostgresTestHost host,
        string ceremony,
        int expiresInMinutes)
    {
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        DateTime nowUtc = DateTime.UtcNow;

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            insert into webauthn_challenges (id, challenge, ceremony, created_at_utc, expires_at_utc)
            values (@id, @challenge, @ceremony, @created, @expires)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("challenge", challenge);
        command.Parameters.AddWithValue("ceremony", ceremony);
        command.Parameters.AddWithValue("created", nowUtc.AddMinutes(-10));
        command.Parameters.AddWithValue("expires", nowUtc.AddMinutes(expiresInMinutes));
        await command.ExecuteNonQueryAsync();

        return challenge;
    }

    /// <summary>
    /// Signs in for real: both anonymous assertion legs, which is what writes a <c>sessions</c> row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no other way to arrange a session. A verified passkey assertion is the only thing
    /// that establishes one, and seeding the row out of band would arrange rows the product never
    /// wrote — which is precisely the shape of arrangement the tests here refuse elsewhere.
    /// </para>
    /// <para>
    /// On a client carrying no token, because a sign-in by definition happens before anyone is signed
    /// in — and because the account's own bearer token has nothing to do with whether the signature
    /// verifies.
    /// </para>
    /// <para>
    /// <paramref name="signCount" /> stays at zero, which is what an authenticator backing a synced
    /// passkey reports every time. <c>PasskeySignatureCounter.Accept</c> reads a repeated zero as no
    /// movement rather than as a clone, so a passkey can sign in as many times as a test needs
    /// without the test keeping a counter ledger — and the stored counter is left where the
    /// re-authentication in <see cref="RevokeAsync" />, which reports one, expects to find it.
    /// </para>
    /// </remarks>
    private static async Task SignInAsync(PostgresTestHost host, SyntheticAuthenticator device, Guid userId)
    {
        HttpClient anonymous = host.Factory.CreateClient();
        byte[] challenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount: 0);

        HttpResponseMessage response = await anonymous.PostAsJsonAsync(AssertionPath, new
        {
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Runs an options leg and returns the challenge bytes it issued.</summary>
    private static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.PostAsync(path, content: null);
        response.EnsureSuccessStatusCode();
        JsonNode options = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
    }

    /// <summary>
    /// The response body as an object, so a test can ask whether a member is <b>there</b> and not
    /// only what it deserializes to.
    /// </summary>
    private static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!.AsObject();

    /// <summary>
    /// The whole response body, with the one member that varies per <b>request</b> rather than per
    /// <b>cause</b> replaced by a fixed placeholder.
    /// </summary>
    /// <remarks>
    /// <c>traceId</c> is a new value on every request, including two requests refused for the identical
    /// reason, so comparing it would compare the trace and not the refusal. The member is replaced
    /// rather than removed, so a <c>traceId</c> that stopped being emitted still fails and any other
    /// member appearing, disappearing or differing fails with it.
    /// </remarks>
    private static async Task<string> ReadComparableBodyAsync(HttpResponseMessage response)
    {
        JsonObject body = await ReadJsonObjectAsync(response);
        if (body.ContainsKey(TraceIdMember))
        {
            body[TraceIdMember] = "<one per request>";
        }

        return body.ToJsonString();
    }

    /// <summary>
    /// The <c>title</c> of a problem-details body, which is the only member that says which of this
    /// route's several 401s answered.
    /// </summary>
    private static async Task<string> ReadTitleAsync(HttpResponseMessage response) =>
        (await ReadJsonObjectAsync(response))["title"]!.GetValue<string>();

    /// <summary>
    /// Reads back the user provisioning minted for <paramref name="subject" />. Nothing the API
    /// returns names it, so the lookup goes through the credential the middleware resolved on.
    /// </summary>
    private static async Task<Guid> ResolveUserIdAsync(PostgresTestHost host, string subject)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select user_id from credentials where provider = 'google' and subject = @subject",
            connection);
        command.Parameters.AddWithValue("subject", subject);

        return await command.ExecuteScalarAsync() switch
        {
            Guid userId => userId,
            var unexpected => throw new InvalidOperationException(
                $"Provisioning wrote no account for subject '{subject}', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// The <c>credentials.id</c> of the federated Google credential provisioning minted for
    /// <paramref name="subject" /> — the row the account signs in on, and the one row of the account
    /// that this route must never be able to name.
    /// </summary>
    /// <remarks>
    /// Resolved on the same predicate <see cref="ResolveUserIdAsync" /> uses and selecting the other
    /// column, because there is no other way to learn the id: nothing the API returns names a
    /// credential, and the federated one has no WebAuthn handle to look it up by.
    /// </remarks>
    private static async Task<Guid> ResolveFederatedCredentialIdAsync(
        NpgsqlConnection admin,
        string subject)
    {
        await using NpgsqlCommand command = new(
            "select id from credentials where provider = 'google' and subject = @subject",
            admin);
        command.Parameters.AddWithValue("subject", subject);

        return await command.ExecuteScalarAsync() switch
        {
            Guid credentialId => credentialId,
            var unexpected => throw new InvalidOperationException(
                $"Provisioning wrote no federated credential for subject '{subject}', "
                + $"got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Translates a device's WebAuthn handle into the <c>credentials.id</c> the route names. The two
    /// id spaces meet here and nowhere else: registration returns 201 with no body, so the only way to
    /// learn which row a device produced is to read the key material filed under its handle.
    /// </summary>
    private static async Task<Guid> ResolveCredentialIdAsync(
        NpgsqlConnection admin,
        SyntheticAuthenticator device)
    {
        await using NpgsqlCommand command = new(
            "select credential_id from passkey_public_keys where webauthn_credential_id = @handle",
            admin);
        command.Parameters.AddWithValue("handle", device.CredentialId);

        return await command.ExecuteScalarAsync() switch
        {
            Guid credentialId => credentialId,
            var unexpected => throw new InvalidOperationException(
                $"No passkey was registered for that handle, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// The other half of every "the rows are gone" assertion. Without it a test whose registration
    /// silently wrote nothing passes with rows that never existed — and the counter is the live
    /// hazard rather than a theoretical one, since a passkey registered at zero is the case a future
    /// "only write the counter when it is non-zero" would leave unseeded.
    /// </summary>
    private static async Task AssertPasskeyIsWholeAsync(NpgsqlConnection admin, Guid credentialId)
    {
        PasskeyRowCounts counts = await CountPasskeyRowsAsync(admin, credentialId);
        await Assert.That(counts.Credentials).IsEqualTo(1L);
        await Assert.That(counts.PublicKeys).IsEqualTo(1L);
        await Assert.That(counts.Counters).IsEqualTo(1L);
    }

    private static async Task<PasskeyRowCounts> CountPasskeyRowsAsync(
        NpgsqlConnection admin,
        Guid credentialId) =>
        new(
            await CountAsync(admin, "select count(*) from credentials where id = @id", credentialId),
            await CountAsync(
                admin,
                "select count(*) from passkey_public_keys where credential_id = @id",
                credentialId),
            await CountAsync(
                admin,
                "select count(*) from passkey_signature_counters where credential_id = @id",
                credentialId));

    /// <summary>
    /// Every <c>wrapped_account_keys</c> row of one account, on the container superuser connection.
    /// </summary>
    /// <remarks>
    /// Unfiltered by credential, so a test can say a row <b>left the account</b> rather than only that
    /// it left the credential it was filed under. On the superuser connection for the reason
    /// <see cref="CountLiveSessionsAsync" /> gives: this table carries <c>user_isolation</c>, so a
    /// policed read would make a surviving row look absent — and a surviving row is what most of the
    /// claims here are about.
    /// </remarks>
    private static async Task<WrappedAccountKeysRow[]> WrappedAccountKeysAsync(
        NpgsqlConnection admin,
        Guid userId)
    {
        await using NpgsqlCommand command = new(
            """
            select credential_id, factor_id, user_id, credential_type, wrapped_content_key, wrapped_index_key
            from wrapped_account_keys
            where user_id = @userId
            order by created_at_utc
            """,
            admin);
        command.Parameters.AddWithValue("userId", userId);

        List<WrappedAccountKeysRow> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new WrappedAccountKeysRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetFieldValue<byte[]>(4),
                reader.GetFieldValue<byte[]>(5)));
        }

        return [.. rows];
    }

    /// <summary>
    /// The credential's sessions that nothing has revoked, on the superuser connection.
    /// </summary>
    /// <remarks>
    /// <c>sessions</c> carries <c>user_isolation</c>, which is <c>FOR ALL</c>, so a policed
    /// connection reports zero rows for a session that is still there exactly as it does for one that
    /// is gone — the same reason every other count in this file goes through
    /// <see cref="PostgresTestHost.ConnectionString" />.
    /// </remarks>
    private static Task<long> CountLiveSessionsAsync(NpgsqlConnection admin, Guid credentialId) =>
        CountAsync(
            admin,
            "select count(*) from sessions where credential_id = @id and revoked_at_utc is null",
            credentialId);

    /// <summary>
    /// Every session of the credential, revoked or not — the other half of an "it is still
    /// unrevoked" claim, which a deleted row would otherwise satisfy by making both numbers zero.
    /// </summary>
    private static Task<long> CountSessionsAsync(NpgsqlConnection admin, Guid credentialId) =>
        CountAsync(admin, "select count(*) from sessions where credential_id = @id", credentialId);

    private static async Task<long> CountAsync(NpgsqlConnection admin, string sql, Guid credentialId)
    {
        await using NpgsqlCommand command = new(sql, admin);
        command.Parameters.AddWithValue("id", credentialId);

        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count, got '{unexpected ?? "null"}'."),
        };
    }
}
