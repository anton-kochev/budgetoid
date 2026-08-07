using System.Security.Cryptography;
using Application.Abstractions;
using Application.Passkeys;
using Application.Passkeys.Reauthentication;
using Application.Users.EraseAccount;
using Domain.Transactions;
using Domain.Users;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// The order erasure deletes in, where it discards the tracked entities, and where the
/// re-authentication gate sits relative to the transaction.
/// </summary>
/// <remarks>
/// <para>
/// A unit test rather than an integration one, and deliberately so. <c>transactions</c> is the child
/// of four of the owned graph's five <c>ON DELETE RESTRICT</c> edges — to <c>budgets</c>,
/// <c>accounts</c>, <c>categories</c> and <c>payees</c> — so PostgreSQL answers <c>23503</c> for a
/// delete that reaches <c>users</c> while any transaction is still there. But whether it reaches
/// <c>users</c> first is not something an integration test can pin: collapse the two saves back into
/// one and EF sorts the batch by the foreign keys <em>between the entity types in it</em>, of which
/// there are none, so the order becomes an implementation detail of the change tracker and the
/// endpoint test starts failing intermittently. A flaky test is worse than a red one.
/// </para>
/// <para>
/// The fifth edge, <c>categories → category_groups</c>, is deliberately not modelled here, because no
/// handler step answers it: both tables cascade from <c>budgets</c>, PostgreSQL queues the check on
/// that edge as an after-row trigger when the <c>category_groups</c> row is deleted — strictly after
/// the cascade into <c>categories</c> was queued — and the after-trigger queue is FIFO. That is a
/// property of the database rather than of this handler, so it is measured where the database is:
/// <c>AccountErasureEndpointTests.Erase_ForAnAccountWithCategoriesAndNoTransaction_LeavesNoneOfEither</c>.
/// </para>
/// <para>
/// The pin is an outcome, not a call-order assertion. <see cref="InMemoryUserRepository" /> carries
/// the database's own rule and refuses a delete while a transaction is still there, so a handler that
/// got the order wrong throws here for the same reason PostgreSQL would — and a handler that
/// legitimately reshapes how it reaches that order, by deleting in chunks for instance, needs no
/// change to these tests. That is why no test below counts calls to a delete-all.
/// </para>
/// <para>
/// The gate is a real <see cref="PasskeyReauthentication" /> over fakes rather than a stub, and there
/// is no interface to stub it behind. A stubbable gate would let a test here prove that erasure works
/// with the gate faked out, which is the one thing that must never be provable.
/// </para>
/// </remarks>
public sealed class EraseAccountHandlerTests
{
    /// <summary>
    /// Minor unit of the USD account the seeded transaction belongs to. Precision is not what this
    /// file is about; the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    /// <summary>
    /// How many times the replaying executor runs the unit of work, standing in for one transient
    /// failure recovered by a retrying provider strategy.
    /// </summary>
    private const int ReplayedAttempts = 2;

    [Test]
    public async Task HandleAsync_DeletesTheTransactionsBeforeTheUser()
    {
        // Arrange — an account with recorded movement in it. transactions → budgets is RESTRICT, so
        // the user row cannot go while this transaction is still there.
        Fixture fixture = Fixture.Build();
        await fixture.Transactions.AddAsync(Transaction.Create(
            fixture.BudgetId,
            Guid.CreateVersion7(),
            -10m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 26),
            "Coffee",
            Fixture.UtcNow));

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — the user is gone, which is only reachable through a transactions delete that
        // happened first: the repository refuses outright otherwise.
        await Assert.That(fixture.Users.Users.Count).IsEqualTo(0);
        await Assert.That(fixture.Transactions.Transactions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WhenTheUserRowIsAlreadyGone_Completes()
    {
        // Arrange — no user row at all, which is the state a retried erasure arrives in. The passkey
        // is still filed, because the gate reads it from its own table rather than through the user.
        Fixture fixture = Fixture.Build(seedUser: false);

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — asked for, not skipped, and not translated into a refusal. Erasure is a request
        // for a post-condition rather than for a row, and a 404 would tell someone their data might
        // still be there.
        await Assert.That(fixture.Users.DeleteCallCount).IsEqualTo(1);
        await Assert.That(fixture.Users.Users.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Where the discard is made, not merely that it is made.
    /// </summary>
    /// <remarks>
    /// A discard hoisted above the executor would run once, on attempt zero, and would be undone by
    /// nothing — the rollback it exists to clean up after happens later, so the second attempt would
    /// start with the first attempt's leftovers exactly as if the call were absent. Every assertion
    /// this file makes about the erasure's outcome stays green under that move, which is what leaves
    /// the placement unheld without this test. Recording the attempt each discard lands on is what
    /// tells the two placements apart; a plain call count cannot.
    /// <para>
    /// The same pin, for the same reason and in the same shape, as
    /// <c>CompleteAssertionHandlerTests.HandleAsync_DiscardsTheTrackedEntitiesInsideEveryAttempt</c>.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_DiscardsTheTrackedEntitiesInsideEveryAttempt()
    {
        // Arrange — an executor that really replays the unit of work, against fakes that really
        // carry the first attempt's leftovers.
        Fixture fixture = Fixture.Build(attempts: ReplayedAttempts);

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert — once per attempt, and never on attempt zero, which is what a call made before the
        // executor was entered would record.
        await Assert.That(fixture.PersistenceState.DiscardedOnAttempt).IsEquivalentTo(new[] { 1, 2 });
    }

    /// <summary>
    /// The gate runs to completion <b>before</b> the transactional delegate, and this is the test that
    /// goes red the instant it is moved inside one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delegate is replayed under a retrying execution strategy, and a nonce is single use. A gate
    /// inside the delegate would call <c>ConsumeAsync</c> a second time on attempt two, find the nonce
    /// already spent, and refuse a <b>valid</b> erasure with the same 401 an attacker gets — because
    /// the database blinked. The other half of the argument cannot be seen from here and is written on
    /// the handler: the consume commits on its own save, so inside the erasure transaction a
    /// rolled-back erasure would <em>restore</em> the spent nonce and make the assertion replayable.
    /// </para>
    /// <para>
    /// Written as an outcome pin with the consume count beside it, in the philosophy this file's
    /// remarks already state: the erasure completed, and the nonce was spent exactly once. A call-order
    /// assertion would pass for a handler that got the order right by accident.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HandleAsync_WhenTheUnitOfWorkIsReplayed_StillErasesTheAccount()
    {
        // Arrange — a replaying executor over a challenge store that models single use rather than
        // assuming it, which is what makes a second consume observable at all.
        Fixture fixture = Fixture.Build(attempts: ReplayedAttempts);

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        await Assert.That(fixture.Users.Users.Count).IsEqualTo(0);
        await Assert.That(fixture.Challenges.ConsumeCallCount).IsEqualTo(1);
    }

    /// <summary>
    /// The account binding at unit level: a genuine assertion from an authenticator registered to
    /// somebody else erases nothing.
    /// </summary>
    /// <remarks>
    /// This is the test the owner-scoped finder exists for. Reuse the unscoped discovery lookup here
    /// and the signature verifies, the ceremony completes, and the request's own account is destroyed
    /// on the strength of a stranger's device. The assertion carries no user handle, so nothing but
    /// the scoped lookup can refuse it.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAPasskeyBelongingToAnotherAccount_ThrowsAndErasesNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Build(passkeyBelongsToAnotherAccount: true);

        // Act
        await ThrowsRefusalAsync(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert — the seeded account is asserted present before the act by the control test below;
        // here what matters is that the count did not move to zero.
        await Assert.That(fixture.Users.Users.Count).IsEqualTo(1);
        await Assert.That(fixture.Users.DeleteCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// A nonce drawn from either of the other two pools erases nothing, even though it is live,
    /// unspent and correctly signed.
    /// </summary>
    /// <remarks>
    /// The stub is built for <see cref="WebAuthnCeremony.Authentication"/>, which is the pool an
    /// <b>anonymous</b> endpoint mints. A gate that checked <c>ConsumeAsync</c> for a non-null answer
    /// rather than for <c>Reauthentication</c> passes every other test in this file and fails this one.
    /// </remarks>
    [Test]
    public async Task HandleAsync_OnAChallengeIssuedForAnotherCeremony_ThrowsAndErasesNothing()
    {
        // Arrange
        Fixture fixture = Fixture.Build(ceremony: WebAuthnCeremony.Authentication);

        // Act
        await ThrowsRefusalAsync(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(fixture.Users.Users.Count).IsEqualTo(1);
        await Assert.That(fixture.Users.DeleteCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WhenTheChallengeIsNotLive_ThrowsAndErasesNothing()
    {
        // Arrange — the store holds bytes the device never signed, which is how it answers null.
        Fixture fixture = Fixture.Build(challengeIsLive: false);

        // Act
        await ThrowsRefusalAsync(() => fixture.Handler.HandleAsync(fixture.Command));

        // Assert
        await Assert.That(fixture.Users.Users.Count).IsEqualTo(1);
        await Assert.That(fixture.Users.DeleteCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// The provable-fail control for the three refusals above.
    /// </summary>
    /// <remarks>
    /// Without it a handler that threw at every call — or one whose gate refused everything — passes
    /// all three. The seeded account is asserted present before the act so that "no rows left" cannot
    /// be satisfied by a fixture that seeded none.
    /// </remarks>
    [Test]
    public async Task HandleAsync_WithAValidAssertion_ErasesTheAccount()
    {
        // Arrange
        Fixture fixture = Fixture.Build();
        await Assert.That(fixture.Users.Users.Count).IsEqualTo(1);

        // Act
        await fixture.Handler.HandleAsync(fixture.Command);

        // Assert
        await Assert.That(fixture.Users.Users.Count).IsEqualTo(0);
        await Assert.That(fixture.Users.DeleteCallCount).IsEqualTo(1);
    }

    /// <summary>
    /// Runs an erasure that is expected to be refused and hands back the refusal.
    /// </summary>
    private static async Task<PasskeyVerificationException> ThrowsRefusalAsync(Func<Task> erasure)
    {
        try
        {
            await erasure();
        }
        catch (PasskeyVerificationException refusal)
        {
            return refusal;
        }

        throw new InvalidOperationException("Expected the erasure to be refused.");
    }

    private const string RelyingPartyId = "localhost";
    private const string Origin = "https://localhost:4200";
    private const int ChallengeBytes = 32;

    /// <summary>
    /// The handler, the gate it erases through, and the collaborators behind both — assembled once so
    /// no test has to restate a six-argument constructor.
    /// </summary>
    private sealed record Fixture(
        EraseAccountHandler Handler,
        EraseAccountCommand Command,
        InMemoryTransactionRepository Transactions,
        InMemoryUserRepository Users,
        InMemoryPasskeyRepository Passkeys,
        StubWebAuthnChallengeStore Challenges,
        RecordingPersistenceState PersistenceState,
        Guid UserId,
        Guid BudgetId)
    {
        /// <summary>
        /// Fixed instant for every seeded row, so nothing in this file depends on the wall clock.
        /// </summary>
        public static readonly DateTime UtcNow = new(2026, 6, 25, 13, 14, 15, DateTimeKind.Utc);

        /// <summary>
        /// Builds the handler over fresh fakes, with a real device holding a real key pair and a real
        /// signature over the challenge the store was handed.
        /// </summary>
        /// <param name="seedUser">Whether the account exists before the erasure runs.</param>
        /// <param name="attempts">
        /// How many times the executor runs the unit of work. One is the ordinary case and is what
        /// every outcome test wants; more than one is what the discard-placement and replay tests
        /// need, and it is a parameter rather than a second fixture so that the two share one wiring.
        /// </param>
        /// <param name="ceremony">Which pool the store says the nonce was drawn from.</param>
        /// <param name="challengeIsLive">
        /// Whether the store holds the bytes the device signed. False leaves it holding a different
        /// nonce, which is how a store answers null without a second fake.
        /// </param>
        /// <param name="passkeyBelongsToAnotherAccount">
        /// Whether the registered passkey is filed under somebody other than the account the request
        /// authenticates as. The assertion is otherwise identical, and carries no user handle, so the
        /// owner-scoped lookup is the only thing that can refuse it.
        /// </param>
        public static Fixture Build(
            bool seedUser = true,
            int attempts = 1,
            WebAuthnCeremony ceremony = WebAuthnCeremony.Reauthentication,
            bool challengeIsLive = true,
            bool passkeyBelongsToAnotherAccount = false)
        {
            Guid budgetId = Guid.CreateVersion7();
            InMemoryTransactionRepository transactions = new();
            InMemoryUserRepository users = new(transactions);

            User user = User.Create("person@example.com", UtcNow);
            if (seedUser)
            {
                users.Seed(user, Credential.CreateFederated(
                    user.Id, Credential.GoogleProvider, "google-erasing", UtcNow));
            }

            // The passkey is filed under whoever owns the device, which is the request's own account
            // unless a test says otherwise. Nothing else about the ceremony changes with it.
            Guid passkeyOwnerId = passkeyBelongsToAnotherAccount ? Guid.CreateVersion7() : user.Id;
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

            // No user handle, and the reason is not that the handle check would get there first — it
            // would not. PasskeyReauthentication runs the owner-scoped lookup at step 4 and the
            // handle check at step 5, so a stranger's credential is already refused by the lookup
            // whatever the handle says; a handle is tolerated when absent and only ever narrows.
            // Omitting it is what keeps this refusal attributable to one thing: with the account's
            // own handle present, a lookup that had lost its owner filter would still be turned down
            // by the check below it, and this test would stay green over a gate with no binding left.
            AssertionResult assertion = device.Authenticate(signedChallenge, Origin, userHandle: null);

            StubUserContext userContext = new(user.Id);

            // A replaying executor even at one attempt, rather than InMemoryTransactionalExecutor:
            // at one attempt the two are behaviourally identical, and going through this one is what
            // gives the persistence state an attempt number to record instead of a constant.
            RetryingTransactionalExecutor executor = new(attempts);

            // The gate materialises a public key and a counter on the same scoped context before the
            // delegate runs, so the discard has to sweep them too — modelled here rather than assumed.
            RecordingPersistenceState persistenceState = new(
                () => executor.Attempts,
                passkeys.DiscardTrackedEntities);

            EraseAccountHandler handler = new(
                transactions,
                users,
                userContext,
                persistenceState,
                executor,
                new PasskeyReauthentication(
                    challenges,
                    passkeys,
                    userContext,
                    new StubPasskeyCeremonyPolicy(RelyingPartyId, Origin)));

            return new Fixture(
                handler,
                new EraseAccountCommand(new ReauthenticationAssertion(
                    assertion.CredentialIdBase64Url,
                    assertion.ClientDataJsonBase64Url,
                    assertion.AuthenticatorDataBase64Url,
                    assertion.SignatureBase64Url,
                    assertion.UserHandleBase64Url)),
                transactions,
                users,
                passkeys,
                challenges,
                persistenceState,
                user.Id,
                budgetId);
        }
    }
}
