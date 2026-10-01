using System.Security.Cryptography;
using Application.Abstractions;
using Application.Passkeys;
using Application.Passkeys.Reauthentication;
using Domain.Users;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// The re-authentication gate on its own, where each refusal's <b>reason</b> is visible.
/// </summary>
/// <remarks>
/// <para>
/// The same division of labour <c>CompleteAssertionHandlerTests</c> already uses against its HTTP
/// suite. Over the wire every refusal on the erasure endpoint is byte-identical by design, so nothing
/// there can say <em>which</em> check turned a request down — which means a refusal that fires for
/// the wrong reason is invisible from a request. <see cref="PasskeyVerificationException.Reason"/>
/// exists for the log and never reaches a caller, and it is what makes the ladder legible here.
/// </para>
/// <para>
/// The device is a real <see cref="SyntheticAuthenticator"/> holding a real key pair, so the
/// accepting cases only pass when the gate reads the wire format the specification describes. Each
/// test moves exactly one value away from a response the gate would otherwise accept.
/// </para>
/// </remarks>
public sealed class PasskeyReauthenticationTests
{
    [Test]
    public async Task VerifyAsync_WithAMemberThatIsNotBase64Url_Throws()
    {
        // Arrange — genuine in every respect except one member, which is text no decoder accepts.
        Fixture fixture = Fixture.Build();
        ReauthenticationAssertion malformed = fixture.Assertion with { Signature = "not base64url!!" };

        // Act
        PasskeyVerificationException refusal =
            await ThrowsRefusalAsync(() => fixture.Gate.VerifyAsync(malformed));

        // Assert — refused before anything is looked up, and before the nonce is spent: the decode is
        // the first thing this path does, which is what stops a caller naming how much memory a
        // refusal costs.
        await Assert.That(refusal.Reason).IsEqualTo("A member of the assertion was not base64url text.");
        await Assert.That(fixture.Challenges.ConsumeCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// The under-ceiling half of the user handle's payload bound, which no request can observe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every refusal on the erasure endpoint answers identically, so both sides of this ceiling look
    /// the same from outside and no observation available over HTTP can separate them. This is the
    /// only place the accepting side is visible.
    /// </para>
    /// <para>
    /// The handle is as large as the protocol lets a device have stored, and that size is written here
    /// rather than read from <see cref="PasskeyPayloadLimits.UserHandleBytes"/> — a payload measured
    /// from the ceiling shrinks with the ceiling and could never notice the cut. Measured from what a
    /// device is allowed to be holding, a ceiling lowered beneath it goes red, and refusing a handle a
    /// real device returns is refusing an erasure the account holder is entitled to.
    /// </para>
    /// <para>
    /// The sentence is the observable, and it says the account comes from the <b>request</b> — which
    /// is where this path differs from sign-in, where the handle is checked against the credential's
    /// own owner. Getting that backwards is the single thing a future reader is most likely to do.
    /// </para>
    /// </remarks>
    [Test]
    public async Task VerifyAsync_WithAUserHandleAtItsCeiling_IsRefusedForTheAccountItNamesRatherThanItsSize()
    {
        // Arrange — a handle this product could never have issued, since the handles it issues are the
        // sixteen bytes of a user id. Reaching the ceremony is the most a request carrying one can do.
        Fixture fixture = Fixture.Build(userHandle: new byte[WebAuthnUserHandleCapBytes]);

        // Act
        PasskeyVerificationException refusal =
            await ThrowsRefusalAsync(() => fixture.Gate.VerifyAsync(fixture.Assertion));

        // Assert
        await Assert.That(refusal.Reason)
            .IsEqualTo("The user handle did not name the account the request is authenticated as.");
    }

    /// <summary>
    /// A counter that went backwards is what a cloned authenticator produces, and the domain reports
    /// it by throwing. This is the test of the <b>translation</b>: an untranslated regression escapes
    /// as a 500 while every other refusal answers 401, and a caller who can tell those apart on an
    /// endpoint that destroys accounts has learned that the handle they presented is real.
    /// </summary>
    [Test]
    public async Task VerifyAsync_WhoseReportedCounterWentBackwards_Throws()
    {
        // Arrange — genuinely signed and correct in every other respect, reporting a counter below the
        // stored one. Ten and five rather than one and zero: a pair where either is zero lands in the
        // synced-authenticator carve-out instead, which is not a regression at all.
        Fixture fixture = Fixture.Build(storedSignCount: SeededCounter, reportedSignCount: RegressedCounter);

        // Act
        PasskeyVerificationException refusal =
            await ThrowsRefusalAsync(() => fixture.Gate.VerifyAsync(fixture.Assertion));

        // Assert
        await Assert.That(refusal.Reason).IsEqualTo("The reported signature counter did not advance.");
        await Assert.That(fixture.Passkeys.SavedCounterValues.Count).IsEqualTo(0);
    }

    [Test]
    public async Task VerifyAsync_WhoseCounterAdvanced_WritesIt()
    {
        // Arrange
        Fixture fixture = Fixture.Build(storedSignCount: 0, reportedSignCount: AdvancedCounter);

        // Act
        await fixture.Gate.VerifyAsync(fixture.Assertion);

        // Assert — an assertion means the same thing on every path it is accepted on, so the counter
        // is honoured here rather than skipped on the grounds that the row is about to be cascaded
        // away anyway.
        await Assert.That(fixture.Passkeys.SavedCounterValues).IsEquivalentTo(new[] { AdvancedCounter });
    }

    /// <summary>
    /// The synced-authenticator carve-out, and the reason the single-column <c>UPDATE</c> grant stays
    /// exercised for a reason rather than on every ceremony.
    /// </summary>
    /// <remarks>
    /// Authenticators backing synced passkeys — which is most of them — always report zero, so a zero
    /// against a stored zero is accepted rather than refused. Writing an unchanged row would be an
    /// UPDATE per erasure ceremony that records nothing, and the pair of tests is what separates
    /// "writes when it moved" from "writes always".
    /// </remarks>
    [Test]
    public async Task VerifyAsync_WhoseCounterDidNotMove_WritesNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Build(storedSignCount: 0, reportedSignCount: 0);

        // Act
        await fixture.Gate.VerifyAsync(fixture.Assertion);

        // Assert
        await Assert.That(fixture.Passkeys.SavedCounterValues.Count).IsEqualTo(0);
    }

    /// <summary>
    /// The control for the ladder above: an assertion with nothing wrong with it completes, and spends
    /// its nonce doing so.
    /// </summary>
    /// <remarks>
    /// Without it a gate that refused everything satisfies every refusal test in this file. The gate
    /// returns nothing on success — the account is already known, so there is no answer to hand back,
    /// which is the shape difference from the sign-in handler.
    /// </remarks>
    [Test]
    public async Task VerifyAsync_WithAValidAssertion_Completes()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        await fixture.Gate.VerifyAsync(fixture.Assertion);

        // Assert — the nonce is consumed before anything is verified, so one issued challenge is not
        // an unlimited grinding target in front of account destruction.
        await Assert.That(fixture.Challenges.ConsumeCallCount).IsEqualTo(1);
    }

    /// <summary>
    /// Runs a ceremony that is expected to be refused and hands back the refusal.
    /// </summary>
    private static async Task<PasskeyVerificationException> ThrowsRefusalAsync(Func<Task> ceremony)
    {
        try
        {
            await ceremony();
        }
        catch (PasskeyVerificationException refusal)
        {
            return refusal;
        }

        throw new InvalidOperationException("Expected the re-authentication to be refused.");
    }

    /// <summary>
    /// The largest user handle WebAuthn permits an authenticator to have stored, which is where
    /// <see cref="PasskeyPayloadLimits.UserHandleBytes"/> takes its value from rather than choosing
    /// one.
    /// </summary>
    private const int WebAuthnUserHandleCapBytes = 64;

    /// <summary>
    /// A stored counter and a value below it. Ten rather than one because the reported counter has to
    /// be below the stored one <b>and</b> above zero: a pair where either is zero lands in the synced
    /// authenticator carve-out instead.
    /// </summary>
    private const uint SeededCounter = 10;

    private const uint RegressedCounter = 5;

    /// <summary>
    /// A counter above the stored zero. One is enough: the rule is that the reported value must exceed
    /// the stored one.
    /// </summary>
    private const uint AdvancedCounter = 1;

    private const string RelyingPartyId = "localhost";
    private const string Origin = "https://localhost:4200";
    private const int ChallengeBytes = 32;

    /// <summary>
    /// One re-authentication ceremony, wired up: a real device, a real signature, and the fakes the
    /// gate reads and writes through.
    /// </summary>
    private sealed record Fixture(
        PasskeyReauthentication Gate,
        ReauthenticationAssertion Assertion,
        InMemoryPasskeyRepository Passkeys,
        StubWebAuthnChallengeStore Challenges,
        Guid UserId)
    {
        /// <summary>Fixed instant for every seeded row, so nothing here depends on the wall clock.</summary>
        public static readonly DateTime UtcNow = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

        /// <summary>
        /// Builds a ceremony a healthy gate accepts.
        /// </summary>
        /// <param name="storedSignCount">The counter the row holds before the ceremony runs.</param>
        /// <param name="reportedSignCount">The counter the device puts in its authenticator data.</param>
        /// <param name="userHandle">
        /// The handle the device returns, or null for the genuine one. Null rather than absent means
        /// "the handle this account's authenticator would really hand back", so a caller overriding it
        /// is saying so out loud.
        /// </param>
        public static Fixture Build(
            uint storedSignCount = 0,
            uint reportedSignCount = 0,
            byte[]? userHandle = null)
        {
            Guid userId = Guid.CreateVersion7();
            SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
            Credential credential = Credential.CreatePasskey(userId, UtcNow);

            InMemoryPasskeyRepository passkeys = new();
            passkeys.Register(
                credential,
                PasskeyPublicKey.Register(credential, device.CredentialId, device.CoseKey, device.Algorithm),
                storedSignCount);

            byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
            StubWebAuthnChallengeStore challenges = new(challenge, WebAuthnCeremony.Reauthentication);
            AssertionResult assertion = device.Authenticate(
                challenge,
                Origin,
                userHandle ?? PasskeyEncoding.ToUserHandle(userId),
                signCount: reportedSignCount);

            PasskeyReauthentication gate = new(
                challenges,
                passkeys,
                new StubUserContext(userId),
                new StubPasskeyCeremonyPolicy(RelyingPartyId, Origin));

            return new Fixture(
                gate,
                new ReauthenticationAssertion(
                    assertion.CredentialIdBase64Url,
                    assertion.ClientDataJsonBase64Url,
                    assertion.AuthenticatorDataBase64Url,
                    assertion.SignatureBase64Url,
                    assertion.UserHandleBase64Url),
                passkeys,
                challenges,
                userId);
        }
    }
}
