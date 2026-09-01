using Application.Accounts.UpdateAccount;
using Application.Currencies;
using Domain.Accounts;
using Domain.Common;
using Domain.Security;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class UpdateAccountHandlerTests
{
    [Test]
    public async Task HandleAsync_UpdatesExistingAccount()
    {
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Checking");
        var handler = new UpdateAccountHandler(repository, new InMemoryCurrencyReadService());

        await handler.HandleAsync(Command(account.Id, "Savings", AccountType.Savings, 25m));

        // BOTH HALVES MOVED, and the index is asserted to be the new one rather than merely to exist.
        // The handler builds one IndexedName from two decoded members; a version that passed only the
        // envelope down would satisfy the name assertion and leave the row's index describing
        // "Checking" — a uniqueness value that disagrees with the row's own content, which no
        // constraint on this side can see and no read can report.
        //
        // The name used to be asserted as the string "Savings", trimmed from "  Savings  ". The value
        // and the trim both left with the column.
        await Assert.That(repository.UpdateCallCount).IsEqualTo(1);
        await Assert.That(account.Name.Envelope.ToArray())
            .IsEquivalentTo(SealedNarrative.Name("Savings").Envelope.ToArray());
        await Assert.That(account.NameKey.ToArray())
            .IsEquivalentTo(SealedNarrative.BlindIndex("Savings").ToArray());
        await Assert.That(account.NameKey.ToArray())
            .IsNotEquivalentTo(SealedNarrative.BlindIndex("Checking").ToArray());
        await Assert.That(account.Type).IsEqualTo(AccountType.Savings);
        await Assert.That(account.OpeningBalance).IsEqualTo(25m);
    }

    [Test]
    public async Task HandleAsync_WithBothOpaqueMembersMalformed_ReportsBothAtOnce()
    {
        // Arrange — THE HANDLER CLAIMS BOTH MEMBERS ARE ATTEMPTED AND BOTH FAILURES REPORTED, and this
        // is the only case that can tell that apart from fail-fast. The pair is produced by one piece of
        // client code, so a caller that got both wrong would otherwise learn about the second only after
        // fixing the first and sending the whole body again.
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Checking");
        var handler = new UpdateAccountHandler(repository, new InMemoryCurrencyReadService());

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() => handler.HandleAsync(
            new UpdateAccountCommand(
                account.Id, "not an envelope", "not an index", AccountType.Savings, 25m)));

        // Assert — both keys, in one refusal, and nothing written. The COUNT is what makes this case
        // impossible for a fail-fast handler to pass: it would carry exactly one key, satisfy whichever
        // ContainsKey happened to name it, and be caught by nothing else in this file.
        await Assert.That(exception.Errors.Count).IsEqualTo(2);
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("NameKey")).IsTrue();
        await Assert.That(repository.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithAMalformedName_LeavesBothHalvesOfTheStoredNameAlone()
    {
        // Arrange — a refused update must change nothing. The name is the half that matters here:
        // Account.Update validates before it assigns, so a version that assigned first would leave a
        // row holding the new ciphertext and the old everything else.
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Checking");
        var handler = new UpdateAccountHandler(repository, new InMemoryCurrencyReadService());

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() => handler.HandleAsync(
            new UpdateAccountCommand(
                account.Id, "not an envelope", EncodedIndex("Savings"), AccountType.Savings, 25m)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Name")).IsTrue();
        await Assert.That(account.Name.Envelope.ToArray())
            .IsEquivalentTo(SealedNarrative.Name("Checking").Envelope.ToArray());
        await Assert.That(account.NameKey.ToArray())
            .IsEquivalentTo(SealedNarrative.BlindIndex("Checking").ToArray());
        await Assert.That(repository.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithAnIndexOfTheWrongWidth_ThrowsValidationExceptionKeyedOnTheIndex()
    {
        // Arrange — the width is the only shape check this side can make on an index: it is a keyed
        // digest with no framing, taken under a key that lives in a browser. Thirty-one bytes of
        // perfectly good base64url is the value that proves the check is a width and not a decode.
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Checking");
        var handler = new UpdateAccountHandler(repository, new InMemoryCurrencyReadService());
        string tooShort = Base64UrlText.Encode(new byte[IndexedName.BlindIndexLength - 1]);

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() => handler.HandleAsync(
            new UpdateAccountCommand(
                account.Id, EncodedName("Savings"), tooShort, AccountType.Savings, 25m)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("NameKey")).IsTrue();
        await Assert.That(repository.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithUnknownAccountId_ThrowsNotFoundException()
    {
        var repository = new InMemoryAccountRepository(Guid.CreateVersion7(), new FakeTimeProvider(UtcNowOffset()));
        var handler = new UpdateAccountHandler(repository, new InMemoryCurrencyReadService());

        try
        {
            await handler.HandleAsync(Command(Guid.CreateVersion7(), "Savings", AccountType.Savings, 25m));
        }
        catch (NotFoundException)
        {
            await Assert.That(repository.UpdateCallCount).IsEqualTo(0);
            return;
        }

        throw new InvalidOperationException("Expected NotFoundException.");
    }

    [Test]
    public async Task HandleAsync_WithAFractionalBalanceOnAZeroDecimalCurrencyAccount_ThrowsValidationException()
    {
        // Arrange — the domain rule is already covered by AccountTests; what this proves is the
        // wiring. The handler must resolve the currency of the account it loaded and pass that
        // minor unit down, so a handler still passing a hard-coded 2 accepts 25.5 yen and fails
        // here. JPY has a minor unit of 0, and there is no such thing as half a yen.
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Cash", AccountType.Checking, 0m, "JPY");
        var currencies = new InMemoryCurrencyReadService();
        currencies.Add(new CurrencyDto("JPY", "Yen", "¥", 0));
        var handler = new UpdateAccountHandler(repository, currencies);

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            handler.HandleAsync(Command(account.Id, "Cash", AccountType.Checking, 25.5m)));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("OpeningBalance")).IsTrue();
        await Assert.That(repository.UpdateCallCount).IsEqualTo(0);
        await Assert.That(account.OpeningBalance).IsEqualTo(0m);
    }

    [Test]
    public async Task HandleAsync_WithAWholeBalanceOnAZeroDecimalCurrencyAccount_UpdatesTheAccount()
    {
        // Arrange — the companion to the rejection above: a zero-decimal currency must still accept
        // its own whole units, or the handler would be refusing every yen amount.
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Cash", AccountType.Checking, 0m, "JPY");
        var currencies = new InMemoryCurrencyReadService();
        currencies.Add(new CurrencyDto("JPY", "Yen", "¥", 0));
        var handler = new UpdateAccountHandler(repository, currencies);

        // Act
        await handler.HandleAsync(Command(account.Id, "Cash", AccountType.Checking, 2500m));

        // Assert
        await Assert.That(repository.UpdateCallCount).IsEqualTo(1);
        await Assert.That(account.OpeningBalance).IsEqualTo(2500m);
    }

    [Test]
    public async Task HandleAsync_WithACurrencyTheReadServiceCannotFind_ThrowsInvalidOperationException()
    {
        // Arrange — the RESTRICT foreign key on accounts.currency_code guarantees the currency row
        // exists, so a missing one is a broken invariant rather than bad user input. It must not be
        // dressed up as a validation error the client could act on.
        var budgetId = Guid.CreateVersion7();
        var repository = new InMemoryAccountRepository(budgetId, new FakeTimeProvider(UtcNowOffset()));
        Account account = await repository.CreateAsync("Checking", AccountType.Checking, 0m, "ZZZ");
        var handler = new UpdateAccountHandler(repository, new InMemoryCurrencyReadService());

        // Act
        InvalidOperationException? caught = null;
        try
        {
            await handler.HandleAsync(Command(account.Id, "Checking", AccountType.Checking, 25m));
        }
        catch (InvalidOperationException exception)
        {
            caught = exception;
        }

        // Assert — the code is asserted rather than the whole sentence: naming it is what makes the
        // failure diagnosable, and the exact wording is not a contract.
        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("ZZZ");
        await Assert.That(repository.UpdateCallCount).IsEqualTo(0);
    }

    /// <summary>
    /// A well-formed body: both halves of the name derived from one label, the way a client derives
    /// them from one text. No identifier among the opaque members — the route parameter stays a
    /// <see cref="Guid" /> here, because a rename re-seals against the row's existing id rather than
    /// against the text somebody put in the URL.
    /// </summary>
    private static UpdateAccountCommand Command(
        Guid id,
        string label,
        AccountType type,
        decimal openingBalance) =>
        new(id, EncodedName(label), EncodedIndex(label), type, openingBalance);

    private static string EncodedName(string label) =>
        Base64UrlText.Encode(SealedNarrative.Name(label).Envelope.Span);

    private static string EncodedIndex(string label) =>
        Base64UrlText.Encode(SealedNarrative.BlindIndex(label).Span);

    private static DateTimeOffset UtcNowOffset() => new(2026, 6, 25, 13, 14, 15, TimeSpan.Zero);

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
