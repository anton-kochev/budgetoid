using Application.Payees.GetPayees;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class GetPayeesHandlerTests
{
    [Test]
    public async Task HandleAsync_ReturnsReadServicePayeesInCreationOrder()
    {
        // Arrange — THE IDS ASCEND AND THE INSTANTS DESCEND, and that inversion is the whole of what
        // makes this case prove anything. "Apple" is seeded first, so it holds the smaller v7 id, and it
        // is stamped an hour LATER than "Zoo". Three candidate orderings therefore disagree:
        //
        //   by name             → Apple, Zoo
        //   by id alone         → Apple, Zoo
        //   by instant then id  → Zoo, Apple
        //
        // Seeded the obvious way — two rows in sequence off one clock — a v7 id sorts by the instant it
        // was minted, so "order by id" and "order by created_at_utc, id" emit byte-identical lists and
        // an id-only read side passes.
        //
        // Ordering by name is not merely unused here, it is unavailable. The column is bytea, so an
        // ordering over it sorts by the first differing byte — which after the version is the nonce,
        // freshly drawn on every seal. That is stable within one read and reshuffled by every save, so
        // a list would silently reorder itself when an unrelated payee was renamed. Only the client
        // holds the text, so only the client can put a list in name order.
        //
        // What this case cannot reach is the production read side: the ordering under test is the
        // fake's, and PayeeReadService's own `ORDER BY p.created_at_utc, p.id` is exercised over
        // PostgreSQL in the integration suite. What it holds here is that the handler hands the read
        // service's order through untouched.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 24, 13, 14, 15, TimeSpan.Zero));
        var repository = new InMemoryPayeeRepository(Guid.CreateVersion7(), clock);
        await repository.CreateAsync("Apple", new DateTime(2026, 6, 24, 14, 0, 0, DateTimeKind.Utc));
        await repository.CreateAsync("Zoo", new DateTime(2026, 6, 24, 13, 0, 0, DateTimeKind.Utc));

        // Act
        var response = await new GetPayeesHandler(repository).HandleAsync(new GetPayeesQuery());

        // Assert — the names are the base64url the route hands back, not text. Nothing on this side can
        // turn one into a name, which is why they are compared against the fixture that produced them.
        //
        // Indexed positionally rather than through a collection assertion, because the subject IS the
        // order: TUnit's IsEquivalentTo defaults to CollectionOrdering.Any and would pass on either
        // permutation, and its failure message on IsEqualTo actively steers a reader into that overload.
        await Assert.That(response.Items.Count).IsEqualTo(2);
        await Assert.That(response.Items[0].Name).IsEqualTo(EncodedName("Zoo"));
        await Assert.That(response.Items[1].Name).IsEqualTo(EncodedName("Apple"));
    }

    [Test]
    public async Task HandleAsync_WhenNoPayees_ReturnsEmptyList()
    {
        // Act
        var response = await new GetPayeesHandler(new InMemoryPayeeRepository(
                Guid.CreateVersion7(),
                new FakeTimeProvider(new DateTimeOffset(2026, 6, 24, 13, 14, 15, TimeSpan.Zero))))
            .HandleAsync(new GetPayeesQuery());

        // Assert
        await Assert.That(response.Items.Count).IsEqualTo(0);
    }

    private static string EncodedName(string label) =>
        Base64UrlText.Encode(SealedNarrative.Name(label).Envelope.Span);
}
