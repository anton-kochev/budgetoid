using Application.Users.EnsureUser;
using Domain.Budgets;
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
        await Assert.That(budget.Name).IsEqualTo(Budget.DefaultName);
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
        await Assert.That(budget.Name).IsEqualTo(Budget.DefaultName);
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

    private static DateTime UtcNow() => new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private sealed class InMemoryUserRepository(User? existingUser = null) : IUserRepository
    {
        public int UpdateProfileCalls { get; private set; }

        public Task<User?> FindByGoogleSubjectAsync(string googleSubject, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(existingUser?.GoogleSubject == googleSubject.Trim() ? existingUser : null);
        }

        public Task<bool> TryAddAsync(User user, CancellationToken cancellationToken = default)
        {
            existingUser = user;
            return Task.FromResult(true);
        }

        public Task UpdateProfileAsync(User user, CancellationToken cancellationToken = default)
        {
            UpdateProfileCalls++;
            return Task.CompletedTask;
        }
    }
}
