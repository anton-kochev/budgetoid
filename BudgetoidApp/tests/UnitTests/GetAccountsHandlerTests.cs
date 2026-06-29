using Application.Accounts.GetAccounts;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class GetAccountsHandlerTests
{
    [Test]
    public async Task HandleAsync_ReturnsReadServiceAccounts()
    {
        var repository = new InMemoryAccountRepository(
            Guid.CreateVersion7(),
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero)));
        await repository.CreateAsync("Savings");
        await repository.CreateAsync("Checking");
        var handler = new GetAccountsHandler(repository);

        var response = await handler.HandleAsync(new GetAccountsQuery());

        await Assert.That(response.Items.Count).IsEqualTo(2);
        await Assert.That(response.Items[0].Name).IsEqualTo("Checking");
        await Assert.That(response.Items[1].Name).IsEqualTo("Savings");
    }
}
