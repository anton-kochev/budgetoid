using System.Security.Cryptography;
using Application.Abstractions;
using Application.Passkeys;
using Application.Passkeys.CompleteAssertion;
using Domain.Sessions;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// The sign-in unit of work under replay.
/// </summary>
/// <remarks>
/// <para>
/// The API installs a retrying execution strategy, so the delegate handed to
/// <see cref="ITransactionalExecutor"/> runs again after a transient failure — against a database
/// that rolled the abandoned attempt back and a change tracker that did not. Two things survive that
/// rollback: a signature counter this attempt already advanced in memory, and a session it already
/// queued for insert. Replaying against either turns a valid passkey into a refusal or one sign-in
/// into two session rows.
/// </para>
/// <para>
/// A unit test rather than an integration one, because a transient failure cannot be induced against
/// a healthy database without faking it somewhere. Forcing the replay in the executor is the honest
/// place: the delegate really runs twice, against collaborators that really do hand back what the
/// first pass left in them.
/// </para>
/// </remarks>
public sealed class CompleteAssertionHandlerTests
{
    /// <summary>
    /// The counter is the half of the problem a person feels: the second pass sees the value the
    /// first pass wrote into the tracked instance, refuses it as a regression, and answers the same
    /// 401 an unknown credential gets — so a valid passkey stops working because the database blinked.
    /// </summary>
    [Test]
    public async Task HandleAsync_WhenTheUnitOfWorkIsReplayed_StillEstablishesTheSession()
    {
        // Arrange — a device that counts, standing at zero and reporting one, so the first pass
        // genuinely advances the counter and the second has something to trip over.
        Ceremony ceremony = Ceremony.Build(reportedSignCount: AdvancedCounter, storedSignCount: 0);

        // Act
        EstablishedSession established =
            (await ceremony.Handler.HandleAsync(ceremony.Command)).Value;

        // Assert — accepted twice, on a counter each pass read fresh, and the session it opens is the
        // full one a passkey earns.
        await Assert.That(established.Kind).IsEqualTo(SessionKind.Full);
        await Assert.That(established.ExpiresAtUtc).IsEqualTo(Ceremony.UtcNow.AddDays(14));
        await Assert.That(ceremony.Passkeys.SavedCounterValues).IsEquivalentTo(
            new uint[] { AdvancedCounter, AdvancedCounter });
    }

    /// <summary>
    /// The other half, and the one nobody notices: a session queued by the abandoned attempt is still
    /// queued when the surviving one commits, so a single sign-in writes two rows.
    /// </summary>
    /// <remarks>
    /// The device reports zero against a stored zero on purpose. That lands in the synced-authenticator
    /// carve-out, where the counter neither advances nor refuses — so nothing on the counter path can
    /// end this delegate early, and the only thing this test can fail for is the session count.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheUnitOfWorkIsReplayed_AddsExactlyOneSession()
    {
        // Arrange
        Ceremony ceremony = Ceremony.Build(reportedSignCount: 0, storedSignCount: 0);

        // Act
        await ceremony.Handler.HandleAsync(ceremony.Command);

        // Assert — one ceremony, one session, however many times the transaction was attempted.
        await Assert.That(ceremony.Sessions.Sessions.Count).IsEqualTo(1);
        await Assert.That(ceremony.Sessions.Sessions[0].UserId).IsEqualTo(ceremony.UserId);
        await Assert.That(ceremony.Sessions.Sessions[0].CredentialId).IsEqualTo(ceremony.CredentialId);
    }

    /// <summary>
    /// Where the discard is made, not merely that it is made.
    /// </summary>
    /// <remarks>
    /// A discard hoisted above the executor would run once and be undone by nothing — the rollback it
    /// exists to clean up after happens later, so the second attempt would start with the first
    /// attempt's leftovers exactly as if the call were absent. Recording the attempt each discard
    /// lands on is what tells the two placements apart; a plain call count cannot.
    /// </remarks>
    [Test]
    public async Task HandleAsync_DiscardsTheTrackedEntitiesInsideEveryAttempt()
    {
        // Arrange — a device on the synced-authenticator path again, so the counter can neither end
        // the delegate early nor be what this test fails for.
        Ceremony ceremony = Ceremony.Build(reportedSignCount: 0, storedSignCount: 0);

        // Act
        await ceremony.Handler.HandleAsync(ceremony.Command);

        // Assert — once per attempt, and never on attempt zero, which is what a call outside the
        // executor would record.
        await Assert.That(ceremony.PersistenceState.DiscardedOnAttempt).IsEquivalentTo(new[] { 1, 2 });
    }

    /// <summary>
    /// The under-ceiling half of the user handle's payload bound, which no request can observe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other member of an assertion is decoded before <c>clientDataJSON</c> is parsed, so an
    /// oversized one is refused while the challenge is still live and the surviving nonce is what
    /// tells a size refusal from a ceremony one. The handle is not read until the credential has been
    /// found, which is well past the point the nonce is consumed — so both sides of its ceiling spend
    /// the challenge, both answer the identical 401 every refusal on that leg answers, and no
    /// observation available over HTTP can separate them. The integration suite drives the
    /// over-ceiling half for exactly that identity; this is the only place the other half is visible.
    /// </para>
    /// <para>
    /// What makes it visible here is <see cref="PasskeyVerificationException.Reason"/>, which exists
    /// for the log and is deliberately never shown to a caller. A handle at the ceiling refused for
    /// naming the wrong account got past the size check; the same handle refused as "not base64url
    /// text" never reached it. Cut <c>UserHandleBytes</c> to tidy it towards the sixteen bytes this
    /// product's handles actually are, and the second sentence is what this test reads — which is
    /// the failure a device storing a longer handle would hit, answered by a 401 nobody can diagnose.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAUserHandleAtItsCeiling_IsRefusedForTheAccountItNamesRatherThanItsSize()
    {
        // Arrange — a handle as large as the protocol lets a device have stored. It can match no
        // account, because a handle this product issues is the sixteen bytes of a user id, so
        // reaching the ceremony is the most a request carrying one can do.
        Ceremony ceremony = Ceremony.Build(
            reportedSignCount: 0,
            storedSignCount: 0,
            userHandle: new byte[WebAuthnUserHandleCapBytes]);

        // Act
        PasskeyVerificationException refusal =
            await ThrowsRefusalAsync(() => ceremony.Handler.HandleAsync(ceremony.Command));

        // Assert
        await Assert.That(refusal.Reason).IsEqualTo("The user handle did not name the credential's account.");
    }

    /// <summary>
    /// The largest user handle WebAuthn permits an authenticator to have stored, which is where
    /// <see cref="PasskeyPayloadLimits.UserHandleBytes"/> takes its value from rather than choosing
    /// one.
    /// </summary>
    /// <remarks>
    /// The protocol's number, written here instead of read from the product's constant, and that is
    /// what lets the test above fail at all. A payload measured from the ceiling shrinks with the
    /// ceiling, so it would decode however far the ceiling was cut and could never notice the cut.
    /// Measured from what a device is allowed to be holding, a ceiling lowered beneath it goes red —
    /// and refusing a handle is refusing a sign-in, which is the only failure a bound like this has.
    /// </remarks>
    private const int WebAuthnUserHandleCapBytes = 64;

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

        throw new InvalidOperationException("Expected the assertion to be refused.");
    }

    /// <summary>
    /// A counter above the stored zero, so the first pass moves it and the second has a value to be
    /// refused for. One is enough: the rule is that the reported value must exceed the stored one.
    /// </summary>
    private const uint AdvancedCounter = 1;

    /// <summary>How many times the executor runs the unit of work, standing in for one transient failure.</summary>
    private const int Attempts = 2;

    private const string RelyingPartyId = "localhost";
    private const string Origin = "https://localhost:4200";

    /// <summary>
    /// One verified sign-in, wired up: a real device, a real signature, and the fakes the handler
    /// writes through.
    /// </summary>
    private sealed record Ceremony
    {
        /// <summary>The instant the handler stamps the session with.</summary>
        public static readonly DateTime UtcNow = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

        public required CompleteAssertionHandler Handler { get; init; }

        public required CompleteAssertionCommand Command { get; init; }

        public required InMemoryPasskeyRepository Passkeys { get; init; }

        public required InMemorySessionRepository Sessions { get; init; }

        public required RecordingPersistenceState PersistenceState { get; init; }

        public required Guid UserId { get; init; }

        public required Guid CredentialId { get; init; }

        /// <summary>
        /// Builds a ceremony a healthy handler accepts, on an executor that replays it.
        /// </summary>
        /// <param name="reportedSignCount">The counter the device puts in its authenticator data.</param>
        /// <param name="storedSignCount">The counter the row holds before the ceremony runs.</param>
        /// <param name="userHandle">
        /// The handle the device returns, or null for the genuine one. Null rather than absent means
        /// "the handle this account's authenticator would really hand back", so a caller overriding
        /// it is saying so out loud.
        /// </param>
        public static Ceremony Build(
            uint reportedSignCount,
            uint storedSignCount,
            byte[]? userHandle = null)
        {
            SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
            Guid userId = Guid.CreateVersion7();
            Credential credential = Credential.CreatePasskey(userId, UtcNow);
            PasskeyPublicKey publicKey = PasskeyPublicKey.Register(
                credential,
                device.CredentialId,
                device.CoseKey,
                device.Algorithm);

            InMemoryPasskeyRepository passkeys = new();
            passkeys.Register(credential, publicKey, storedSignCount);

            byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
            AssertionResult assertion = device.Authenticate(
                challenge,
                Origin,
                userHandle ?? PasskeyEncoding.ToUserHandle(userId),
                signCount: reportedSignCount);

            InMemorySessionRepository sessions = new();
            RetryingTransactionalExecutor executor = new(Attempts);
            RecordingPersistenceState persistenceState = new(
                () => executor.Attempts,
                passkeys.DiscardTrackedEntities,
                sessions.DiscardTrackedEntities);

            CompleteAssertionHandler handler = new(
                new StubWebAuthnChallengeStore(challenge, WebAuthnCeremony.Authentication),
                passkeys,
                sessions,
                new RecordingUserContextWriter(),
                new StubPasskeyCeremonyPolicy(RelyingPartyId, Origin),
                executor,
                persistenceState,
                new FakeTimeProvider(new DateTimeOffset(UtcNow)));

            return new Ceremony
            {
                Handler = handler,
                Command = new CompleteAssertionCommand(
                    assertion.CredentialIdBase64Url,
                    assertion.ClientDataJsonBase64Url,
                    assertion.AuthenticatorDataBase64Url,
                    assertion.SignatureBase64Url,
                    assertion.UserHandleBase64Url),
                Passkeys = passkeys,
                Sessions = sessions,
                PersistenceState = persistenceState,
                UserId = userId,
                CredentialId = credential.Id,
            };
        }

        private const int ChallengeBytes = 32;
    }
}
