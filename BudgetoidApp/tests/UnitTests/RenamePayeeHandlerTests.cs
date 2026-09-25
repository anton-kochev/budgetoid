using Application.Payees.RenamePayee;
using Domain.Common;
using Domain.Payees;
using Domain.Security;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using TUnit.Assertions.Enums;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class RenamePayeeHandlerTests
{
    [Test]
    public async Task HandleAsync_ReplacesBothHalvesOfTheNameAndSaves()
    {
        // Arrange — a payee created under a misspelling and renamed to the correction, which is the
        // most common use of this route. The two labels are what make the halves distinguishable:
        // SealedNarrative derives both from the same text, so "Starbucks" produces an envelope and an
        // index that neither matches "Starbuks".
        Fixture fixture = Fixture.Create();
        Payee payee = await fixture.Payees.CreateAsync("Starbuks");

        // Act
        await fixture.Handler.HandleAsync(
            new RenamePayeeCommand(payee.Id, EncodedName("Starbucks"), EncodedIndex("Starbucks")));

        // Assert — THE INDEX IS ASSERTED TO BE THE NEW ONE AND EXPLICITLY NOT THE PREVIOUS ONE. A
        // handler that decoded only the envelope and reused the row's existing index would pass the
        // first assertion and leave a row holding new ciphertext under the old name's index — a payee
        // the client can neither find nor re-create, and a second row for the same counterparty the next
        // time somebody names it. Payee.Rename spells both failures out.
        //
        // CollectionOrdering.Matching IS PART OF THE ASSERTION. IsEqualTo over two byte[] compares
        // REFERENCES and fails even when the contents and the order agree, and TUnit's failure message
        // names IsEquivalentTo as the fix — which DEFAULTS TO CollectionOrdering.Any and would then pass
        // on any permutation of an envelope's bytes. The negative below takes no ordering: it says the
        // index is not the old VALUE, and any permutation of the old value is also not it.
        await Assert.That(payee.Name.Envelope.ToArray())
            .IsEquivalentTo(SealedNarrative.Name("Starbucks").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(payee.NameKey.ToArray())
            .IsEquivalentTo(SealedNarrative.BlindIndex("Starbucks").ToArray(), CollectionOrdering.Matching);
        await Assert.That(payee.NameKey.ToArray())
            .IsNotEquivalentTo(SealedNarrative.BlindIndex("Starbuks").ToArray());
        await Assert.That(fixture.Payees.UpdateCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_WhenNoSuchPayee_ThrowsNotFoundAndSavesNothing()
    {
        // Arrange — the lookup runs through the budget query filter in production, so another budget's
        // payee is indistinguishable from one that never existed. Both end here.
        Fixture fixture = Fixture.Create();

        // Act
        NotFoundException exception = await ThrowsAsync<NotFoundException>(() =>
            fixture.Handler.HandleAsync(new RenamePayeeCommand(
                Guid.CreateVersion7(),
                EncodedName("Starbucks"),
                EncodedIndex("Starbucks"))));

        // Assert
        await Assert.That(exception.Message).IsEqualTo("Payee was not found.");
        await Assert.That(fixture.Payees.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("not base64url at all")]
    [Arguments("")]
    public async Task HandleAsync_WithAMalformedName_RefusesAndLeavesBothHalvesUnchanged(string name)
    {
        // Arrange — the refusal has to reach the caller BEFORE Payee.Rename runs, not merely instead of
        // the save. The entity handed back by the repository is the tracked instance in production, so a
        // handler that mutated it and then threw would leave a dirty entity for the next SaveChanges on
        // that context to commit — a rename nobody asked for, arriving with some later request.
        Fixture fixture = Fixture.Create();
        Payee payee = await fixture.Payees.CreateAsync("Starbuks");

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(
                new RenamePayeeCommand(payee.Id, name, EncodedIndex("Starbucks"))));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(payee.Name.Envelope.ToArray())
            .IsEquivalentTo(SealedNarrative.Name("Starbuks").Envelope.ToArray(), CollectionOrdering.Matching);
        await Assert.That(payee.NameKey.ToArray())
            .IsEquivalentTo(SealedNarrative.BlindIndex("Starbuks").ToArray(), CollectionOrdering.Matching);
        await Assert.That(fixture.Payees.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithAnIndexOfTheWrongWidth_RefusesKeyedOnTheIndex()
    {
        // Arrange — a blind index is a keyed digest with no framing, so the width is the only shape
        // check this side can make. Thirty-one bytes of perfectly good base64url is the value that
        // proves the check is a width and not merely a decode: nothing here can recompute an index, so a
        // wrong 32 bytes keys perfectly and matches no payee the client will ever look for.
        Fixture fixture = Fixture.Create();
        Payee payee = await fixture.Payees.CreateAsync("Starbuks");
        string tooShort = Base64UrlText.Encode(new byte[IndexedName.BlindIndexLength - 1]);

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(
                new RenamePayeeCommand(payee.Id, EncodedName("Starbucks"), tooShort)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("NameKey")).IsTrue();
        await Assert.That(fixture.Payees.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithBothOpaqueMembersMalformed_ReportsBothAtOnce()
    {
        // Arrange — THE HANDLER CLAIMS EVERY MEMBER IS ATTEMPTED AND EVERY FAILURE REPORTED, and this is
        // the only case that can tell that apart from fail-fast. Every other case in this file sends one
        // bad member, so a handler that threw the moment the envelope decode failed would satisfy all of
        // them. The pair is produced by one piece of client code, so a caller that got both wrong would
        // otherwise learn about the second only after fixing the first and sending everything again.
        Fixture fixture = Fixture.Create();
        Payee payee = await fixture.Payees.CreateAsync("Starbuks");

        // Act
        ValidationException exception = await ThrowsAsync<ValidationException>(() =>
            fixture.Handler.HandleAsync(
                new RenamePayeeCommand(payee.Id, "not an envelope", "not an index")));

        // Assert — both keys, in one refusal, and the COUNT is what makes this case impossible for a
        // fail-fast handler to pass: it would carry exactly one key and satisfy whichever ContainsKey
        // happened to name it.
        await Assert.That(exception.Errors.Count).IsEqualTo(2);
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("NameKey")).IsTrue();
        await Assert.That(fixture.Payees.UpdateCallCount).IsEqualTo(0);
    }

    private static string EncodedName(string label) =>
        Base64UrlText.Encode(SealedNarrative.Name(label).Envelope.Span);

    private static string EncodedIndex(string label) =>
        Base64UrlText.Encode(SealedNarrative.BlindIndex(label).Span);

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class Fixture
    {
        private Fixture()
        {
        }

        public required InMemoryPayeeRepository Payees { get; init; }
        public required RenamePayeeHandler Handler { get; init; }

        public static Fixture Create()
        {
            var payees = new InMemoryPayeeRepository(
                Guid.CreateVersion7(),
                new FakeTimeProvider(new DateTimeOffset(2026, 6, 24, 13, 14, 15, TimeSpan.Zero)));

            return new Fixture { Payees = payees, Handler = new RenamePayeeHandler(payees) };
        }
    }
}
