using Application.Accounts.DeleteAccount;
using Domain.Accounts;
using Domain.Common;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class DeleteAccountHandlerTests
{
    [Test]
    public async Task HandleAsync_DeletesUnreferencedAccount()
    {
        var userId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(userId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Checking");
        var handler = new DeleteAccountHandler(repository);

        await handler.HandleAsync(new DeleteAccountCommand(account.Id));

        await Assert.That(repository.DeleteCallCount).IsEqualTo(1);
        await Assert.That(await repository.GetByIdAsync(account.Id)).IsNull();
    }

    [Test]
    public async Task HandleAsync_WithUnknownAccountId_ThrowsNotFoundException()
    {
        var repository = new InMemoryAccountRepository(Guid.CreateVersion7(), new FakeTimeProvider(UtcNowOffset()));
        var handler = new DeleteAccountHandler(repository);

        try
        {
            await handler.HandleAsync(new DeleteAccountCommand(Guid.CreateVersion7()));
        }
        catch (NotFoundException)
        {
            await Assert.That(repository.DeleteCallCount).IsEqualTo(0);
            return;
        }

        throw new InvalidOperationException("Expected NotFoundException.");
    }

    [Test]
    public async Task HandleAsync_WithReferencedAccount_ThrowsValidationExceptionAndDoesNotDelete()
    {
        var userId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(userId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Checking");
        repository.MarkReferenced(account.Id);
        var handler = new DeleteAccountHandler(repository);

        try
        {
            await handler.HandleAsync(new DeleteAccountCommand(account.Id));
        }
        catch (ValidationException exception)
        {
            await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
            await Assert.That(repository.DeleteCallCount).IsEqualTo(0);
            await Assert.That(await repository.GetByIdAsync(account.Id)).IsNotNull();
            return;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }

    private static DateTimeOffset UtcNowOffset() => new(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
}
