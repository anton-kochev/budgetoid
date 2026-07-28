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
    public async Task ExistingSubject_WithUnchangedProfile_DoesNotSaveChanges()
    {
        // Arrange
        User user = User.Create("google-1", "person@example.com", "Person", UtcNow());
        var users = new InMemoryUserRepository(user);
        var budgets = new InMemoryBudgetRepository();
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", " person@example.com ", " Person "));

        // Assert
        await Assert.That(provisioned.UserId).IsEqualTo(user.Id);
        await Assert.That(users.UpdateProfileCalls).IsEqualTo(0);
    }

    [Test]
    public async Task ExistingSubject_WithChangedProfile_SavesOnceAndRefreshesProfile()
    {
        // Arrange
        User user = User.Create("google-1", "old@example.com", "Old", UtcNow());
        var users = new InMemoryUserRepository(user);
        var budgets = new InMemoryBudgetRepository();
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "new@example.com", "New"));

        // Assert
        await Assert.That(provisioned.UserId).IsEqualTo(user.Id);
        await Assert.That(users.UpdateProfileCalls).IsEqualTo(1);
        await Assert.That(user.Email.Value).IsEqualTo("new@example.com");
        await Assert.That(user.DisplayName).IsEqualTo("New");
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
            new EnsureUserCommand("google-new", "new@example.com", "New Person"));

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
    public async Task EnsureUser_ExistingSubjectWithABudget_DoesNotCreateAnother()
    {
        // Arrange
        User user = User.Create("google-1", "person@example.com", "Person", UtcNow());
        var users = new InMemoryUserRepository(user);
        var budgets = new InMemoryBudgetRepository();
        Budget existingBudget = Budget.CreateDefault(user.Id, UtcNow());
        budgets.Seed(existingBudget);
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser first = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com", "Person"));
        ProvisionedUser second = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com", "Person"));

        // Assert
        await Assert.That(budgets.Budgets.Count).IsEqualTo(1);
        await Assert.That(budgets.AddCallCount).IsEqualTo(0);
        await Assert.That(first.BudgetId).IsEqualTo(existingBudget.Id);
        await Assert.That(second.BudgetId).IsEqualTo(existingBudget.Id);
    }

    [Test]
    public async Task EnsureUser_ExistingSubjectWithoutABudget_CreatesTheMissingBudget()
    {
        // Arrange — a user row from a partially completed provisioning, with no budget yet.
        User user = User.Create("google-1", "person@example.com", "Person", UtcNow());
        var users = new InMemoryUserRepository(user);
        var budgets = new InMemoryBudgetRepository();
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com", "Person"));

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
        User user = User.Create("google-1", "person@example.com", "Person", UtcNow());
        var users = new InMemoryUserRepository(user);
        var budgets = new InMemoryBudgetRepository();
        Budget concurrentBudget = Budget.CreateDefault(user.Id, UtcNow());
        budgets.FailNextAdd(concurrentBudget);
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com", "Person"));

        // Assert
        await Assert.That(provisioned.UserId).IsEqualTo(user.Id);
        await Assert.That(provisioned.BudgetId).IsEqualTo(concurrentBudget.Id);
        await Assert.That(budgets.Budgets.Count).IsEqualTo(1);
        await Assert.That(budgets.AddCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureUser_WhenUserInsertLosesTheSubjectRace_ReturnsTheConcurrentlyCreatedUser()
    {
        // Arrange — a concurrent request inserts the same google_subject between our read and our
        // write, which the repository reports as false rather than as a throw.
        var users = new InMemoryUserRepository();
        User concurrentUser = User.Create("google-1", "person@example.com", "Person", UtcNow());
        users.FailNextAddWithSubjectRace(concurrentUser);
        var budgets = new InMemoryBudgetRepository();
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "person@example.com", "Person"));

        // Assert
        await Assert.That(provisioned.UserId).IsEqualTo(concurrentUser.Id);
        await Assert.That(users.AddCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureUser_NewSubjectWithAnEmailAnotherAccountHolds_ThrowsConflictException()
    {
        // Arrange — a brand-new google_subject whose email is already linked to a different account.
        var users = new InMemoryUserRepository();
        users.FailNextAddWithEmailConflict();
        var budgets = new InMemoryBudgetRepository();
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act — anything other than ConflictException escapes this helper and fails the test. The
        // insert is refused and the re-read by subject finds nothing, so this is not a race that
        // path can resolve — and that empty re-read is precisely where the exception now comes from,
        // in place of the InvalidOperationException the branch used to raise.
        ConflictException exception = await ThrowsConflictExceptionAsync(() =>
            handler.HandleAsync(new EnsureUserCommand("google-new", "taken@example.com", "New Person")));

        // Assert
        await Assert.That(exception.Message).IsNotEmpty();
        await Assert.That(users.AddCallCount).IsEqualTo(1);
        await Assert.That(budgets.Budgets.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EnsureUser_ExistingSubjectWhoseRefreshedEmailCollides_SignsInOnTheStoredEmail()
    {
        // Arrange — the IdP now reports an email another user already holds. Identity is the
        // google_subject, so this must not fail the sign-in.
        User user = User.Create("google-1", "stored@example.com", "Stored", UtcNow());
        var users = new InMemoryUserRepository(user);
        users.RejectNextProfileUpdate();
        var budgets = new InMemoryBudgetRepository();
        var handler = new EnsureUserHandler(users, budgets, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        // Act
        ProvisionedUser provisioned = await handler.HandleAsync(
            new EnsureUserCommand("google-1", "taken@example.com", "Fresh"));

        // Assert — the session continues, and it continues on the stored (stale) profile.
        await Assert.That(provisioned.UserId).IsEqualTo(user.Id);
        await Assert.That(provisioned.BudgetId).IsNotEqualTo(Guid.Empty);
        await Assert.That(users.UpdateProfileCalls).IsEqualTo(1);
        await Assert.That(user.Email.Value).IsEqualTo("stored@example.com");
        await Assert.That(user.DisplayName).IsEqualTo("Stored");
    }

    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

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
    /// In-memory <see cref="IUserRepository"/> that reproduces the three ways the users table can
    /// refuse a write, all of them reported as <see langword="false"/>: a lost race on the unique
    /// <c>google_subject</c>, an email already linked to another account on insert, and the same
    /// email collision on a profile refresh. The two insert refusals are deliberately
    /// indistinguishable from the outside — what separates them is whether a row is then there to be
    /// re-read, which is the caller's question to ask. The rejected refresh additionally discards
    /// the change from the caller's own instance, the way <c>ReloadAsync</c> does.
    /// </summary>
    private sealed class InMemoryUserRepository : IUserRepository
    {
        private User? _existingUser;
        private string _persistedEmail = string.Empty;
        private string? _persistedDisplayName;
        private bool _failNextAddWithSubjectRace;
        private User? _raceWinner;
        private bool _failNextAddWithEmailConflict;
        private bool _rejectNextProfileUpdate;

        public InMemoryUserRepository(User? existingUser = null)
        {
            _existingUser = existingUser;
            CapturePersistedProfile();
        }

        public int UpdateProfileCalls { get; private set; }
        public int AddCallCount { get; private set; }

        /// <summary>
        /// Makes the next <see cref="TryAddAsync"/> call report a lost <c>google_subject</c> race.
        /// The winning row is stored instead, which is what makes the caller's re-read path
        /// observable.
        /// </summary>
        public void FailNextAddWithSubjectRace(User insertedByConcurrentRequest)
        {
            _failNextAddWithSubjectRace = true;
            _raceWinner = insertedByConcurrentRequest;
        }

        /// <summary>
        /// Makes the next <see cref="TryAddAsync"/> call report a refused insert because another
        /// account already holds that email. It reports <see langword="false"/> like any other
        /// refusal, and stores nothing — the absent row is the only difference from a lost race, and
        /// the caller's re-read is what finds it.
        /// </summary>
        public void FailNextAddWithEmailConflict() => _failNextAddWithEmailConflict = true;

        /// <summary>
        /// Makes the next <see cref="UpdateProfileAsync"/> call reject the refresh because another
        /// user holds the new email.
        /// </summary>
        public void RejectNextProfileUpdate() => _rejectNextProfileUpdate = true;

        public Task<User?> FindByGoogleSubjectAsync(string googleSubject, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_existingUser?.GoogleSubject == googleSubject.Trim() ? _existingUser : null);
        }

        public Task<bool> TryAddAsync(User user, CancellationToken cancellationToken = default)
        {
            AddCallCount++;

            if (_failNextAddWithEmailConflict)
            {
                _failNextAddWithEmailConflict = false;

                // Nothing is stored, on purpose: the handler's follow-up FindByGoogleSubjectAsync
                // has to come back empty, because that empty re-read is the signal it keys on.
                return Task.FromResult(false);
            }

            if (_failNextAddWithSubjectRace)
            {
                _failNextAddWithSubjectRace = false;
                _existingUser = _raceWinner;
                _raceWinner = null;
                CapturePersistedProfile();
                return Task.FromResult(false);
            }

            _existingUser = user;
            CapturePersistedProfile();
            return Task.FromResult(true);
        }

        public Task<bool> UpdateProfileAsync(User user, CancellationToken cancellationToken = default)
        {
            UpdateProfileCalls++;

            if (_rejectNextProfileUpdate)
            {
                _rejectNextProfileUpdate = false;

                // Rolling the caller's instance back is the whole contract: a fake that returned
                // false while leaving the rejected email in place would let a handler bug through.
                user.UpdateProfile(_persistedEmail, _persistedDisplayName);
                return Task.FromResult(false);
            }

            CapturePersistedProfile();
            return Task.FromResult(true);
        }

        /// <summary>
        /// Snapshots what the stored row now holds, standing in for the original values EF's change
        /// tracker keeps and <c>ReloadAsync</c> restores.
        /// </summary>
        private void CapturePersistedProfile()
        {
            _persistedEmail = _existingUser?.Email.Value ?? string.Empty;
            _persistedDisplayName = _existingUser?.DisplayName;
        }
    }
}
