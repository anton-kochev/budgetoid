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
    /// restate a seven-argument constructor.
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

            // The session fake is wired into the recovery-code fake as the cascade the database
            // performs when a set's credentials row goes. Without it the "revoke, then delete" ordering
            // could be got wrong and still look right — see the ordering test's remarks.
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

            // The discard is forwarded to the recovery-code fake only. Wiring the session fake's in too
            // would clear the very rows the ordering test is about — RevokePasskeyHandlerTests makes
            // the same choice for the same reason.
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
