using Application.Users.EnsureUser;
using Domain.Budgets;
using Domain.Common;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class EnsureUserHandlerTests
{
    [Test]
    public async Task EnsureUser_ReturningUserWhoseProviderEmailChanged_KeepsTheRegisteredEmail()
    {
        // Arrange — the provider now reports a different email than the one registration captured.
        // It gates registration and is never consulted again, so what it reports later is not
        // authority to change anything: an email change is a separate exchange the user deliberately
        // initiates.
        User user = User.Create("old@example.com", UtcNow());
        var users = new InMemoryUserRepository(user, GoogleCredentialFor(user, "google-1"));
        var budgets = new InMemoryBudgetRepository();
        var handler = new EnsureUserHandler(
            users, budgets, new RecordingUserContextWriter(),
            new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "new@example.com"));

        // Assert — the sign-in resolves to the same account, and that account still holds the email
        // it registered with. Reintroducing a silent per-request refresh fails this line.
        // The stored row is read off the fake rather than through the repository, because discovery
        // now answers with an id and nothing else — by design, since that lookup runs on a session
        // that has no identity yet and must touch no policed table. The claim under test is unchanged:
        // what the users table holds is still what registration wrote.
        await Assert.That(provisioned.UserId).IsEqualTo(user.Id);
        User stored = users.StoredUsers[provisioned.UserId];
        await Assert.That(stored.Email.Value).IsEqualTo("old@example.com");
    }

    [Test]
    public async Task EnsureUser_NewSubject_CreatesExactlyOneDefaultBudgetOwnedByTheUser()
    {
        // Arrange
        var users = new InMemoryUserRepository();
        var budgets = new InMemoryBudgetRepository();
        var handler = new EnsureUserHandler(
            users, budgets, new RecordingUserContextWriter(),
            new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-new", "new@example.com"));

        // Assert
        await Assert.That(budgets.Budgets.Count).IsEqualTo(1);
        Budget budget = budgets.Budgets[0];
        await Assert.That(budget.UserId).IsEqualTo(provisioned.UserId);
        await Assert.That(budget.Name).IsNull();
        await Assert.That(budget.BaseCurrencyCode).IsNull();
        await Assert.That(budget.CreatedAtUtc).IsEqualTo(UtcNow());
        await Assert.That(provisioned.BudgetId).IsEqualTo(budget.Id);
    }

    [Test]
    public async Task EnsureUser_NewSubject_StoresExactlyOneFederatedGoogleCredential()
    {
        // Arrange
        var users = new InMemoryUserRepository();
        var budgets = new InMemoryBudgetRepository();
        var handler = new EnsureUserHandler(
            users, budgets, new RecordingUserContextWriter(),
            new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-new", "new@example.com"));

        // Assert — the subject reaches the credential row and nothing else, which is what keeps a
        // second sign-in method addable later without touching the user row.
        await Assert.That(users.AddedCredentials.Count).IsEqualTo(1);
        Credential credential = users.AddedCredentials[0];
        await Assert.That(credential.UserId).IsEqualTo(provisioned.UserId);
        await Assert.That(credential.Type).IsEqualTo(CredentialType.Federated);
        await Assert.That(credential.Provider).IsEqualTo(Credential.GoogleProvider);
        await Assert.That(credential.Subject).IsEqualTo("google-new");
    }

    [Test]
    public async Task EnsureUser_ExistingCredentialWithABudget_DoesNotCreateAnother()
    {
        // Arrange
        User user = User.Create("person@example.com", UtcNow());
        var users = new InMemoryUserRepository(user, GoogleCredentialFor(user, "google-1"));
        var budgets = new InMemoryBudgetRepository();
        Budget existingBudget = Budget.CreateDefault(user.Id, UtcNow());
        budgets.Seed(existingBudget);
        var handler = new EnsureUserHandler(
            users, budgets, new RecordingUserContextWriter(),
            new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser first = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));
        ProvisionedUser second = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));

        // Assert
        await Assert.That(budgets.Budgets.Count).IsEqualTo(1);
        await Assert.That(budgets.AddCallCount).IsEqualTo(0);
        await Assert.That(first.BudgetId).IsEqualTo(existingBudget.Id);
        await Assert.That(second.BudgetId).IsEqualTo(existingBudget.Id);
    }

    [Test]
    public async Task EnsureUser_ExistingCredentialWithoutABudget_CreatesTheMissingBudget()
    {
        // Arrange — a user row from a partially completed provisioning, with no budget yet.
        User user = User.Create("person@example.com", UtcNow());
        var users = new InMemoryUserRepository(user, GoogleCredentialFor(user, "google-1"));
        var budgets = new InMemoryBudgetRepository();
        var handler = new EnsureUserHandler(
            users, budgets, new RecordingUserContextWriter(),
            new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));

        // Assert
        await Assert.That(provisioned.UserId).IsEqualTo(user.Id);
        await Assert.That(budgets.Budgets.Count).IsEqualTo(1);
        Budget budget = budgets.Budgets[0];
        await Assert.That(budget.UserId).IsEqualTo(user.Id);
        await Assert.That(budget.Name).IsNull();
        await Assert.That(provisioned.BudgetId).IsEqualTo(budget.Id);
    }

    [Test]
    public async Task EnsureUser_WhenBudgetInsertLosesTheRace_ReturnsTheConcurrentlyCreatedBudget()
    {
        // Arrange — a concurrent request inserts the default budget between our read and our write.
        User user = User.Create("person@example.com", UtcNow());
        var users = new InMemoryUserRepository(user, GoogleCredentialFor(user, "google-1"));
        var budgets = new InMemoryBudgetRepository();
        Budget concurrentBudget = Budget.CreateDefault(user.Id, UtcNow());
        budgets.FailNextAdd(concurrentBudget);
        var handler = new EnsureUserHandler(
            users, budgets, new RecordingUserContextWriter(),
            new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));

        // Assert
        await Assert.That(provisioned.UserId).IsEqualTo(user.Id);
        await Assert.That(provisioned.BudgetId).IsEqualTo(concurrentBudget.Id);
        await Assert.That(budgets.Budgets.Count).IsEqualTo(1);
        await Assert.That(budgets.AddCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureUser_WhenUserInsertLosesTheCredentialRace_ReturnsTheConcurrentlyCreatedUser()
    {
        // Arrange — a concurrent request inserts a credential on the same provider and subject
        // between our read and our write, which the repository reports as false rather than as a
        // throw.
        var users = new InMemoryUserRepository();
        User concurrentUser = User.Create("person@example.com", UtcNow());
        users.FailNextAddWithCredentialRace(concurrentUser, GoogleCredentialFor(concurrentUser, "google-1"));
        var budgets = new InMemoryBudgetRepository();
        var handler = new EnsureUserHandler(
            users, budgets, new RecordingUserContextWriter(),
            new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));

        // Assert
        await Assert.That(provisioned.UserId).IsEqualTo(concurrentUser.Id);
        await Assert.That(users.AddCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureUser_NewSubjectWithAnEmailAnotherAccountHolds_ThrowsConflictException()
    {
        // Arrange — a brand-new subject whose email is already linked to a different account.
        var users = new InMemoryUserRepository();
        users.FailNextAddWithEmailConflict();
        var budgets = new InMemoryBudgetRepository();
        var handler = new EnsureUserHandler(
            users, budgets, new RecordingUserContextWriter(),
            new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act — anything other than ConflictException escapes this helper and fails the test. The
        // insert is refused and the re-read by credential finds nothing, so this is not a race that
        // path can resolve — and that empty re-read is precisely where the exception now comes from,
        // in place of the InvalidOperationException the branch used to raise.
        ConflictException exception = await ThrowsConflictExceptionAsync(() =>
            handler.HandleAsync(new EnsureUserCommand("google-new", "taken@example.com")));

        // Assert
        await Assert.That(exception.Message).IsNotEmpty();
        await Assert.That(users.AddCallCount).IsEqualTo(1);
        await Assert.That(budgets.Budgets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EnsureUser_ExistingSubject_PublishesTheResolvedUserId()
    {
        // Arrange — a returning sign-in, where the credential row already names an account.
        User user = User.Create("person@example.com", UtcNow());
        var users = new InMemoryUserRepository(user, GoogleCredentialFor(user, "google-1"));
        var budgets = new InMemoryBudgetRepository();
        var writer = new RecordingUserContextWriter();
        var handler = new EnsureUserHandler(
            users, budgets, writer,
            new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));

        // Assert — exactly one publication, carrying the id the credential resolved. The budgets read
        // that follows is policed on app.current_user_id, so a handler that resolved the id and kept
        // it to itself would find no budget for an account that has one and provision a second.
        await Assert.That(writer.Published.Count).IsEqualTo(1);
        await Assert.That(writer.Published[0]).IsEqualTo(user.Id);
        await Assert.That(writer.Published[0]).IsEqualTo(provisioned.UserId);
    }

    [Test]
    public async Task EnsureUser_NewSubject_PublishesTheNewUserIdBeforeTheUserIsWritten()
    {
        // Arrange — the repository is armed to snapshot the writer as TryAddAsync is entered. Ordering
        // is the entire claim, and User.Create mints the id with Guid.CreateVersion7 client-side, so
        // the id genuinely exists before the row does and nothing stops the publication preceding the
        // insert.
        var users = new InMemoryUserRepository();
        var budgets = new InMemoryBudgetRepository();
        var writer = new RecordingUserContextWriter();
        users.ObservePublicationsDuring(writer);
        var handler = new EnsureUserHandler(
            users, budgets, writer,
            new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-new", "new@example.com"));

        // Assert — the id was already published when the insert began, and it is the id being
        // inserted. Publish it afterwards and the users INSERT runs on a session with no
        // app.current_user_id, so WITH CHECK (id = current_user_id) refuses the row and registration
        // is broken for every new account — while a test that read the writer only at the end would
        // still be green, which is exactly why the snapshot is taken from inside the call.
        await Assert.That(users.PublishedWhenAddWasEntered.Count).IsEqualTo(1);
        await Assert.That(users.PublishedWhenAddWasEntered[0]).IsEqualTo(provisioned.UserId);
        await Assert.That(users.AddedCredentials[0].UserId).IsEqualTo(provisioned.UserId);
    }

    [Test]
    public async Task EnsureUser_LostTheRace_PublishesTheWinningUserId()
    {
        // Arrange — the same lost credential race the returns-the-concurrent-user test above drives,
        // read from the publication side rather than the return value.
        var users = new InMemoryUserRepository();
        User concurrentUser = User.Create("person@example.com", UtcNow());
        users.FailNextAddWithCredentialRace(concurrentUser, GoogleCredentialFor(concurrentUser, "google-1"));
        var budgets = new InMemoryBudgetRepository();
        var writer = new RecordingUserContextWriter();
        var handler = new EnsureUserHandler(
            users, budgets, writer,
            new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com"));

        // Assert — the loser published its own id before its doomed insert, so the count is not the
        // subject; what matters is who gets the last word, because the session carries that id into
        // the budgets read and on into the rest of the request. A stale loser id there polices every
        // later query against a user row that was never written. Pinning the last publication to
        // ProvisionedUser.UserId as well as to the winner states the rule that outlives this
        // particular race: the caller is never told one account while the session holds another.
        await Assert.That(writer.Published[^1]).IsEqualTo(concurrentUser.Id);
        await Assert.That(writer.Published[^1]).IsEqualTo(provisioned.UserId);
    }

    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private static Credential GoogleCredentialFor(User user, string subject) =>
        Credential.CreateFederated(user.Id, Credential.GoogleProvider, subject, UtcNow());

    private static async Task<ConflictException> ThrowsConflictExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ConflictException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ConflictException.");
    }

    /// <summary>
    /// In-memory <see cref="IUserRepository"/> that reproduces the two ways the write can be refused,
    /// both of them reported as <see langword="false"/>: a lost race on the unique
    /// <c>(provider, subject)</c> credential rule, and an email already linked to another account.
    /// The two are deliberately indistinguishable from the outside — what separates them is whether a
    /// row is then there to be re-read, which is the caller's question to ask.
    /// </summary>
    private sealed class InMemoryUserRepository : IUserRepository
    {
        private readonly List<Credential> _addedCredentials = [];
        private readonly Dictionary<Guid, User> _storedUsers = [];
        private List<Guid> _publishedWhenAddWasEntered = [];
        private RecordingUserContextWriter? _observedWriter;
        private User? _existingUser;
        private Credential? _existingCredential;
        private bool _failNextAddWithCredentialRace;
        private User? _raceWinner;
        private Credential? _raceWinnerCredential;
        private bool _failNextAddWithEmailConflict;

        public InMemoryUserRepository(User? existingUser = null, Credential? existingCredential = null)
        {
            _existingUser = existingUser;
            _existingCredential = existingCredential;

            if (existingUser is not null)
            {
                _storedUsers[existingUser.Id] = existingUser;
            }
        }

        public int AddCallCount { get; private set; }

        /// <summary>Every credential handed to <see cref="TryAddAsync"/>, refused calls included.</summary>
        public IReadOnlyList<Credential> AddedCredentials => _addedCredentials;

        /// <summary>
        /// Every user row this repository holds, by id. Discovery no longer hands back a user, so this
        /// is the only way left to ask what was actually stored — and it is a fair question for a test
        /// to ask, because the row is what a later request reads.
        /// </summary>
        public IReadOnlyDictionary<Guid, User> StoredUsers => _storedUsers;

        /// <summary>
        /// What <paramref name="writer"/> had published at the instant <see cref="TryAddAsync"/> was
        /// entered.
        /// </summary>
        /// <remarks>
        /// The snapshot has to be taken from inside the call because the ordering is the claim. Read
        /// once the handler has returned, a handler that publishes before its insert and one that
        /// publishes after are indistinguishable — both end with the same list — yet only the first
        /// survives the <c>WITH CHECK</c> on the users INSERT.
        /// </remarks>
        public void ObservePublicationsDuring(RecordingUserContextWriter writer) => _observedWriter = writer;

        /// <summary>
        /// The snapshot <see cref="ObservePublicationsDuring"/> arms. Empty when nothing was observed.
        /// </summary>
        public IReadOnlyList<Guid> PublishedWhenAddWasEntered => _publishedWhenAddWasEntered;

        /// <summary>
        /// Makes the next <see cref="TryAddAsync"/> call report a lost race on the unique
        /// <c>(provider, subject)</c> rule. The winning pair is stored instead, which is what makes
        /// the caller's re-read path observable.
        /// </summary>
        public void FailNextAddWithCredentialRace(User winner, Credential winnerCredential)
        {
            _failNextAddWithCredentialRace = true;
            _raceWinner = winner;
            _raceWinnerCredential = winnerCredential;
        }

        /// <summary>
        /// Makes the next <see cref="TryAddAsync"/> call report a refused insert because another
        /// account already holds that email. It reports <see langword="false"/> like any other
        /// refusal, and stores nothing — the absent row is the only difference from a lost race, and
        /// the caller's re-read is what finds it.
        /// </summary>
        public void FailNextAddWithEmailConflict() => _failNextAddWithEmailConflict = true;

        // Answers from the credential alone, which is not a simplification of the fake but the
        // contract: the real query may not join users, because discovery runs before the session
        // carries an identity and users is policed. Returning _existingUser?.Id here would let a
        // production query that still joined pass this suite.
        public Task<Guid?> FindUserIdByFederatedCredentialAsync(
            string provider,
            string subject,
            CancellationToken cancellationToken = default)
        {
            bool matches = _existingCredential is not null
                           && _existingCredential.Provider == provider.Trim()
                           && _existingCredential.Subject == subject.Trim();

            return Task.FromResult<Guid?>(matches ? _existingCredential!.UserId : null);
        }

        public Task<bool> TryAddAsync(User user, Credential credential, CancellationToken cancellationToken = default)
        {
            AddCallCount++;
            _addedCredentials.Add(credential);

            // Before anything else in this method: a snapshot taken after the store mutated would be
            // measuring the fake, not the handler.
            if (_observedWriter is not null)
            {
                _publishedWhenAddWasEntered = [.. _observedWriter.Published];
            }

            if (_failNextAddWithEmailConflict)
            {
                _failNextAddWithEmailConflict = false;

                // Nothing is stored, on purpose: the handler's follow-up
                // FindUserIdByFederatedCredentialAsync has to come back with no id, because that
                // empty re-read is the signal it keys on.
                return Task.FromResult(false);
            }

            if (_failNextAddWithCredentialRace)
            {
                _failNextAddWithCredentialRace = false;
                _existingUser = _raceWinner;
                _existingCredential = _raceWinnerCredential;

                if (_raceWinner is not null)
                {
                    _storedUsers[_raceWinner.Id] = _raceWinner;
                }

                _raceWinner = null;
                _raceWinnerCredential = null;
                return Task.FromResult(false);
            }

            _existingUser = user;
            _existingCredential = credential;
            _storedUsers[user.Id] = user;
            return Task.FromResult(true);
        }

        /// <summary>
        /// Drops the row and, with it, the credential that resolved to it — the cascade the real
        /// delete relies on, in the only shape this fake can hold. A row that is already gone is not
        /// an error, as the interface documents: the call states a post-condition rather than acting
        /// on a row.
        /// </summary>
        /// <remarks>
        /// No erasure test drives this fake — erasure has its own in <c>UnitTests.Fakes</c>, which
        /// carries the RESTRICT rule this one has no collaborator to model. It is implemented in
        /// state rather than as a throw so that the fake stays internally consistent: leaving
        /// <see cref="FindUserIdByFederatedCredentialAsync"/> resolving an id whose row was deleted
        /// would be a behaviour the real repository cannot produce.
        /// </remarks>
        public Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            _storedUsers.Remove(userId);

            if (_existingUser?.Id == userId)
            {
                _existingUser = null;
                _existingCredential = null;
            }

            return Task.CompletedTask;
        }
    }
}
