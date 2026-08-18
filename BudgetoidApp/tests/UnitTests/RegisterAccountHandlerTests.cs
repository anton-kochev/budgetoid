using System.Security.Cryptography;
using Application.Abstractions;
using Application.Passkeys.CompleteRegistration;
using Application.RecoveryCodes.GenerateRecoveryCodes;
using Application.Registration;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;
using ValidationException = Domain.Common.ValidationException;

namespace UnitTests;

/// <summary>
/// The order of <c>RegisterAccountHandler</c>'s ladder, one test per adjacent pair of rungs that could
/// be swapped.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file exists because an integration test provably cannot see what it asserts.</b> Every rung
/// from 1 to 11 answers <b>400</b>, and most of them are keyed on the same member, so two neighbouring
/// checks in the wrong order produce a response identical in status, identical in shape, and different
/// only in one sentence — which nothing over HTTP compares unless it was written to. Worse, three of the
/// swaps produce no wrong answer at all on a <em>correct</em> request and only bite on a malformed one:
/// they are invisible to the happy path by construction.
/// </para>
/// <para>
/// <b>Each test therefore arranges two faults at once and asserts which one is reported.</b> One fault
/// tells nothing about ordering — whichever check exists reports it. Two adjacent faults, and the
/// question "which sentence came back" is exactly the question "which check ran first". That is the whole
/// shape of this file, and a later reader tempted to split a test into two single-fault cases would be
/// deleting the only thing it measures.
/// </para>
/// <para>
/// <b>What is deliberately not here.</b> The thirty rows one registration writes, the grants that let
/// them be written and the <c>22P02</c> a transaction opened too early produces are all asserted end to
/// end in <c>IntegrationTests.AccountRegistrationTests</c>, against the real least-privilege connection.
/// Nothing in memory can reproduce a grant matrix, and a second weaker copy of that claim here would be
/// free to disagree with the database about what a save does.
/// </para>
/// <para>
/// The device is a real <see cref="SyntheticAuthenticator" /> holding a real key pair, for the reason
/// every other passkey test in this project uses one: the verifier is the collaborator that has to be
/// satisfied genuinely, and a stub in its place would let a test here prove that an account can be
/// created with the signature faked out.
/// </para>
/// </remarks>
public sealed class RegisterAccountHandlerTests
{
    private const string RelyingPartyId = "localhost";
    private const string Origin = "https://localhost";

    /// <summary>
    /// An origin the host does not allow, used wherever a genuinely signed response has to fail
    /// verification.
    /// </summary>
    /// <remarks>
    /// The response is signed for real over this origin rather than tampered with after the fact, so the
    /// refusal comes from the origin check the verifier makes and not from a signature that never covered
    /// the substituted value.
    /// </remarks>
    private const string ForeignOrigin = "https://not-this-site.example";

    /// <summary>
    /// How wide a registration challenge is.
    /// </summary>
    /// <remarks>
    /// <see cref="RegistrationAccountId.For" /> refuses any other width, so a shorter fixture would be
    /// turned away by a check that says nothing about the rung under test.
    /// </remarks>
    private const int ChallengeBytes = 32;

    /// <summary>The exact width of a verifier, decoded.</summary>
    private const int VerifierLength = 32;

    /// <summary>How many codes a card holds. Restated rather than read off the validation unit.</summary>
    private const int RequiredCodeCount = 10;

    private const string Subject = "google-registering";
    private const string Email = "registering@budgetoid.test";

    /// <summary>The member every refusal about the ceremony's own response is keyed under.</summary>
    private const string ResponseField = "Response";

    /// <summary>The member the passkey's factor identifier is refused under.</summary>
    private const string FactorIdField = "FactorId";

    /// <summary>Fixed instant, so nothing here depends on the wall clock.</summary>
    private static readonly DateTimeOffset UtcNow = new(2026, 3, 4, 9, 15, 0, TimeSpan.Zero);

    /// <summary>
    /// The byte that says which of the two envelopes an assertion is looking at.
    /// </summary>
    /// <remarks>
    /// Two values rather than one, because the two columns are otherwise indistinguishable: same width,
    /// same version, both required. It is
    /// <c>IntegrationTests.WrappedKeyFixture</c>'s choice, spelled here because the two projects share no
    /// fixture.
    /// </remarks>
    private const byte ContentKeyPurpose = 0xC0;

    private const byte IndexKeyPurpose = 0x1D;

    /// <summary>
    /// Rung 4 runs before rung 5: a spent challenge is reported as a spent challenge, whatever else is
    /// wrong with the response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The nonce has to be burnt before the response is judged, or every refusal below it is
    /// replayable.</b> A caller holding one issued challenge could otherwise grind responses against it
    /// until one verified — and on this path the challenge is also what the account identifier is derived
    /// from, so a replayable challenge is a replayable account id.
    /// </para>
    /// <para>
    /// <b>Two faults, and both refusals are keyed on the same member</b>, which is why this is asserted on
    /// the sentence. The challenge is already spent and the response is signed for an origin the host does
    /// not allow: a handler that verified first would answer "the registration response was refused",
    /// which is true, useless, and would have judged a response nothing had yet decided it was allowed to
    /// be judging.
    /// </para>
    /// <para>
    /// The consume count is asserted too, and it is not the same claim: a handler that answered the
    /// challenge sentence from some earlier check without ever calling the store would satisfy the
    /// sentence assertion perfectly while leaving the nonce live.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithASpentChallengeAndAForgedResponse_RefusesOnTheChallenge()
    {
        // Arrange — a challenge this store issued and has already answered once, and a response signed
        // for an origin this host does not allow.
        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
        StubWebAuthnChallengeStore store = new(challenge, WebAuthnCeremony.AccountRegistration);
        await store.ConsumeAsync(challenge);

        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        AttestationResult forged = device.Register(challenge, ForeignOrigin, signCount: 0, prfEnabled: true);
        Fixture fixture = Build(store);

        // Act
        ValidationException refusal = await ThrowsAsync<ValidationException>(
            () => fixture.Handler.HandleAsync(CommandFor(forged)));

        // Assert
        await Assert.That(MessageOf(refusal, ResponseField))
            .IsEqualTo("The challenge is not a live account-registration challenge.");
        await Assert.That(store.ConsumeCallCount).IsEqualTo(2);
        await Assert.That(fixture.Repository.Calls).IsEmpty();
    }

    /// <summary>
    /// Rung 5 runs before rung 6: a response that does not verify is refused as a response, not as a
    /// device that cannot hold the keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything above the prf gate judges signed material; the prf gate judges a sentence the client
    /// wrote about its own hardware.</b> That claim may only be weighed once the response is genuine in
    /// every verifiable respect — when the device is the only thing left it could be about. Judged first,
    /// a caller sending a response that verifies against nothing would be told to go and buy a different
    /// authenticator.
    /// </para>
    /// <para>
    /// Both faults are present: the response is signed for an origin the host does not allow, and the
    /// client reports no extension results at all. Both refusals are keyed on the response, so the
    /// sentence is again what separates them.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAWrongOriginResponseAndNoPrf_RefusesOnTheResponse()
    {
        // Arrange
        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
        StubWebAuthnChallengeStore store = new(challenge, WebAuthnCeremony.AccountRegistration);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        AttestationResult wrongOrigin = device.Register(challenge, ForeignOrigin, signCount: 0, prfEnabled: null);
        Fixture fixture = Build(store);

        // Act
        ValidationException refusal = await ThrowsAsync<ValidationException>(
            () => fixture.Handler.HandleAsync(CommandFor(wrongOrigin, reportsEnabledPrf: false)));

        // Assert — the response's own refusal, and explicitly not the device's.
        string message = MessageOf(refusal, ResponseField);
        await Assert.That(message).Contains("The registration response was refused");
        await Assert.That(message).DoesNotContain("cannot hold the account's keys");
        await Assert.That(fixture.Repository.Calls).IsEmpty();
    }

    /// <summary>
    /// Rung 6 runs before rungs 7 to 10: a device that cannot do PRF is told so, and not that its payload
    /// is malformed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A client that cannot do PRF cannot have produced a wrapped key either</b>, so the key-custody
    /// members are very often absent or nonsense on exactly the requests the prf gate is for. Judged
    /// first, such a request is told its <em>payload</em> was malformed — which sends somebody holding a
    /// device that genuinely lacks the extension off to debug their client, for as long as it takes them
    /// to give up.
    /// </para>
    /// <para>
    /// <b>This is the swap the two refusals' keys make visible</b>, unlike the two pairs above it: the prf
    /// gate is keyed on the response and the factor identifier on its own member, so the assertion is that
    /// the errors bag names one and not the other. It is asserted both ways round, because a bag carrying
    /// both keys would satisfy a one-sided check while telling the caller two things at once — one of
    /// which is the wrong thing to act on.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithNoPrfAndAMalformedFactorId_RefusesOnTheDevice()
    {
        // Arrange — a genuine ceremony in every respect the server can verify, a client reporting no
        // extension results, and a factor identifier that is not a canonical uuid.
        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
        StubWebAuthnChallengeStore store = new(challenge, WebAuthnCeremony.AccountRegistration);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        AttestationResult attestation = device.Register(challenge, Origin, signCount: 0, prfEnabled: null);
        Fixture fixture = Build(store);

        // Act
        ValidationException refusal = await ThrowsAsync<ValidationException>(
            () => fixture.Handler.HandleAsync(CommandFor(
                attestation,
                reportsEnabledPrf: false,
                factorId: "NOT-A-UUID")));

        // Assert
        await Assert.That(MessageOf(refusal, ResponseField))
            .Contains("cannot hold the account's keys");
        await Assert.That(refusal.Errors.Keys).DoesNotContain(FactorIdField);
        await Assert.That(fixture.Repository.Calls).IsEmpty();
    }

    /// <summary>
    /// Rung 11 runs before the write: a passkey claiming one of its own card's factor identifiers never
    /// reaches the repository.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Left to <c>PK_wrapped_account_keys</c> this arrives mid-save, and the sentence is wrong.</b> The
    /// database's conflict says an identifier is <em>already registered</em> — naming a factor nobody has
    /// registered, on a request that was merely wrong, and sending the caller to look for a request they
    /// never made. So the rule sits in the application, and what this test states is the half a status
    /// code cannot: the repository was <b>not called</b>.
    /// </para>
    /// <para>
    /// <b>Not rung 10's distinctness check.</b> That one compares the ten codes among themselves; this
    /// compares the passkey's identifier against them, and the ten here are distinct — as they are on
    /// every card a real client mints — so only the eleventh-factor rule can refuse this.
    /// </para>
    /// <para>
    /// The repeated identifier is the <b>last</b> code's: a check that looked at the first submission and
    /// trusted the other nine would pass an arrangement built on the first.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenThePasskeyClaimsACodesFactor_RefusesBeforeCallingTheRepository()
    {
        // Arrange
        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
        StubWebAuthnChallengeStore store = new(challenge, WebAuthnCeremony.AccountRegistration);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        AttestationResult attestation = device.Register(challenge, Origin, signCount: 0, prfEnabled: true);
        IReadOnlyList<RecoveryCodeSubmission> card = Card();
        Fixture fixture = Build(store);

        // Act
        ValidationException refusal = await ThrowsAsync<ValidationException>(
            () => fixture.Handler.HandleAsync(CommandFor(
                attestation,
                factorId: card[^1].FactorId,
                codes: card)));

        // Assert — the application's own sentence, keyed on the member the caller can correct.
        await Assert.That(MessageOf(refusal, FactorIdField))
            .IsEqualTo("The passkey's factor identifier must differ from every recovery code's.");

        // And nothing was handed to the port, which is the half the sentence cannot say.
        await Assert.That(fixture.Repository.Calls).IsEmpty();
        await Assert.That(fixture.Writer.Published).IsEmpty();
    }

    /// <summary>
    /// Rung 13 runs before rung 14: the derived identity is published before the save is attempted, and
    /// exactly once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The swap here is silent and fatal.</b> <c>app.current_user_id</c> reaches the database on the
    /// next connection open, and the <c>users</c> INSERT is checked against it, so an identity published
    /// after the save is an identity that statement ran without: every policed row meets <c>''::uuid</c>
    /// and the request dies with <c>22P02</c>. Nothing about the handler's return value differs between
    /// the two orders, which is why this is observed from <em>inside</em> the call —
    /// <see cref="RecordingRegistrationRepository.IdentitiesPublishedAtEachCall" /> is the only place a
    /// unit test can stand.
    /// </para>
    /// <para>
    /// <b>The value is asserted and not only its presence.</b> An identity published from the provider's
    /// subject, from a fresh uuid, or from the <c>users</c> row the handler happens to have built would
    /// all satisfy "something was published"; only
    /// <see cref="RegistrationAccountId.For" /> over the challenge the store answered gives the value the
    /// authenticator was handed at options time, and an account written under any other one answers no
    /// assertion that device will ever produce.
    /// </para>
    /// <para>
    /// <b>Once, and never again afterwards.</b> A handler that re-published after the save — tidily, to
    /// "confirm" the account — would be publishing on a path that a retry could reach twice, which is the
    /// rule the absence of a transactional wrap on this path exists to avoid needing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_PublishesTheDerivedIdentityBeforeItRegisters()
    {
        // Arrange
        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
        StubWebAuthnChallengeStore store = new(challenge, WebAuthnCeremony.AccountRegistration);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        AttestationResult attestation = device.Register(challenge, Origin, signCount: 0, prfEnabled: true);
        Fixture fixture = Build(store);
        Guid expected = RegistrationAccountId.For(challenge);

        // Act
        await fixture.Handler.HandleAsync(CommandFor(attestation));

        // Assert — the port was reached, and the identity was already published when it was.
        await Assert.That(fixture.Repository.Calls.Count).IsEqualTo(1);
        await Assert.That(fixture.Repository.IdentitiesPublishedAtEachCall[0])
            .IsEquivalentTo(new[] { expected });

        // And nothing was published after it either.
        await Assert.That(fixture.Writer.Published).IsEquivalentTo(new[] { expected });

        // The account written under that identifier is the same one, so the publication and the row
        // cannot drift apart in the direction the assertions above cannot see.
        await Assert.That(fixture.Repository.Calls[0].User.Id).IsEqualTo(expected);
    }

    /// <summary>
    /// A refusal below rung 13 names nobody and writes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mirror of the test above, and it is a separate claim.</b> That one says the identity is
    /// published before the save; this one says it is not published on a request that never gets there.
    /// Publishing early "so the connection is configured" reads like tidying and is exactly the change
    /// that would keep every refusal test in this file green: naming an account off a provider token
    /// before a passkey has proved anything is invisible until somebody looks for it.
    /// </para>
    /// <para>
    /// The fault is the card's size rather than the prf gate, deliberately, so this is not the third test
    /// in the file to be about the same rung — and the card is judged at rung 10, still below the
    /// publication.
    /// </para>
    /// <para>
    /// <b>What this does not cover, and cannot.</b> A registration refused by the repository — a subject,
    /// an address, an authenticator or a factor already spoken for — is refused <em>above</em> rung 13, so
    /// its identity is published and the save is attempted. That is correct, and the claim that such a
    /// request leaves the database exactly as it found it belongs to the conflict tests in
    /// <c>IntegrationTests.AccountRegistrationTests</c>, where a real save can be observed rolling back.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheCardIsTheWrongSize_PublishesNoIdentityAndWritesNothing()
    {
        // Arrange
        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
        StubWebAuthnChallengeStore store = new(challenge, WebAuthnCeremony.AccountRegistration);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        AttestationResult attestation = device.Register(challenge, Origin, signCount: 0, prfEnabled: true);
        Fixture fixture = Build(store);

        // Act
        ValidationException refusal = await ThrowsAsync<ValidationException>(
            () => fixture.Handler.HandleAsync(CommandFor(
                attestation,
                codes: Card(RequiredCodeCount - 1))));

        // Assert
        await Assert.That(refusal.Errors).IsNotEmpty();
        await Assert.That(fixture.Writer.Published).IsEmpty();
        await Assert.That(fixture.Repository.Calls).IsEmpty();
    }

    /// <summary>
    /// The handler and the two collaborators the assertions read, assembled once.
    /// </summary>
    private sealed record Fixture(
        RegisterAccountHandler Handler,
        RecordingRegistrationRepository Repository,
        RecordingUserContextWriter Writer);

    /// <summary>
    /// Builds the handler over the given challenge store and fakes for everything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store is the parameter because it is the only collaborator a test arranges differently — it
    /// carries the nonce and the pool, which is what two of the tests here are about.
    /// </para>
    /// <para>
    /// The user repository is empty and is read by one branch only: the disambiguating re-read the
    /// <c>EmailTaken</c> outcome needs. No test here reaches that branch, and the one that would is an
    /// integration test, because what it has to prove is which index PostgreSQL named.
    /// </para>
    /// <para>
    /// The writer is constructed first and handed to the repository as a closure, so the repository can
    /// snapshot it without either fake knowing about the other's type.
    /// </para>
    /// </remarks>
    private static Fixture Build(
        StubWebAuthnChallengeStore store,
        RegistrationOutcome outcome = RegistrationOutcome.Registered)
    {
        RecordingUserContextWriter writer = new();
        RecordingRegistrationRepository repository = new(outcome, () => writer.Published);

        return new Fixture(
            new RegisterAccountHandler(
                store,
                new StubPasskeyCeremonyPolicy(RelyingPartyId, Origin),
                repository,
                new InMemoryUserRepository(new InMemoryTransactionRepository()),
                writer,
                new FakeTimeProvider(UtcNow)),
            repository,
            writer);
    }

    /// <summary>
    /// A command that is correct in every respect but the ones a caller names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every unnamed member is faultless, and that is what keeps each test on its own subject.</b> A
    /// malformed envelope or a repeated factor introduced by the fixture would be refused before or
    /// instead of the rung a test was written for, and the test would go on passing while measuring
    /// nothing.
    /// </para>
    /// <para>
    /// <b>The extension results are asked for as a <see cref="bool" /> and never as a nullable record,
    /// and this file's first draft is the argument.</b> A <c>PasskeyClientExtensionResults?</c> parameter
    /// defaulting to the enabled value cannot express <see langword="null" /> at all — <c>null</c> is how
    /// a caller asks for the default — so the two tests that pass "no prf" silently sent an enabled
    /// result, and both went green having measured nothing about the prf gate. The parameter that can be
    /// used wrongly is the parameter that will be.
    /// </para>
    /// <para>
    /// One shape of "no prf" is enough here, because what these tests are about is <em>where</em> the gate
    /// sits rather than what it accepts. All four shapes the wire can carry — absent object, object with
    /// no <c>prf</c>, <c>prf: null</c>, <c>prf.enabled: false</c> — are driven separately in
    /// <c>IntegrationTests.AccountRegistrationTests</c>, which is the layer that can send them.
    /// </para>
    /// </remarks>
    private static RegisterAccountCommand CommandFor(
        AttestationResult attestation,
        bool reportsEnabledPrf = true,
        string? factorId = null,
        IReadOnlyList<RecoveryCodeSubmission>? codes = null) =>
        new(
            Subject,
            Email,
            attestation.ClientDataJsonBase64Url,
            attestation.AttestationObjectBase64Url,
            reportsEnabledPrf
                ? new PasskeyClientExtensionResults(new PasskeyPrfResults(Enabled: true))
                : null,
            factorId ?? Guid.CreateVersion7().ToString("D"),
            Base64UrlText.Encode(Envelope(ContentKeyPurpose)),
            Base64UrlText.Encode(Envelope(IndexKeyPurpose)),
            codes ?? Card());

    /// <summary>
    /// A card of <paramref name="count" /> whole submissions: a verifier, a factor of its own, and the
    /// envelope pair sealed under the key that code derives.
    /// </summary>
    /// <remarks>
    /// A fresh factor per submission, so no card refuses itself at rung 10 — the rule two tests here have
    /// to get past to reach the rungs they are about.
    /// </remarks>
    private static IReadOnlyList<RecoveryCodeSubmission> Card(int count = RequiredCodeCount) =>
    [
        .. Enumerable.Range(0, count).Select(_ => new RecoveryCodeSubmission(
            Base64UrlText.Encode(RandomNumberGenerator.GetBytes(VerifierLength)),
            Guid.CreateVersion7().ToString("D"),
            Base64UrlText.Encode(Envelope(ContentKeyPurpose)),
            Base64UrlText.Encode(Envelope(IndexKeyPurpose)))),
    ];

    /// <summary>
    /// A well-formed wrapped-key envelope: the one version the contract defines, the byte that says which
    /// of the two columns this is, and random bytes to the exact width.
    /// </summary>
    /// <remarks>
    /// The width and the version are read off <see cref="WrappedAccountKeys" /> rather than restated:
    /// nothing in this file is about the envelope's shape, and a copy of either bound would turn every
    /// test here red on the day it moved, for a reason none of them is about.
    /// </remarks>
    private static byte[] Envelope(byte purpose)
    {
        byte[] envelope = RandomNumberGenerator.GetBytes(WrappedAccountKeys.EnvelopeLength);
        envelope[0] = WrappedAccountKeys.EnvelopeVersion;
        envelope[1] = purpose;

        return envelope;
    }

    /// <summary>
    /// The one message a refusal carries under <paramref name="field" />, or a failure naming every field
    /// it did carry.
    /// </summary>
    /// <remarks>
    /// Throwing rather than returning a placeholder, so a refusal keyed on the wrong member fails at the
    /// line that asked for it and says which member it was keyed on instead — which on this ladder is half
    /// of what is being measured.
    /// </remarks>
    private static string MessageOf(ValidationException refusal, string field) =>
        refusal.Errors.TryGetValue(field, out string[]? messages) && messages.Length > 0
            ? messages[0]
            : throw new InvalidOperationException(
                $"The refusal carries no '{field}'. It carries: {string.Join(", ", refusal.Errors.Keys)}.");

    /// <summary>
    /// Runs <paramref name="action" /> and returns the exception it was expected to throw.
    /// </summary>
    /// <remarks>
    /// The catch names <typeparamref name="TException" /> exactly, so an exception of any other type
    /// escapes and fails the test as itself rather than being reported as "the expected exception was not
    /// thrown" — which matters more here than usual, since every test in this file distinguishes one
    /// refusal from another.
    /// </remarks>
    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
