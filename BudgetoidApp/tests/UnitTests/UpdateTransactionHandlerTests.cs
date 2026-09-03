using Application.Abstractions;
using Application.Transactions.UpdateTransaction;
using Domain.Accounts;
using Domain.Payees;
using Domain.Transactions;
using Microsoft.Extensions.Time.Testing;
using TestSupport;
using TUnit.Assertions.Enums;
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

        // Act — the note is the SEALED envelope over the label as base64url, because the member is a
        // wire value the handler decodes. "Latte" itself is not a legal envelope and now answers 400.
        await fixture.Handler.HandleAsync(Command(transaction.Id) with
        {
            Description = new Optional<string?>(SealedNarrative.EncodedDescription("Latte")),
        });

        // Assert — the note is read back as the bytes it carries, not as a string: the entity holds a
        // NarrativeField, and neither it nor ReadOnlyMemory<byte> gives content equality for free.
        //
        // CollectionOrdering.Matching IS PART OF THE ASSERTION. IsEqualTo over two byte[] compares
        // REFERENCES, and TUnit's failure message names IsEquivalentTo as the fix — whose default is
        // CollectionOrdering.Any, so the bare overload passes on every permutation of an envelope's
        // bytes.
        await Assert.That(transaction.PayeeId).IsEqualTo(payee.Id);
        await Assert.That(transaction.Description).IsNotNull();
        await Assert.That(transaction.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Description("Latte").Envelope.ToArray(),
                CollectionOrdering.Matching);
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
    public async Task HandleAsync_WithAnExplicitNullDescription_ClearsTheNote()
    {
        // Arrange — THE DESCRIPTION'S OWN THREE-STATE CONTRACT WAS EXERCISED BY NOTHING IN THIS FILE.
        // Every case here is about the payee, and the description rides along as a passenger in exactly
        // one state — present with a value — so the handler's `IsSet` outer test and `Value is null`
        // inner test were, for this member, held by the comment above them and by no assertion at all.
        //
        // Reversed — branching on `Value is { } text` first — present-and-null falls through with the
        // ABSENT case, so the one request that clears a note does nothing and answers 204. That is the
        // trap UpdateTransactionHandler writes out at the member and the one the payee blocks below it
        // already have cases for. This is the description's.
        //
        // A transaction that HAS a note, so that clearing is a change rather than a restatement of what
        // was already there: seeded from null, this case passes for a handler that never touches the
        // field.
        Fixture fixture = await Fixture.CreateAsync();
        Transaction transaction = await fixture.SeedTransactionAsync(payeeId: null);

        // Act
        await fixture.Handler.HandleAsync(Command(transaction.Id) with
        {
            Description = new Optional<string?>(null),
        });

        // Assert — `IsNull` and never IsNullOrEmpty. The mutant this case exists for leaves the OLD note
        // attached, which is worse than the payee's equivalent for the reason the handler states: a
        // counterparty that failed to detach is visible on the screen, while a note that failed to clear
        // looks exactly like a note nobody asked to remove.
        await Assert.That(transaction.Description).IsNull();
        await Assert.That(fixture.Transactions.UpdateCallCount).IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_WithNoDescriptionMember_LeavesTheNoteAttached()
    {
        // Arrange — the other half of the pair, and the half that catches the reversed order from the
        // other side. Under `Value is { } text` first, ABSENT and present-and-null are indistinguishable:
        // one of them then behaves wrongly whichever branch the fall-through lands in, so a single case
        // could be satisfied by a handler that got the other one backwards. Both are written.
        Fixture fixture = await Fixture.CreateAsync();
        Transaction transaction = await fixture.SeedTransactionAsync(payeeId: null);

        // Act — Description left at `default`, which is Optional<string?> with IsSet false. This is the
        // state a PATCH body that never mentions the member arrives in, and it is the one state the
        // ENTITY cannot express: Transaction.Update is a full assignment, so "leave it alone" exists
        // only as the handler choosing to pass the value the row already holds.
        await fixture.Handler.HandleAsync(Command(transaction.Id) with
        {
            Amount = new Optional<decimal>(-19.99m),
        });

        // Assert — the seeded note, byte for byte, and the amount really did change, so a handler that
        // did nothing at all cannot pass this case on the note alone.
        //
        // CollectionOrdering.Matching for the reason the first case in this file gives.
        await Assert.That(transaction.Description).IsNotNull();
        await Assert.That(transaction.Description!.Envelope.ToArray())
            .IsEquivalentTo(
                SealedNarrative.Description("Corner shop").Envelope.ToArray(),
                CollectionOrdering.Matching);
        await Assert.That(transaction.Amount).IsEqualTo(-19.99m);
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

        // Act — a sealed note, for the reason the first case in this file gives.
        await fixture.Handler.HandleAsync(Command(transaction.Id) with
        {
            Description = new Optional<string?>(SealedNarrative.EncodedDescription("Latte")),
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
            // The id is minted HERE and threaded in, because Transaction.Create no longer mints one: it
            // is the associated data the description was sealed against, so the factory takes it and
            // never invents it. The seeded note is a sealed envelope over a label; the cases that assert
            // on it read the value the handler wrote, never this one.
            //
            // The fifth argument is the minor unit, two for USD. It was spelled `minorUnit: 2` and is
            // positional now: with the id inserted ahead of it the name sat at a different index than
            // the parameter it named, which the compiler reports as CS8323 rather than as the arity
            // error every other call site here produces.
            Transaction transaction = Transaction.Create(
                Guid.CreateVersion7(),
                BudgetId,
                Account.Id,
                -4.50m,
                2,
                new DateOnly(2026, 6, 12),
                SealedNarrative.Description("Corner shop"),
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
