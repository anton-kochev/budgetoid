using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.Passkeys;
using Domain.Users;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Drives both WebAuthn ceremonies over real HTTP with a real authenticator.
/// </summary>
/// <remarks>
/// The unit suite already proves the verifier reads the wire format the way the specification
/// describes it. What only a request can show is the rest of the exchange: that the anonymous legs are
/// reachable without a token, that a challenge is spent exactly once whichever way the attempt ends,
/// that every refusal leaves byte-identical, and that a verified assertion writes one session for the
/// account the passkey belongs to and for nobody else.
/// </remarks>
public sealed class PasskeyCeremonyTests
{
    [Test]
    public async Task Registration_ThenAssertion_EstablishesOneFullSessionForThatAccount()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterAsync(authenticated, authenticator);

        // Act — the two anonymous legs, on a client carrying no token at all, which is the state a
        // sign-in actually arrives in.
        HttpClient anonymous = factory.CreateClient();
        AssertionResult assertion = await BuildAssertionAsync(anonymous, authenticator, owner.UserId);
        HttpResponseMessage response = await PostAssertionAsync(anonymous, assertion);
        JsonNode body = await ReadJsonAsync(response);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body["kind"]!.GetValue<string>()).IsEqualTo("full");

        // Read through the seeding connection, because the response deliberately carries no session
        // id: the row is the only place the established session is observable at all.
        Guid credentialId = await FindPasskeyCredentialIdAsync(host, authenticator.CredentialId);
        IReadOnlyList<SessionRow> sessions = await ReadSessionsAsync(host);
        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].UserId).IsEqualTo(owner.UserId);
        await Assert.That(sessions[0].CredentialId).IsEqualTo(credentialId);
        await Assert.That(sessions[0].Kind).IsEqualTo("full");
    }

    [Test]
    public async Task Assertion_ForACredentialThatWasNeverRegistered_Returns401AndEstablishesNoSession()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient anonymous = factory.CreateClient();
        SyntheticAuthenticator stranger = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        AssertionResult assertion = await BuildAssertionAsync(anonymous, stranger, userId: null);
        HttpResponseMessage response = await PostAssertionAsync(anonymous, assertion);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await Assert.That((await ReadSessionsAsync(host)).Count).IsEqualTo(0);
    }

    /// <summary>
    /// Every refusal the sign-in leg can reach, driven end to end and compared whole — status and
    /// body together, not the body alone.
    /// </summary>
    /// <remarks>
    /// A refusal that named its cause would be a credential-enumeration oracle: "no such credential"
    /// told apart from "wrong signature" is how a caller discovers which handles are registered without
    /// ever holding one, and a status code that varied would tell them the same thing without a single
    /// byte of the body changing. Two reasons compared against each other prove only that those two
    /// agree, so this drives all of them and asserts they collapse to one value; that is what makes it
    /// the test a newly added refusal has to pass rather than one it can quietly sit beside.
    /// </remarks>
    [Test]
    public async Task EveryReachableAssertionRefusal_ProducesTheIdenticalResponse()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        RepositoryTestHost.SeededOwner bystander = await host.SeedOwnerAsync(OtherSubject, OtherEmail);
        SyntheticAuthenticator registered = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(owner.UserId, registered.CredentialId, registered.CoseKey, registered.Algorithm);

        // A second passkey on the same account, standing at ten, because a counter regression is only
        // reachable against a stored value an assertion can report below.
        SyntheticAuthenticator counted = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(
            owner.UserId,
            counted.CredentialId,
            counted.CoseKey,
            counted.Algorithm,
            signatureCounter: SeededCounter);
        SyntheticAuthenticator unknown = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        HttpClient anonymous = factory.CreateClient();
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        byte[] ownerHandle = PasskeyEncoding.ToUserHandle(owner.UserId);

        // Act
        List<(string Reason, HttpResponseMessage Response)> refusals = [];

        AssertionResult stranger = await BuildAssertionAsync(anonymous, unknown, userId: null);
        refusals.Add(("unknown credential", await PostAssertionAsync(anonymous, stranger)));

        // One challenge answered twice: the tampered attempt burns it, so the second post is a
        // faultless response refused for nothing but the nonce already being spent.
        byte[] spentChallenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        AssertionResult answered = registered.Authenticate(spentChallenge, ApiFactory.PasskeyOrigin, ownerHandle);
        refusals.Add(("invalid signature", await PostAssertionAsync(anonymous, WithFlippedSignature(answered))));
        refusals.Add(("consumed challenge", await PostAssertionAsync(anonymous, answered)));

        byte[] originChallenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        AssertionResult elsewhere = registered.Authenticate(originChallenge, LookalikeOrigin, ownerHandle);
        refusals.Add(("untrusted origin", await PostAssertionAsync(anonymous, elsewhere)));

        byte[] counterChallenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        AssertionResult wentBackwards = counted.Authenticate(
            counterChallenge,
            ApiFactory.PasskeyOrigin,
            ownerHandle,
            signCount: RegressedCounter);
        refusals.Add(("counter regression", await PostAssertionAsync(anonymous, wentBackwards)));

        byte[] handleChallenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        AssertionResult wrongAccount = registered.Authenticate(
            handleChallenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(bystander.UserId));
        refusals.Add(("mismatched user handle", await PostAssertionAsync(anonymous, wrongAccount)));

        // A registration nonce, issued to the signed-in account on the authenticated leg and spent on
        // the anonymous one.
        byte[] registrationChallenge = await BeginCeremonyAsync(authenticated, RegistrationOptionsPath);
        AssertionResult wrongCeremony = registered.Authenticate(
            registrationChallenge,
            ApiFactory.PasskeyOrigin,
            ownerHandle);
        refusals.Add(("wrong ceremony type", await PostAssertionAsync(anonymous, wrongCeremony)));

        // Every payload ceiling, one entry each. An oversized member is the cheapest oracle of the
        // lot — reachable without a credential handle, a signature, or a challenge — so an early
        // exit that answered differently would be the one worth attacking. Each member is driven on
        // its own so a ceiling that stopped being applied is one failing entry rather than a gap
        // some other member's ceiling covers up.
        //
        // Every entry answers a live challenge, and that is not decoration. Four of the five members
        // are decoded before clientDataJSON is parsed, so a nonce nobody issued would do for them;
        // the user handle is not read until the credential has been found, well past the point the
        // nonce is consumed, so an entry for it against a dead challenge would be refused for the
        // challenge and never reach the ceiling at all. One challenge per member rather than one
        // shared: the handle entry spends the one it answers, and the entries are driven in whatever
        // order the enum lists them.
        foreach (AssertionMember member in Enum.GetValues<AssertionMember>())
        {
            byte[] ceilingChallenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
            AssertionResult oversized = registered.Authenticate(
                ceilingChallenge,
                ApiFactory.PasskeyOrigin,
                ownerHandle);
            refusals.Add((
                $"oversized {member}",
                await PostAssertionAsync(anonymous, oversized, member, CeilingFor(member) * 2)));
        }

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

        // The count, so that deleting a refusal from the list above is a failure rather than a shorter
        // and still perfectly green test.
        await Assert.That(observed.Count).IsEqualTo(ReachableAssertionRefusals);
        await Assert.That(observed.Select(entry => entry.Response).Distinct().Count()).IsEqualTo(1);
        await Assert.That(refusals[0].Response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(first.Contains(PasskeyVerificationExceptionHandler.Title, StringComparison.Ordinal))
            .IsTrue();
        await Assert.That((await ReadSessionsAsync(host)).Count).IsEqualTo(0);
    }

    /// <summary>
    /// A response assembled from two ceremonies: a genuine signature by a registered authenticator,
    /// carrying the user handle of a different account. Nothing but the handle check refuses it.
    /// </summary>
    [Test]
    public async Task Assertion_WhoseUserHandleNamesAnotherAccount_Returns401AndEstablishesNoSession()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        RepositoryTestHost.SeededOwner bystander = await host.SeedOwnerAsync(OtherSubject, OtherEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(owner.UserId, authenticator.CredentialId, authenticator.CoseKey, authenticator.Algorithm);
        HttpClient anonymous = factory.CreateClient();

        // Act
        AssertionResult assertion = await BuildAssertionAsync(anonymous, authenticator, bystander.UserId);
        HttpResponseMessage response = await PostAssertionAsync(anonymous, assertion);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await Assert.That((await ReadSessionsAsync(host)).Count).IsEqualTo(0);
    }

    /// <summary>
    /// The other half of the handle rule: absent is tolerated. A conforming authenticator may omit the
    /// handle, and it proves nothing the signature has not already proved — so a check written as
    /// "the handle must name the account" rather than "a handle that is there must" would lock out
    /// every device that omits it.
    /// </summary>
    [Test]
    public async Task Assertion_WithNoUserHandle_EstablishesTheSessionForTheCredentialsOwner()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(owner.UserId, authenticator.CredentialId, authenticator.CoseKey, authenticator.Algorithm);
        HttpClient anonymous = factory.CreateClient();

        // Act — userId: null is what leaves the handle off the response entirely.
        AssertionResult assertion = await BuildAssertionAsync(anonymous, authenticator, userId: null);
        HttpResponseMessage response = await PostAssertionAsync(anonymous, assertion);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        IReadOnlyList<SessionRow> sessions = await ReadSessionsAsync(host);
        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].UserId).IsEqualTo(owner.UserId);
    }

    /// <summary>
    /// The two nonce pools are kept apart by the ceremony a challenge was issued for, and by nothing
    /// else: the store looks a challenge up by its bytes alone. A signed-in caller who took a
    /// registration nonce off the authenticated leg would otherwise be able to spend it here.
    /// </summary>
    [Test]
    public async Task Assertion_BuiltOnARegistrationChallenge_Returns401AndEstablishesNoSession()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(owner.UserId, authenticator.CredentialId, authenticator.CoseKey, authenticator.Algorithm);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        HttpClient anonymous = factory.CreateClient();

        // Act — a live, unspent challenge in every respect except the ceremony it was issued for.
        byte[] registrationChallenge = await BeginCeremonyAsync(authenticated, RegistrationOptionsPath);
        AssertionResult assertion = authenticator.Authenticate(
            registrationChallenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(owner.UserId));
        HttpResponseMessage response = await PostAssertionAsync(anonymous, assertion);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await Assert.That((await ReadSessionsAsync(host)).Count).IsEqualTo(0);
    }

    /// <summary>
    /// The mirror: an authentication nonce spent on the registration leg. Cheap, and it says the rule
    /// is a two-way one rather than a single guard on the sign-in side.
    /// </summary>
    [Test]
    public async Task Registration_BuiltOnAnAssertionChallenge_IsRefused()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        byte[] assertionChallenge = await BeginCeremonyAsync(factory.CreateClient(), AssertionOptionsPath);
        AttestationResult attestation = authenticator.Register(assertionChallenge, ApiFactory.PasskeyOrigin);
        HttpResponseMessage response = await PostRegistrationAsync(authenticated, attestation);

        // Assert — the registration leg is authenticated throughout, so it may say what was wrong, and
        // a refused response leaves nothing filed.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await CountPasskeyPublicKeysAsync(host)).IsEqualTo(0L);
    }

    /// <summary>
    /// The third pool, spent on the sign-in leg. A re-authentication nonce authorizes account
    /// destruction, so one that could also open a session would let a person talked through one
    /// erasure prompt be signed in instead — and, worse, the reverse door is what this file's
    /// companion test on the erasure side closes.
    /// </summary>
    /// <remarks>
    /// This completes as a 3×3 what the two tests above keep as a 2×2. Without it the new ceremony is
    /// a one-way guard: erasure refuses the older pools while the older legs accept the new one.
    /// </remarks>
    [Test]
    public async Task Assertion_BuiltOnAReauthenticationChallenge_Returns401AndEstablishesNoSession()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(owner.UserId, authenticator.CredentialId, authenticator.CoseKey, authenticator.Algorithm);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        HttpClient anonymous = factory.CreateClient();

        // Act — a live, unspent challenge in every respect except the ceremony it was issued for.
        byte[] reauthenticationChallenge = await BeginCeremonyAsync(authenticated, ReauthenticationOptionsPath);
        AssertionResult assertion = authenticator.Authenticate(
            reauthenticationChallenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(owner.UserId));
        HttpResponseMessage response = await PostAssertionAsync(anonymous, assertion);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await Assert.That((await ReadSessionsAsync(host)).Count).IsEqualTo(0);
    }

    /// <summary>
    /// The last cell of the 3×3: a re-authentication nonce spent on the registration leg. Cheap, and
    /// it says the separation is a property of the vocabulary rather than a guard someone remembered
    /// to write on two of the three finish legs.
    /// </summary>
    [Test]
    public async Task Registration_BuiltOnAReauthenticationChallenge_IsRefused()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        byte[] reauthenticationChallenge = await BeginCeremonyAsync(authenticated, ReauthenticationOptionsPath);
        AttestationResult attestation = authenticator.Register(reauthenticationChallenge, ApiFactory.PasskeyOrigin);
        HttpResponseMessage response = await PostRegistrationAsync(authenticated, attestation);

        // Assert — the registration leg is authenticated throughout, so it may say what was wrong, and
        // a refused response leaves nothing filed.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await CountPasskeyPublicKeysAsync(host)).IsEqualTo(0L);
    }

    /// <summary>
    /// A counter that went backwards is what a cloned authenticator produces, and the domain reports it
    /// by throwing. This is the test of the <b>translation</b>: an untranslated regression escapes as a
    /// 500 while an unknown credential answers 401, and a caller who can tell those apart has learned
    /// that the handle they presented is real.
    /// </summary>
    [Test]
    public async Task Assertion_WhoseReportedCounterWentBackwards_Returns401AndEstablishesNoSession()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(
            owner.UserId,
            authenticator.CredentialId,
            authenticator.CoseKey,
            authenticator.Algorithm,
            signatureCounter: SeededCounter);
        HttpClient anonymous = factory.CreateClient();

        // Act — genuinely signed and correct in every other respect, reporting a counter below the
        // stored one.
        byte[] challenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        AssertionResult assertion = authenticator.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(owner.UserId),
            signCount: RegressedCounter);
        HttpResponseMessage response = await PostAssertionAsync(anonymous, assertion);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await Assert.That((await ReadSessionsAsync(host)).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Assertion_ReplayingAConsumedChallenge_Returns401()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(owner.UserId, authenticator.CredentialId, authenticator.CoseKey, authenticator.Algorithm);
        HttpClient anonymous = factory.CreateClient();
        AssertionResult assertion = await BuildAssertionAsync(anonymous, authenticator, owner.UserId);

        // Act — the identical request twice. Single use is the property, so nothing about the second
        // request differs from the first, down to the bytes.
        HttpResponseMessage first = await PostAssertionAsync(anonymous, assertion);
        HttpResponseMessage replay = await PostAssertionAsync(anonymous, assertion);

        // Assert
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await ReadSessionsAsync(host)).Count).IsEqualTo(1);
    }

    /// <summary>
    /// A failed attempt has to burn the nonce, or one issued challenge becomes something an attacker
    /// can grind responses against. The second attempt here is a <b>valid</b> assertion over the same
    /// challenge: only a challenge the failure already spent refuses it.
    /// </summary>
    [Test]
    public async Task Assertion_WhenVerificationFails_StillConsumesTheChallenge()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(owner.UserId, authenticator.CredentialId, authenticator.CoseKey, authenticator.Algorithm);
        HttpClient anonymous = factory.CreateClient();
        byte[] challenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        byte[] userHandle = PasskeyEncoding.ToUserHandle(owner.UserId);

        // Act
        AssertionResult failing = authenticator.Authenticate(challenge, ApiFactory.PasskeyOrigin, userHandle);
        HttpResponseMessage refused = await PostAssertionAsync(anonymous, WithFlippedSignature(failing));

        AssertionResult valid = authenticator.Authenticate(challenge, ApiFactory.PasskeyOrigin, userHandle);
        HttpResponseMessage afterFailure = await PostAssertionAsync(anonymous, valid);

        // Assert
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(afterFailure.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await ReadSessionsAsync(host)).Count).IsEqualTo(0);
    }

    /// <summary>
    /// The anonymous leg may still be called with a valid provider token, in which case provisioning
    /// has already put that account on the request. The session belongs to whoever the verified passkey
    /// belongs to, which need not be the same person.
    /// </summary>
    [Test]
    public async Task Assertion_PresentedWithAnotherUsersBearerToken_EstablishesTheSessionForThePasskeysOwner()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner passkeyOwner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        RepositoryTestHost.SeededOwner bystander = await host.SeedOwnerAsync(OtherSubject, OtherEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(
            passkeyOwner.UserId,
            authenticator.CredentialId,
            authenticator.CoseKey,
            authenticator.Algorithm);

        // Act
        HttpClient bystanderClient = factory.CreateAuthenticatedClient(OtherSubject, OtherEmail);
        AssertionResult assertion = await BuildAssertionAsync(bystanderClient, authenticator, passkeyOwner.UserId);
        HttpResponseMessage response = await PostAssertionAsync(bystanderClient, assertion);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        IReadOnlyList<SessionRow> sessions = await ReadSessionsAsync(host);
        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].UserId).IsEqualTo(passkeyOwner.UserId);
        await Assert.That(sessions[0].UserId).IsNotEqualTo(bystander.UserId);
    }

    [Test]
    public async Task Assertion_DoesNotProvisionAUserFromTheAnonymousRequest()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(owner.UserId, authenticator.CredentialId, authenticator.CoseKey, authenticator.Algorithm);
        long usersBefore = await CountUsersAsync(host);
        HttpClient anonymous = factory.CreateClient();

        // Act
        AssertionResult assertion = await BuildAssertionAsync(anonymous, authenticator, owner.UserId);
        HttpResponseMessage response = await PostAssertionAsync(anonymous, assertion);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await CountUsersAsync(host)).IsEqualTo(usersBefore);
    }

    [Test]
    public async Task AssertionEndpoints_AreReachableWithoutAuthentication()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient anonymous = factory.CreateClient();
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        HttpResponseMessage options = await anonymous.PostAsync(AssertionOptionsPath, content: null);
        AssertionResult assertion = await BuildAssertionAsync(anonymous, authenticator, userId: null);
        HttpResponseMessage completion = await PostAssertionAsync(anonymous, assertion);

        // Assert — the completion leg is refused, but by the ceremony rather than by authentication,
        // and the title is what tells the two 401s apart.
        await Assert.That(options.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await ReadTitleAsync(completion)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
    }

    [Test]
    public async Task RegistrationEndpoints_Return401WithoutAuthentication()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        HttpClient anonymous = factory.CreateClient();

        // Act
        HttpResponseMessage options = await anonymous.PostAsync(RegistrationOptionsPath, content: null);
        HttpResponseMessage completion = await anonymous.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = "AA",
            attestationObject = "AA",
        });

        // Assert
        await Assert.That(options.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(completion.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(options)).IsNotEqualTo(PasskeyVerificationExceptionHandler.Title);
    }

    [Test]
    public async Task RegistrationOptions_RequestThePrfExtension()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);

        // Act
        JsonNode options = await PostForJsonAsync(authenticated, RegistrationOptionsPath);

        // Assert — on the JSON that actually leaves the server, because the extension is only requested
        // if the client sees it. A property present in the options type but dropped on the wire asks
        // for nothing.
        await Assert.That(options["extensions"]).IsNotNull();
        await Assert.That(options["extensions"]!["prf"]).IsNotNull();
    }

    [Test]
    public async Task RegistrationOptions_RequireADiscoverableCredentialAndUserVerification()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);

        // Act
        JsonNode options = await PostForJsonAsync(authenticated, RegistrationOptionsPath);
        JsonNode selection = options["authenticatorSelection"]!;

        // Assert — a credential that is not discoverable is one the sign-in leg can never offer, since
        // it sends no allowCredentials; a preferred user verification is one an authenticator may skip.
        await Assert.That(selection["residentKey"]!.GetValue<string>()).IsEqualTo("required");
        await Assert.That(selection["requireResidentKey"]!.GetValue<bool>()).IsTrue();
        await Assert.That(selection["userVerification"]!.GetValue<string>()).IsEqualTo("required");
    }

    [Test]
    public async Task RegistrationOptions_OfferOnlyAlgorithmsTheServerCanVerify()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);

        // Act
        JsonNode options = await PostForJsonAsync(authenticated, RegistrationOptionsPath);
        JsonArray parameters = options["pubKeyCredParams"]!.AsArray();

        // Assert — exactly, not at least: an algorithm offered here and unverifiable on the way back is
        // a registration that succeeds and a sign-in that never can.
        await Assert.That(parameters.Count).IsEqualTo(2);
        await Assert.That(parameters[0]!["alg"]!.GetValue<int>()).IsEqualTo((int)CoseAlgorithm.Es256);
        await Assert.That(parameters[1]!["alg"]!.GetValue<int>()).IsEqualTo((int)CoseAlgorithm.Rs256);
        await Assert.That(parameters[0]!["type"]!.GetValue<string>()).IsEqualTo("public-key");
        await Assert.That(parameters[1]!["type"]!.GetValue<string>()).IsEqualTo("public-key");
    }

    /// <summary>
    /// The test that notices <c>ListWebAuthnCredentialIdsForUserAsync</c> losing its owner filter.
    /// <c>passkey_public_keys</c> carries no row-level security policy and no query filter, so nothing
    /// beneath the application narrows that read — an exempt table cannot catch this for itself, and an
    /// unfiltered read would hand one account every other account's credential handles.
    /// </summary>
    [Test]
    public async Task RegistrationOptions_ForOneAccount_ExcludeNoOtherAccountsCredential()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        RepositoryTestHost.SeededOwner other = await host.SeedOwnerAsync(OtherSubject, OtherEmail);
        SyntheticAuthenticator ownersDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator othersDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(owner.UserId, ownersDevice.CredentialId, ownersDevice.CoseKey, ownersDevice.Algorithm);
        await host.SeedPasskeyAsync(other.UserId, othersDevice.CredentialId, othersDevice.CoseKey, othersDevice.Algorithm);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);

        // Act
        JsonNode options = await PostForJsonAsync(authenticated, RegistrationOptionsPath);
        JsonArray excluded = options["excludeCredentials"]!.AsArray();

        // Assert
        await Assert.That(excluded.Count).IsEqualTo(1);
        await Assert.That(excluded[0]!["id"]!.GetValue<string>())
            .IsEqualTo(Base64UrlText.Encode(ownersDevice.CredentialId));
        await Assert.That(excluded[0]!["id"]!.GetValue<string>())
            .IsNotEqualTo(Base64UrlText.Encode(othersDevice.CredentialId));
    }

    [Test]
    public async Task AssertionOptions_ReturnNoAllowCredentials()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(owner.UserId, authenticator.CredentialId, authenticator.CoseKey, authenticator.Algorithm);

        // Act
        JsonNode options = await PostForJsonAsync(factory.CreateClient(), AssertionOptionsPath);

        // Assert — sending one means the server first decided whose credentials these are, which means
        // the request had to name an account, and an endpoint that answers differently per account is
        // an account-enumeration oracle.
        await Assert.That(options.AsObject().ContainsKey("allowCredentials")).IsFalse();
    }

    /// <summary>
    /// Registration refuses an authenticator that reports no <c>prf</c> extension result, and says so
    /// in a sentence the person holding the device can act on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two cases rather than one is what forces the predicate to be "present <b>and</b> true". A check
    /// written against a missing member — "the client sent no extension results" — passes the
    /// unreported case and lets the disabled one straight through; a check written against the flag
    /// alone — "the reported value is false" — does the exact reverse. Neither mistake survives both
    /// cases, and either survives one of them.
    /// </para>
    /// <para>
    /// What keeps the two distinguishable at all is <see cref="PostRegistrationAsync"/>, which sends
    /// <c>clientExtensionResults</c> as JSON null when the result is null instead of sending a present
    /// object carrying false. "Simplifying" that helper to always send the object would silently turn
    /// this into one case run twice, and the pair would stop proving anything.
    /// </para>
    /// <para>
    /// The sentence says the device "did not report an enabled prf extension result" rather than that
    /// it "returned no prf extension result", because <c>prf.enabled: false</c> <b>is</b> a returned
    /// result — one that means no. The wording is a literal transcription of the predicate and is
    /// therefore true of every form this refusal covers rather than of only one of them.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(false)]
    [Arguments(null)]
    public async Task Registration_WhoseAuthenticatorReportsNoPrfResult_Returns400NamingTheAuthenticator(
        bool? prfEnabled)
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act — a genuine ceremony in every respect except what the device says about the extension.
        byte[] challenge = await BeginCeremonyAsync(authenticated, RegistrationOptionsPath);
        AttestationResult attestation = authenticator.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            prfEnabled: prfEnabled);
        HttpResponseMessage response = await PostRegistrationAsync(authenticated, attestation);

        // Assert — the sentence is the acceptance criterion, so it is pinned whole rather than by a
        // fragment of it: it names the authenticator as the reason, says what to use instead, and
        // names no vendor.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ReadValidationErrorAsync(response)).IsEqualTo(
            "This authenticator cannot hold the account's keys: it did not report an enabled prf "
            + "extension result. Register a passkey from a device whose authenticator supports the prf "
            + "extension — most current phones, laptops and hardware security keys do.");
    }

    /// <summary>
    /// A response whose <c>prf</c> result is present but says nothing: the object is there and the
    /// <c>enabled</c> member is absent. Refused, in the same sentence as every other form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the form where the client object exists but says nothing about the extension. With the
    /// member absent, <c>Enabled</c> binds to <c>null</c> — not to <c>false</c> — so nothing here
    /// states a negative; what refuses the request is the gate's own requirement that the flag be
    /// <b>present and true</b>. The test pins that silence refuses rather than passes, which is what a
    /// check written as "not explicitly false" would get wrong.
    /// </para>
    /// <para>
    /// The body is assembled by hand because <see cref="PostRegistrationAsync"/> cannot express it:
    /// its parameter is a <c>bool?</c>, and neither of its two shapes is a present object with no
    /// members. Everything else is the genuine ceremony — only <c>clientExtensionResults</c> is built
    /// here.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WhosePrfResultCarriesNoEnabledMember_Returns400NamingTheAuthenticator()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act — a genuine ceremony carrying a prf object with the enabled member left out.
        byte[] challenge = await BeginCeremonyAsync(authenticated, RegistrationOptionsPath);
        AttestationResult attestation = authenticator.Register(challenge, ApiFactory.PasskeyOrigin);
        HttpResponseMessage response = await authenticated.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { } },
        });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ReadValidationErrorAsync(response)).IsEqualTo(
            "This authenticator cannot hold the account's keys: it did not report an enabled prf "
            + "extension result. Register a passkey from a device whose authenticator supports the prf "
            + "extension — most current phones, laptops and hardware security keys do.");
    }

    /// <summary>
    /// A response whose <c>prf</c> result reports <c>enabled</c> as an explicit JSON null. Refused, in
    /// the same sentence as every other form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This form used to fail model binding: with <c>Enabled</c> a non-nullable <c>bool</c>, an
    /// explicit null was rejected before the handler ran and the caller got the framework's own
    /// ProblemDetails rather than this feature's sentence. Widening the member to <c>bool?</c> is what
    /// routes it to the handler instead, where the "present and true" gate judges it like any other
    /// form, and this test is what holds that — a member narrowed back to <c>bool</c> stops answering
    /// with the sentence asserted here.
    /// </para>
    /// <para>
    /// The body is assembled by hand for the same reason as the test above:
    /// <see cref="PostRegistrationAsync"/> takes a <c>bool?</c> and can express neither a present
    /// object with no members nor one carrying an explicit null.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WhosePrfResultReportsANullEnabledMember_Returns400NamingTheAuthenticator()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act — a genuine ceremony carrying a prf object whose enabled member is an explicit null.
        byte[] challenge = await BeginCeremonyAsync(authenticated, RegistrationOptionsPath);
        AttestationResult attestation = authenticator.Register(challenge, ApiFactory.PasskeyOrigin);
        HttpResponseMessage response = await authenticated.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = (bool?)null } },
        });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ReadValidationErrorAsync(response)).IsEqualTo(
            "This authenticator cannot hold the account's keys: it did not report an enabled prf "
            + "extension result. Register a passkey from a device whose authenticator supports the prf "
            + "extension — most current phones, laptops and hardware security keys do.");
    }

    /// <summary>
    /// A present <c>clientExtensionResults</c> object carrying no <c>prf</c> member at all. Refused, in
    /// the same sentence as every other form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the form a real browser sends. <c>getClientExtensionResults()</c> always returns an
    /// object, so an authenticator with no PRF support produces <c>{}</c> — a present object with the
    /// member missing. It is <b>not</b> a JSON null: that is what the <c>[Arguments(null)]</c> case
    /// exercises, and it only appears on the wire if client code deliberately substitutes it.
    /// </para>
    /// <para>
    /// Said plainly so the enumeration of forms in these tests is not misleading about which one
    /// arrives first in production: this one does, and the others are the shapes a hand-written or
    /// hostile client can also produce.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WhoseClientExtensionResultsCarryNoPrfMember_Returns400NamingTheAuthenticator()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act — a genuine ceremony carrying the empty object a device without PRF support produces.
        byte[] challenge = await BeginCeremonyAsync(authenticated, RegistrationOptionsPath);
        AttestationResult attestation = authenticator.Register(challenge, ApiFactory.PasskeyOrigin);
        HttpResponseMessage response = await authenticated.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { },
        });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ReadValidationErrorAsync(response)).IsEqualTo(
            "This authenticator cannot hold the account's keys: it did not report an enabled prf "
            + "extension result. Register a passkey from a device whose authenticator supports the prf "
            + "extension — most current phones, laptops and hardware security keys do.");
    }

    /// <summary>
    /// A response that is wrong twice over — wrong origin <b>and</b> no <c>prf</c> result — is refused
    /// for the origin, not for its authenticator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test owns the ordering that the handler's own comment, <c>passkeys.md</c> and ADR 0013 all
    /// assert: the extension claim is weighed last, after everything signed has been judged. A
    /// response that is malformed, replayed or wrong-origin must never be told its authenticator is at
    /// fault, because that sentence would be a lie about the device — and one the person would act on
    /// by going out to buy another.
    /// </para>
    /// <para>
    /// Before this test the ordering held only incidentally: the unrelated ceiling tests happen to
    /// send no <c>clientExtensionResults</c> and so would have noticed a prf check moved to the front,
    /// but none of them is about the ordering and any of them could stop covering it without anyone
    /// noticing.
    /// </para>
    /// <para>
    /// One assertion is enough. The prf sentence starts differently, so the prefix asserted here
    /// excludes it; a second assertion saying the same thing the other way round would only be a
    /// second thing to keep in step.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WhoseOriginIsWrongAndReportsNoPrfResult_IsRefusedForTheOriginRatherThanTheAuthenticator()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act — two faults in one response, so only the order of the checks can decide which is named.
        byte[] challenge = await BeginCeremonyAsync(authenticated, RegistrationOptionsPath);
        AttestationResult attestation = authenticator.Register(
            challenge,
            LookalikeOrigin,
            prfEnabled: null);
        HttpResponseMessage response = await PostRegistrationAsync(authenticated, attestation);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ReadValidationErrorAsync(response))
            .StartsWith("The registration response was refused:");
    }

    /// <summary>
    /// The provable-fail control beside the refusal above: an authenticator that does report a
    /// <c>prf</c> result registers, and the whole passkey is filed.
    /// </summary>
    /// <remarks>
    /// Without this, a handler that refused <b>every</b> registration passes the refusal test
    /// perfectly. This is the test such a handler fails, and that is what makes the pair provable
    /// rather than one-sided.
    /// </remarks>
    [Test]
    public async Task Registration_WhoseAuthenticatorReportsAPrfResult_FilesTheCredentialAndItsKeyAndCounter()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        byte[] challenge = await BeginCeremonyAsync(authenticated, RegistrationOptionsPath);
        AttestationResult attestation = authenticator.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            prfEnabled: true);
        HttpResponseMessage response = await PostRegistrationAsync(authenticated, attestation);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Nothing in the body: reporting the flag back would read as the server having established
        // it, when all it did was repeat what the client just said.
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(string.Empty);

        await Assert.That(await CountPasskeyCredentialsAsync(host)).IsEqualTo(1L);
        await Assert.That(await CountPasskeyPublicKeysAsync(host)).IsEqualTo(1L);
        await Assert.That(await CountPasskeySignatureCountersAsync(host)).IsEqualTo(1L);
    }

    /// <summary>
    /// A registration refused for its authenticator files nothing at all against the account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is true by <b>where the refusal sits</b> rather than by code written to make it true: the
    /// check precedes <c>Credential.CreatePasskey</c>, so there is no row to undo. The test exists so
    /// that moving the refusal below the save goes red rather than passing on the strength of a
    /// rollback nobody wrote.
    /// </para>
    /// <para>
    /// It does <b>not</b> prove FR-108's first clause — that an incomplete registration owns no
    /// budget-owned row — and cannot: <c>EnsureUserHandler</c> provisions the account's default budget
    /// on every authenticated request, before any ceremony runs, so a budget already exists by the
    /// time this refusal happens. Satisfying that clause means moving provisioning behind the passkey,
    /// which is a different change.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_RefusedForItsAuthenticator_FilesNothingAgainstTheAccount()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act
        byte[] challenge = await BeginCeremonyAsync(authenticated, RegistrationOptionsPath);
        AttestationResult attestation = authenticator.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            prfEnabled: false);
        HttpResponseMessage response = await PostRegistrationAsync(authenticated, attestation);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await CountPasskeyCredentialsAsync(host)).IsEqualTo(0L);
        await Assert.That(await CountPasskeyPublicKeysAsync(host)).IsEqualTo(0L);
        await Assert.That(await CountPasskeySignatureCountersAsync(host)).IsEqualTo(0L);
    }

    /// <summary>
    /// A registration refused for its authenticator has already spent the challenge, so the same device
    /// cannot simply answer again — it has to go back to the options leg for a fresh nonce.
    /// </summary>
    /// <remarks>
    /// <c>passkeys.md</c> states this, and it is true by construction: <c>ConsumeAsync</c> precedes the
    /// prf gate. Nothing pinned it, though, and the plausible "optimisation" — not burning a nonce on a
    /// refusal that judged nothing but a client-written claim — would make the document false with
    /// nothing going red. The second attempt here is a <b>fully valid</b> response from the same
    /// authenticator over the same challenge, so only the spent nonce can refuse it, and the sentence
    /// asserted is the one that says exactly that.
    /// </remarks>
    [Test]
    public async Task Registration_RefusedForItsAuthenticator_ThenRetriedOnTheSameChallenge_FindsItSpent()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        byte[] challenge = await BeginCeremonyAsync(authenticated, RegistrationOptionsPath);

        // Act — refused for the authenticator, then the same device answering the same challenge with
        // nothing at all wrong with the response.
        AttestationResult refusedAttempt = authenticator.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            prfEnabled: false);
        HttpResponseMessage refused = await PostRegistrationAsync(authenticated, refusedAttempt);

        AttestationResult retry = authenticator.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            prfEnabled: true);
        HttpResponseMessage afterRefusal = await PostRegistrationAsync(authenticated, retry);

        // Assert
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(afterRefusal.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ReadValidationErrorAsync(afterRefusal))
            .IsEqualTo("The challenge is not a live registration challenge.");
        await Assert.That(await CountPasskeyPublicKeysAsync(host)).IsEqualTo(0L);
    }

    [Test]
    public async Task Registration_OfTheSameAuthenticatorCredentialTwice_IsRefused()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterAsync(authenticated, authenticator);

        // Act — a second complete ceremony from the same device, on its own fresh challenge.
        byte[] challenge = await BeginCeremonyAsync(authenticated, RegistrationOptionsPath);
        AttestationResult again = authenticator.Register(challenge, ApiFactory.PasskeyOrigin);
        HttpResponseMessage response = await PostRegistrationAsync(authenticated, again);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// The negative side of "only a passkey opens a session that reads budget content": a provider
    /// token reaches an ordinary endpoint and establishes nothing.
    /// </summary>
    [Test]
    public async Task AnAccountReachedByItsGoogleTokenAlone_HasNoSessionRow()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);

        // Act
        HttpResponseMessage response = await authenticated.GetAsync("/api/transactions");

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await ReadSessionsAsync(host)).Count).IsEqualTo(0);
    }

    /// <summary>
    /// A member above its ceiling is refused before the ceremony begins, and the proof is the
    /// challenge: it is still there afterwards.
    /// </summary>
    /// <remarks>
    /// Every refusal on this leg returns the identical response, so the status and body cannot say
    /// which check turned a request down. The nonce can. The ceilings are applied before
    /// <c>clientDataJSON</c> is parsed and therefore before the challenge is spent, while every
    /// refusal past that point spends it — so a genuine assertion still succeeding on the same
    /// challenge is the one observation that says the request was refused for its size and never
    /// reached the ceremony at all. That is what makes this a test of the ceiling rather than of the
    /// 401 the request would have earned anyway.
    /// </remarks>
    [Test]
    [Arguments(AssertionMember.ClientDataJson)]
    [Arguments(AssertionMember.AuthenticatorData)]
    [Arguments(AssertionMember.Signature)]
    [Arguments(AssertionMember.CredentialId)]
    public async Task Assertion_WhoseMemberExceedsItsCeiling_IsRefusedWithoutReachingTheCeremony(
        AssertionMember member)
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(
            owner.UserId,
            authenticator.CredentialId,
            authenticator.CoseKey,
            authenticator.Algorithm);
        HttpClient anonymous = factory.CreateClient();
        byte[] challenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        AssertionResult genuine = authenticator.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(owner.UserId));

        // Act — the same ceremony twice over one challenge, with one member blown past its ceiling
        // the first time and left alone the second.
        HttpResponseMessage oversized = await PostAssertionAsync(
            anonymous,
            genuine,
            member,
            CeilingFor(member) * 2);
        HttpResponseMessage afterwards = await PostAssertionAsync(anonymous, genuine);

        // Assert
        await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(oversized)).IsEqualTo(PasskeyVerificationExceptionHandler.Title);
        await Assert.That(afterwards.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await ReadSessionsAsync(host)).Count).IsEqualTo(1);
    }

    /// <summary>
    /// The other side of every ceiling, and the reason none of them can be quietly set to zero: a
    /// member at the largest size its ceiling admits still runs the whole ceremony.
    /// </summary>
    /// <remarks>
    /// Without this, each "too big" test above passes just as well against a limit of one byte — and
    /// a ceiling one byte below what a conforming authenticator produces refuses real devices while
    /// answering the same 401 an attacker gets, which is the one failure mode a bound like this must
    /// not have. The observation is again the challenge: reaching the ceremony spends it, so the
    /// genuine assertion that follows is refused for the nonce rather than accepted.
    /// </remarks>
    [Test]
    [Arguments(AssertionMember.ClientDataJson)]
    [Arguments(AssertionMember.AuthenticatorData)]
    [Arguments(AssertionMember.Signature)]
    [Arguments(AssertionMember.CredentialId)]
    public async Task Assertion_WhoseMemberSitsAtItsCeiling_ReachesTheCeremonyAndSpendsTheChallenge(
        AssertionMember member)
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await host.SeedPasskeyAsync(
            owner.UserId,
            authenticator.CredentialId,
            authenticator.CoseKey,
            authenticator.Algorithm);
        HttpClient anonymous = factory.CreateClient();
        byte[] challenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        AssertionResult genuine = authenticator.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(owner.UserId));

        // Act
        HttpResponseMessage atCeiling = await PostAssertionAsync(anonymous, genuine, member, CeilingFor(member));
        HttpResponseMessage afterwards = await PostAssertionAsync(anonymous, genuine);

        // Assert — refused, but for what the member says rather than for how long it is, and the
        // spent challenge is what says the difference.
        await Assert.That(atCeiling.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(afterwards.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await ReadSessionsAsync(host)).Count).IsEqualTo(0);
    }

    /// <summary>
    /// The registration leg carries the same ceilings and answers differently, because it may: this
    /// caller is signed in, so it is told which member was too large and how large it may be.
    /// </summary>
    [Test]
    public async Task Registration_WhoseAttestationObjectExceedsItsCeiling_Returns400NamingTheMember()
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act — a real ceremony in every respect except the size of the one member under test.
        byte[] challenge = await BeginCeremonyAsync(authenticated, RegistrationOptionsPath);
        AttestationResult attestation = authenticator.Register(challenge, ApiFactory.PasskeyOrigin);
        HttpResponseMessage response = await authenticated.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = Base64UrlText.Encode(
                RandomNumberGenerator.GetBytes(PasskeyPayloadLimits.AttestationObjectBytes * 2)),
        });

        // Assert — a sentence a person can act on, and nothing filed against the account.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ReadValidationErrorAsync(response)).IsEqualTo(
            $"attestationObject was not base64url text within {PasskeyPayloadLimits.AttestationObjectBytes} bytes.");
        await Assert.That(await CountPasskeyPublicKeysAsync(host)).IsEqualTo(0L);
    }

    /// <summary>
    /// The other side of the registration ceiling, and the reason it cannot be quietly cut down: an
    /// attestation object within the bound is judged by the ceremony rather than turned away for its
    /// length.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, the "too big" test above passes just as well against a ceiling of five hundred
    /// bytes — and a ceiling below what a conforming authenticator produces locks a whole device
    /// family out of registering while every test stays green, which is the one failure mode a bound
    /// like this must not have.
    /// </para>
    /// <para>
    /// The two sizes are what make the pair a pair, and only one of them could do the job alone. A
    /// payload measured from the ceiling shrinks with the ceiling, so it reaches the ceremony however
    /// far the ceiling is cut and can never notice the cut; what it does pin is the boundary being
    /// inclusive, which an off-by-one in the decode would move. The other is measured from what the
    /// product must be able to accept whatever the ceiling says, so a ceiling lowered beneath it goes
    /// red.
    /// </para>
    /// <para>
    /// The observable is the sentence the caller is told, which this leg may answer honestly because
    /// the caller is signed in: "the registration response was refused" says the object got past both
    /// decodes and the challenge and reached the verifier, and the size sentence says it never did.
    /// Random bytes rather than a real attestation object, because what is being measured is which
    /// check the request reaches, not whether an object this large can be assembled — no device
    /// produces one, which is exactly why the ceiling has room to spare above it.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(PasskeyPayloadLimits.AttestationObjectBytes)]
    [Arguments(LargestStorableAttestationObjectBytes)]
    public async Task Registration_WhoseAttestationObjectIsWithinItsCeiling_ReachesTheCeremony(
        int decodedBytes)
    {
        // Arrange
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        await host.SeedOwnerAsync(OwnerSubject, OwnerEmail);
        HttpClient authenticated = factory.CreateAuthenticatedClient(OwnerSubject, OwnerEmail);
        SyntheticAuthenticator authenticator = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // Act — a real ceremony in every respect except the size of the one member under test.
        byte[] challenge = await BeginCeremonyAsync(authenticated, RegistrationOptionsPath);
        AttestationResult attestation = authenticator.Register(challenge, ApiFactory.PasskeyOrigin);
        HttpResponseMessage response = await authenticated.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = Base64UrlText.Encode(RandomNumberGenerator.GetBytes(decodedBytes)),
        });

        // Assert — refused, but for what the object says rather than for how long it is, and nothing
        // filed against the account either way.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ReadValidationErrorAsync(response))
            .StartsWith("The registration response was refused:");
        await Assert.That(await CountPasskeyPublicKeysAsync(host)).IsEqualTo(0L);
    }

    [Test]
    public async Task AssertionOptions_RemoveChallengesThatHaveExpired()
    {
        // Arrange — a row inserted directly, because the sweep is only observable on a challenge that
        // is already past its expiry, and no ceremony can produce one from the outside.
        await using RepositoryTestHost host = await StartRepositoryHostAsync();
        await using ApiFactory factory = CreateApiFactory(host);
        Guid expiredId = await InsertExpiredChallengeAsync(host);

        // Act
        HttpResponseMessage options = await factory.CreateClient().PostAsync(AssertionOptionsPath, content: null);

        // Assert
        await Assert.That(options.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await CountChallengeAsync(host, expiredId)).IsEqualTo(0L);
    }

    private const string OwnerSubject = "passkey-owner";
    private const string OwnerEmail = "passkey-owner@example.com";
    private const string OtherSubject = "passkey-other";
    private const string OtherEmail = "passkey-other@example.com";

    /// <summary>
    /// A domain the attacker owns outright, whose name begins with the one allowed origin. Refused by
    /// an equality comparison and accepted by a prefix one.
    /// </summary>
    private const string LookalikeOrigin = ApiFactory.PasskeyOrigin + ".attacker.example";

    /// <summary>
    /// A stored counter and a value below it. Ten rather than one because the reported counter has to
    /// be below the stored one <b>and</b> above zero: a pair where either is zero lands in the synced
    /// authenticator carve-out instead, which is not a regression at all.
    /// </summary>
    private const uint SeededCounter = 10;

    private const uint RegressedCounter = 5;

    /// <summary>
    /// How many refusals the test above drives: the seven distinct checks that can turn a sign-in
    /// down, plus one oversized member for each of the five the payload ceilings bound. Every one of
    /// them has to leave the identical response.
    /// </summary>
    private const int ReachableAssertionRefusals = 12;

    /// <summary>
    /// What attested credential data costs before the credential id and the key it wraps: the
    /// 37-byte authenticator data header — a relying party id hash, one flags byte and a four-byte
    /// counter — the 16-byte AAGUID, and the two bytes stating how long the credential id is.
    /// </summary>
    private const int AttestedCredentialDataOverheadBytes = 37 + 16 + 2;

    /// <summary>
    /// The largest attestation object this product could be handed and still store everything inside
    /// it: the two domain maxima, plus what the shape carries around them.
    /// </summary>
    /// <remarks>
    /// Read from the domain's own ceilings rather than from the payload one, and that is the whole
    /// point of the constant. A credential id longer than
    /// <see cref="PasskeyPublicKey.MaxWebAuthnCredentialIdLength"/> or a key longer than
    /// <see cref="PasskeyPublicKey.MaxCoseKeyLength"/> could not be stored if it were accepted, so
    /// this is the size above which the payload ceiling is refusing nothing the product could have
    /// used — and below which it is refusing a device the product could have registered.
    /// </remarks>
    private const int LargestStorableAttestationObjectBytes =
        AttestedCredentialDataOverheadBytes
        + PasskeyPublicKey.MaxWebAuthnCredentialIdLength
        + PasskeyPublicKey.MaxCoseKeyLength;

    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";
    private const string AssertionOptionsPath = "/api/passkeys/assertion/options";
    private const string AssertionPath = "/api/passkeys/assertion";

    /// <summary>
    /// The third nonce pool's options leg, which lives in this file's authenticated group. Named here
    /// because the two cross-ceremony refusals above have to draw from it, and there is no finish leg
    /// for it in this file — the ceremony is completed by the erasure endpoint, whose own tests live
    /// in <c>ErasureReauthenticationTests</c>.
    /// </summary>
    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";

    /// <summary>
    /// One <c>sessions</c> row, read out of the database rather than out of a response, because the
    /// assertion response deliberately carries no session id.
    /// </summary>
    private readonly record struct SessionRow(Guid UserId, Guid CredentialId, string Kind);

    /// <summary>
    /// Hosts the API over the repository host's container: that host is the one with the passkey
    /// seeding on it, and it hands over both identities the application expects — the least-privilege
    /// role it serves requests on, and the elevated account startup migrates and provisions with.
    /// </summary>
    private static ApiFactory CreateApiFactory(RepositoryTestHost host) =>
        new(host.AppConnectionString, adminConnectionString: host.ConnectionString);

    private static async Task<RepositoryTestHost> StartRepositoryHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// Runs an options leg and returns the challenge bytes it issued.
    /// </summary>
    private static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path)
    {
        JsonNode options = await PostForJsonAsync(client, path);
        return Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
    }

    private static async Task<JsonNode> PostForJsonAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.PostAsync(path, content: null);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response);
    }

    /// <summary>
    /// Runs both authenticated legs of a registration and returns what the device produced.
    /// </summary>
    private static async Task<AttestationResult> RegisterAsync(
        HttpClient client,
        SyntheticAuthenticator authenticator)
    {
        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult result = authenticator.Register(challenge, ApiFactory.PasskeyOrigin);
        HttpResponseMessage response = await PostRegistrationAsync(client, result);
        response.EnsureSuccessStatusCode();
        return result;
    }

    private static Task<HttpResponseMessage> PostRegistrationAsync(HttpClient client, AttestationResult result)
    {
        // JSON null rather than a present object carrying false when the device reported nothing about
        // the extension: the two are different claims, and collapsing them here would hide the
        // response reporting them as one.
        object? clientExtensionResults = result.PrfEnabled is { } enabled
            ? new { prf = new { enabled } }
            : null;

        return client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = result.ClientDataJsonBase64Url,
            attestationObject = result.AttestationObjectBase64Url,
            clientExtensionResults,
        });
    }

    /// <summary>
    /// Runs the anonymous options leg and has the device answer the challenge it issued.
    /// </summary>
    private static async Task<AssertionResult> BuildAssertionAsync(
        HttpClient client,
        SyntheticAuthenticator authenticator,
        Guid? userId)
    {
        byte[] challenge = await BeginCeremonyAsync(client, AssertionOptionsPath);
        return authenticator.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            userId is { } id ? PasskeyEncoding.ToUserHandle(id) : null);
    }

    private static Task<HttpResponseMessage> PostAssertionAsync(HttpClient client, AssertionResult result) =>
        client.PostAsJsonAsync(AssertionPath, new
        {
            credentialId = result.CredentialIdBase64Url,
            clientDataJson = result.ClientDataJsonBase64Url,
            authenticatorData = result.AuthenticatorDataBase64Url,
            signature = result.SignatureBase64Url,
            userHandle = result.UserHandleBase64Url,
        });

    /// <summary>
    /// The caller-supplied members of an assertion that <see cref="PasskeyPayloadLimits"/> bounds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public because TUnit builds the parameterised cases from these values. Named rather than
    /// numbered so a failing case says which ceiling drifted.
    /// </para>
    /// <para>
    /// <see cref="UserHandle"/> is bounded like the rest and reached at a different moment, which is
    /// why it appears in the identical-response test below and in neither of the parameterised
    /// ceiling pairs. The other four are decoded before <c>clientDataJSON</c> is parsed, so their
    /// ceilings are applied before the challenge is spent and the surviving nonce is what tells a
    /// size refusal from a ceremony one. The handle is not read until the credential has been found,
    /// well past the point the nonce is consumed, so both sides of its ceiling spend the challenge
    /// and that observable can say nothing about it. Its at-ceiling half is pinned in the unit suite
    /// instead, on <c>CompleteAssertionHandler</c>, where the refusal's reason is visible and the two
    /// refusals are genuinely different sentences.
    /// </para>
    /// </remarks>
    public enum AssertionMember
    {
        ClientDataJson,
        AuthenticatorData,
        Signature,
        CredentialId,
        UserHandle,
    }

    /// <summary>
    /// The ceiling each member is bounded by, read from the production constants rather than
    /// restated — a test carrying its own copy of the number would keep passing after the real one
    /// moved.
    /// </summary>
    private static int CeilingFor(AssertionMember member) => member switch
    {
        AssertionMember.ClientDataJson => PasskeyPayloadLimits.ClientDataJsonBytes,
        AssertionMember.AuthenticatorData => PasskeyPayloadLimits.AssertionAuthenticatorDataBytes,
        AssertionMember.Signature => PasskeyPayloadLimits.SignatureBytes,
        AssertionMember.CredentialId => PasskeyPayloadLimits.CredentialIdBytes,
        AssertionMember.UserHandle => PasskeyPayloadLimits.UserHandleBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(member), member, "No ceiling is defined for this member."),
    };

    /// <summary>
    /// The same ceremony with one member resized to <paramref name="decodedBytes"/> and everything
    /// else left genuine, so the only thing that can decide the response is the member under test.
    /// </summary>
    /// <remarks>
    /// <c>clientDataJSON</c> is padded rather than replaced, and that difference is load-bearing.
    /// The handler parses it and recovers the challenge from it before anything is verified, so
    /// random bytes of the right length would be refused as malformed <b>before</b> the challenge is
    /// spent — which is the same observation an oversized member produces, and the test would no
    /// longer be able to tell the two apart. Padding keeps the object valid and the challenge intact,
    /// so a member within its ceiling reaches the ceremony exactly as a real one does.
    /// </remarks>
    private static Task<HttpResponseMessage> PostAssertionAsync(
        HttpClient client,
        AssertionResult result,
        AssertionMember member,
        int decodedBytes)
    {
        string resized = member is AssertionMember.ClientDataJson
            ? Base64UrlText.Encode(PadClientDataJson(result.ClientDataJson, decodedBytes))
            : Base64UrlText.Encode(RandomNumberGenerator.GetBytes(decodedBytes));

        return client.PostAsJsonAsync(AssertionPath, new
        {
            credentialId = member is AssertionMember.CredentialId ? resized : result.CredentialIdBase64Url,
            clientDataJson = member is AssertionMember.ClientDataJson ? resized : result.ClientDataJsonBase64Url,
            authenticatorData = member is AssertionMember.AuthenticatorData
                ? resized
                : result.AuthenticatorDataBase64Url,
            signature = member is AssertionMember.Signature ? resized : result.SignatureBase64Url,
            userHandle = member is AssertionMember.UserHandle ? resized : result.UserHandleBase64Url,
        });
    }

    /// <summary>
    /// Grows a genuine <c>clientDataJSON</c> to exactly <paramref name="decodedBytes"/> by appending
    /// one more member to the object.
    /// </summary>
    /// <remarks>
    /// A member the specification does not define, which a client is explicitly permitted to send and
    /// the parser is required to ignore — so this is a larger response of the shape a real one has,
    /// not a malformed one that happens to be long.
    /// </remarks>
    private static byte[] PadClientDataJson(byte[] clientDataJson, int decodedBytes)
    {
        const string opening = ",\"padding\":\"";
        const string closing = "\"}";

        // The trailing brace is replaced by the appended member and a new one.
        int padding = decodedBytes - clientDataJson.Length - opening.Length - closing.Length + 1;
        if (padding < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(decodedBytes),
                decodedBytes,
                "The target is smaller than the client data the ceremony produced.");
        }

        StringBuilder builder = new(Encoding.UTF8.GetString(clientDataJson));
        builder.Length -= 1;
        builder.Append(opening).Append('a', padding).Append(closing);

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>
    /// The one validation message a refused registration carries, so a test asserts on the sentence
    /// the caller is actually told rather than on the status alone.
    /// </summary>
    /// <remarks>
    /// Read from the <c>Response</c> key by name rather than from whichever key happens to come first.
    /// Every refusal on this leg is filed under that field by <c>CompleteRegistrationHandler</c>, and
    /// several tests now depend on this helper — taking the first key would keep them green after a
    /// refusal moved to a different field, which is a change the caller would see.
    /// </remarks>
    private static async Task<string> ReadValidationErrorAsync(HttpResponseMessage response)
    {
        JsonNode errors = (await ReadJsonAsync(response))["errors"]!;

        return errors["Response"]!.AsArray()[0]!.GetValue<string>();
    }

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
    /// The whole response body, with the one member that varies per <b>request</b> rather than per
    /// <b>cause</b> replaced by a fixed placeholder.
    /// </summary>
    /// <remarks>
    /// <c>traceId</c> is the correlation identifier the problem-details pipeline stamps on every
    /// problem response in this application; it is a new value on every request, including two
    /// requests refused for the identical reason, so comparing it would compare the trace and not the
    /// refusal. Nothing else is normalised, and that is what keeps the comparison a whole-body one:
    /// the member is replaced rather than removed, so a <c>traceId</c> that stopped being emitted
    /// still fails, and any other member appearing, disappearing or differing fails with it.
    /// </remarks>
    private static async Task<string> ReadComparableBodyAsync(HttpResponseMessage response)
    {
        JsonObject body = (await ReadJsonAsync(response)).AsObject();
        if (body.ContainsKey(TraceIdMember))
        {
            body[TraceIdMember] = "<one per request>";
        }

        return body.ToJsonString();
    }

    private const string TraceIdMember = "traceId";

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

    private static async Task<string> ReadTitleAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response))["title"]!.GetValue<string>();

    /// <summary>
    /// Every <c>sessions</c> row in the database, on the container's superuser connection so that
    /// row-level security cannot make an existing row look absent — the whole point of several of the
    /// assertions above is that no row was written at all.
    /// </summary>
    private static async Task<IReadOnlyList<SessionRow>> ReadSessionsAsync(RepositoryTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select user_id, credential_id, kind from sessions order by created_at_utc",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        List<SessionRow> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add(new SessionRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
        }

        return rows;
    }

    private static async Task<Guid> FindPasskeyCredentialIdAsync(
        RepositoryTestHost host,
        byte[] webAuthnCredentialId)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "select credential_id from passkey_public_keys where webauthn_credential_id = @handle",
            connection);
        command.Parameters.AddWithValue("handle", webAuthnCredentialId);

        return await command.ExecuteScalarAsync() switch
        {
            Guid credentialId => credentialId,
            var unexpected => throw new InvalidOperationException(
                $"Expected a credential id from 'passkey_public_keys', got '{unexpected ?? "null"}'."),
        };
    }

    private static Task<long> CountPasskeyPublicKeysAsync(RepositoryTestHost host) =>
        ScalarCountAsync(host, "select count(*) from passkey_public_keys", parameter: null);

    /// <summary>
    /// Scoped to the passkey rows, because <c>credentials</c> holds both kinds and seeding an account
    /// already writes it a federated one — an unscoped count would never be zero and would never be
    /// one either.
    /// </summary>
    private static Task<long> CountPasskeyCredentialsAsync(RepositoryTestHost host) =>
        ScalarCountAsync(host, "select count(*) from credentials where type = 'passkey'", parameter: null);

    private static Task<long> CountPasskeySignatureCountersAsync(RepositoryTestHost host) =>
        ScalarCountAsync(host, "select count(*) from passkey_signature_counters", parameter: null);

    private static Task<long> CountUsersAsync(RepositoryTestHost host) =>
        ScalarCountAsync(host, "select count(*) from users", parameter: null);

    private static Task<long> CountChallengeAsync(RepositoryTestHost host, Guid id) =>
        ScalarCountAsync(host, "select count(*) from webauthn_challenges where id = @id", ("id", id));

    private static async Task<long> ScalarCountAsync(
        RepositoryTestHost host,
        string sql,
        (string Name, object Value)? parameter)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        if (parameter is { } bound)
        {
            command.Parameters.AddWithValue(bound.Name, bound.Value);
        }

        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException($"Expected a count, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Writes a challenge that expired before the request under test ran. Created earlier than it
    /// expires, because <c>CK_webauthn_challenges_lifetime</c> refuses a row that was never live.
    /// </summary>
    private static async Task<Guid> InsertExpiredChallengeAsync(RepositoryTestHost host)
    {
        Guid id = Guid.CreateVersion7();
        DateTime nowUtc = DateTime.UtcNow;

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            insert into webauthn_challenges (id, challenge, ceremony, created_at_utc, expires_at_utc)
            values (@id, @challenge, 'authentication', @created, @expires)
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("challenge", new byte[32]);
        command.Parameters.AddWithValue("created", nowUtc.AddMinutes(-10));
        command.Parameters.AddWithValue("expires", nowUtc.AddMinutes(-5));
        await command.ExecuteNonQueryAsync();

        return id;
    }
}
