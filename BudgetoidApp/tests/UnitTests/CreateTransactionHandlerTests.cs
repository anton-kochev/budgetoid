using Application.Transactions.CreateTransaction;
using Domain.Accounts;
using Domain.Common;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class CreateTransactionHandlerTests
{
    [Test]
    public async Task HandleAsync_WithValidCommand_PersistsAccountIdAndReturnsAccountName()
    {
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(createdAtUtc);
        var accounts = new InMemoryAccountRepository(userId, timeProvider);
        var currencies = new InMemoryCurrencyReadService();
        Account account = await accounts.CreateAsync("Checking");
        var payees = new InMemoryPayeeRepository(userId, timeProvider);
        var handler = new CreateTransactionHandler(repository, accounts, currencies, payees, new StubUserContext(userId), timeProvider);
        var date = new DateOnly(2026, 6, 12);

        var dto = await handler.HandleAsync(new CreateTransactionCommand(-42.50m, date, account.Id, "Groceries"));
        var stored = await repository.GetAllAsync();

        await Assert.That(repository.AddCallCount).IsEqualTo(1);
        await Assert.That(stored.Count).IsEqualTo(1);
        await Assert.That(stored[0].UserId).IsEqualTo(userId);
        await Assert.That(stored[0].AccountId).IsEqualTo(account.Id);
        await Assert.That(dto.Id).IsEqualTo(stored[0].Id);
        await Assert.That(dto.Amount).IsEqualTo(-42.50m);
        await Assert.That(dto.Date).IsEqualTo(date);
        await Assert.That(dto.Description).IsEqualTo("Groceries");
        await Assert.That(dto.AccountId).IsEqualTo(account.Id);
        await Assert.That(dto.AccountName).IsEqualTo("Checking");
        await Assert.That(dto.CurrencyCode).IsEqualTo("USD");
        await Assert.That(dto.CurrencySymbol).IsEqualTo("$");
        await Assert.That(dto.CreatedAtUtc).IsEqualTo(createdAtUtc.UtcDateTime);
        await Assert.That(stored[0].CreatedAtUtc).IsEqualTo(createdAtUtc.UtcDateTime);
        await Assert.That(dto.PayeeId).IsNull();
        await Assert.That(dto.PayeeName).IsNull();
        await Assert.That(stored[0].PayeeId).IsNull();
        await Assert.That(payees.GetOrCreateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithUnknownAccountId_ThrowsValidationExceptionAndDoesNotPersist()
    {
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(createdAtUtc);
        var accounts = new InMemoryAccountRepository(userId, timeProvider);
        var currencies = new InMemoryCurrencyReadService();
        var payees = new InMemoryPayeeRepository(userId, timeProvider);
        var handler = new CreateTransactionHandler(repository, accounts, currencies, payees, new StubUserContext(userId), timeProvider);

        try
        {
            await handler.HandleAsync(new CreateTransactionCommand(-4.50m, new DateOnly(2026, 6, 12), Guid.CreateVersion7(), "Coffee", "Starbucks"));
        }
        catch (ValidationException exception)
        {
            await Assert.That(exception.Errors.ContainsKey("AccountId")).IsTrue();
            await Assert.That(repository.AddCallCount).IsEqualTo(0);
            await Assert.That(payees.GetOrCreateCallCount).IsEqualTo(0);
            return;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }

    [Test]
    public async Task HandleAsync_WithBlankDescription_ReturnsEmptyDescription()
    {
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(createdAtUtc);
        var accounts = new InMemoryAccountRepository(userId, timeProvider);
        var currencies = new InMemoryCurrencyReadService();
        Account account = await accounts.CreateAsync();
        var payees = new InMemoryPayeeRepository(userId, timeProvider);
        var handler = new CreateTransactionHandler(repository, accounts, currencies, payees, new StubUserContext(userId), timeProvider);

        var dto = await handler.HandleAsync(new CreateTransactionCommand(-4.50m, new DateOnly(2026, 6, 12), account.Id, "   "));
        var stored = (await repository.GetAllAsync()).Single();

        await Assert.That(stored.Description).IsNull();
        await Assert.That(dto.Description).IsEqualTo("");
    }

    [Test]
    public async Task HandleAsync_WithNewPayeeName_CreatesPayeeLinksTransactionAndReturnsPayeeFields()
    {
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(createdAtUtc);
        var accounts = new InMemoryAccountRepository(userId, timeProvider);
        var currencies = new InMemoryCurrencyReadService();
        Account account = await accounts.CreateAsync();
        var payees = new InMemoryPayeeRepository(userId, timeProvider);
        var handler = new CreateTransactionHandler(repository, accounts, currencies, payees, new StubUserContext(userId), timeProvider);

        var dto = await handler.HandleAsync(new CreateTransactionCommand(-4.50m, new DateOnly(2026, 6, 12), account.Id, "Coffee", "  Starbucks  "));
        var stored = (await repository.GetAllAsync()).Single();
        var payee = (await payees.GetAllAsync()).Single();

        await Assert.That(stored.PayeeId).IsEqualTo(payee.Id);
        await Assert.That(dto.PayeeId).IsEqualTo(payee.Id);
        await Assert.That(dto.PayeeName).IsEqualTo("Starbucks");
        await Assert.That(payee.Name).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task HandleAsync_WithExistingPayeeNameDifferentCase_ReusesPayee()
    {
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(createdAtUtc);
        var accounts = new InMemoryAccountRepository(userId, timeProvider);
        var currencies = new InMemoryCurrencyReadService();
        Account account = await accounts.CreateAsync();
        var payees = new InMemoryPayeeRepository(userId, timeProvider);
        await payees.GetOrCreateAsync("Starbucks");
        var existing = (await payees.GetAllAsync()).Single();
        var handler = new CreateTransactionHandler(repository, accounts, currencies, payees, new StubUserContext(userId), timeProvider);

        var dto = await handler.HandleAsync(new CreateTransactionCommand(-4.50m, new DateOnly(2026, 6, 12), account.Id, "Coffee", "starbucks"));

        await Assert.That((await payees.GetAllAsync()).Count).IsEqualTo(1);
        await Assert.That(dto.PayeeId).IsEqualTo(existing.Id);
        await Assert.That(dto.PayeeName).IsEqualTo("Starbucks");
    }

    [Test]
    public async Task HandleAsync_WithInvalidCommand_DoesNotPersistOrCreatePayee()
    {
        var repository = new InMemoryTransactionRepository();
        var userId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(createdAtUtc);
        var accounts = new InMemoryAccountRepository(userId, timeProvider);
        var currencies = new InMemoryCurrencyReadService();
        Account account = await accounts.CreateAsync();
        var payees = new InMemoryPayeeRepository(userId, timeProvider);
        var handler = new CreateTransactionHandler(repository, accounts, currencies, payees, new StubUserContext(userId), timeProvider);

        try
        {
            await handler.HandleAsync(new CreateTransactionCommand(0m, DateOnly.FromDateTime(DateTime.UtcNow), account.Id, "Invalid", "Starbucks"));
        }
        catch (ValidationException)
        {
            await Assert.That(repository.AddCallCount).IsEqualTo(0);
            await Assert.That(payees.GetOrCreateCallCount).IsEqualTo(0);
            await Assert.That((await payees.GetAllAsync()).Count).IsEqualTo(0);
            return;
        }

        throw new InvalidOperationException("Expected ValidationException.");
    }
}
