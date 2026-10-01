using Application.Payees.CreatePayee;
using Domain.Common;
using Domain.Payees;
using Domain.Security;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using TUnit.Assertions.Enums;
using UnitTests.Fakes;

namespace UnitTests;

/// <summary>
/// The handler that replaced find-or-create. It looks nothing up: the server holds no index key and
/// cannot fold a name's case, so what is left on this side is a shape check, an insert, and a unique
/// index over the blind index.
/// </summary>
public sealed class CreatePayeeHandlerTests
{
    [Test]
    public async Task HandleAsync_StampsTheAmbientBudgetPersistsAndReturnsTheDto()
    {
        // Arrange — an id minted here and threaded in, so the assertion below is not "an id came back"
        // but "this one did".
        var budgetId = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 24, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryPayeeRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreatePayeeHandler(
            repository,
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));

        // Act
        var dto = await handler.HandleAsync(Command(id, "Starbucks"));
        Payee? stored = await repository.GetByIdAsync(dto.Id);

        // Assert — the id the CALLER sent, not one the handler or the factory minted. It is the
        // associated data the name was sealed against, so a path that invented one produces a row whose
        // name nobody can open, with nothing else in this test noticing.
        //
        // The budget and the instant come from the handler and not from the repository, which lost both
        // when it lost find-or-create.
        await Assert.That(repository.AddCallCount).IsEqualTo(1);
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.BudgetId).IsEqualTo(budgetId);
        await Assert.That(stored.CreatedAtUtc).IsEqualTo(createdAtUtc.UtcDateTime);
        await Assert.That(dto.Id).IsEqualTo(id);
        await Assert.That(dto.Name).IsEqualTo(EncodedName("Starbucks"));
    }

    [Test]
    public async Task HandleAsync_StoresBothHalvesOfTheNameItWasSent()
    {
        // Arrange — the DTO returns no blind index, deliberately, so the only way to see the index the
        // handler stored is to read the entity back off the repository.
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 24, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryPayeeRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreatePayeeHandler(
            repository,
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));

        // Act
        var dto = await handler.HandleAsync(Command(Guid.CreateVersion7(), "Starbucks"));
        Payee? stored = await repository.GetByIdAsync(dto.Id);

        // Assert — the handler decodes two opaque members and hands them to one IndexedName. A handler
        // that decoded the name twice, or that passed the envelope where the index belongs, would still
        // return a 201 and a DTO indistinguishable from this one, because the index is on no read.
        //
        // On this table that would be worse than on accounts: the index is the whole of counterparty
        // deduplication, so a row indexed under something other than its own name is a payee the client
        // can neither find nor re-create.
        //
        // CollectionOrdering.Matching IS PART OF THE ASSERTION. IsEqualTo over two byte[] compares
        // REFERENCES and fails even when the contents and the order agree, and TUnit's failure message
        // names IsEquivalentTo as the fix — which DEFAULTS TO CollectionOrdering.Any and would then pass
        // on any permutation of an envelope's bytes. Order is the whole of what a ciphertext is.
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.Name.Envelope.ToArray())
            .IsEquivalentTo(SealedNarrative.Name("Starbucks").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(stored.NameKey.ToArray())
            .IsEquivalentTo(SealedNarrative.BlindIndex("Starbucks").ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("0199C3D4-0000-7000-8000-0000000000AA")]
    [Arguments("{0199c3d4-0000-7000-8000-0000000000aa}")]
    [Arguments("0199c3d40000700080000000000000aa")]
    [Arguments(" 0199c3d4-0000-7000-8000-0000000000aa ")]
    [Arguments("00000000-0000-0000-0000-000000000000")]
    public async Task HandleAsync_WithANonCanonicalId_ThrowsValidationExceptionKeyedOnTheId(string id)
    {
        // Arrange — five spellings a uuid parser accepts and this route must not, because the client
        // sealed the name against the ONE spelling this API can reproduce. The all-zero uuid rides the
        // same check: it is a legal uuid, so left to the primary key the first such row stores and the
        // second collides under a constraint name that says nothing about the caller that never chose
        // an id at all.
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 24, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryPayeeRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreatePayeeHandler(
            repository,
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() => handler.HandleAsync(
            new CreatePayeeCommand(id, EncodedName("Starbucks"), EncodedIndex("Starbucks"))));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
        await Assert.That(repository.AddCallCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("not base64url at all")]
    [Arguments("")]
    public async Task HandleAsync_WithAMalformedName_ThrowsValidationExceptionKeyedOnTheName(string name)
    {
        // Arrange — the envelope member is opaque to this server, so the only thing it can refuse is
        // the shape: base64url text decoding to a well-framed envelope under the column's cap.
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 24, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryPayeeRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreatePayeeHandler(
            repository,
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() => handler.HandleAsync(
            new CreatePayeeCommand(
                Guid.CreateVersion7().ToString("D"),
                name,
                EncodedIndex("Starbucks"))));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(repository.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithAnIndexOfTheWrongWidth_ThrowsValidationExceptionKeyedOnTheIndex()
    {
        // Arrange — a blind index is a keyed digest with no framing, so the width is the only shape
        // check this side can make. Thirty-one bytes of perfectly good base64url is the value that
        // proves the check is a width and not merely a decode.
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 24, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryPayeeRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreatePayeeHandler(
            repository,
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));
        string tooShort = Base64UrlText.Encode(new byte[IndexedName.BlindIndexLength - 1]);

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() => handler.HandleAsync(
            new CreatePayeeCommand(
                Guid.CreateVersion7().ToString("D"),
                EncodedName("Starbucks"),
                tooShort)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("NameKey")).IsTrue();
        await Assert.That(repository.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithEveryOpaqueMemberMalformed_ReportsAllThreeAtOnce()
    {
        // Arrange — THE HANDLER CLAIMS EVERY MEMBER IS ATTEMPTED AND EVERY FAILURE REPORTED, and this is
        // the only case that can tell that apart from fail-fast. Every other case in this file sends one
        // bad member, so a handler that threw the moment the id check failed would satisfy all of them.
        //
        // The three members are produced by one piece of client code and are opaque to this side in the
        // same way, so a caller that got them all wrong would otherwise learn about the second only
        // after fixing the first and sending everything again — three round trips for one broken client.
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 24, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryPayeeRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreatePayeeHandler(
            repository,
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() => handler.HandleAsync(
            new CreatePayeeCommand(
                Guid.CreateVersion7().ToString("D").ToUpperInvariant(),
                "not an envelope",
                "not an index")));

        // Assert — all three keys, in one refusal, and the COUNT is what makes this case impossible for
        // a fail-fast handler to pass: it would carry exactly one key, satisfy whichever ContainsKey
        // happened to name it, and be caught by nothing else in this file.
        await Assert.That(exception.Errors.Count).IsEqualTo(3);
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("NameKey")).IsTrue();
        await Assert.That(repository.AddCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// A well-formed body: the canonical spelling of <paramref name="id" />, and both halves of the
    /// name derived from one label the way a client derives them from one text.
    /// </summary>
    private static CreatePayeeCommand Command(Guid id, string label) =>
        new(id.ToString("D"), EncodedName(label), EncodedIndex(label));

    // The wire spelling of each half: unpadded base64url, the one alphabet every binary member of this
    // API crosses JSON in. Built from the fixture rather than from a literal, so a framing change moves
    // one file.
    private static string EncodedName(string label) =>
        Base64UrlText.Encode(SealedNarrative.Name(label).Envelope.Span);

    private static string EncodedIndex(string label) =>
        Base64UrlText.Encode(SealedNarrative.BlindIndex(label).Span);

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
