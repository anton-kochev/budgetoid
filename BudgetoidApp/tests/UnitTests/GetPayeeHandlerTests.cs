using Application.Payees.GetPayee;
using Domain.Payees;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// The read behind the <c>Location</c> header <c>POST /api/payees</c> sets. It exists so that header
/// names something that resolves.
/// </summary>
public sealed class GetPayeeHandlerTests
{
    [Test]
    public async Task HandleAsync_ReturnsTheNamedPayee()
    {
        // Arrange — two rows, so the assertion is "this one" rather than "a payee came back". A handler
        // reaching for the read service's list and taking its first entry passes with one row seeded.
        var repository = new InMemoryPayeeRepository(
            Guid.CreateVersion7(),
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 24, 13, 14, 15, TimeSpan.Zero)));
        await repository.CreateAsync("Apple");
        Payee wanted = await repository.CreateAsync("Zoo");

        // Act
        var dto = await new GetPayeeHandler(repository).HandleAsync(new GetPayeeQuery(wanted.Id));

        // Assert — the name is the base64url envelope, encoded the same way the 201 body encodes it.
        // Whether those two encodings agree is a question about two different code paths and is asked
        // over HTTP, not here: this side only sees the read service's.
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.Id).IsEqualTo(wanted.Id);
        await Assert.That(dto.Name)
            .IsEqualTo(Base64UrlText.Encode(SealedNarrative.Name("Zoo").Envelope.Span));
    }

    [Test]
    public async Task HandleAsync_WhenNoSuchPayee_ReturnsNullRatherThanThrowing()
    {
        // Arrange — null is the answer the route turns into a 404. In production the same null is what a
        // payee belonging to another budget produces, because the read runs through the BudgetIsolation
        // query filter; that half is exercised against the real filters and not against this fake, which
        // holds one budget's rows and cannot model a second.
        var repository = new InMemoryPayeeRepository(
            Guid.CreateVersion7(),
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 24, 13, 14, 15, TimeSpan.Zero)));
        await repository.CreateAsync("Apple");

        // Act
        var dto = await new GetPayeeHandler(repository).HandleAsync(
            new GetPayeeQuery(Guid.CreateVersion7()));

        // Assert
        await Assert.That(dto).IsNull();
    }
}
