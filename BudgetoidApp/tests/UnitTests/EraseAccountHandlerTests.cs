using Application.Users.EraseAccount;
using Domain.Transactions;
using Domain.Users;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// The order erasure deletes in, and where it discards the tracked entities.
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
/// <c>AccountErasureEndpointTests.Delete_ForAnAccountWithCategoriesAndNoTransaction_LeavesNoneOfEither</c>.
/// </para>
/// <para>
/// The pin is an outcome, not a call-order assertion. <see cref="InMemoryUserRepository" /> carries
/// the database's own rule and refuses a delete while a transaction is still there, so a handler that
/// got the order wrong throws here for the same reason PostgreSQL would — and a handler that
/// legitimately reshapes how it reaches that order, by deleting in chunks for instance, needs no
/// change to these tests. That is why no test below counts calls to a delete-all.
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
        await fixture.Handler.HandleAsync(new EraseAccountCommand());

        // Assert — the user is gone, which is only reachable through a transactions delete that
        // happened first: the repository refuses outright otherwise.
        await Assert.That(fixture.Users.Users.Count).IsEqualTo(0);
        await Assert.That(fixture.Transactions.Transactions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WhenTheUserRowIsAlreadyGone_Completes()
    {
        // Arrange — nothing seeded at all, which is the state a retried erasure arrives in.
        Fixture fixture = Fixture.Build(seedUser: false);

        // Act
        await fixture.Handler.HandleAsync(new EraseAccountCommand());

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
        await fixture.Handler.HandleAsync(new EraseAccountCommand());

        // Assert — once per attempt, and never on attempt zero, which is what a call made before the
        // executor was entered would record.
        await Assert.That(fixture.PersistenceState.DiscardedOnAttempt).IsEquivalentTo(new[] { 1, 2 });
    }

    /// <summary>
    /// The handler and the collaborators it erases through, assembled once so no test has to restate
    /// a five-argument constructor.
    /// </summary>
    private sealed record Fixture(
        EraseAccountHandler Handler,
        InMemoryTransactionRepository Transactions,
        InMemoryUserRepository Users,
        RecordingPersistenceState PersistenceState,
        Guid UserId,
        Guid BudgetId)
    {
        /// <summary>
        /// Fixed instant for every seeded row, so nothing in this file depends on the wall clock.
        /// </summary>
        public static readonly DateTime UtcNow = new(2026, 6, 25, 13, 14, 15, DateTimeKind.Utc);

        /// <summary>
        /// Builds the handler over fresh fakes.
        /// </summary>
        /// <param name="seedUser">Whether the account exists before the erasure runs.</param>
        /// <param name="attempts">
        /// How many times the executor runs the unit of work. One is the ordinary case and is what
        /// every outcome test wants; more than one is what the discard-placement test needs, and it
        /// is a parameter rather than a second fixture so that the two share one wiring.
        /// </param>
        public static Fixture Build(bool seedUser = true, int attempts = 1)
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

            // A replaying executor even at one attempt, rather than InMemoryTransactionalExecutor:
            // at one attempt the two are behaviourally identical, and going through this one is what
            // gives the persistence state an attempt number to record instead of a constant.
            RetryingTransactionalExecutor executor = new(attempts);
            RecordingPersistenceState persistenceState = new(() => executor.Attempts);

            EraseAccountHandler handler = new(
                transactions,
                users,
                new StubUserContext(user.Id),
                persistenceState,
                executor);

            return new Fixture(handler, transactions, users, persistenceState, user.Id, budgetId);
        }
    }
}
