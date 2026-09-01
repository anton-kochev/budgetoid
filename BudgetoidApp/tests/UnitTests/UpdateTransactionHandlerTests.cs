using Application.Abstractions;
using Application.Transactions.UpdateTransaction;
using Domain.Accounts;
using Domain.Payees;
using Domain.Transactions;
using Microsoft.Extensions.Time.Testing;
using UnitTests.Fakes;
// Domain.Common.ValidationException by name, because Application.Abstractions declares one too and
// only the Domain's is what the handler throws.
using ValidationException = Domain.Common.ValidationException;

namespace UnitTests;

/// <summary>
/// The three states of <c>Optional&lt;Guid?&gt; PayeeId</c>, which is the most fragile branch this
/// slice produced.
/// </summary>
/// <remarks>
/// <para>
/// <b>The wrong implementation these cases exist for compiles and reads well:</b>
/// <c>if (command.PayeeId.Value is { } id) transaction.AssignPayee(id);</c> — no <c>IsSet</c> arm at
/// all. Absent and present-and-null then fall through together, so the one request that asks for a
/// payee to be detached does nothing and answers 204. It takes <em>two</em> of the cases below to see
/// that: the clear case reddens, and the absent case is what stops somebody "fixing" it into an
/// unconditional clear-on-null, which passes the clear and silently detaches every payee on every
/// other edit.
/// </para>
/// <para>
/// The member used to be an <c>Optional&lt;string?&gt; PayeeName</c>, and the fourth reading it carried
/// — a blank or whitespace name meaning "no payee" — is gone with it. An identifier has no blank; the
/// clear is spelled by the explicit null and by nothing else.
/// </para>
/// </remarks>
public sealed class UpdateTransactionHandlerTests
{
    [Test]
    public async Task HandleAsync_WithNoPayeeMember_LeavesThePayeeAttached()
    {
        // Arrange — a transaction that already names a payee, and an edit that mentions something else
        // entirely. Absent must mean "leave it alone".
        Fixture fixture = await Fixture.CreateAsync();
        Payee payee = await fixture.Payees.CreateAsync("Starbucks");
        Transaction transaction = await fixture.SeedTransactionAsync(payee.Id);

        // Act
        await fixture.Handler.HandleAsync(Command(transaction.Id) with
        {
            Description = new Optional<string?>("Latte"),
        });

        // Assert
        await Assert.That(transaction.PayeeId).IsEqualTo(payee.Id);
        await Assert.That(transaction.Description).IsEqualTo("Latte");
        await Assert.That(fixture.Transactions.UpdateCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_WithAnExplicitNullPayee_ClearsThePayee()
    {
        // Arrange — present-and-null, the one state that asks for a detach. There is no other spelling
        // for it since the member became an identifier.
        Fixture fixture = await Fixture.CreateAsync();
        Payee payee = await fixture.Payees.CreateAsync("Starbucks");
        Transaction transaction = await fixture.SeedTransactionAsync(payee.Id);

        // Act
        await fixture.Handler.HandleAsync(Command(transaction.Id) with
        {
            PayeeId = new Optional<Guid?>(null),
        });

        // Assert
        await Assert.That(transaction.PayeeId).IsNull();
        await Assert.That(fixture.Transactions.UpdateCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_WithAPayeeId_AttachesThatPayee()
    {
        // Arrange — present with a value, replacing one payee with another. Two payees rather than one,
        // so the assertion is "this one" and not merely "something is attached".
        Fixture fixture = await Fixture.CreateAsync();
        Payee first = await fixture.Payees.CreateAsync("Starbucks");
        Payee second = await fixture.Payees.CreateAsync("Pret");
        Transaction transaction = await fixture.SeedTransactionAsync(first.Id);

        // Act
        await fixture.Handler.HandleAsync(Command(transaction.Id) with
        {
            PayeeId = new Optional<Guid?>(second.Id),
        });

        // Assert
        await Assert.That(transaction.PayeeId).IsEqualTo(second.Id);
        await Assert.That(fixture.Transactions.UpdateCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_WithAnUnknownPayeeId_RefusesAndLeavesTheTransactionAlone()
    {
        // Arrange — the guard is a filtered READ, and deleting it compiles. The id would then reach the
        // database as a foreign key nothing satisfies: a 23503, which TransactionRepository.UpdateAsync
        // filters by constraint name for the account and category keys only, so a payee violation
        // propagates and becomes a 500 — a defect report for what is a bad request.
        //
        // The fake holds one budget's rows and cannot model another budget's payee, so the cross-budget
        // half — where the same read returns null because of the BudgetIsolation query filter — is
        // closed over HTTP in the integration suite and not here.
        Fixture fixture = await Fixture.CreateAsync();
        Payee payee = await fixture.Payees.CreateAsync("Starbucks");
        Transaction transaction = await fixture.SeedTransactionAsync(payee.Id);

        // Act
        ValidationException exception = await ThrowsValidationExceptionAsync(() =>
            fixture.Handler.HandleAsync(Command(transaction.Id) with
            {
                PayeeId = new Optional<Guid?>(Guid.CreateVersion7()),
            }));

        // Assert — the refusal reaches the caller with the entity untouched, which is the second half:
        // the instance handed back by the repository is the tracked one in production, so a handler that
        // mutated it before refusing would leave an edit for the next SaveChanges to commit.
        await Assert.That(exception.Errors.ContainsKey("PayeeId")).IsTrue();
        await Assert.That(transaction.PayeeId).IsEqualTo(payee.Id);
        await Assert.That(fixture.Transactions.UpdateCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task HandleAsync_WithNoPayeeMemberOnATransactionThatHasNone_LeavesItDetached()
    {
        // Arrange — the other half of the absent case. A payee exists in the budget and is not attached,
        // which separates "none was asked for" from "none was available", and rules out a handler that
        // reached for some default payee when the member was missing.
        Fixture fixture = await Fixture.CreateAsync();
        await fixture.Payees.CreateAsync("Starbucks");
        Transaction transaction = await fixture.SeedTransactionAsync(payeeId: null);

        // Act
        await fixture.Handler.HandleAsync(Command(transaction.Id) with
        {
            Description = new Optional<string?>("Latte"),
        });

        // Assert
        await Assert.That(transaction.PayeeId).IsNull();
    }

    /// <summary>
    /// An edit that mentions nothing. Every member is the absent state, so a case names only the one
    /// field it is about with a <c>with</c> expression — the shape the three-state contract is easiest
    /// to read in.
    /// </summary>
    private static UpdateTransactionCommand Command(Guid id) => new(
        id,
        default,
        default,
        default,
        default,
        default,
        default);

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
        public required TimeProvider Clock { get; init; }
        public required InMemoryTransactionRepository Transactions { get; init; }
        public required InMemoryPayeeRepository Payees { get; init; }
        public required UpdateTransactionHandler Handler { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var budgetId = Guid.CreateVersion7();
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 12, 13, 14, 15, TimeSpan.Zero));
            var accounts = new InMemoryAccountRepository(budgetId, clock);
            Account account = await accounts.CreateAsync("Checking");
            var transactions = new InMemoryTransactionRepository();
            var payees = new InMemoryPayeeRepository(budgetId, clock);
            var categoryGroups = new InMemoryCategoryGroupRepository(budgetId, clock);
            var categories = new InMemoryCategoryRepository(budgetId, clock, categoryGroups);

            return new Fixture
            {
                BudgetId = budgetId,
                Account = account,
                Clock = clock,
                Transactions = transactions,
                Payees = payees,

                // NO ITransactionalExecutor, AND THE ABSENT ARGUMENT IS THE ASSERTION. This handler used
                // to commit a payee it created from a name alongside the edit that named it, and wrapped
                // the pair. One write is left, so the boundary went with the second; re-adding the
                // parameter is a compile break here rather than a silently passing test.
                Handler = new UpdateTransactionHandler(
                    transactions,
                    accounts,
                    new InMemoryCurrencyReadService(),
                    payees,
                    categories),
            };
        }

        public async Task<Transaction> SeedTransactionAsync(Guid? payeeId)
        {
            Transaction transaction = Transaction.Create(
                BudgetId,
                Account.Id,
                -4.50m,
                minorUnit: 2,
                new DateOnly(2026, 6, 12),
                "Coffee",
                Clock.GetUtcNow().UtcDateTime);

            if (payeeId is { } id)
            {
                transaction.AssignPayee(id);
            }

            await Transactions.AddAsync(transaction);
            return transaction;
        }
    }
}
