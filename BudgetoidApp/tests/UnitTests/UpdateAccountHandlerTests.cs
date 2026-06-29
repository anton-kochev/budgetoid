using Application.Accounts.UpdateAccount;
using Domain.Accounts;
using Domain.Common;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class UpdateAccountHandlerTests
{
    [Test]
    public async Task HandleAsync_UpdatesExistingAccount()
    {
        var userId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(userId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Checking");
        var handler = new UpdateAccountHandler(repository);

        await handler.HandleAsync(new UpdateAccountCommand(account.Id, "  Savings  ", AccountType.Savings, 25m));

        await Assert.That(repository.UpdateCallCount).IsEqualTo(1);
        await Assert.That(account.Name).IsEqualTo("Savings");
        await Assert.That(account.Type).IsEqualTo(AccountType.Savings);
        await Assert.That(account.OpeningBalance).IsEqualTo(25m);
    }

    [Test]
    public async Task HandleAsync_WithUnknownAccountId_ThrowsNotFoundException()
    {
        var repository = new InMemoryAccountRepository(Guid.CreateVersion7(), new FakeTimeProvider(UtcNowOffset()));
        var handler = new UpdateAccountHandler(repository);

        try
        {
            await handler.HandleAsync(new UpdateAccountCommand(Guid.CreateVersion7(), "Savings", AccountType.Savings, 25m));
        }
        catch (NotFoundException)
        {
            await Assert.That(repository.UpdateCallCount).IsEqualTo(0);
            return;
        }

        throw new InvalidOperationException("Expected NotFoundException.");
    }

    private static DateTimeOffset UtcNowOffset() => new(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
}
