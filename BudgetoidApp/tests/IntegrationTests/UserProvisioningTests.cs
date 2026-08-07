using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.Passkeys;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// When an authenticated request is allowed to bring an account into existence, and what happens when
/// it is not.
/// </summary>
/// <remarks>
/// <para>
/// A Google id token outlives an erasure by up to an hour. While it is still valid, any request that
/// provisions on sight turns one in-flight poll or one forgotten second tab into a resurrected
/// account — and the resurrected account holds no passkey, so the erasure endpoint refuses it forever.
/// Leaving would stop meaning leaving. Provisioning is therefore opt-in per route group: the six data
/// groups mint, everything else resolves or is refused.
/// </para>
/// <para>
/// Every count is read on <see cref="PostgresTestHost.ConnectionString" /> — the container superuser —
/// and never on the application role. <c>user_isolation</c> is <c>FOR ALL</c>, so a policed connection
/// reports zero rows for a row that is still there exactly as it does for one that is gone: read on the
/// app role, "no account was written" could not fail.
/// </para>
/// <para>
/// These live beside <c>AuthenticationTests</c> rather than inside it because that file is about the
/// claim gate — which claims are required and what a request missing one is told — and this one is
/// about what the request is then allowed to create. Only
/// <see cref="AuthenticatedRequest_MissingEmailVerifiedClaim_IsStillRefusedOnAMarkedRoute" /> spans
/// both, and it is here because the thing it pins is the order of the two.
/// </para>
/// </remarks>
public sealed class UserProvisioningTests
{
    private const string ErasurePath = "/api/me/erasure";
    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";
    private const string AssertionOptionsPath = "/api/passkeys/assertion/options";
    private const string AssertionPath = "/api/passkeys/assertion";

    /// <summary>
    /// The refusal a request whose email is present but not asserted verified receives. Copied from the
    /// middleware's own sentence rather than referenced, exactly as <c>AuthenticationTests</c> does: the
    /// point of pinning it is that a caller's corrective action is different for this 401 than for the
    /// others, and a shared constant would let the sentence and the expectation move together.
    /// </summary>
    private const string UnverifiedEmailTitle =
        "Authenticated principal's email address is not asserted as verified.";

    /// <summary>
    /// The erasure route mints nothing, even for a subject the product has never seen.
    /// </summary>
    /// <remarks>
    /// This is the test that separates "erasure works after an erasure" from "erasure works". A marker
    /// applied opt-out — every group provisions unless it says otherwise — leaves the second-erasure
    /// case green and this one red, because nobody would have thought to exempt a route a brand-new
    /// subject was never expected to reach.
    /// </remarks>
    [Test]
    public async Task AuthenticatedRequestToAnErasureRoute_WithNoAccount_WritesNoUserRow()
    {
        // Arrange — a subject with no account at all and no erasure behind it, so nothing about this
        // case depends on the erasure path having run.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-never-seen");

        // Act — a well-formed body on purpose: a request turned away for its shape would say nothing
        // about whether reaching this route creates an account.
        HttpResponseMessage response = await client.PostAsJsonAsync(ErasurePath, new
        {
            credentialId = "AA",
            clientDataJson = "AA",
            authenticatorData = "AA",
            signature = "AA",
            userHandle = (string?)null,
        });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await CountUsersAsync(admin)).IsEqualTo(0L);
        await Assert.That(await CountCredentialsAsync(admin)).IsEqualTo(0L);
        await Assert.That(await CountBudgetsAsync(admin)).IsEqualTo(0L);
    }

    /// <summary>
    /// The control for the whole change: signing in for the first time still sets an account up.
    /// </summary>
    /// <remarks>
    /// Without this, "nothing ever provisions" satisfies every refusal test in this file and every
    /// no-row assertion in the erasure suite. It is the one test that fails if the marker is applied
    /// nowhere.
    /// </remarks>
    [Test]
    public async Task FirstAuthenticatedRequestToADataRoute_StillProvisions()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-first-visit");

        // Act — the ordinary first request a signed-in person makes.
        HttpResponseMessage response = await client.GetAsync(ApiFactory.AccountProvisioningPath);

        // Assert — a normal answer, and exactly one account behind it. Exactly one rather than at least
        // one: a route that provisioned twice would satisfy a non-zero check.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await CountUsersAsync(admin)).IsEqualTo(1L);
        await Assert.That(await CountCredentialsAsync(admin)).IsEqualTo(1L);
        await Assert.That(await CountBudgetsAsync(admin)).IsEqualTo(1L);
    }

    /// <summary>
    /// The second control: an account that does exist reaches the unmarked routes normally, and those
    /// requests write nothing whatsoever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reaching-them-normally half is why this test was written, and it still carries it: without
    /// it the fix could be "every unmarked route answers 401", which passes both refusal tests above
    /// and locks every signed-in person out of registering a passkey or erasing their account. That
    /// claim now lives in the three status assertions rather than in the name. The erasure call is
    /// deliberately refused <b>by the ceremony</b> and the title is what says so: reaching the gate at
    /// all is the claim, and a provisioning refusal would carry a different sentence.
    /// </para>
    /// <para>
    /// <b>A pin rather than a driver — it is green before the change and after it.</b> The name is
    /// what changed: "an unmarked route writes nothing" was a slight overstatement while the budget
    /// heal existed, because the resolve path really could insert a row, and only the fact that these
    /// accounts already own their budget kept it from doing so here. With the heal gone the resolve
    /// path has no insert left at all, so the sentence is now literally true and the counts say it for
    /// all three tables instead of for <c>users</c> alone.
    /// </para>
    /// <para>
    /// Before and after rather than against literals: the subject is that the three calls changed
    /// nothing, and a pair of literals would also be satisfied by a run that deleted a row and minted
    /// a replacement.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AuthenticatedRequestToAnUnmarkedRoute_WithAnExistingAccount_WritesNothing()
    {
        // Arrange — one request to a marked route, which is the whole of how an account comes to exist.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-established");
        await ApiFactory.EstablishAccountAsync(client);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        (long usersBefore, long credentialsBefore, long budgetsBefore) = (
            await CountUsersAsync(admin),
            await CountCredentialsAsync(admin),
            await CountBudgetsAsync(admin));

        // The account really is complete before the act, so a later "unchanged" means unchanged from
        // something rather than unchanged from nothing.
        await Assert.That(usersBefore).IsEqualTo(1L);
        await Assert.That(credentialsBefore).IsEqualTo(1L);
        await Assert.That(budgetsBefore).IsEqualTo(1L);

        // Act — one endpoint from each unmarked group an authenticated caller may reach.
        HttpResponseMessage registrationOptions = await client.PostAsync(RegistrationOptionsPath, content: null);
        HttpResponseMessage reauthenticationOptions =
            await client.PostAsync(ReauthenticationOptionsPath, content: null);
        HttpResponseMessage erasure = await client.PostAsJsonAsync(ErasurePath, new { });

        // Assert
        await Assert.That(registrationOptions.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(reauthenticationOptions.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(erasure.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(erasure)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);

        // And the three calls wrote nothing at all — not a user, not a credential, not a budget.
        await Assert.That(await CountUsersAsync(admin)).IsEqualTo(usersBefore);
        await Assert.That(await CountCredentialsAsync(admin)).IsEqualTo(credentialsBefore);
        await Assert.That(await CountBudgetsAsync(admin)).IsEqualTo(budgetsBefore);
    }

    /// <summary>
    /// The <see cref="Microsoft.AspNetCore.Authorization.IAllowAnonymous" /> arm: a stale provider token
    /// on the sign-in legs changes nothing, and creates nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the exact shape the defect takes in a browser. The person erases their account, the tab
    /// stays open, and the next thing the application does is a passkey sign-in — with the dead Google
    /// token still attached, because nothing has told the client to drop it. Refusing that request would
    /// break sign-in for an hour; provisioning on it resurrects the account it just destroyed. The
    /// third answer is the right one: an unresolvable credential on an anonymous route is treated
    /// exactly as no credential at all.
    /// </para>
    /// <para>
    /// The control is the same sign-in run again with no bearer header. Two different answers would mean
    /// the token is reaching the ceremony, which it must not — the session belongs to whoever the
    /// verified passkey belongs to, and to nobody the request happened to be carrying.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AssertionLegs_AcceptAStaleBearerTokenForAnErasedAccount()
    {
        // Arrange — the account that leaves, erased through the real ceremony.
        const string erasedSubject = "google-erased-holder";
        const string signingInSubject = "google-signing-in";
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient leaving = host.Factory.CreateAuthenticatedClient(erasedSubject);
        SyntheticAuthenticator leavingDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await ApiFactory.EstablishAccountAsync(leaving);
        await RegisterPasskeyAsync(leaving, leavingDevice);
        HttpResponseMessage erasure = await EraseAsync(leaving, leavingDevice, await ResolveUserIdAsync(host, erasedSubject));

        // A second account with a passkey of its own, because a sign-in has to reach an account that
        // exists: what is under test is the anonymous legs' treatment of the dead token, not the
        // reachability of an erased account's material.
        HttpClient returning = host.Factory.CreateAuthenticatedClient(signingInSubject);
        SyntheticAuthenticator returningDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await ApiFactory.EstablishAccountAsync(returning);
        await RegisterPasskeyAsync(returning, returningDevice);
        Guid returningUserId = await ResolveUserIdAsync(host, signingInSubject);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        long usersBeforeSignIn = await CountUsersAsync(admin);
        await Assert.That(usersBeforeSignIn).IsEqualTo(1L);

        // Act — the same sign-in twice: once carrying the erased account's still-valid token, once
        // carrying nothing at all. Everything the gate looks at is identical except the bearer header
        // and the signature counter, and the counter has to move. One device signs both ceremonies, and
        // a passkey that reported the same counter twice is what a cloned one looks like — so the
        // second assertion would be refused with a 401 about the counter, which has nothing to do with
        // the token this test is about, and the control would fail while the stale-token call passed.
        // The value is the caller's to supply because SyntheticAuthenticator keeps no counter state:
        // Authenticate takes signCount as a parameter defaulting to 1, so two calls left at the default
        // report the same number. Registration files the counter at 0, so 1 then 2 is the first
        // advancing pair.
        HttpClient stale = host.Factory.CreateAuthenticatedClient(erasedSubject);
        HttpResponseMessage withStaleToken =
            await SignInAsync(stale, returningDevice, returningUserId, signCount: 1);
        HttpResponseMessage withoutToken =
            await SignInAsync(host.Factory.CreateClient(), returningDevice, returningUserId, signCount: 2);

        // Assert
        await Assert.That(erasure.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(withStaleToken.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The control, compared against each other rather than each against a literal: the claim is that
        // the token makes no difference, so the two answers have to be the same answer.
        await Assert.That(withStaleToken.StatusCode).IsEqualTo(withoutToken.StatusCode);
        await Assert.That(await ReadKindAsync(withStaleToken)).IsEqualTo(await ReadKindAsync(withoutToken));

        // And the dead token resurrected nothing on its way through.
        await Assert.That(await CountUsersAsync(admin)).IsEqualTo(usersBeforeSignIn);
    }

    /// <summary>
    /// The sign-in ceremony starts for a token that carries no <c>email_verified</c> claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim gate runs above the endpoint lookup, so it refuses before anything has asked whether
    /// this route is anonymous at all. The consequence is not theoretical: a person revokes the email
    /// grant in their provider account while leaving the application authorized, the client interceptor
    /// keeps attaching the id token to every <c>/api/</c> call, and passkey sign-in — the one path whose
    /// whole purpose is to work without the provider — is refused on a claim this ceremony never reads
    /// and never stores.
    /// </para>
    /// <para>
    /// Driven on the <b>options</b> leg alone, and paired with the finish leg below rather than folded
    /// into it, because the two legs fail independently: a middleware that let the options leg through
    /// and still refused the finish would issue a challenge nobody can spend, which reads to a caller as
    /// a broken sign-in and to this test as a pass.
    /// </para>
    /// <para>
    /// The account behind the subject is deliberately absent. Sign-in has to start for a caller the
    /// application cannot identify yet — that is what "anonymous" means here — and seeding one would let
    /// a fix that merely resolves earlier pass.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AssertionOptions_WithATokenMissingTheEmailVerifiedClaim_AreStillIssued()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client =
            host.Factory.CreateAuthenticatedClientWithoutEmailVerifiedClaim("google-grant-revoked");

        // Act
        HttpResponseMessage response = await client.PostAsync(AssertionOptionsPath, content: null);

        // Assert — the status first, so the refusal this test is about is what a failure names, rather
        // than a missing member of a problem-details body.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // And a spendable challenge really came back. A 200 carrying no challenge would be a route that
        // answers without minting a nonce, which no client can sign against.
        JsonNode options = await ReadJsonAsync(response);
        byte[] challenge = Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
        await Assert.That(challenge.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// And the sign-in it starts finishes: a session is established for a token carrying no
    /// <c>email_verified</c> claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from the options leg above on purpose. Only the finish leg proves a person actually got
    /// back in — the options leg proves a challenge was issued, which is a strictly weaker claim and one
    /// a half-fixed middleware satisfies.
    /// </para>
    /// <para>
    /// The passkey is registered by the <b>same</b> subject on a client whose token is complete, because
    /// that is the sequence the defect describes: the grant is revoked after the passkey exists, not
    /// before. Registration is authenticated and marked, so it needs the verified address; only the
    /// sign-in that comes afterwards does not.
    /// </para>
    /// <para>
    /// One accepted assertion, so the device's default counter of 1 is left alone —
    /// <c>SyntheticAuthenticator</c> holds no counter state, and a second accepted ceremony at the same
    /// value is what a cloned authenticator looks like.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Assertion_WithATokenMissingTheEmailVerifiedClaim_CompletesTheSignIn()
    {
        // Arrange — the account and its passkey, established while the address was still asserted
        // verified.
        const string subject = "google-grant-revoked-later";
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient beforeRevocation = host.Factory.CreateAuthenticatedClient(subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await ApiFactory.EstablishAccountAsync(beforeRevocation);
        await RegisterPasskeyAsync(beforeRevocation, device);
        Guid userId = await ResolveUserIdAsync(host, subject);

        // Act — the same person, same device, and a token the provider now issues without the claim.
        HttpClient afterRevocation =
            host.Factory.CreateAuthenticatedClientWithoutEmailVerifiedClaim(subject);
        HttpResponseMessage response = await SignInAsync(afterRevocation, device, userId, signCount: 1);

        // Assert — signed in, and to the whole account rather than a locked session: a passkey is what
        // the full kind is derived from, so a 200 carrying anything else would mean the ceremony was
        // completed by something other than the passkey.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await ReadKindAsync(response)).IsEqualTo("full");
    }

    /// <summary>
    /// A sign-in belongs to whoever the verified passkey belongs to, never to whoever the request
    /// happened to be carrying a token for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test passes before the change and after it, and has no control that can be made to fail
    /// by reverting the fix. It is a pinning test, not a driver.</b>
    /// <c>CompleteAssertionHandler</c> never reads <see cref="Application.Abstractions.IUserContext" />
    /// for the account — it takes the account off the credential and publishes it over whatever
    /// provisioning left — so the property holds today by that handler's own construction.
    /// </para>
    /// <para>
    /// It is written anyway because the change underneath it moves the property from one kind of
    /// guarantee to another. Today the middleware resolves the bearer token's account and publishes it
    /// before the ceremony runs, and the handler <em>overwrites</em> that identity: the pairing is
    /// defended against. Once the anonymous arm is read first, the sign-in legs resolve no identity at
    /// all and there is nothing to overwrite: the pairing cannot arise. This is the test that fails if
    /// someone later moves the anonymous arm back below credential resolution <b>and</b> the handler
    /// starts trusting what provisioning published — the combination that would hand the passkey
    /// owner's session to the token holder, and the one nothing else in this suite would notice.
    /// </para>
    /// <para>
    /// Both accounts are live. The erased-account case is
    /// <see cref="AssertionLegs_AcceptAStaleBearerTokenForAnErasedAccount" /> and asks a different
    /// question — that one is about a token that resolves to nobody, this one about a token that
    /// resolves to somebody else.
    /// </para>
    /// <para>
    /// <c>PasskeyCeremonyTests.Assertion_PresentedWithAnotherUsersBearerToken_EstablishesTheSessionForThePasskeysOwner</c>
    /// states the same property from the ceremony's side, and states it as the handler's rule. The
    /// difference is what produced the two accounts: that one seeds both rows directly and never runs
    /// provisioning, so it says nothing about the middleware. Here both accounts and the passkey are
    /// established through the real endpoints, which is what puts the middleware's ordering between the
    /// token and the ceremony. If the two are ever collapsed, this is the one to keep and that is the
    /// reason.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AssertionLegs_CarryingAnotherLiveAccountsToken_SignInAsThePasskeysOwner()
    {
        // Arrange — the account whose token rides along, signed in the ordinary way and holding no
        // passkey of its own.
        const string tokenHolderSubject = "google-token-holder";
        const string passkeyOwnerSubject = "google-passkey-owner";
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient tokenHolder = host.Factory.CreateAuthenticatedClient(tokenHolderSubject);
        await ApiFactory.EstablishAccountAsync(tokenHolder);
        Guid tokenHolderUserId = await ResolveUserIdAsync(host, tokenHolderSubject);

        // The account the sign-in is actually for, with a real passkey behind it.
        HttpClient passkeyOwner = host.Factory.CreateAuthenticatedClient(passkeyOwnerSubject);
        SyntheticAuthenticator ownerDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await ApiFactory.EstablishAccountAsync(passkeyOwner);
        await RegisterPasskeyAsync(passkeyOwner, ownerDevice);
        Guid passkeyOwnerUserId = await ResolveUserIdAsync(host, passkeyOwnerSubject);

        // Two distinct accounts, asserted rather than assumed: if provisioning ever collapsed two
        // subjects onto one row, every assertion below would hold for the wrong reason.
        await Assert.That(tokenHolderUserId).IsNotEqualTo(passkeyOwnerUserId);

        // Act — both legs on a client carrying the token holder's valid bearer, the owner's device
        // signing. A fresh client rather than the one above so the header set is the only thing shared.
        HttpResponseMessage response = await SignInAsync(
            host.Factory.CreateAuthenticatedClient(tokenHolderSubject),
            ownerDevice,
            passkeyOwnerUserId,
            signCount: 1);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Exactly one session, and it names the passkey's owner. Compared as the whole set rather than
        // "contains the owner": a run that opened a session for each account would satisfy a contains
        // check, and handing the token holder a session is the failure this test exists for. Joined into
        // one string so a failure prints which account it went to.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(string.Join(", ", await ReadSessionOwnersAsync(admin)))
            .IsEqualTo(passkeyOwnerUserId.ToString());

        // And the token holder's account is otherwise as it was — nothing minted on top of the two.
        await Assert.That(await CountUsersAsync(admin)).IsEqualTo(2L);
    }

    /// <summary>
    /// The refusal is the middleware's own, produced before anything downstream is asked for a budget.
    /// </summary>
    /// <remarks>
    /// The status alone would be satisfied by two wrong answers. A request that fell through to the
    /// endpoint would ask <c>IUserContext.UserId</c> for an identity nobody published and surface as a
    /// 500; one that reached the database on an unset <c>app.current_user_id</c> would meet
    /// <c>''::uuid</c> in the policy and fail with <c>22P02</c>, which is also not a 401 anyone designed.
    /// The title is what says the request stopped in the middleware, and it must be its own — telling
    /// this refusal apart from a refused passkey is a distinction the caller needs, since the corrective
    /// action is "sign in again", not "try another device".
    /// </remarks>
    [Test]
    public async Task AuthenticatedRequestWithNoAccount_IsRefusedBeforeAnyBudgetIsRead()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-unknown-subject");

        // Act — an unmarked route whose handler would need both an identity and an ambient budget.
        // The body is read once and held: HttpContent hands back the same stream on every call, so a
        // second read would come back empty and the assertions would be measuring the reader.
        HttpResponseMessage response = await client.PostAsync(RegistrationOptionsPath, content: null);
        string title = await ReadTitleAsync(response);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        await Assert.That(title).IsEqualTo(UserProvisioningMiddleware.NoAccountTitle);

        // Distinct from the two 401s already reachable on this path, both of which would mislead: one
        // says the token is malformed, the other says the passkey was rejected.
        await Assert.That(title).IsNotEqualTo(PasskeyVerificationExceptionHandler.Title);
        await Assert.That(title).IsNotEqualTo(UnverifiedEmailTitle);
    }

    /// <summary>
    /// The claim gate did not move below the marker check.
    /// </summary>
    /// <remarks>
    /// The natural way to write the new middleware is to look up the endpoint's metadata first and
    /// branch, which puts the marked path back on "find or create" before anybody has asked whether the
    /// address is verified. Every fresh-subject refusal test would stay green and unverified addresses
    /// would land in <c>users</c> again. Driven on a <b>marked</b> route for exactly that reason: on an
    /// unmarked one the request would be refused for having no account and this test would pass without
    /// the gate existing.
    /// </remarks>
    [Test]
    public async Task AuthenticatedRequest_MissingEmailVerifiedClaim_IsStillRefusedOnAMarkedRoute()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClientWithoutEmailVerifiedClaim("google-no-claim");

        // Act
        HttpResponseMessage response = await client.GetAsync(ApiFactory.AccountProvisioningPath);

        // Assert — refused by the claim gate, named by its own sentence, and nothing written.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsEqualTo(UnverifiedEmailTitle);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await CountUsersAsync(admin)).IsEqualTo(0L);
        await Assert.That(await CountCredentialsAsync(admin)).IsEqualTo(0L);
    }

    /// <summary>
    /// Runs both authenticated legs of a registration, so the account really holds a passkey a
    /// signature can be verified against.
    /// </summary>
    private static async Task RegisterPasskeyAsync(HttpClient client, SyntheticAuthenticator device)
    {
        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(challenge, ApiFactory.PasskeyOrigin, prfEnabled: true);
        HttpResponseMessage response = await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Runs the whole erasure exchange: the options leg, the authenticator, the request.</summary>
    private static async Task<HttpResponseMessage> EraseAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId));

        return await client.PostAsJsonAsync(ErasurePath, new
        {
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });
    }

    /// <summary>
    /// Runs both anonymous legs of a sign-in on whichever client is handed in, so the only difference
    /// between the two calls in the test above is the bearer header.
    /// </summary>
    /// <param name="signCount">
    /// The counter the device reports. Required rather than defaulted, because the caller drives the
    /// same device through two accepted ceremonies and the clone check refuses a counter that did not
    /// advance — a defaultable parameter here is a trap the next caller falls into exactly once.
    /// </param>
    private static async Task<HttpResponseMessage> SignInAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId,
        uint signCount)
    {
        byte[] challenge = await BeginCeremonyAsync(client, AssertionOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount);

        return await client.PostAsJsonAsync(AssertionPath, new
        {
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });
    }

    /// <summary>Runs an options leg and returns the challenge bytes it issued.</summary>
    private static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.PostAsync(path, content: null);
        response.EnsureSuccessStatusCode();
        JsonNode options = await ReadJsonAsync(response);

        return Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
    }

    /// <summary>
    /// Reads back the user provisioning minted for <paramref name="subject" />. Nothing the API returns
    /// names it, so the lookup goes through the credential the middleware resolved on.
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

    private static Task<long> CountUsersAsync(NpgsqlConnection connection) =>
        ScalarCountAsync(connection, "select count(*) from users");

    /// <summary>
    /// The account behind every <c>sessions</c> row, oldest first. Ordered so the result is comparable
    /// as a string rather than as an unordered set, and read on the container superuser like every other
    /// count here — <c>user_isolation</c> is <c>FOR ALL</c>, so a policed connection would report a
    /// session belonging to the wrong account exactly as it reports no session at all.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> ReadSessionOwnersAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            "select user_id from sessions order by created_at_utc",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<Guid> owners = [];
        while (await reader.ReadAsync())
        {
            owners.Add(reader.GetGuid(0));
        }

        return owners;
    }

    /// <summary>
    /// Counts the federated credentials only. <c>credentials</c> holds passkeys too, and a test asserting
    /// "provisioning wrote nothing" is asking about the Google row it would have minted.
    /// </summary>
    private static Task<long> CountCredentialsAsync(NpgsqlConnection connection) =>
        ScalarCountAsync(connection, "select count(*) from credentials where type = 'federated'");

    private static Task<long> CountBudgetsAsync(NpgsqlConnection connection) =>
        ScalarCountAsync(connection, "select count(*) from budgets");

    /// <summary>
    /// Reads a count, refusing anything else. Pattern-matched rather than cast-and-null-forgive: a null
    /// or unexpected scalar means the query changed shape, and that should fail loudly here instead of
    /// at the assertion.
    /// </summary>
    private static async Task<long> ScalarCountAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);

        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{sql}', got '{unexpected ?? "null"}'."),
        };
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

    private static async Task<string> ReadTitleAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response))["title"]!.GetValue<string>();

    private static async Task<string> ReadKindAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response))["kind"]!.GetValue<string>();

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
