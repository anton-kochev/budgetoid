using Application.Currencies;
using Application.Transactions.CreateTransaction;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Common;
using Domain.Payees;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using UnitTests.Fakes;

namespace UnitTests;

public sealed class CreateTransactionHandlerTests
{
    [Test]
    public async Task HandleAsync_WithValidCommand_PersistsAndReturnsEnrichedAccountFields()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();
        var date = new DateOnly(2026, 6, 12);

        // Act
        var dto = await fixture.Handler.HandleAsync(
            new CreateTransactionCommand(-42.50m, date, fixture.Account.Id, "Groceries"));
        var stored = (await fixture.Transactions.GetAllAsync()).Single();

        // Assert
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(1);
        await Assert.That(stored.BudgetId).IsEqualTo(fixture.BudgetId);
        await Assert.That(dto.Id).IsEqualTo(stored.Id);
        await Assert.That(dto.AccountId).IsEqualTo(fixture.Account.Id);
        // The account's name crosses this DTO as the base64url envelope the accounts column holds, not
        // as text — the enrichment still reaches the right account, which is what this line is for, but
        // it can no longer say what that account is called.
        await Assert.That(dto.AccountName)
            .IsEqualTo(Base64UrlText.Encode(SealedNarrative.Name("Checking").Envelope.Span));
        await Assert.That(dto.CurrencyCode).IsEqualTo("USD");
        await Assert.That(dto.CurrencySymbol).IsEqualTo("$");
        await Assert.That(dto.CategoryId).IsNull();
        await Assert.That(dto.CategoryName).IsNull();
        await Assert.That(dto.CategoryGroupId).IsNull();
        await Assert.That(dto.CategoryGroupName).IsNull();
    }

    [Test]
    public async Task HandleAsync_WithUnknownAccount_RejectsOnTheAccountBeforeReachingThePayee()
    {
        // Arrange — BOTH identifiers name nothing, which is what makes the order of the two checks
        // visible. With only the account wrong, a handler that resolved the payee first would still
        // report the account and this case would prove nothing about which ran.
        //
        // It used to assert that no payee had been CREATED before the account check. That subject is
        // gone with find-or-create: this handler writes no payee, it reads one. What is left to hold is
        // the order in which a caller is told about two bad identifiers.
        Fixture fixture = await Fixture.CreateAsync();

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            fixture.Handler.HandleAsync(new CreateTransactionCommand(
                -4.50m,
                new DateOnly(2026, 6, 12),
                Guid.CreateVersion7(),
                "Coffee",
                PayeeId: Guid.CreateVersion7())));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("AccountId")).IsTrue();
        await Assert.That(exception.Errors.ContainsKey("PayeeId")).IsFalse();
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithAPayeeId_LinksThatPayeeAndReturnsItsSealedName()
    {
        // Arrange — the payee is SEEDED rather than named, which is the whole of what this slice
        // changed. The handler used to take a name and create the row; it now takes the id of a row
        // POST /api/payees already wrote, because payees.name is an envelope drawn under a fresh nonce
        // and no lookup by name is a question this side can answer.
        Fixture fixture = await Fixture.CreateAsync();
        Payee payee = await fixture.Payees.CreateAsync("Starbucks");

        // Act
        var dto = await fixture.Handler.HandleAsync(new CreateTransactionCommand(
            -4.50m,
            new DateOnly(2026, 6, 12),
            fixture.Account.Id,
            "Coffee",
            PayeeId: payee.Id));
        var stored = (await fixture.Transactions.GetAllAsync()).Single();

        // Assert — the name crosses the DTO as the base64url envelope the payees column holds, not as
        // text. It used to be asserted as "Starbucks", trimmed from "  Starbucks  "; both the value and
        // the trim are gone with the column.
        //
        // The envelope is bound to the PAYEE's row id and not to this transaction's, so a client
        // rebuilding the transaction's own binding gets an authentication failure rather than garbage.
        // Nothing on this side can see that, which is why the assertion is against the fixture that
        // sealed it.
        await Assert.That(stored.PayeeId).IsEqualTo(payee.Id);
        await Assert.That(dto.PayeeId).IsEqualTo(payee.Id);
        await Assert.That(dto.PayeeName)
            .IsEqualTo(Base64UrlText.Encode(SealedNarrative.Name("Starbucks").Envelope.Span));
    }

    [Test]
    public async Task HandleAsync_WithAnUnknownPayeeId_ThrowsValidationExceptionAndDoesNotPersist()
    {
        // Arrange — the guard this case exists for is a filtered READ, and deleting it compiles. The id
        // would then reach the database as a foreign key nothing satisfies: a 23503, which
        // TransactionRepository.AddAsync does not catch at all, so it surfaces through
        // GlobalExceptionHandler as a 500 — a defect report for what is a bad request.
        //
        // The fake holds one budget's rows and cannot model another budget's payee, so the cross-budget
        // half of this — where the same read returns null because of the BudgetIsolation query filter —
        // is closed over HTTP in the integration suite and not here.
        Fixture fixture = await Fixture.CreateAsync();

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            fixture.Handler.HandleAsync(new CreateTransactionCommand(
                -4.50m,
                new DateOnly(2026, 6, 12),
                fixture.Account.Id,
                "Coffee",
                PayeeId: Guid.CreateVersion7())));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("PayeeId")).IsTrue();
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithNoPayeeId_AttachesNoPayee()
    {
        // Arrange — the default of the member is absent, and absent must mean no payee rather than an
        // empty id reaching Transaction.AssignPayee, which treats Guid.Empty as a programmer error.
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.Payees.CreateAsync("Starbucks");

        // Act
        var dto = await fixture.Handler.HandleAsync(new CreateTransactionCommand(
            -4.50m,
            new DateOnly(2026, 6, 12),
            fixture.Account.Id,
            "Coffee"));
        var stored = (await fixture.Transactions.GetAllAsync()).Single();

        // Assert — a payee exists in the budget and is not attached, which is what separates "none was
        // asked for" from "none was available".
        await Assert.That(stored.PayeeId).IsNull();
        await Assert.That(dto.PayeeId).IsNull();
        await Assert.That(dto.PayeeName).IsNull();
    }

    [Test]
    public async Task HandleAsync_WithCategory_LinksCategoryAndReturnsCurrentHierarchy()
    {
        // Arrange
        Fixture fixture = await Fixture.CreateAsync();
        CategoryGroup categoryGroup =
            await fixture.CategoryGroups.CreateAsync(SealedNarrative.Indexed("Essential Obligations"));
        Category category = await fixture.Categories.CreateAsync(categoryGroup.Id, "Groceries");

        // Act
        var dto = await fixture.Handler.HandleAsync(new CreateTransactionCommand(
            -4.50m,
            new DateOnly(2026, 6, 12),
            fixture.Account.Id,
            "Food",
            CategoryId: category.Id));
        var stored = (await fixture.Transactions.GetAllAsync()).Single();

        // Assert
        await Assert.That(stored.CategoryId).IsEqualTo(category.Id);
        await Assert.That(dto.CategoryId).IsEqualTo(category.Id);
        await Assert.That(dto.CategoryName).IsEqualTo("Groceries");
        await Assert.That(dto.CategoryGroupId).IsEqualTo(categoryGroup.Id);
        await Assert.That(dto.CategoryGroupName)
            .IsEqualTo(SealedNarrative.EncodedName("Essential Obligations"));
    }

    [Test]
    public async Task HandleAsync_WithUnknownCategory_RejectsWithoutPersisting()
    {
        // Arrange — a good payee beside the bad category, so the refusal is unambiguously the
        // category's. It used to assert that no payee had been created; the handler creates none now,
        // and what remains is that nothing is written.
        Fixture fixture = await Fixture.CreateAsync();
        Payee payee = await fixture.Payees.CreateAsync("Starbucks");

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            fixture.Handler.HandleAsync(new CreateTransactionCommand(
                -4.50m,
                new DateOnly(2026, 6, 12),
                fixture.Account.Id,
                "Coffee",
                PayeeId: payee.Id,
                CategoryId: Guid.CreateVersion7())));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("CategoryId")).IsTrue();
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithInvalidTransaction_DoesNotPersist()
    {
        // Arrange — the invalid amount is one with too many decimal places for the account's USD.
        // A zero amount used to serve here and no longer can: zero is a legitimate ledger entry.
        Fixture fixture = await Fixture.CreateAsync();
        Payee payee = await fixture.Payees.CreateAsync("Starbucks");

        // Act
        _ = await ThrowsValidationExceptionAsync(() =>
            fixture.Handler.HandleAsync(new CreateTransactionCommand(
                1.234m,
                new DateOnly(2026, 6, 12),
                fixture.Account.Id,
                "Invalid",
                PayeeId: payee.Id)));

        // Assert
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithZeroAmount_PersistsTheTransaction()
    {
        // Arrange — a fully discounted purchase is worth recording for its payee and date even
        // though it nets to nothing, and the handler must not stand in the way of that.
        Fixture fixture = await Fixture.CreateAsync();

        // Act
        var dto = await fixture.Handler.HandleAsync(new CreateTransactionCommand(
            0m,
            new DateOnly(2026, 6, 12),
            fixture.Account.Id,
            "Fully discounted"));
        var stored = (await fixture.Transactions.GetAllAsync()).Single();

        // Assert
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(1);
        await Assert.That(dto.Amount).IsEqualTo(0m);
        await Assert.That(stored.Amount).IsEqualTo(0m);
    }

    [Test]
    public async Task HandleAsync_ValidatesTheAmountAgainstTheAccountCurrencyMinorUnit()
    {
        // Arrange — the handler already resolves the account's currency; this proves it passes that
        // currency's minor unit to the domain rather than a hard-coded 2. JPY has a minor unit of 0,
        // so -42.50 is not a representable amount of yen, and a handler still passing 2 accepts it.
        Fixture fixture = await Fixture.CreateAsync();
        fixture.Currencies.Add(new CurrencyDto("JPY", "Yen", "¥", 0));
        Account yenAccount = await fixture.Accounts.CreateAsync("Cash", AccountType.Checking, 0m, "JPY");

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            fixture.Handler.HandleAsync(new CreateTransactionCommand(
                -42.50m,
                new DateOnly(2026, 6, 12),
                yenAccount.Id,
                "Ramen")));

        // Assert
        await Assert.That(exception.Errors.ContainsKey("Amount")).IsTrue();
        await Assert.That(fixture.Transactions.AddCallCount).IsEqualTo(0);
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

    private sealed class Fixture
    {
        private Fixture()
        {
        }

        public required Guid BudgetId { get; init; }
        public required Account Account { get; init; }
        public required InMemoryAccountRepository Accounts { get; init; }
        public required InMemoryCurrencyReadService Currencies { get; init; }
        public required InMemoryTransactionRepository Transactions { get; init; }
        public required InMemoryPayeeRepository Payees { get; init; }
        public required InMemoryCategoryGroupRepository CategoryGroups { get; init; }
        public required InMemoryCategoryRepository Categories { get; init; }
        public required CreateTransactionHandler Handler { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var budgetId = Guid.CreateVersion7();
            var timeProvider = new FakeTimeProvider(
                new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero));
            var accounts = new InMemoryAccountRepository(budgetId, timeProvider);
            Account account = await accounts.CreateAsync("Checking");
            var transactions = new InMemoryTransactionRepository();
            var payees = new InMemoryPayeeRepository(budgetId, timeProvider);
            var categoryGroups = new InMemoryCategoryGroupRepository(budgetId, timeProvider);
            var categories = new InMemoryCategoryRepository(budgetId, timeProvider, categoryGroups);
            var currencies = new InMemoryCurrencyReadService();
            var handler = new CreateTransactionHandler(
                transactions,
                accounts,
                currencies,
                payees,
                categories,
                categoryGroups,
                // NO ITransactionalExecutor, AND THE ABSENT ARGUMENT IS THE ASSERTION. The handler
                // used to commit two rows — a payee it created from a name and the transaction that
                // needed it — and wrapped them so a failure between the two committed neither. One
                // write is left, so the boundary went with the second. Re-adding the parameter is a
                // compile break here rather than a silently passing test, which is the intended shape.
                new StubBudgetContext(budgetId),
                timeProvider);

            return new Fixture
            {
                BudgetId = budgetId,
                Account = account,
                Accounts = accounts,
                Currencies = currencies,
                Transactions = transactions,
                Payees = payees,
                CategoryGroups = categoryGroups,
                Categories = categories,
                Handler = handler,
            };
        }
    }
}
