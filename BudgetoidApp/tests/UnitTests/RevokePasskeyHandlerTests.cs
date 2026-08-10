using System.Security.Cryptography;
using Application.Abstractions;
using Application.Passkeys;
using Application.Passkeys.Reauthentication;
using Application.Passkeys.RevokePasskey;
using Application.Sessions.RevokeSessionsForCredential;
using Domain.Common;
using Domain.Sessions;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// The two orderings inside a revocation — the floor refuses <em>before</em> anything is removed, and
/// the sessions a passkey opened are ended <em>before</em> the credential row goes — and the number
/// the response reports about them.
/// </summary>
/// <remarks>
/// <para>
/// A unit test as well as an endpoint one, and the reason is the word <em>before</em>. Over HTTP a
/// refusal that had already deleted rows would be caught by counting them afterwards, but only for the
/// rows that survive the request boundary; here the ordering is measured directly against the fake, so
/// a check placed after the delete — or after a session sweep — is red on the second assertion rather
/// than on the status. The rule itself is a cross-row claim no <c>CHECK</c> and no unique index can
/// express, which is why it lives in this handler and nowhere below it; see
/// <c>docs/business-logic/users-and-ownership.md</c>, which carries the ADR 0002 argument for the
/// placement.
/// </para>
/// <para>
/// The gate is a real <see cref="PasskeyReauthentication" /> over fakes rather than a stub, exactly as
/// <see cref="EraseAccountHandlerTests" /> builds it, and for the same reason: there is no interface to
/// stub it behind, and a stubbable gate would let a test here prove that revocation works with the
/// proof faked out — the one thing that must never be provable. The device holds a real key pair and
/// signs the real nonce the store was handed.
/// </para>
/// <para>
/// The floor is one <b>passkey</b>, not one credential. The account below therefore holds its federated
/// Google credential too, so a count that forgot the type predicate would read two and let the last
/// passkey go.
/// </para>
/// </remarks>
public sealed class RevokePasskeyHandlerTests
{
    private const string RelyingPartyId = "localhost";
    private const string Origin = "https://localhost:4200";
    private const int ChallengeBytes = 32;

    /// <summary>
    /// How many times the executor runs the unit of work in
    /// <see cref="HandleAsync_WhenTheUnitOfWorkIsReplayed_RevokesAndDeletesExactlyOnce" />. Two is the
    /// smallest number that is a replay at all, and nothing that test measures gets sharper with more.
    /// </summary>
    private const int ReplayedAttempts = 2;

    /// <summary>Fixed instant for every seeded row, so nothing here depends on the wall clock.</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 10, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// One account, one passkey, a genuine fresh proof — and the passkey stays.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing about this request is wrong: the assertion is signed by a device this account really
    /// registered, over a nonce the store really issued for a re-authentication, and the id names that
    /// account's own credential. It is refused purely because of what the account would be left with,
    /// which is why the answer is <see cref="ConflictException" /> — 409 — rather than a validation
    /// failure or a not-found. A person left holding only the federated credential could still sign in,
    /// still could not reach any budget content, and could not even prove presence for an erasure.
    /// </para>
    /// <para>
    /// <b>The "deletes nothing" half is the point of writing this at unit level.</b> A handler that
    /// counts <em>after</em> the delete, or that ends the credential's sessions on the way past, throws
    /// the exception this test demands while having already taken the passkey away — the person is
    /// signed out of a credential the response tells them they still hold. Asserting the throw alone
    /// would call that implementation correct.
    /// </para>
    /// <para>
    /// <b>A second account holds a passkey of its own, and it is what makes the count's owner predicate
    /// measurable.</b> With one account seeded, "every passkey the fake holds" and "this account's
    /// passkeys" are the same set — measured on the real repository, where dropping <c>user_id</c> from
    /// the count changed no answer in either suite — so the floor could be measured against a number
    /// that never falls to one and nothing here would notice. In production two accounts holding one
    /// passkey each would both read two, no floor would ever fire, and the first person to revoke their
    /// only passkey would be locked out permanently. The bystander is seeded and never touched again;
    /// it is not spare scenery.
    /// </para>
    /// <para>
    /// <b>The session sweep is wired in and asserted never to have run</b>, which is the stronger of
    /// the two ways to say it. Handing the handler no
    /// <see cref="RevokeSessionsForCredentialHandler" /> at all would only prove the refusal path never
    /// <em>dereferenced</em> the collaborator, and it would buy that proof by making the argument
    /// optional in production — a missing registration becoming a runtime failure mid-request rather
    /// than something the container refuses at startup. A zero call count proves the refusal path never
    /// <em>used</em> it, which is the actual rule, and it keeps holding if the sweep ever gains a second
    /// call site.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_ForTheOnlyRemainingPasskey_ThrowsAndDeletesNothing()
    {
        // Arrange
        Guid userId = Guid.CreateVersion7();
        StubUserContext userContext = new(userId);
        InMemoryPasskeyRepository passkeys = new();

        // The one passkey the account holds, filed the way a completed registration would file it.
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        Credential passkey = Credential.CreatePasskey(userId, UtcNow);
        passkeys.Register(
            passkey,
            PasskeyPublicKey.Register(passkey, device.CredentialId, device.CoseKey, device.Algorithm),
            signatureCounter: 0);

        // A bystander account with one passkey of its own, filed in the same repository and never named
        // by anything below. It is what makes the OWNER predicate on the count load-bearing: without
        // it, an unscoped count reads the same one this account holds and the floor stays green over a
        // rule that could never fire for anybody. With it, an unscoped count reads two and the refusal
        // below never happens. Do not remove it as unused setup — see the remarks.
        SyntheticAuthenticator bystanderDevice = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        Credential bystanderPasskey = Credential.CreatePasskey(Guid.CreateVersion7(), UtcNow);
        passkeys.Register(
            bystanderPasskey,
            PasskeyPublicKey.Register(
                bystanderPasskey,
                bystanderDevice.CredentialId,
                bystanderDevice.CoseKey,
                bystanderDevice.Algorithm),
            signatureCounter: 0);

        // The stored nonce and the signed nonce are the same bytes, so the gate has no reason to
        // refuse: a 401-shaped failure here would make this test green about something other than
        // the floor.
        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
        StubWebAuthnChallengeStore challenges = new(challenge, WebAuthnCeremony.Reauthentication);
        AssertionResult assertion = device.Authenticate(challenge, Origin, userHandle: null);

        // A pass-through executor and a constant attempt number: where the discard lands under a replay
        // is pinned by EraseAccountHandlerTests and by the revocation's own endpoint tests, and nothing
        // this test measures moves with it. The discard is still wired through to the fake, because the
        // handler makes it before the lookup and a state that swallowed it would hide that.
        InMemoryTransactionalExecutor executor = new();
        RecordingPersistenceState persistenceState = new(() => 1, passkeys.DiscardTrackedEntities);

        // The session sweep is fully wired, so the refusal has every opportunity to reach it — the
        // claim below is that it did not, counted rather than inferred from an argument left out.
        InMemorySessionRepository sessions = new();

        RevokePasskeyHandler handler = new(
            passkeys,
            userContext,
            persistenceState,
            executor,
            new PasskeyReauthentication(
                challenges,
                passkeys,
                userContext,
                new StubPasskeyCeremonyPolicy(RelyingPartyId, Origin)),
            new RevokeSessionsForCredentialHandler(sessions, new FakeTimeProvider(new DateTimeOffset(UtcNow))));

        RevokePasskeyCommand command = new(
            passkey.Id,
            new ReauthenticationAssertion(
                assertion.CredentialIdBase64Url,
                assertion.ClientDataJsonBase64Url,
                assertion.AuthenticatorDataBase64Url,
                assertion.SignatureBase64Url,
                assertion.UserHandleBase64Url));

        // Act
        await ThrowsAsync<ConflictException>(() => handler.HandleAsync(command));

        // Assert — the credential is still there, and it is read back through the same owner-and-type
        // scoped lookup the handler resolves it with, so "still held" means still reachable by the
        // account that owns it rather than merely still in a list.
        await Assert.That(await passkeys.FindPasskeyCredentialAsync(passkey.Id, userId)).IsNotNull();
        await Assert.That(await passkeys.CountPasskeysForUserAsync(userId)).IsEqualTo(1);

        // And the refusal ended no sessions: the sweep was there to be called and was never asked to
        // run, which is what a zero call count says and what an unpassed collaborator could not.
        await Assert.That(sessions.RevokeForCredentialCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// The sessions the passkey established are stamped <b>before</b> the credential row goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The ordering itself, which the endpoint test cannot state.</b> Over HTTP the evidence is
    /// the <c>sessionsEnded</c> count, and a count is an <em>outcome</em>: a future refactor could
    /// reproduce the right number by some other route and leave the rule holding by accident. Here
    /// the ordering is measured directly.
    /// </para>
    /// <para>
    /// <b>Why the observation happens at the delete rather than after it.</b>
    /// <c>sessions.credential_id</c> is <c>ON DELETE CASCADE</c>, so once the credential is gone its
    /// session rows are gone whether anything revoked them first or not — asserting afterwards proves
    /// nothing, which is the argument
    /// <c>docs/business-logic/sessions.md</c> makes for the test nobody should write. A pair of call
    /// counters compared before and after is no better: two counters that both moved say both things
    /// happened, never that one preceded the other. So the passkey fake is asked, at the instant
    /// <c>DeletePasskeyAsync</c> is entered, whether that credential's sessions already carry a
    /// revocation instant.
    /// </para>
    /// <para>
    /// The two <see cref="Session" /> objects are held by reference rather than read back out of the
    /// repository, deliberately. <c>Session.Revoke</c> stamps the object, and a handler that discards
    /// its tracked entities between the revocation and the delete — which is exactly the fix the
    /// endpoint test
    /// <c>Revocation_WhenTheCredentialHasLiveSessions_DoesNotFailOnAMissingSessionDeleteGrant</c>
    /// forces — empties the fake's list. Reading the list at that moment would find nothing and an
    /// "all of them are stamped" predicate would be vacuously true over zero rows, which is the one
    /// way this test could go green on a handler that revoked nothing.
    /// </para>
    /// <para>
    /// <c>null</c> rather than <c>false</c> is what the observation reports when the delete never ran
    /// at all, so the assertion below cannot be satisfied by a handler that refused before reaching
    /// it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_RevokesTheSessionsBeforeDeletingTheCredential()
    {
        // Arrange
        TwoPasskeyAccount account = ArrangeAccountWithTwoPasskeys();
        StubUserContext userContext = new(account.UserId);

        // Two live sessions on the passkey that goes, held by reference — see the remarks.
        Session first = Session.Establish(account.Revoked, UtcNow, UtcNow.AddHours(1));
        Session second = Session.Establish(account.Revoked, UtcNow, UtcNow.AddHours(1));
        InMemorySessionRepository sessions = new();
        await sessions.AddAsync(first);
        await sessions.AddAsync(second);

        // The question the fake asks the moment the delete is entered.
        account.Passkeys.ObserveAtDelete = _ =>
            first.RevokedAtUtc is not null && second.RevokedAtUtc is not null;

        RevokePasskeyHandler handler = BuildHandler(account, userContext, sessions);

        // Act
        await handler.HandleAsync(account.CommandRevokingTheOtherPasskey());

        // Assert — non-null first, because null and false fail for different reasons: null says the
        // delete never ran at all, false says the credential was removed while the sessions it
        // opened still looked live.
        await Assert.That(account.Passkeys.ObservationAtDelete).IsNotNull();
        await Assert.That(account.Passkeys.ObservationAtDelete).IsTrue();

        // And the instant came from the clock the handler was given, not from the wall clock: one
        // sweep is one decision to end access and should read as one instant in the record.
        await Assert.That(sessions.LastRevokedAtUtc).IsEqualTo(UtcNow);
        await Assert.That(first.RevokedAtUtc).IsEqualTo(UtcNow);
        await Assert.That(second.RevokedAtUtc).IsEqualTo(UtcNow);
    }

    /// <summary>
    /// The number in the response is the number the session repository reported, not a constant.
    /// </summary>
    /// <remarks>
    /// Three sessions rather than one, because a hardcoded <c>1</c>, a <c>Count > 0 ? 1 : 0</c>, or a
    /// sweep that stopped at the first match all pass a single-session arrangement. The endpoint test
    /// asserts the same fact over the wire; this one says where the number has to come from, and goes
    /// red on a handler that counts the rows itself instead of reporting what the revocation returned
    /// — a count of its own would include sessions a concurrent sweep had already ended.
    /// </remarks>
    [Test]
    public async Task HandleAsync_ReportsTheNumberOfSessionsTheRevocationEnded()
    {
        // Arrange
        TwoPasskeyAccount account = ArrangeAccountWithTwoPasskeys();
        StubUserContext userContext = new(account.UserId);
        InMemorySessionRepository sessions = new();
        await sessions.AddAsync(Session.Establish(account.Revoked, UtcNow, UtcNow.AddHours(1)));
        await sessions.AddAsync(Session.Establish(account.Revoked, UtcNow, UtcNow.AddHours(1)));
        await sessions.AddAsync(Session.Establish(account.Revoked, UtcNow, UtcNow.AddHours(1)));
        RevokePasskeyHandler handler = BuildHandler(account, userContext, sessions);

        // Act
        PasskeyRevocation revocation = await handler.HandleAsync(account.CommandRevokingTheOtherPasskey());

        // Assert
        await Assert.That(revocation.SessionsEnded).IsEqualTo(3);
    }

    /// <summary>
    /// The gate runs to completion <b>before</b> the transactional delegate, and this is the test that
    /// goes red the instant it is moved inside one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The placement carries two arguments, and this test holds the second one only.</b> Measured
    /// rather than supposed. Moving <c>reauthentication.VerifyAsync</c> inside the delegate does redden
    /// one other test —
    /// <c>CredentialRevocationTests.EveryReachableRevocationRefusal_ProducesTheIdenticalResponse</c>,
    /// whose <em>consumed challenge</em> entry starts answering 200 — but for the <b>first</b>
    /// argument: <c>ConsumeAsync</c> joins the ambient transaction, so a refused attempt rolls back,
    /// puts the spent nonce back on the table, and the same assertion is replayable. A <em>refusal</em>
    /// is what makes that visible, and one has to be driven to see it.
    /// </para>
    /// <para>
    /// <b>The second argument is a valid revocation being refused, and nothing covered it.</b> The
    /// delegate is replayed under a retrying execution strategy, so a gate inside it consumes a second
    /// time on attempt two, finds the nonce already spent, and turns a correct request into the same
    /// 401 an attacker gets — because the database blinked. Every other test in either suite runs the
    /// delegate exactly once, and a gate called once is a gate called correctly, so none of them can
    /// see it: under the mutation the act below throws <c>PasskeyVerificationException</c> and every
    /// assertion here is unreachable. <see cref="EraseAccountHandlerTests" /> holds the same pin for
    /// erasure and does not cover this one: it drives a different handler, and this story made the
    /// re-authentication pool have two spenders rather than one.
    /// </para>
    /// <para>
    /// <b>Written as an outcome pin with the consume count beside it</b>, in this file's philosophy: the
    /// revocation completed and the nonce was spent exactly once. A call-order assertion would pass for
    /// a handler that got the order right by accident.
    /// </para>
    /// <para>
    /// <b>The executor is handed a rollback, unlike erasure's, and that is not this test being kinder to
    /// the handler.</b> An erasure's delegate empties tables and deletes a row that is allowed to be
    /// absent already, so it survives its own leftovers; a revocation's second attempt would look up a
    /// credential the first attempt really removed from a list that has no rollback, and answer 404 for
    /// a row production would have put back. That 404 would be the fake's, not the handler's. The
    /// rollback restores exactly what a real <c>ROLLBACK</c> restores — the credential row, and the
    /// session rows unstamped — and touches nothing in the change tracker, which is where the leftovers
    /// a replay must genuinely survive live.
    /// </para>
    /// <para>
    /// The discard placement is asserted alongside, because the replay is what makes it observable at
    /// all and the arrangement is already here. Both calls belong inside every attempt: one hoisted
    /// above the executor would run once, on attempt zero, and would be undone by nothing — the
    /// rollback it exists to clean up after happens later. The attempt number each discard lands on is
    /// what tells the two placements apart; a plain call count cannot.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheUnitOfWorkIsReplayed_RevokesAndDeletesExactlyOnce()
    {
        // Arrange
        TwoPasskeyAccount account = ArrangeAccountWithTwoPasskeys();
        StubUserContext userContext = new(account.UserId);

        // One live session on the passkey that goes, so the number the surviving attempt reports is a
        // number it had to count rather than the zero an empty arrangement produces either way.
        InMemorySessionRepository sessions = new();
        await sessions.AddAsync(Session.Establish(account.Revoked, UtcNow, UtcNow.AddHours(1)));

        // A replaying executor, told what a ROLLBACK puts back: the credential row the abandoned
        // attempt deleted, and the credential's sessions as unstamped rows read again rather than the
        // revoked copies that attempt left in memory. Session.Revoke is idempotent and keeps the first
        // instant, so without the second half the replayed sweep would match one row, end nothing, and
        // report zero — a number produced by the fake and not by the handler.
        RetryingTransactionalExecutor executor = new(
            ReplayedAttempts,
            () =>
            {
                account.RestoreTheRevokedPasskey();
                sessions.DiscardTrackedEntities();

                return sessions.AddAsync(Session.Establish(account.Revoked, UtcNow, UtcNow.AddHours(1)));
            });

        // The attempt number rather than a constant, which is what gives the discard assertion below
        // something to tell attempt zero apart from attempts one and two by.
        RecordingPersistenceState persistenceState = new(
            () => executor.Attempts,
            account.Passkeys.DiscardTrackedEntities);

        RevokePasskeyHandler handler = BuildHandler(account, userContext, sessions, executor, persistenceState);

        // Act
        PasskeyRevocation revocation = await handler.HandleAsync(account.CommandRevokingTheOtherPasskey());

        // Assert — the nonce was spent once, whatever the provider did to the transaction around it.
        await Assert.That(account.Challenges.ConsumeCallCount).IsEqualTo(1);

        // And the replay left the outcome of a single revocation: the named passkey gone, read back
        // through the same owner-and-type scoped lookup the handler resolves it with, the account's
        // other passkey untouched, and the sessions of the one that went reported as ended.
        await Assert.That(await account.Passkeys.FindPasskeyCredentialAsync(account.Revoked.Id, account.UserId))
            .IsNull();
        await Assert.That(await account.Passkeys.FindPasskeyCredentialAsync(account.Proving.Id, account.UserId))
            .IsNotNull();
        await Assert.That(await account.Passkeys.CountPasskeysForUserAsync(account.UserId)).IsEqualTo(1);
        await Assert.That(revocation.SessionsEnded).IsEqualTo(1);

        // Two discards per attempt, and never on attempt zero — which is what a call made before the
        // executor was entered would record.
        await Assert.That(persistenceState.DiscardedOnAttempt).IsEquivalentTo(new[] { 1, 1, 2, 2 });
    }

    /// <summary>
    /// An account holding two passkeys, with a genuine fresh proof from the one that stays.
    /// </summary>
    /// <param name="Proving">The passkey that signs the re-authentication and is not removed.</param>
    /// <param name="Revoked">The passkey the command names.</param>
    /// <param name="RestoreTheRevokedPasskey">
    /// Files <see cref="Revoked" /> again, with the key material and counter value it was registered
    /// with — what a <c>ROLLBACK</c> does to the row a deleted attempt removed. It lives here because
    /// the public key it needs is built in the arrangement and is otherwise not kept.
    /// </param>
    private sealed record TwoPasskeyAccount(
        Guid UserId,
        InMemoryPasskeyRepository Passkeys,
        Credential Proving,
        Credential Revoked,
        StubWebAuthnChallengeStore Challenges,
        AssertionResult Assertion,
        Action RestoreTheRevokedPasskey)
    {
        /// <summary>The revocation of <see cref="Revoked" />, proved by <see cref="Proving" />.</summary>
        public RevokePasskeyCommand CommandRevokingTheOtherPasskey() =>
            new(
                Revoked.Id,
                new ReauthenticationAssertion(
                    Assertion.CredentialIdBase64Url,
                    Assertion.ClientDataJsonBase64Url,
                    Assertion.AuthenticatorDataBase64Url,
                    Assertion.SignatureBase64Url,
                    Assertion.UserHandleBase64Url));
    }

    /// <summary>
    /// Two registered passkeys rather than one, so the "an account's last passkey does not go" floor
    /// cannot turn a test about sessions into a test about the refusal above.
    /// </summary>
    private static TwoPasskeyAccount ArrangeAccountWithTwoPasskeys()
    {
        Guid userId = Guid.CreateVersion7();
        InMemoryPasskeyRepository passkeys = new();

        SyntheticAuthenticator provingDevice = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        Credential proving = Credential.CreatePasskey(userId, UtcNow);
        passkeys.Register(
            proving,
            PasskeyPublicKey.Register(proving, provingDevice.CredentialId, provingDevice.CoseKey, provingDevice.Algorithm),
            signatureCounter: 0);

        SyntheticAuthenticator revokedDevice = SyntheticAuthenticator.CreateEs256(RelyingPartyId);
        Credential revoked = Credential.CreatePasskey(userId, UtcNow);
        PasskeyPublicKey revokedKey = PasskeyPublicKey.Register(
            revoked,
            revokedDevice.CredentialId,
            revokedDevice.CoseKey,
            revokedDevice.Algorithm);
        passkeys.Register(revoked, revokedKey, signatureCounter: 0);

        // The stored nonce and the signed nonce are the same bytes, so the gate has no reason to
        // refuse: a 401-shaped failure would make these tests green about something other than what
        // they measure.
        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeBytes);
        StubWebAuthnChallengeStore challenges = new(challenge, WebAuthnCeremony.Reauthentication);

        return new TwoPasskeyAccount(
            userId,
            passkeys,
            proving,
            revoked,
            challenges,
            provingDevice.Authenticate(challenge, Origin, userHandle: null),
            () => passkeys.Register(revoked, revokedKey, signatureCounter: 0));
    }

    /// <summary>
    /// The handler over the fakes, with a real <see cref="PasskeyReauthentication" /> for the reason
    /// the class remarks give: a stubbable gate would let a test prove that revocation works with the
    /// proof faked out.
    /// </summary>
    /// <remarks>
    /// The persistence state forwards only to the passkey fake. Wiring the session fake's discard in
    /// too would clear the very rows this arrangement is about, and where the discards land is pinned
    /// by <see cref="HandleAsync_WhenTheUnitOfWorkIsReplayed_RevokesAndDeletesExactlyOnce" /> — which
    /// is also the one test here that passes its own two arguments, because a constant attempt number
    /// and a delegate run once cannot express a replay.
    /// </remarks>
    private static RevokePasskeyHandler BuildHandler(
        TwoPasskeyAccount account,
        StubUserContext userContext,
        InMemorySessionRepository sessions,
        ITransactionalExecutor? executor = null,
        IPersistenceState? persistenceState = null) =>
        new(
            account.Passkeys,
            userContext,
            persistenceState ?? new RecordingPersistenceState(() => 1, account.Passkeys.DiscardTrackedEntities),
            executor ?? new InMemoryTransactionalExecutor(),
            new PasskeyReauthentication(
                account.Challenges,
                account.Passkeys,
                userContext,
                new StubPasskeyCeremonyPolicy(RelyingPartyId, Origin)),
            new RevokeSessionsForCredentialHandler(sessions, new FakeTimeProvider(new DateTimeOffset(UtcNow))));

    /// <summary>
    /// Runs <paramref name="action" /> and returns the exception it was expected to throw.
    /// </summary>
    /// <remarks>
    /// The catch names <typeparamref name="TException" /> exactly, so an exception of any other type
    /// escapes and fails the test as itself, rather than being reported as "the expected exception was
    /// not thrown" and leaving a reader to guess what happened instead.
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
