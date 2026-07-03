using Application.Users.EnsureUser;
using Domain.Users;
using Microsoft.Extensions.Time.Testing;

namespace UnitTests;

public sealed class EnsureUserHandlerTests
{
    [Test]
    public async Task ExistingSubject_WithUnchangedProfile_DoesNotSaveChanges()
    {
        User user = User.Create("google-1", "person@example.com", "Person", UtcNow());
        var repository = new InMemoryUserRepository(user);
        var handler = new EnsureUserHandler(repository, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        Guid userId = await handler.HandleAsync(new EnsureUserCommand("google-1", " person@example.com ", " Person "));

        await Assert.That(userId).IsEqualTo(user.Id);
        await Assert.That(repository.UpdateProfileCalls).IsEqualTo(0);
    }

    [Test]
    public async Task ExistingSubject_WithChangedProfile_SavesOnceAndRefreshesProfile()
    {
        User user = User.Create("google-1", "old@example.com", "Old", UtcNow());
        var repository = new InMemoryUserRepository(user);
        var handler = new EnsureUserHandler(repository, new FakeTimeProvider(new DateTimeOffset(UtcNow())));

        Guid userId = await handler.HandleAsync(new EnsureUserCommand("google-1", "new@example.com", "New"));

        await Assert.That(userId).IsEqualTo(user.Id);
        await Assert.That(repository.UpdateProfileCalls).IsEqualTo(1);
        await Assert.That(user.Email.Value).IsEqualTo("new@example.com");
        await Assert.That(user.DisplayName).IsEqualTo("New");
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
