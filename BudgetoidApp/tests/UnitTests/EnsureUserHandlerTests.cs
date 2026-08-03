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
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "new@example.com"));

        // Assert — the sign-in resolves to the same account, and that account still holds the email
        // it registered with. Reintroducing a silent per-request refresh fails this line.
        await Assert.That(provisioned.UserId).IsEqualTo(user.Id);
        User stored = (await users.FindByFederatedCredentialAsync(Credential.GoogleProvider, "google-1"))!;
        await Assert.That(stored.Email.Value).IsEqualTo("old@example.com");
    }

    [Test]
    public async Task EnsureUser_NewSubject_CreatesExactlyOneDefaultBudgetOwnedByTheUser()
    {
        // Arrange
        var users = new InMemoryUserRepository();
        var budgets = new InMemoryBudgetRepository();
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

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
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

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
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

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
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

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
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

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
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

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
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

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
        }

        public int AddCallCount { get; private set; }

        /// <summary>Every credential handed to <see cref="TryAddAsync"/>, refused calls included.</summary>
        public IReadOnlyList<Credential> AddedCredentials => _addedCredentials;

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

        public Task<User?> FindByFederatedCredentialAsync(
            string provider,
            string subject,
            CancellationToken cancellationToken = default)
        {
            bool matches = _existingCredential is not null
                           && _existingCredential.Provider == provider.Trim()
                           && _existingCredential.Subject == subject.Trim();

            return Task.FromResult(matches ? _existingUser : null);
        }

        public Task<bool> TryAddAsync(User user, Credential credential, CancellationToken cancellationToken = default)
        {
            AddCallCount++;
            _addedCredentials.Add(credential);

            if (_failNextAddWithEmailConflict)
            {
                _failNextAddWithEmailConflict = false;

                // Nothing is stored, on purpose: the handler's follow-up
                // FindByFederatedCredentialAsync has to come back empty, because that empty re-read
                // is the signal it keys on.
                return Task.FromResult(false);
            }

            if (_failNextAddWithCredentialRace)
            {
                _failNextAddWithCredentialRace = false;
                _existingUser = _raceWinner;
                _existingCredential = _raceWinnerCredential;
                _raceWinner = null;
                _raceWinnerCredential = null;
                return Task.FromResult(false);
            }

            _existingUser = user;
            _existingCredential = credential;
            return Task.FromResult(true);
        }
    }
}
