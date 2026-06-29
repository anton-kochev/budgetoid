using Application.Accounts.CreateAccount;
using Domain.Accounts;
using Domain.Common;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class CreateAccountHandlerTests
{
    [Test]
    public async Task HandleAsync_StampsContextUserPersistsAndReturnsDto()
    {
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryAccountRepository(userId, new FakeTimeProvider(createdAtUtc));
        var currencies = new InMemoryCurrencyReadService();
        var handler = new CreateAccountHandler(repository, currencies, new StubUserContext(userId), new FakeTimeProvider(createdAtUtc));

        var dto = await handler.HandleAsync(new CreateAccountCommand("  Checking  ", AccountType.Checking, 100m, "usd"));
        var stored = await repository.GetByIdAsync(dto.Id);

        await Assert.That(repository.AddCallCount).IsEqualTo(1);
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.UserId).IsEqualTo(userId);
        await Assert.That(dto.Name).IsEqualTo("Checking");
        await Assert.That(dto.Type).IsEqualTo(AccountType.Checking);
        await Assert.That(dto.OpeningBalance).IsEqualTo(100m);
        await Assert.That(dto.CreatedAtUtc).IsEqualTo(createdAtUtc.UtcDateTime);
        await Assert.That(dto.CurrencyCode).IsEqualTo("USD");
        await Assert.That(dto.CurrencyName).IsEqualTo("US Dollar");
        await Assert.That(dto.CurrencySymbol).IsEqualTo("$");
        await Assert.That(dto.CurrencyMinorUnit).IsEqualTo(2);
    }

    [Test]
    public async Task HandleAsync_WithUnknownCurrency_ThrowsValidationExceptionAndDoesNotPersist()
    {
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryAccountRepository(userId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreateAccountHandler(
            repository,
            new InMemoryCurrencyReadService(),
            new StubUserContext(userId),
            new FakeTimeProvider(createdAtUtc));

        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            handler.HandleAsync(new CreateAccountCommand("Checking", AccountType.Checking, 100m, "ZZZ")));

        await Assert.That(exception.Errors.ContainsKey("CurrencyCode")).IsTrue();
        await Assert.That(repository.AddCallCount).IsEqualTo(0);
    }

    private static async Task<ValidationException> ThrowsValidationExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ValidationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }
}
