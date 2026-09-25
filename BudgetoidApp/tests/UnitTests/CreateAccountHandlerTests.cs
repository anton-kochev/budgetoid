using Application.Accounts.CreateAccount;
using Application.Currencies;
using Domain.Accounts;
using Domain.Common;
using Domain.Security;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class CreateAccountHandlerTests
{
    [Test]
    public async Task HandleAsync_StampsContextUserPersistsAndReturnsDto()
    {
        // Arrange
        var budgetId = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var currencies = new InMemoryCurrencyReadService();
        var handler = new CreateAccountHandler(repository, currencies, new StubBudgetContext(budgetId), new FakeTimeProvider(createdAtUtc));

        // Act
        var dto = await handler.HandleAsync(Command(id, "Checking", openingBalance: 100m, currencyCode: "usd"));
        var stored = await repository.GetByIdAsync(dto.Id);

        // Assert — the id the CALLER sent, not one the handler or the factory minted. It is the
        // associated data the name was sealed against, so a path that invented one produces a row whose
        // name nobody can open, with nothing else in this test noticing.
        //
        // The name is asserted as the base64url the route hands back, and it used to be asserted as the
        // string "Checking" trimmed from "  Checking  ". Both are gone with the column: the DTO member
        // is still a string and no longer holds a name.
        await Assert.That(repository.AddCallCount).IsEqualTo(1);
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.BudgetId).IsEqualTo(budgetId);
        await Assert.That(dto.Id).IsEqualTo(id);
        await Assert.That(dto.Name).IsEqualTo(EncodedName("Checking"));
        await Assert.That(dto.Type).IsEqualTo(AccountType.Checking);
        await Assert.That(dto.OpeningBalance).IsEqualTo(100m);
        await Assert.That(dto.CreatedAtUtc).IsEqualTo(createdAtUtc.UtcDateTime);
        await Assert.That(dto.CurrencyCode).IsEqualTo("USD");
        await Assert.That(dto.CurrencyName).IsEqualTo("US Dollar");
        await Assert.That(dto.CurrencySymbol).IsEqualTo("$");
        await Assert.That(dto.CurrencyMinorUnit).IsEqualTo(2);
    }

    [Test]
    public async Task HandleAsync_StoresBothHalvesOfTheNameItWasSent()
    {
        // Arrange — the DTO returns no blind index, deliberately, so the only way to see the index the
        // handler stored is to read the entity back off the repository.
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreateAccountHandler(
            repository,
            new InMemoryCurrencyReadService(),
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));

        // Act
        var dto = await handler.HandleAsync(Command(Guid.CreateVersion7(), "Checking"));
        Account? stored = await repository.GetByIdAsync(dto.Id);

        // Assert — the handler decodes two opaque members and hands them to one IndexedName. A handler
        // that decoded the name twice, or that passed the envelope where the index belongs, would still
        // return a 201 and a DTO indistinguishable from this one, because the index is on no read.
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.Name.Envelope.ToArray())
            .IsEquivalentTo(SealedNarrative.Name("Checking").Envelope.ToArray());
        await Assert.That(stored.NameKey.ToArray())
            .IsEquivalentTo(SealedNarrative.BlindIndex("Checking").ToArray());
    }

    [Test]
    public async Task HandleAsync_WithUnknownCurrency_ThrowsValidationExceptionAndDoesNotPersist()
    {
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreateAccountHandler(
            repository,
            new InMemoryCurrencyReadService(),
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));

        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            handler.HandleAsync(Command(Guid.CreateVersion7(), "Checking", currencyCode: "ZZZ")));

        await Assert.That(exception.Errors.ContainsKey("CurrencyCode")).IsTrue();
        await Assert.That(repository.AddCallCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("not base64url at all", "Name")]
    [Arguments("", "Name")]
    public async Task HandleAsync_WithAMalformedName_ThrowsValidationExceptionKeyedOnTheMember(
        string name,
        string expectedKey)
    {
        // Arrange — the envelope member is opaque to this server, so the only thing it can refuse is
        // the shape: base64url text decoding to a well-framed envelope under the column's cap.
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreateAccountHandler(
            repository,
            new InMemoryCurrencyReadService(),
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() => handler.HandleAsync(
            new CreateAccountCommand(
                Guid.CreateVersion7().ToString("D"),
                name,
                EncodedIndex("Checking"),
                AccountType.Checking,
                0m,
                "USD")));

        // Assert
        await Assert.That(exception.Errors.ContainsKey(expectedKey)).IsTrue();
        await Assert.That(repository.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithAnIndexOfTheWrongWidth_ThrowsValidationExceptionKeyedOnTheIndex()
    {
        // Arrange — a blind index is a keyed digest with no framing, so the width is the only shape
        // check this side can make. Thirty-one bytes of perfectly good base64url is the value that
        // proves the check is a width and not merely a decode.
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreateAccountHandler(
            repository,
            new InMemoryCurrencyReadService(),
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));
        string tooShort = Base64UrlText.Encode(new byte[IndexedName.BlindIndexLength - 1]);

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() => handler.HandleAsync(
            new CreateAccountCommand(
                Guid.CreateVersion7().ToString("D"),
                EncodedName("Checking"),
                tooShort,
                AccountType.Checking,
                0m,
                "USD")));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("NameKey")).IsTrue();
        await Assert.That(repository.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithEveryOpaqueMemberMalformed_ReportsAllThreeAtOnce()
    {
        // Arrange — THE HANDLER CLAIMS EVERY MEMBER IS ATTEMPTED AND EVERY FAILURE REPORTED, and this is
        // the only case that can tell that apart from fail-fast. The three members are produced by one
        // piece of client code and are opaque to this side in the same way, so a caller that got them
        // all wrong would otherwise learn about the second only after fixing the first and sending
        // everything again — three round trips for one broken client.
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var handler = new CreateAccountHandler(
            repository,
            new InMemoryCurrencyReadService(),
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() => handler.HandleAsync(
            new CreateAccountCommand(
                Guid.CreateVersion7().ToString("D").ToUpperInvariant(),
                "not an envelope",
                "not an index",
                AccountType.Checking,
                0m,
                "USD")));

        // Assert — all three keys, in one refusal, and the COUNT is what makes this case impossible for a
        // fail-fast handler to pass: it would carry exactly one key, satisfy whichever ContainsKey
        // happened to name it, and be caught by nothing else in this file.
        await Assert.That(exception.Errors.Count).IsEqualTo(3);
        await Assert.That(exception.Errors.ContainsKey("Id")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("NameKey")).IsTrue();
        await Assert.That(repository.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_ValidatesTheOpeningBalanceAgainstTheResolvedCurrencyMinorUnit()
    {
        // Arrange — the handler already resolves the currency; this proves it passes that currency's
        // minor unit to the domain rather than a hard-coded 2. JPY has a minor unit of 0, so 100.50
        // is not a representable amount of yen, and a handler still passing 2 accepts it.
        var budgetId = Guid.CreateVersion7();
        var createdAtUtc = new DateTimeOffset(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(createdAtUtc));
        var currencies = new InMemoryCurrencyReadService();
        currencies.Add(new CurrencyDto("JPY", "Yen", "¥", 0));
        var handler = new CreateAccountHandler(
            repository,
            currencies,
            new StubBudgetContext(budgetId),
            new FakeTimeProvider(createdAtUtc));

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            handler.HandleAsync(Command(Guid.CreateVersion7(), "Cash", openingBalance: 100.50m, currencyCode: "JPY")));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
        await Assert.That(repository.AddCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// A well-formed body: the canonical spelling of <paramref name="id" />, and both halves of the
    /// name derived from one label the way a client derives them from one text.
    /// </summary>
    private static CreateAccountCommand Command(
        Guid id,
        string label,
        decimal openingBalance = 0m,
        string currencyCode = "USD") =>
        new(id.ToString("D"), EncodedName(label), EncodedIndex(label), AccountType.Checking, openingBalance, currencyCode);

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
