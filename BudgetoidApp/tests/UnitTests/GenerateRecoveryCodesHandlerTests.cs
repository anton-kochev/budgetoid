using System.Security.Cryptography;
using Application.Abstractions;
using Application.Passkeys;
using Application.Passkeys.Reauthentication;
using Application.RecoveryCodes.GenerateRecoveryCodes;
using Application.Sessions.RevokeSessionsForCredential;
using Domain.Sessions;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;
using ValidationException = Domain.Common.ValidationException;

namespace UnitTests;

/// <summary>
/// Issuing an account's set of recovery codes, and re-issuing one: what the gate refuses, what the set
/// is allowed to look like, what a replacement takes with it, and what a replayed unit of work leaves
/// behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server never sees a code.</b> The browser mints one with at least 128 bits of entropy,
/// derives a verifier <c>V = HKDF(code, …)</c>, and sends only <c>V</c>; the row stores
/// <c>SHA-256(V)</c>. Story 12.1 derives the account's key-encryption key from the same code on an
/// independent HKDF branch, so a code arriving here would hand the operator the KEK and make NFR-015 —
/// "no party but a holder of one of the account's own recovery factors obtains the keys" — false.
/// </para>
/// <para>
/// The consequence these tests are shaped by: <b>the server cannot check entropy.</b> It receives
/// fixed-length opaque bytes, and a set of ten identical zero-filled verifiers is indistinguishable
/// here from a set a good generator produced. What it can pin is the verifier's exact width, the set's
/// exact size, and that the set's verifiers are distinct — and that is the whole of the validation
/// family below. Entropy is verified by a client-side test and by nothing in this codebase; a reader
/// looking for it here should not conclude it is missing everywhere.
/// </para>
/// <para>
/// The gate is a real <see cref="PasskeyReauthentication" /> over fakes rather than a stub, exactly as
/// <see cref="EraseAccountHandlerTests" /> and <see cref="RevokePasskeyHandlerTests" /> build it, and
/// for the same reason: there is no interface to stub it behind, and a stubbable gate would let a test
/// here prove that a set of codes can be minted with the proof faked out — the one thing that must
/// never be provable. The device holds a real key pair and signs the real nonce the store was handed.
/// </para>
/// <para>
/// <b>Which control covers which claim:</b>
/// <see cref="HandleAsync_WithAValidAssertion_StoresOneHashPerVerifierAndNoCode" /> is the
/// provable-fail control for all three refusals in the gate family — without it a handler that threw
/// at every call passes each of them — and it is the control for the validation family too, since a
/// handler refusing every set satisfies those the same way.
/// <see cref="HandleAsync_ForAnAccountWithNoPreviousSet_DeletesNothingAndEndsNoSession" /> is the
/// control for the regeneration pair: it is what stops "the previous set is gone" being satisfied by a
/// handler that deletes unconditionally, and what makes the reported session count a number rather
/// than a constant zero that happens to be right once.
/// </para>
/// <para>
/// <b>The re-established session is its own family, and it exists because of a defect the older half of
/// this file could not see.</b> Replacing a set sweeps the sessions that set opened, and the person doing
/// the replacing is very often signed in <em>on one of them</em> — they lost the authenticator, redeemed a
/// code, registered a replacement passkey, and are now regenerating the card. Every assertion written
/// before this family was about the <b>replaced credential</b> — that its sessions were stamped, and how
/// many — and all of them are true of a handler that hands the person ten fresh codes and throws them out
/// of the flow in the same response. The missing claim is about the <b>account's live sessions
/// afterwards</b>, which is what
/// <see cref="HandleAsync_WhenTheReplacedSetHadALiveSession_LeavesTheAccountOneLiveSession" /> states and
/// nothing else does.
/// </para>
/// <para>
/// <b>The condition is <c>sessionsEnded &gt; 0</c>, and the three negatives are what pin it there.</b>
/// <see cref="HandleAsync_ForAnAccountWithNoPreviousSet_EstablishesNoSession" /> kills "always establish";
/// <see cref="HandleAsync_WhenTheReplacedSetHadNoSessions_EstablishesNothing" /> kills "establish whenever
/// there was a previous set", which is the same rule everywhere except on the account that never signed in
/// with its codes; and
/// <see cref="HandleAsync_WhenTheReplacedSetsSessionsWereAlreadyRevoked_EstablishesNothing" /> kills
/// "establish whenever the sweep matched a row", which is deliberately not the number the repository
/// reports. The rule is about the <em>set</em> and not about the caller, because nothing on this request
/// presents a session: the server cannot know whose session it swept, so it asks whether the replaced set
/// was carrying any at all.
/// </para>
/// <para>
/// <b>That reading is generous in exactly one direction, and no test here demands otherwise.</b> It has no
/// false negatives — a live session over the replaced set is always swept — and two false positives: a
/// live session on another device, and a session unrevoked but past its expiry, since the sweep filters on
/// <c>RevokedAtUtc is null</c> and not on expiry. Each costs one inert row. A test demanding the narrower
/// "live at <see cref="UtcNow" />" reading would be pinning an asymmetry this design deliberately declines,
/// and would have to be deleted by whoever noticed.
/// </para>
/// </remarks>
public sealed class GenerateRecoveryCodesHandlerTests
{
    /// <summary>
    /// How many codes an issued set holds.
    /// </summary>
    /// <remarks>
    /// <b>Product policy, and it lives on the Application handler</b> — the placement
    /// <c>CompleteAssertionHandler.SessionLifetime</c> makes the argument for. It is not a domain
    /// invariant: a set of nine codes is not a malformed set, it is a smaller quantity of a thing
    /// somebody chose, and ADR 0002 keeps policy above the invariants because the bottom is the most
    /// expensive layer to change. It is not a database constraint either — a <c>CHECK</c> counting
    /// sibling rows cannot be written without a trigger, and pushing procedural logic down to satisfy
    /// "lowest layer" is the boundary that ADR draws.
    /// <para>
    /// Restated here rather than read off the handler, because the number is the pin: a test taking its
    /// expectation from the type under test agrees with whatever that type later decides.
    /// </para>
    /// </remarks>
    private const int RequiredCodeCount = 10;

    /// <summary>
    /// The exact width of a verifier, decoded. See <see cref="RecoveryCodeHashTests" />, which pins the
    /// same number against the entity; this file pins what the handler does with a set of them.
    /// </summary>
    private const int VerifierLength = 32;

    private const string RelyingPartyId = "localhost";
    private const string Origin = "https://localhost:4200";
    private const int ChallengeBytes = 32;

    /// <summary>
    /// How many times the executor runs the unit of work in
    /// <see cref="HandleAsync_WhenTheUnitOfWorkIsReplayed_GeneratesExactlyOneSet" />. Two is the
    /// smallest number that is a replay at all, and nothing that test measures gets sharper with more.
    /// </summary>
    private const int ReplayedAttempts = 2;

    /// <summary>Fixed instant for every seeded row, so nothing here depends on the wall clock.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 11, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// How long the session a replacement re-establishes lasts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same interval a passkey sign-in and a redemption get, and equality is the rule rather than a
    /// coincidence.</b> The caller has just proved possession of a passkey through the gate in front of
    /// this route, which is stronger than whatever opened the session the sweep took — so a shorter
    /// lifetime here would quietly tell somebody who regenerated their card that the way back in they were
    /// left with is worth less than the one they were signed in on.
    /// </para>
    /// <para>
    /// Restated here rather than read off the handler, for the reason <see cref="RequiredCodeCount" />
    /// gives: a test taking its expectation from the type under test agrees with whatever that type later
    /// decides.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    /// <summary>When the account's previous set, where a test seeds one, was issued.</summary>
    private static readonly DateTime IssuedEarlier = UtcNow.AddDays(-30);

    /// <summary>
    /// A nonce the store never issued mints nothing, however well-formed the set of verifiers is.
    /// </summary>
    /// <remarks>
    /// <b>Written first, and it is the test the whole file is arranged around.</b> "The feature works
    /// with the gate faked out" must never be provable, which is why
    /// <see cref="PasskeyReauthentication" /> is a concrete class with no interface — and why the
    /// refusal is measured as rows that do not exist rather than as an exception type. A set of
    /// recovery codes is a full-session credential: whoever holds one can sign in and reach the
    /// account's content without the authenticator, so minting one on an unproven request is handing
    /// out an account to whoever is holding a stolen bearer token.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheChallengeIsNotLive_GeneratesNothing()
    {
        // Arrange — the store holds bytes the device never signed, which is how it answers null.
        Fixture fixture = Fixture.Build(challengeIsLive: false);

        // Act
        await ThrowsAsync<PasskeyVerificationException>(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(fixture.RecoveryCodes.Hashes.Count).IsEqualTo(0);
        await Assert.That(fixture.RecoveryCodes.Credentials.Count).IsEqualTo(0);
    }

    /// <summary>
    /// A nonce drawn from either of the other two pools mints nothing, even though it is live, unspent
    /// and correctly signed.
    /// </summary>
    /// <remarks>
    /// The stub is built for <see cref="WebAuthnCeremony.Authentication" />, which is the pool an
    /// <b>anonymous</b> endpoint mints. A gate that checked <c>ConsumeAsync</c> for a non-null answer
    /// rather than for <c>Reauthentication</c> passes every other test in this file and fails this one
    /// — and a caller who could spend an anonymously minted nonce here would be minting a full-session
    /// credential without ever having proved presence.
    /// </remarks>
    [Test]
    public async Task HandleAsync_OnAChallengeIssuedForAnotherCeremony_GeneratesNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Build(ceremony: WebAuthnCeremony.Authentication);

        // Act
        await ThrowsAsync<PasskeyVerificationException>(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(fixture.RecoveryCodes.Hashes.Count).IsEqualTo(0);
        await Assert.That(fixture.RecoveryCodes.Credentials.Count).IsEqualTo(0);
    }

    /// <summary>
    /// A genuine assertion from an authenticator registered to somebody else mints nothing.
    /// </summary>
    /// <remarks>
    /// The test the owner-scoped finder exists for. Reuse the unscoped discovery lookup in the gate and
    /// the signature verifies, the ceremony completes, and this request's own account is handed a fresh
    /// set of codes on the strength of a stranger's device — and, because generation <em>replaces</em>,
    /// the account's real set is destroyed in the same breath. The assertion carries no user handle, so
    /// nothing but the scoped lookup can refuse it.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAPasskeyBelongingToAnotherAccount_GeneratesNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Build(passkeyBelongsToAnotherAccount: true);

        // Act
        await ThrowsAsync<PasskeyVerificationException>(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(fixture.RecoveryCodes.Hashes.Count).IsEqualTo(0);
        await Assert.That(fixture.RecoveryCodes.Credentials.Count).IsEqualTo(0);
    }

    /// <summary>
    /// One row per verifier, each holding the hash — and no verifier is stored verbatim anywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second assertion is not the first restated. A handler that filed the verifier itself as the
    /// "hash" would fail the first, but so would one that hashed the wrong member or hashed twice, and
    /// only the second says which of those is the one that would hand a database reader ten live
    /// recovery codes. It is stated as "no stored value equals any verifier in the command", not "the
    /// first stored value differs from the first verifier", because the ordering of the set is not
    /// something this test wants to pin.
    /// </para>
    /// <para>
    /// The set is filed against one credential of type <see cref="CredentialType.RecoveryCodes" />
    /// owned by the request's own account. No account is named on the command — the identity is
    /// <c>IUserContext.UserId</c>, the rule <c>EraseAccountCommand</c> and <c>RevokePasskeyCommand</c>
    /// both state — so a handler reading an account from anywhere else is red here.
    /// </para>
    /// <para>
    /// The instant comes from the handler's own <see cref="TimeProvider" />: one issuing decision reads
    /// as one instant across all ten rows, and a handler reaching for the wall clock would spread it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAValidAssertion_StoresOneHashPerVerifierAndNoCode()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — one row per verifier, and every row is the SHA-256 of one of them. Compared as
        // ordered hex so the claim is about the set of values rather than about their order.
        string[] storedHashes =
            [.. fixture.RecoveryCodes.Hashes.Select(hash => Convert.ToHexString(hash.VerifierHash.ToArray())).Order()];
        string[] expectedHashes =
            [.. fixture.Verifiers.Select(verifier => Convert.ToHexString(SHA256.HashData(verifier))).Order()];
        await Assert.That(storedHashes).IsEquivalentTo(expectedHashes);

        // And nothing in the command was stored verbatim.
        string[] verifiers = [.. fixture.Verifiers.Select(verifier => Convert.ToHexString(verifier))];
        await Assert.That(storedHashes.Any(stored => verifiers.Contains(stored))).IsFalse();

        // One credential for the whole set, of the account that asked, at the handler's clock.
        await Assert.That(fixture.RecoveryCodes.Credentials.Count).IsEqualTo(1);
        Credential set = fixture.RecoveryCodes.Credentials[0];
        await Assert.That(set.UserId).IsEqualTo(fixture.UserId);
        await Assert.That(set.Type).IsEqualTo(CredentialType.RecoveryCodes);
        await Assert.That(fixture.RecoveryCodes.Hashes.All(hash => hash.CredentialId == set.Id)).IsTrue();
        await Assert.That(fixture.RecoveryCodes.Hashes.All(hash => hash.CreatedAtUtc == UtcNow)).IsTrue();
    }

    /// <summary>
    /// A first set replaces nothing and ends nobody's session.
    /// </summary>
    /// <remarks>
    /// The provable-fail control for the regeneration pair below. An unconditional
    /// <c>DeleteSetAsync</c> against an account with no previous set leaves exactly the same rows a
    /// conditional one does, so no row count can tell them apart — the call itself has to be counted.
    /// The same is true of the sweep: a handler that revoked the sessions of a credential that does not
    /// exist would report the same zero this one does, and would do it by ending sessions of whatever
    /// credential id it happened to be holding.
    /// </remarks>
    [Test]
    public async Task HandleAsync_ForAnAccountWithNoPreviousSet_DeletesNothingAndEndsNoSession()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        RecoveryCodesGeneration generation = await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        await Assert.That(fixture.RecoveryCodes.DeleteSetCallCount).IsEqualTo(0);
        await Assert.That(fixture.Sessions.RevokeForCredentialCallCount).IsEqualTo(0);
        await Assert.That(generation.SessionsEnded).IsEqualTo(0);
    }

    /// <summary>
    /// A set of any size other than the one the product issues is refused, and nothing is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both directions and the empty set, because the three fail for different reasons. Too few is a
    /// person left with fewer ways back into their account than the screen told them they had; too many
    /// is a client the server no longer agrees with about what a set is; zero is the argument a handler
    /// is most likely to treat as "nothing to do" and answer 200 to, having replaced a live set with
    /// nothing.
    /// </para>
    /// <para>
    /// Refused with a real sentence, because past the gate the caller has proved possession of an
    /// authenticator registered to this account and there is nobody left to enumerate about — the same
    /// argument <c>CompleteRegistrationHandler</c> makes for its own sentences, and the reason this
    /// refusal is a 400 with a field on it rather than one more byte-identical 401.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(0)]
    [Arguments(RequiredCodeCount - 1)]
    [Arguments(RequiredCodeCount + 1)]
    public async Task HandleAsync_WithTheWrongNumberOfVerifiers_IsRefused(int count)
    {
        // Arrange
        Fixture fixture = Fixture.Build(Verifiers(count));

        // Act
        ValidationException exception =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(GenerateRecoveryCodesCommand.Verifiers))).IsTrue();
        await Assert.That(fixture.RecoveryCodes.Hashes.Count).IsEqualTo(0);
    }

    /// <summary>
    /// A verifier that does not decode to exactly the specified width is refused, and nothing is
    /// written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One byte either side, because the bound is the assertion. Short means a shorter secret than the
    /// design claims, and it would hash to a perfectly well-formed 32-byte row that nothing downstream
    /// could tell from a real one — the column's length check watches the <em>hash</em>, which is 32
    /// bytes whatever went into it, so the database cannot catch this and the application is the only
    /// place it stops. Long means the client and the server disagree about what a verifier is, and
    /// since the same code also derives the account's key-encryption key, a width quietly accepted here
    /// surfaces as a key that will not unwrap.
    /// </para>
    /// <para>
    /// One malformed verifier out of ten, not ten, so the refusal is attributable: a check written over
    /// the first element only, or over the set's total length, is red on this arrangement and green on
    /// a uniformly malformed one.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(VerifierLength - 1)]
    [Arguments(VerifierLength + 1)]
    public async Task HandleAsync_WithAVerifierOfTheWrongWidth_IsRefused(int width)
    {
        // Arrange — nine well-formed verifiers and one that is not.
        byte[][] verifiers = Verifiers(RequiredCodeCount);
        verifiers[^1] = RandomNumberGenerator.GetBytes(width);
        Fixture fixture = Fixture.Build(verifiers);

        // Act
        ValidationException exception =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(GenerateRecoveryCodesCommand.Verifiers))).IsTrue();
        await Assert.That(fixture.RecoveryCodes.Hashes.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Ten verifiers of which two are the same is refused, and nothing is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule the count check cannot express: this set <em>is</em> ten members long and is nine codes
    /// deep. Left to the database it becomes a primary-key collision on <c>verifier_hash</c> — a 500
    /// for a caller whose request was merely wrong, and, worse, one arriving after the previous set has
    /// already been deleted inside the same transaction. Left to nothing at all it becomes a person
    /// holding a card that says ten and an account that will accept nine.
    /// </para>
    /// <para>
    /// It is also the one member of this family that says something about the client's generator, which
    /// is the closest this layer gets to the entropy it cannot measure: a client repeating a verifier
    /// inside one set is a client whose randomness is not what it claims, and that is worth a refusal
    /// even though a duplicate <em>across</em> sets is invisible here by design.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithADuplicateVerifierInTheSet_IsRefused()
    {
        // Arrange
        byte[][] verifiers = Verifiers(RequiredCodeCount);
        verifiers[^1] = verifiers[0];
        Fixture fixture = Fixture.Build(verifiers);

        // Act
        ValidationException exception =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(nameof(GenerateRecoveryCodesCommand.Verifiers))).IsTrue();
        await Assert.That(fixture.RecoveryCodes.Hashes.Count).IsEqualTo(0);
    }

    /// <summary>
    /// The three refusals above say three different things.
    /// </summary>
    /// <remarks>
    /// The "real sentence" half of the family, which no single refusal test can state. Each of the
    /// three asserts that a field error is present, and one shared message — "The recovery codes are
    /// invalid." — satisfies all three while telling a caller who has already proved presence nothing
    /// they can act on. Distinctness is what makes the sentences carry their own content; the register
    /// they are written in is the one <c>CompleteRegistrationHandler.Refused</c> uses.
    /// </remarks>
    [Test]
    public async Task HandleAsync_RefusesEachMalformedSetWithASentenceOfItsOwn()
    {
        // Arrange
        byte[][] wrongWidth = Verifiers(RequiredCodeCount);
        wrongWidth[^1] = RandomNumberGenerator.GetBytes(VerifierLength - 1);
        byte[][] duplicated = Verifiers(RequiredCodeCount);
        duplicated[^1] = duplicated[0];

        // Act
        string[] sentences =
        [
            await RefusalSentence(Verifiers(RequiredCodeCount - 1)),
            await RefusalSentence(wrongWidth),
            await RefusalSentence(duplicated),
        ];

        // Assert
        await Assert.That(sentences.All(sentence => !string.IsNullOrWhiteSpace(sentence))).IsTrue();
        await Assert.That(sentences.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(sentences.Length);
    }

    /// <summary>
    /// A malformed set presented without a fresh assertion is refused <b>on the assertion</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ordering assertion, and the reason the validation family cannot be hoisted above the gate
    /// however much tidier that would read. Validating first would answer an <em>unproven</em> caller
    /// with the required set size and the required verifier width — the two facts a client needs to
    /// present a set at all — and would do it for a caller holding nothing but a bearer token. Past the
    /// gate those same sentences cost nothing, because the caller has already proved possession of an
    /// authenticator registered to this account.
    /// </para>
    /// <para>
    /// Stated as "which refusal came back", not as a call order: a handler that got the order right by
    /// accident is not what this pins, and the exception type is exactly the observable a caller has.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAMalformedSetAndNoFreshAssertion_RefusesOnTheAssertion()
    {
        // Arrange — both things are wrong at once, and only one of them may be reported.
        Fixture fixture = Fixture.Build(Verifiers(RequiredCodeCount - 1), challengeIsLive: false);

        // Act, Assert — PasskeyVerificationException, which the endpoint answers with the same
        // byte-identical 401 every other unproven request gets. A ValidationException here is the leak.
        await ThrowsAsync<PasskeyVerificationException>(() => fixture.Handler.HandleAsync(fixture.Command));
        await Assert.That(fixture.RecoveryCodes.Hashes.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Re-issuing removes every code of the set it replaces (AC 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not "adds ten more". An account holds at most one set —
    /// <c>IX_credentials_user_id_recovery_codes</c> owns that rule, because two sets would be two
    /// remaining-counts with nothing saying which one binds — so a handler that only inserted would be
    /// refused by the index in production and would, if it ever were not, leave the codes on a card the
    /// person has already thrown away still working.
    /// </para>
    /// <para>
    /// The previous codes are asserted gone by value rather than by count. Twenty rows and ten rows are
    /// both distinguishable from ten <em>correct</em> rows only if the values are compared: a handler
    /// that deleted the new set instead of the old one leaves a count of ten and an account whose codes
    /// are all stale.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAccountAlreadyHoldsASet_RemovesEveryPreviousCode()
    {
        // Arrange
        Fixture fixture = Fixture.Build(seedPreviousSet: true);
        string[] previousHashes =
            [.. fixture.RecoveryCodes.Hashes.Select(hash => Convert.ToHexString(hash.VerifierHash.ToArray()))];
        await Assert.That(previousHashes.Length).IsEqualTo(RequiredCodeCount);

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — the account holds one set, it is the new one, and none of the old codes survived.
        string[] storedHashes =
            [.. fixture.RecoveryCodes.Hashes.Select(hash => Convert.ToHexString(hash.VerifierHash.ToArray())).Order()];
        string[] expectedHashes =
            [.. fixture.Verifiers.Select(verifier => Convert.ToHexString(SHA256.HashData(verifier))).Order()];
        await Assert.That(storedHashes).IsEquivalentTo(expectedHashes);
        await Assert.That(storedHashes.Any(stored => previousHashes.Contains(stored))).IsFalse();

        // And the set's own credential went with them, which is what the cascade hangs off.
        await Assert.That(fixture.RecoveryCodes.Credentials.Count).IsEqualTo(1);
        await Assert.That(fixture.RecoveryCodes.Credentials[0].Id).IsNotEqualTo(fixture.PreviousSet!.Id);
    }

    /// <summary>
    /// Re-issuing ends the sessions the replaced set established, <b>before</b> that set is deleted,
    /// and reports how many.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two mutations produce the same wrong number, which is why this test asserts two different
    /// things.</b> Delete the explicit revocation and the count is 0. Swap the revocation and the
    /// delete and the database's own <c>ON DELETE CASCADE</c> from <c>credentials</c> has already taken
    /// the session rows, so the sweep matches nothing and reports <b>0 as well</b> — the schema
    /// afterwards is byte-identical either way, so no row count can tell the two apart from each other
    /// or from a correct handler. <c>RevokePasskeyHandler</c> states this double-control for its own
    /// path; this is the same shape.
    /// </para>
    /// <para>
    /// So the count is asserted <em>and</em> the ordering is observed at the instant the delete is
    /// entered. <see cref="InMemoryRecoveryCodeRepository.ObservationAtDelete" /> answers
    /// <see langword="null" /> when the delete never ran at all, so the assertion cannot be satisfied
    /// by a handler that refused before reaching it — null and false fail for different reasons and
    /// both are worth telling apart.
    /// </para>
    /// <para>
    /// The two <see cref="Session" /> objects are held by reference rather than read back out of the
    /// repository, for the reason <see cref="RevokePasskeyHandlerTests" /> gives: the cascade empties
    /// the fake's list, and an "all of them are stamped" predicate over zero rows is vacuously true —
    /// the one way this test could go green on a handler that revoked nothing.
    /// </para>
    /// <para>
    /// The revocation goes through <see cref="RevokeSessionsForCredentialHandler" /> and never straight
    /// to <c>ISessionRepository</c>, because the handler is where the clock is read: one decision to
    /// end access is stamped as one instant. That is what the instant assertion at the end pins, and it
    /// is why the fake's clock is the handler's <see cref="FakeTimeProvider" /> rather than the wall.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheAccountAlreadyHoldsASet_EndsTheSessionsItEstablishedAndReportsHowMany()
    {
        // Arrange — two live sessions opened by the set being replaced, held by reference.
        Fixture fixture = Fixture.Build(seedPreviousSet: true);
        Session first = Session.Establish(fixture.PreviousSet!, IssuedEarlier, UtcNow.AddHours(1));
        Session second = Session.Establish(fixture.PreviousSet!, IssuedEarlier, UtcNow.AddHours(1));
        await fixture.Sessions.AddAsync(first);
        await fixture.Sessions.AddAsync(second);

        // The question the fake asks the moment the delete is entered.
        fixture.RecoveryCodes.ObserveAtDelete = _ =>
            first.RevokedAtUtc is not null && second.RevokedAtUtc is not null;

        // Act
        RecoveryCodesGeneration generation = await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — non-null first, because null says the delete never ran at all and false says the
        // credential was removed while the sessions it opened still looked live.
        await Assert.That(fixture.RecoveryCodes.ObservationAtDelete).IsNotNull();
        await Assert.That(fixture.RecoveryCodes.ObservationAtDelete).IsTrue();

        // The number in the response is the number the sweep ended, and the sweep really stamped them.
        await Assert.That(generation.SessionsEnded).IsEqualTo(2);
        await Assert.That(first.RevokedAtUtc).IsEqualTo(UtcNow);
        await Assert.That(second.RevokedAtUtc).IsEqualTo(UtcNow);
        await Assert.That(fixture.Sessions.LastRevokedAtUtc).IsEqualTo(UtcNow);
    }

    /// <summary>
    /// Replacing a set that was carrying a live session leaves the account holding <b>one</b> live
    /// session, opened over the <b>new</b> set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The test this whole family exists for, and the one the older half of this file could not have
    /// been.</b> Every assertion above about a replacement is about the <em>replaced credential</em> —
    /// <c>SessionsEnded</c> counts what the sweep ended, and the ordering test observes that its sessions
    /// were stamped before the delete. Both are true of a handler that sweeps the caller's own session and
    /// leaves them with none, which is precisely what happens to the person this route is for: they lost
    /// the authenticator, redeemed a code, registered a replacement passkey, and are regenerating the card
    /// while signed in on the session that redemption opened. Ten fresh codes and an immediate sign-out is
    /// the worst possible moment to be thrown out of the flow.
    /// </para>
    /// <para>
    /// <b>Stated over the account's live sessions, never over the sweep's count or the replaced
    /// credential.</b> That is the whole difference between this test and its neighbours: it reads what is
    /// left rather than what was ended, so no rearrangement of the sweep can satisfy it. The credential the
    /// surviving session hangs off is asserted to be the new set's, because a session left pointing at the
    /// replaced credential is a session the database's own cascade already took — the fake models that
    /// cascade, so such a handler is red here rather than plausibly green.
    /// </para>
    /// <para>
    /// <b>Nothing here says the surviving session is the caller's.</b> It cannot: the request presents no
    /// session, so the server does not know which one it swept, and the rule is therefore about the set
    /// rather than about the caller. See the class remarks for the two false positives that reading
    /// accepts and why each is cheap.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheReplacedSetHadALiveSession_LeavesTheAccountOneLiveSession()
    {
        // Arrange — an account holding a set, and one live session that set opened.
        Fixture fixture = Fixture.Build(seedPreviousSet: true);
        await fixture.Sessions.AddAsync(Session.Establish(fixture.PreviousSet!, IssuedEarlier, UtcNow.AddHours(1)));

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — one set, and it is the replacement.
        await Assert.That(fixture.RecoveryCodes.Credentials.Count).IsEqualTo(1);
        Credential set = fixture.RecoveryCodes.Credentials[0];
        await Assert.That(set.Id).IsNotEqualTo(fixture.PreviousSet!.Id);

        // And the account is still signed in — on the new set, and on nothing else.
        Session[] live = [.. fixture.Sessions.Sessions.Where(session => session.RevokedAtUtc is null)];
        await Assert.That(live.Length).IsEqualTo(1);
        await Assert.That(live[0].CredentialId).IsEqualTo(set.Id);
        await Assert.That(live[0].RevokedAtUtc).IsNull();
    }

    /// <summary>
    /// Replacing a set that was carrying a live session still <b>reports the sweep</b>, and the sweep
    /// really ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The negative control for the test above, and it is aimed at one specific wrong fix.</b> "The
    /// account is left with a live session" is satisfied perfectly by a handler that stops sweeping
    /// altogether — the replaced set's sessions would then survive the revocation and be taken by the
    /// delete's cascade exactly as they are now, and the schema afterwards would be byte-identical.
    /// <c>SessionsEnded</c> is the only observable that says otherwise, which is why it is a response
    /// member at all, and why it is asserted here beside the session that is stamped by reference.
    /// </para>
    /// <para>
    /// <b>One session rather than the two
    /// <see cref="HandleAsync_WhenTheAccountAlreadyHoldsASet_EndsTheSessionsItEstablishedAndReportsHowMany" />
    /// drives, and it is the same arrangement as the test above rather than a second one.</b> The claim
    /// being controlled is about that arrangement: a reader deleting the sweep to make the account keep its
    /// session has to see this go red on the very shape they were editing. The count from both sides, and
    /// the ordering against the delete, stay where they are.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheReplacedSetHadALiveSession_StillReportsTheSweep()
    {
        // Arrange — held by reference, because the delete's cascade empties the fake's list and an
        // "it was stamped" predicate over zero rows is vacuously true.
        Fixture fixture = Fixture.Build(seedPreviousSet: true);
        Session swept = Session.Establish(fixture.PreviousSet!, IssuedEarlier, UtcNow.AddHours(1));
        await fixture.Sessions.AddAsync(swept);

        // Act
        RecoveryCodesGeneration generation = await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — the number in the response, and the row it is a number about.
        await Assert.That(generation.SessionsEnded).IsEqualTo(1);
        await Assert.That(fixture.Sessions.RevokeForCredentialCallCount).IsEqualTo(1);
        await Assert.That(swept.RevokedAtUtc).IsEqualTo(UtcNow);
    }

    /// <summary>
    /// The re-established session is a full one, expiring <see cref="SessionLifetime" /> after the
    /// handler's own instant — and it is stamped with that same instant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Full, and it is derived rather than chosen.</b> <see cref="Session.Establish" /> takes the
    /// <see cref="Credential" /> and reads the kind off its type, which is what makes "a sign-in reaching
    /// more of the account than its credential may" unrepresentable — so this assertion is really that the
    /// new session was opened over the <em>new set's credential</em> and not fabricated from a kind
    /// somebody named. A recovery-code set reaches the account's content because it is the secret the
    /// content keys are wrapped under.
    /// </para>
    /// <para>
    /// <b>One clock and one instant.</b> The revocation, the new credential, the ten hash rows and this
    /// session are all stamped from the <see cref="DateTime" /> the handler read before the transaction
    /// opened, so a replayed attempt does not spread one issuing decision across several instants. A
    /// handler establishing the session outside the transactional delegate — or reading the clock a second
    /// time for it — is red on the created-at assertion rather than on the expiry, which is why both are
    /// stated.
    /// </para>
    /// <para>
    /// The response's expiry and the stored row's are asserted against the same expression, because the
    /// member the client reads and the row the database holds disagreeing is exactly the defect worth
    /// catching: a client that renders one expiry while the server enforces another tells a person they
    /// have longer than they do.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheReplacedSetHadALiveSession_EstablishesAFullSessionAtTheHandlersInstant()
    {
        // Arrange
        Fixture fixture = Fixture.Build(seedPreviousSet: true);
        await fixture.Sessions.AddAsync(Session.Establish(fixture.PreviousSet!, IssuedEarlier, UtcNow.AddHours(1)));

        // Act
        RecoveryCodesGeneration generation = await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — what the caller is told.
        await Assert.That(generation.Session).IsNotNull();
        await Assert.That(generation.Session!.Kind).IsEqualTo(SessionKind.Full);
        await Assert.That(generation.Session.ExpiresAtUtc).IsEqualTo(UtcNow + SessionLifetime);

        // And the row that was written, which is what the caller was told about. Filtered and counted
        // rather than taken with Single, so a handler that wrote none fails as an assertion about how many
        // sessions the account holds instead of as a LINQ exception that names nothing.
        Session[] live = [.. fixture.Sessions.Sessions.Where(session => session.RevokedAtUtc is null)];
        await Assert.That(live.Length).IsEqualTo(1);

        Session established = live[0];
        await Assert.That(established.Kind).IsEqualTo(SessionKind.Full);
        await Assert.That(established.CredentialType).IsEqualTo(CredentialType.RecoveryCodes);
        await Assert.That(established.CreatedAtUtc).IsEqualTo(UtcNow);
        await Assert.That(established.ExpiresAtUtc).IsEqualTo(UtcNow + SessionLifetime);
        await Assert.That(established.UserId).IsEqualTo(fixture.UserId);
    }

    /// <summary>
    /// A first issue establishes nothing.
    /// </summary>
    /// <remarks>
    /// <b>The test that kills "always establish".</b> Without it the whole family is satisfied by a handler
    /// that opens a session on every generation — which would hand a full session to somebody who has
    /// merely written down a card for the first time, on an account whose codes have never signed anybody
    /// in and whose replaced set does not exist. The response member is asserted
    /// <see langword="null" /> <em>and</em> the repository is asserted empty, because a handler could
    /// report nothing while writing a row, and a client that believes what it is told is not evidence about
    /// what was stored.
    /// </remarks>
    [Test]
    public async Task HandleAsync_ForAnAccountWithNoPreviousSet_EstablishesNoSession()
    {
        // Arrange
        Fixture fixture = Fixture.Build();

        // Act
        RecoveryCodesGeneration generation = await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        await Assert.That(generation.Session).IsNull();
        await Assert.That(fixture.Sessions.Sessions.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Replacing a set that had opened no session establishes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what pins the condition to the count rather than to the existence of a previous set.</b>
    /// "Establish whenever there was a set to replace" agrees with the correct rule on every other
    /// arrangement in this file, and differs only here: an account that generated a card, never redeemed a
    /// code, and is regenerating from a device signed in with its passkey. That person's session was opened
    /// by their passkey, the sweep never touches it, and a second one opened over the codes is a session
    /// nobody asked for on a credential they have not used.
    /// </para>
    /// <para>
    /// <c>SessionsEnded</c> is asserted at zero beside it, so a handler that established nothing because
    /// its sweep had silently stopped running cannot pass this while failing
    /// <see cref="HandleAsync_WhenTheReplacedSetHadALiveSession_StillReportsTheSweep" /> alone.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheReplacedSetHadNoSessions_EstablishesNothing()
    {
        // Arrange — a previous set, and deliberately not one session on it.
        Fixture fixture = Fixture.Build(seedPreviousSet: true);

        // Act
        RecoveryCodesGeneration generation = await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        await Assert.That(generation.SessionsEnded).IsEqualTo(0);
        await Assert.That(generation.Session).IsNull();
        await Assert.That(fixture.Sessions.Sessions.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Replacing a set whose sessions were <b>already revoked</b> establishes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The condition is what the sweep <em>ended</em>, not what it matched, and this is the only test
    /// that can tell those apart.</b> <see cref="ISessionRepository.RevokeForCredentialAsync" /> counts the
    /// sessions that were still live — a re-run, or a second report of the same compromise, matches the
    /// same rows and reports zero — and both fake and repository narrow on <c>revoked_at_utc is null</c>
    /// for that reason. A handler keyed on "the sweep touched a row" would open a session for somebody
    /// whose access was deliberately ended earlier, by a revocation or by a previous regeneration, and
    /// would hand it to whoever is holding the bearer token now.
    /// </para>
    /// <para>
    /// The session is revoked at <see cref="IssuedEarlier" /> rather than at <see cref="UtcNow" />, so the
    /// instant it ended is provably not this request's — a row stamped with the handler's own clock would
    /// be indistinguishable from one this very sweep ended.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheReplacedSetsSessionsWereAlreadyRevoked_EstablishesNothing()
    {
        // Arrange — a session this set opened and something else already ended.
        Fixture fixture = Fixture.Build(seedPreviousSet: true);
        Session ended = Session.Establish(fixture.PreviousSet!, IssuedEarlier, UtcNow.AddHours(1));
        ended.Revoke(IssuedEarlier);
        await fixture.Sessions.AddAsync(ended);

        // Act
        RecoveryCodesGeneration generation = await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — the sweep matched the row and ended nothing, so nothing is re-established.
        await Assert.That(generation.SessionsEnded).IsEqualTo(0);
        await Assert.That(generation.Session).IsNull();
        await Assert.That(fixture.Sessions.Sessions.Any(session => session.RevokedAtUtc is null)).IsFalse();

        // And the instant access actually ended was not rewritten by the sweep that ran here.
        await Assert.That(ended.RevokedAtUtc).IsEqualTo(IssuedEarlier);
    }

    /// <summary>
    /// A refused request writes no session, whichever of the two gates refused it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The insert lives behind the re-authentication gate and behind the validation, and the two
    /// failures are worth driving separately because they stop the handler at different lines.</b> An
    /// unproven request never enters the transactional delegate at all; a malformed set is refused after
    /// the gate and still before it. A handler that hoisted the establish above either — the natural shape
    /// if somebody decides the session should be opened "as soon as we know there is one to replace" —
    /// would hand a full session to a caller holding nothing but a bearer token, on an account whose codes
    /// it failed to replace.
    /// </para>
    /// <para>
    /// <b>Each arrangement seeds a live session over the previous set</b>, so the claim is that the
    /// account's sessions are exactly as they were rather than that a table is empty: a refusal that swept
    /// on its way to saying no would sign the person out and satisfy a bare "no new row" check.
    /// </para>
    /// <para>
    /// <b>Driven in one test rather than parameterized</b>, because the two refusals are two exception
    /// types and <see cref="ThrowsAsync{TException}" /> names its type exactly on purpose — a parameter
    /// carrying "some exception" would let either arrangement start failing for the other's reason without
    /// anything going red. The shape is
    /// <see cref="HandleAsync_RefusesEachMalformedSetWithASentenceOfItsOwn" />'s.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheRequestIsRefused_WritesNoSessionAndSweepsNothing()
    {
        // Arrange — two accounts, each holding a set with one live session on it, and two requests that
        // are refused at different lines of the handler.
        Fixture unproven = Fixture.Build(seedPreviousSet: true, challengeIsLive: false);
        Session unprovensSession = Session.Establish(unproven.PreviousSet!, IssuedEarlier, UtcNow.AddHours(1));
        await unproven.Sessions.AddAsync(unprovensSession);

        Fixture malformed = Fixture.Build(Verifiers(RequiredCodeCount - 1), seedPreviousSet: true);
        Session malformedsSession = Session.Establish(malformed.PreviousSet!, IssuedEarlier, UtcNow.AddHours(1));
        await malformed.Sessions.AddAsync(malformedsSession);

        // Act
        await ThrowsAsync<PasskeyVerificationException>(() => unproven.Handler.HandleAsync(unproven.Command));
        await ThrowsAsync<ValidationException>(() => malformed.Handler.HandleAsync(malformed.Command));

        // Assert — each account's sessions are the ones it had, unswept and unjoined by a new one.
        foreach ((Fixture fixture, Session seeded) in new[]
                 {
                     (unproven, unprovensSession),
                     (malformed, malformedsSession),
                 })
        {
            await Assert.That(fixture.Sessions.Sessions.Count).IsEqualTo(1);
            await Assert.That(fixture.Sessions.Sessions[0].Id).IsEqualTo(seeded.Id);
            await Assert.That(seeded.RevokedAtUtc).IsNull();
            await Assert.That(fixture.Sessions.RevokeForCredentialCallCount).IsEqualTo(0);
        }
    }

    /// <summary>
    /// A replayed unit of work leaves exactly one set, and spends the nonce once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gate sits outside the delegate, and the delegate is replayed.</b>
    /// <c>ITransactionalExecutor</c> runs under a retrying execution strategy, so a transient failure
    /// runs the whole body again: a gate inside it would consume a second time, find the nonce already
    /// spent, and refuse a <em>valid</em> request with the same 401 an attacker gets, because the
    /// database blinked. The other half of the argument cannot be seen from here and belongs on the
    /// handler — <c>ConsumeAsync</c> commits on its own save, so inside this transaction a rolled-back
    /// attempt would <em>restore</em> the spent nonce and make the assertion replayable.
    /// <c>EraseAccountHandlerTests.HandleAsync_WhenTheUnitOfWorkIsReplayed_StillErasesTheAccount</c> is
    /// the model.
    /// </para>
    /// <para>
    /// <b>The set count is the second half, and it is what the discard buys.</b> Rows queued for insert
    /// by an abandoned attempt are the change tracker's, not the database's — a <c>ROLLBACK</c> never
    /// saw them — so a delegate that does not discard queues a second credential and ten more hashes,
    /// and the surviving attempt commits twenty codes and two sets against an index that permits one.
    /// The fake models that distinction rather than assuming it away.
    /// </para>
    /// <para>
    /// The discard's <em>placement</em> is asserted as "inside every attempt, never on attempt zero"
    /// rather than as an exact call count. One hoisted above the executor would run once and be undone
    /// by nothing — the rollback it exists to clean up after happens later. How many discards an
    /// attempt makes is a different question, owned where its reason lives: a second one before the
    /// delete is what keeps EF from cascading into tracked sessions and dying with 42501 on a
    /// <c>DELETE FROM sessions</c> the role deliberately cannot issue, and that is a claim only a real
    /// database can settle.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheUnitOfWorkIsReplayed_GeneratesExactlyOneSet()
    {
        // Arrange — a genuinely replaying executor. No rollback delegate is handed in, because this
        // account holds no previous set: the attempt removes no row, so there is nothing a ROLLBACK
        // would put back, and the only leftovers are the tracked ones the discard is about.
        Fixture fixture = Fixture.Build(attempts: ReplayedAttempts);

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — one set, ten codes, one spent nonce.
        await Assert.That(fixture.RecoveryCodes.Credentials.Count).IsEqualTo(1);
        await Assert.That(fixture.RecoveryCodes.Hashes.Count).IsEqualTo(RequiredCodeCount);
        await Assert.That(fixture.Challenges.ConsumeCallCount).IsEqualTo(1);

        // And every discard happened inside an attempt.
        await Assert.That(fixture.PersistenceState.DiscardedOnAttempt.Contains(0)).IsFalse();
        await Assert.That(fixture.PersistenceState.DiscardedOnAttempt.Distinct().Order().ToArray())
            .IsEquivalentTo(new[] { 1, ReplayedAttempts });
    }

    /// <summary>
    /// A replayed unit of work over an account that <b>already holds a set</b> leaves exactly one set,
    /// spends the nonce once, and ends the replaced set's sessions once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The combination the replay test above cannot reach, and the reason it is a separate test.</b>
    /// That one runs against an account with no previous set, so <c>FindRecoveryCodeCredentialAsync</c>
    /// answers <see langword="null" /> on both attempts and the whole regeneration branch — the sweep,
    /// the second discard, the delete — is never entered twice. Everything a replay can get wrong about
    /// <em>replacing</em> a set is invisible from there: an attempt that deleted a row the next attempt
    /// has to find again, and a sweep whose count is a number the fake happens to be holding.
    /// </para>
    /// <para>
    /// <b>The executor is handed a rollback, unlike the first-issue replay's, and that is not this test
    /// being kinder to the handler.</b> An abandoned attempt here really removed the previous set from
    /// the store, and the fakes hold their rows in plain lists with nothing to undo — so without it the
    /// second attempt would meet an account that looks as though it never had a set, and would answer
    /// correctly for a reason production never produces. The rollback restores exactly what a real
    /// <c>ROLLBACK</c> restores: the credential row, its ten codes, and the session it opened as an
    /// unstamped row read again rather than the revoked copy the abandoned attempt left in memory.
    /// <c>Session.Revoke</c> is idempotent and keeps the first instant, so without that second half the
    /// replayed sweep would match one row, end nothing, and report zero — a number produced by the fake
    /// and not by the handler. <c>RevokePasskeyHandlerTests</c> makes the same choice for the same
    /// reason. Nothing in the change tracker is touched, which is where the leftovers a replay must
    /// genuinely survive live.
    /// </para>
    /// <para>
    /// <b><see cref="InMemoryRecoveryCodeRepository.DeleteSetCallCount" /> is asserted at two</b>, and it
    /// is what says the arrangement was a replay of the regeneration rather than of something simpler: a
    /// second attempt that found no previous set would delete once, report zero sessions, and satisfy
    /// every other assertion here.
    /// </para>
    /// <para>
    /// <b>Two discards per attempt, and never on attempt zero.</b> The first is replay hygiene — rows
    /// queued for insert by an abandoned attempt are the tracker's, not the database's, so without it the
    /// surviving attempt commits two credentials and twenty codes against an index that permits one set.
    /// The second sits between the sweep and the delete, and what it buys can only be measured against a
    /// real database:
    /// <c>RecoveryCodeGenerationTests.Generation_WhenTheReplacedSetHasLiveSessions_DoesNotFailOnAMissingSessionDeleteGrant</c>
    /// is where that lives. What is measurable here is the <em>placement</em>: one hoisted above the
    /// executor would run once, on attempt zero, and would be undone by nothing — the rollback it exists
    /// to clean up after happens later. The attempt number each discard lands on is what tells the two
    /// placements apart; a plain call count cannot.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenAReplacementUnitOfWorkIsReplayed_LeavesExactlyOneSet()
    {
        // Arrange — an account holding a set, one live session opened by it, and an executor that runs
        // the whole unit of work twice.
        Fixture fixture = Fixture.Build(seedPreviousSet: true, attempts: ReplayedAttempts);
        RecoveryCodeHash[] previousCodes = [.. fixture.RecoveryCodes.Hashes];
        string[] previousHashes = [.. previousCodes.Select(hash => Convert.ToHexString(hash.VerifierHash.ToArray()))];
        await Assert.That(previousHashes.Length).IsEqualTo(RequiredCodeCount);

        await fixture.Sessions.AddAsync(Session.Establish(fixture.PreviousSet!, IssuedEarlier, UtcNow.AddHours(1)));

        // What a ROLLBACK puts back before the replay: the rows the abandoned attempt removed, and
        // nothing the change tracker was holding. See the remarks.
        fixture.RollBackAbandonedAttempt = () =>
        {
            fixture.RecoveryCodes.Seed(fixture.PreviousSet!, previousCodes);
            fixture.Sessions.DiscardTrackedEntities();

            return fixture.Sessions.AddAsync(
                Session.Establish(fixture.PreviousSet!, IssuedEarlier, UtcNow.AddHours(1)));
        };

        // Act
        RecoveryCodesGeneration generation = await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — the nonce was spent once, whatever the provider did to the transaction around it.
        await Assert.That(fixture.Challenges.ConsumeCallCount).IsEqualTo(1);

        // One set, ten codes, and they are the new ones — not twenty, and not the old ten under a new
        // credential.
        await Assert.That(fixture.RecoveryCodes.Credentials.Count).IsEqualTo(1);
        await Assert.That(fixture.RecoveryCodes.Credentials[0].Id).IsNotEqualTo(fixture.PreviousSet!.Id);

        string[] storedHashes =
            [.. fixture.RecoveryCodes.Hashes.Select(hash => Convert.ToHexString(hash.VerifierHash.ToArray())).Order()];
        string[] expectedHashes =
            [.. fixture.Verifiers.Select(verifier => Convert.ToHexString(SHA256.HashData(verifier))).Order()];
        await Assert.That(storedHashes).IsEquivalentTo(expectedHashes);
        await Assert.That(storedHashes.Any(stored => previousHashes.Contains(stored))).IsFalse();

        // Each attempt really did find a previous set to replace, which is what makes this a replay of
        // the regeneration branch rather than of the first-issue one.
        await Assert.That(fixture.RecoveryCodes.DeleteSetCallCount).IsEqualTo(ReplayedAttempts);

        // The surviving attempt's own sweep, counted once rather than accumulated across attempts.
        await Assert.That(generation.SessionsEnded).IsEqualTo(1);

        // Two discards per attempt, and never on attempt zero — which is what a call made before the
        // executor was entered would record.
        await Assert.That(fixture.PersistenceState.DiscardedOnAttempt).IsEquivalentTo(new[] { 1, 1, 2, 2 });
    }

    /// <summary>
    /// A replayed replacement leaves exactly one set and exactly <b>one</b> live session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Separate from
    /// <see cref="HandleAsync_WhenAReplacementUnitOfWorkIsReplayed_LeavesExactlyOneSet" /> rather than
    /// folded into it, because it is a different claim about the same replay.</b> That test is about the
    /// codes — one credential, ten hashes, and the discard that stops an abandoned attempt's queued rows
    /// committing beside the surviving attempt's. This one is about the session the surviving attempt
    /// opens: a delegate that establishes one and does not survive being run twice leaves the account
    /// signed in twice over, one row per attempt, from a single request nobody retried.
    /// </para>
    /// <para>
    /// <b>What the rollback stands for here is both halves at once, and the fake cannot separate them.</b>
    /// In production the session the abandoned attempt queued is a tracked insert the first
    /// <c>DiscardTrackedEntities()</c> removes, and the row it would have written is one a <c>ROLLBACK</c>
    /// never saw — the two point the same way, and
    /// <see cref="InMemorySessionRepository.DiscardTrackedEntities" /> clears one list for both. It is
    /// invoked from the rollback rather than wired into
    /// <see cref="RecordingPersistenceState" /> for the reason the fixture states: wired in, the discard at
    /// the top of the delegate would clear the <em>seeded</em> session too, and every sweep in this file
    /// would match nothing. That is a limit of a fake holding committed and pending rows in one list, and
    /// it is why <c>RecoveryCodeGenerationTests</c> owns the same claim against a real database.
    /// </para>
    /// <para>
    /// The surviving session's credential is asserted to be the surviving <em>set's</em>, which is what
    /// makes this more than a count: an attempt that opened its session over the credential a later attempt
    /// deleted leaves exactly one row too, pointing at nothing the account still holds.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenAReplacementUnitOfWorkIsReplayed_LeavesExactlyOneLiveSession()
    {
        // Arrange — an account holding a set, one live session opened by it, and an executor that runs the
        // whole unit of work twice.
        Fixture fixture = Fixture.Build(seedPreviousSet: true, attempts: ReplayedAttempts);
        RecoveryCodeHash[] previousCodes = [.. fixture.RecoveryCodes.Hashes];
        await fixture.Sessions.AddAsync(Session.Establish(fixture.PreviousSet!, IssuedEarlier, UtcNow.AddHours(1)));

        // What a ROLLBACK puts back before the replay: the rows the abandoned attempt removed, and the
        // set's session as an unstamped row read again rather than the revoked copy left in memory —
        // Session.Revoke keeps the first instant, so without that the replayed sweep would end nothing and
        // report a zero the fake produced. See the remarks for what the discard is standing in for.
        fixture.RollBackAbandonedAttempt = () =>
        {
            fixture.RecoveryCodes.Seed(fixture.PreviousSet!, previousCodes);
            fixture.Sessions.DiscardTrackedEntities();

            return fixture.Sessions.AddAsync(
                Session.Establish(fixture.PreviousSet!, IssuedEarlier, UtcNow.AddHours(1)));
        };

        // Act
        RecoveryCodesGeneration generation = await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — one set, and the surviving attempt's own sweep counted once rather than accumulated.
        await Assert.That(fixture.RecoveryCodes.Credentials.Count).IsEqualTo(1);
        await Assert.That(generation.SessionsEnded).IsEqualTo(1);

        // One live session, over the set the account is left holding.
        Credential set = fixture.RecoveryCodes.Credentials[0];
        Session[] live = [.. fixture.Sessions.Sessions.Where(session => session.RevokedAtUtc is null)];
        await Assert.That(live.Length).IsEqualTo(1);
        await Assert.That(live[0].CredentialId).IsEqualTo(set.Id);
    }

    /// <summary>
    /// Drives a refusal and hands back the one sentence it carried.
    /// </summary>
    private static async Task<string> RefusalSentence(byte[][] verifiers)
    {
        Fixture fixture = Fixture.Build(verifiers);
        ValidationException exception =
            await ThrowsAsync<ValidationException>(() => fixture.Handler.HandleAsync(fixture.Command));

        return string.Join(" ", exception.Errors.Values.SelectMany(messages => messages));
    }

    /// <summary>
    /// <paramref name="count" /> verifiers of <paramref name="length" /> bytes each, all distinct.
    /// </summary>
    /// <remarks>
    /// Random rather than fixed vectors, which costs nothing here: no assertion depends on the value of
    /// a verifier — every expected hash is computed from the bytes the test itself produced — so
    /// repeatability is not at stake, and randomness is what keeps the duplicate test's collision the
    /// only one in its set.
    /// </remarks>
    private static byte[][] Verifiers(int count, int length = VerifierLength) =>
        [.. Enumerable.Range(0, count).Select(_ => RandomNumberGenerator.GetBytes(length))];

    /// <summary>
    /// Runs <paramref name="action" /> and returns the exception it was expected to throw.
    /// </summary>
    /// <remarks>
    /// The catch names <typeparamref name="TException" /> exactly, so an exception of any other type
    /// escapes and fails the test as itself rather than being reported as "the expected exception was
    /// not thrown" — which matters more here than usual, since half these tests distinguish one refusal
    /// from another.
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

    /// <summary>
    /// The handler, the command, and every collaborator behind both — assembled once so no test has to
    /// restate an eight-argument constructor.
    /// </summary>
    /// <param name="Verifiers">
    /// The verifiers the command carries, as bytes. The command carries them as base64url text, which
    /// is how every binary member of this exchange crosses JSON; the decoded form is kept here because
    /// the expected hashes are computed from it.
    /// </param>
    /// <param name="PreviousSet">
    /// The credential of the set the account already held, where a test seeded one.
    /// </param>
    private sealed record Fixture(
        GenerateRecoveryCodesHandler Handler,
        GenerateRecoveryCodesCommand Command,
        InMemoryRecoveryCodeRepository RecoveryCodes,
        InMemorySessionRepository Sessions,
        StubWebAuthnChallengeStore Challenges,
        RecordingPersistenceState PersistenceState,
        IReadOnlyList<byte[]> Verifiers,
        Guid UserId,
        Credential? PreviousSet)
    {
        /// <summary>
        /// What a <c>ROLLBACK</c> puts back before the executor replays the unit of work, or
        /// <see langword="null" /> when the abandoned attempt removed no row there is anything to
        /// restore.
        /// </summary>
        /// <remarks>
        /// Settable after construction rather than a <see cref="Build" /> parameter, because only the
        /// test knows what the abandoned attempt will have taken — the seeded set's rows, and whatever
        /// sessions the test opened on it — and the executor is built before this fixture exists.
        /// <see cref="RetryingTransactionalExecutor" /> states why the restoration has to be the
        /// caller's: the fakes hold their rows in plain lists with nothing to undo, so without it the
        /// second attempt meets a store that looks as though the first one committed, which is a state
        /// production never produces. Left unset it is a no-op, which is what every test running the
        /// delegate once, and the first-issue replay, want.
        /// </remarks>
        public Func<Task>? RollBackAbandonedAttempt { get; set; }

        /// <summary>
        /// Builds the handler over fresh fakes, with a real device holding a real key pair and a real
        /// signature over the challenge the store was handed.
        /// </summary>
        /// <param name="verifiers">
        /// The set the caller presents. Null means a well-formed one of the required size, which is
        /// what every test that is not about the validation family wants.
        /// </param>
        /// <param name="attempts">
        /// How many times the executor runs the unit of work. One is the ordinary case; more than one
        /// is what the replay test needs, and it is a parameter rather than a second fixture so the two
        /// share one wiring.
        /// </param>
        /// <param name="ceremony">Which pool the store says the nonce was drawn from.</param>
        /// <param name="challengeIsLive">
        /// Whether the store holds the bytes the device signed. False leaves it holding a different
        /// nonce, which is how a store answers null without a second fake.
        /// </param>
        /// <param name="passkeyBelongsToAnotherAccount">
        /// Whether the registered passkey is filed under somebody other than the account the request
        /// authenticates as. The assertion is otherwise identical and carries no user handle, so the
        /// owner-scoped lookup is the only thing that can refuse it.
        /// </param>
        /// <param name="seedPreviousSet">
        /// Whether the account already holds a set of codes, issued a month ago.
        /// </param>
        public static Fixture Build(
            byte[][]? verifiers = null,
            int attempts = 1,
            WebAuthnCeremony ceremony = WebAuthnCeremony.Reauthentication,
            bool challengeIsLive = true,
            bool passkeyBelongsToAnotherAccount = false,
            bool seedPreviousSet = false)
        {
            Guid userId = Guid.CreateVersion7();
            StubUserContext userContext = new(userId);

            // ONE session store, and every reader of it below is handed THIS instance. It is wired
            // into the recovery-code fake as the cascade the database performs when a set's credentials
            // row goes — without that, the "revoke, then delete" ordering could be got wrong and still
            // look right, see the ordering test's remarks — it backs the revocation handler that
            // sweeps the replaced set, it is injected into the handler that opens the session over the
            // new one, and it is the same object every test reads back through Fixture.Sessions. The
            // sweep and the establishment seeing two stores would leave every assertion in the
            // re-established-session family measuring nothing.
            InMemorySessionRepository sessions = new();
            InMemoryRecoveryCodeRepository recoveryCodes =
                new(credential => sessions.RemoveForCredential(credential.Id));

            Credential? previousSet = null;
            if (seedPreviousSet)
            {
                Credential previous = Credential.CreateRecoveryCodes(userId, IssuedEarlier);
                recoveryCodes.Seed(
                    previous,
                    [
                        .. GenerateRecoveryCodesHandlerTests.Verifiers(RequiredCodeCount)
                            .Select(verifier => RecoveryCodeHash.From(previous, verifier, IssuedEarlier)),
                    ]);
                previousSet = previous;
            }

            // The passkey is filed under whoever owns the device, which is the request's own account
            // unless a test says otherwise. Nothing else about the ceremony changes with it.
            Guid passkeyOwnerId = passkeyBelongsToAnotherAccount ? Guid.CreateVersion7() : userId;
            SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
            Credential passkey = Credential.CreatePasskey(passkeyOwnerId, UtcNow);
            InMemoryPasskeyRepository passkeys = new();
            passkeys.Register(
                passkey,
                PasskeyPublicKey.Register(passkey, device.CredentialId, device.CoseKey, device.Algorithm),
                signatureCounter: 0);

            // Signed bytes and stored bytes are the same nonce unless the caller wants a dead one.
            byte[] signedChallenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
            byte[] storedChallenge = challengeIsLive
                ? signedChallenge
                : RandomNumberGenerator.GetBytes(ChallengeBytes);
            StubWebAuthnChallengeStore challenges = new(storedChallenge, ceremony);

            // No user handle, for the reason EraseAccountHandlerTests spells out: with the account's
            // own handle present, a lookup that had lost its owner filter would still be turned down by
            // the handle check below it, and the stranger's-passkey test would stay green over a gate
            // with no binding left.
            AssertionResult assertion = device.Authenticate(signedChallenge, Origin, userHandle: null);

            // A replaying executor even at one attempt, rather than InMemoryTransactionalExecutor: at
            // one attempt the two are behaviourally identical, and going through this one is what gives
            // the persistence state an attempt number to record instead of a constant.
            //
            // The rollback is forwarded to the fixture rather than declared here, because what a
            // ROLLBACK puts back depends on what the test arranged — see RollBackAbandonedAttempt. It is
            // never invoked before the second attempt, so a caller that sets nothing gets the plain
            // replay the first-issue test expects.
            Fixture? built = null;
            RetryingTransactionalExecutor executor = new(
                attempts,
                () => built?.RollBackAbandonedAttempt?.Invoke() ?? Task.CompletedTask);

            // The discard is forwarded to the two fakes holding rows an abandoned attempt queued, and
            // deliberately NOT to the session fake: that one holds committed and pending sessions in a
            // single list, so wiring it in would clear the seeded session at the top of the delegate
            // and every sweep in this file would match nothing. The replay tests invoke it from their
            // own rollback instead — RevokePasskeyHandlerTests makes the same choice for the same
            // reason.
            RecordingPersistenceState persistenceState = new(
                () => executor.Attempts,
                recoveryCodes.DiscardTrackedEntities,
                passkeys.DiscardTrackedEntities);

            FakeTimeProvider timeProvider = new(new DateTimeOffset(UtcNow));

            GenerateRecoveryCodesHandler handler = new(
                recoveryCodes,
                userContext,
                persistenceState,
                executor,
                new PasskeyReauthentication(
                    challenges,
                    passkeys,
                    userContext,
                    new StubPasskeyCeremonyPolicy(RelyingPartyId, Origin)),
                new RevokeSessionsForCredentialHandler(sessions, timeProvider),

                // NOT A STRAY ARGUMENT. The handler opens a session over the NEW set when the
                // replacement swept any, and that write belongs to an injected ISessionRepository —
                // the shape RedeemRecoveryCodeHandler already has — not to a handler whose name says
                // revoke. Passing the same `sessions` instance the revocation handler above holds is
                // load-bearing: the sweep and the establishment must see one store, or the
                // re-established-session tests stop meaning anything.
                //
                // The parameter it binds to is being added by the change that moves the session write
                // off RevokeSessionsForCredentialHandler; until that lands this call has one argument
                // more than the constructor declares and the project does not compile. The position —
                // beside the revocation handler, before the clock — is where a reader can see the two
                // sharing a store; move it only together with the constructor.
                sessions,
                timeProvider);

            byte[][] presented = verifiers ?? GenerateRecoveryCodesHandlerTests.Verifiers(RequiredCodeCount);

            built = new Fixture(
                handler,
                new GenerateRecoveryCodesCommand(
                    [.. presented.Select(verifier => Base64UrlText.Encode(verifier))],
                    new ReauthenticationAssertion(
                        assertion.CredentialIdBase64Url,
                        assertion.ClientDataJsonBase64Url,
                        assertion.AuthenticatorDataBase64Url,
                        assertion.SignatureBase64Url,
                        assertion.UserHandleBase64Url)),
                recoveryCodes,
                sessions,
                challenges,
                persistenceState,
                presented,
                userId,
                previousSet);

            return built;
        }
    }
}
