using Application.Accounts.GetAccounts;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class GetAccountsHandlerTests
{
    [Test]
    public async Task HandleAsync_ReturnsReadServiceAccountsInCreationOrder()
    {
        // Arrange — "Savings" is seeded FIRST and at the EARLIER instant, and the labels are chosen so
        // the two orderings disagree: by name this list is Checking then Savings, by creation instant it
        // is Savings then Checking. A read side still ordering by name fails on the first item.
        //
        // Ordering by name is not merely unused here, it is unavailable. The column is bytea, so an
        // ordering over it sorts by the first differing byte — which after the version is the nonce,
        // freshly drawn on every seal. That is stable within one read and reshuffled by every save, so a
        // list would silently reorder itself when an unrelated account was renamed. Only the client
        // holds the text, so only the client can put a list in name order.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero));
        var repository = new InMemoryAccountRepository(Guid.CreateVersion7(), clock);
        await repository.CreateAsync("Savings");
        clock.Advance(TimeSpan.FromHours(1));
        await repository.CreateAsync("Checking");
        var handler = new GetAccountsHandler(repository);

        // Act
        var response = await handler.HandleAsync(new GetAccountsQuery());

        // Assert — the names are the base64url the route hands back, not text. Nothing on this side can
        // turn one into a name, which is why they are compared against the fixture that produced them.
        await Assert.That(response.Items.Count).IsEqualTo(2);
        await Assert.That(response.Items[0].Name).IsEqualTo(EncodedName("Savings"));
        await Assert.That(response.Items[1].Name).IsEqualTo(EncodedName("Checking"));
    }

    private static string EncodedName(string label) =>
        Base64UrlText.Encode(SealedNarrative.Name(label).Envelope.Span);
}
