using System.Globalization;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Common;
using Domain.Payees;
using Domain.Transactions;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// The schema rules the transactions table holds, and both of the narrowings
/// <see cref="TransactionRepository" /> writes on top of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two narrowings are narrowed on different things, and that is why they are tested in
/// different shapes.</b> <see cref="TransactionRepository.UpdateAsync" /> filters a <c>23503</c> by
/// constraint name — two of them, on the account and category foreign keys — and turns each into a
/// <see cref="ValidationException" /> keyed on the field the caller named.
/// <see cref="TransactionRepository.DeleteAllForAmbientBudgetAsync" /> filters a concurrency conflict,
/// which carries no SQLSTATE and no constraint name, by the <i>entries</i>. Each is pinned here in both
/// directions: the violation it does model, so the fix for a widened filter cannot be to delete the
/// <c>catch</c>, and a violation it does not, so a widened filter reddens something.
/// </para>
/// <para>
/// <b>The mis-attribution mechanism is <c>RepositoryConstraintAttributionTests</c>', not a second
/// one.</b> <c>SaveChangesAsync</c> flushes everything the scoped context is tracking, not only the
/// entity the repository was handed, so the escape tests below track one unrelated row that breaks a
/// <i>different</i> constraint carrying the <i>same</i> SQLSTATE and then call the repository with an
/// entity of its own that is beyond reproach. Read that file's remarks for the argument. What is worth
/// stating here is only why this repository's half lives beside its method: that file covers the five
/// repositories reachable through a budget, and its coverage is a placement decision rather than a
/// judgement about which repositories deserve one.
/// </para>
/// <para>
/// <b>None of the three <c>UpdateAsync</c> tests is reachable through <c>UpdateTransactionHandler</c>,
/// and that is the point rather than a caveat.</b> The handler resolves the account and the category
/// through their budget-filtered repositories before it mutates anything, so it answers a missing one
/// with its own <see cref="ValidationException" /> and PostgreSQL never sees the row. These
/// <c>catch</c> clauses are the backstop for the row that vanishes between that read and this save,
/// which is a race no test can stage through the handler — so the state is arranged directly, exactly
/// as <c>Database_RejectsATransactionReferencingAnAccountInAnotherBudget</c> bypasses the handler for
/// the same reason.
/// </para>
/// </remarks>
public sealed class TransactionRepositoryTests
{
    /// <summary>
    /// Minor unit of the USD accounts these tests seed. Precision is not what any of them is about;
    /// the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task AddAsync_StoresCreatedAtUtcAsTimestampWithTimeZone()
    {
        await using RepositoryTestHost host = await StartHostAsync();
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");

        await using (BudgetoidDbContext db = new(options))
        {
            Account account = Account.Create(budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, DateTime.UtcNow);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();

            await new TransactionRepository(db).AddAsync(
                Transaction.Create(
                    budgetId,
                    account.Id,
                    1m,
                    UsdMinorUnit,
                    new DateOnly(2026, 6, 12),
                    "Test",
                    new DateTime(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc)));
        }

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("select pg_typeof(created_at_utc)::text from transactions limit 1",
            connection);
        string? type = (string?)await command.ExecuteScalarAsync();

        await Assert.That(type).IsEqualTo("timestamp with time zone");
    }

    [Test]
    public async Task AmountColumn_UsesNumeric14Scale4()
    {
        // Scale 4 rather than 2 because the minor unit is a property of the currency, not of the
        // column: BHD and KWD have three decimal places and would otherwise be silently rounded on
        // write. Ten integer digits are left, which still clears the domain's 1e9 magnitude cap
        // tenfold. This column and accounts.opening_balance must not drift apart.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            select numeric_precision, numeric_scale
            from information_schema.columns
            where table_name = 'transactions' and column_name = 'amount'
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        await Assert.That(reader.GetInt32(0)).IsEqualTo(14);
        await Assert.That(reader.GetInt32(1)).IsEqualTo(4);
    }

    [Test]
    public async Task Database_RejectsATransactionReferencingAnAccountInAnotherBudget()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        Guid budgetA = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid budgetB = await host.SeedBudgetAsync("google-b", "b@example.com");
        Guid accountA;
        await using (BudgetoidDbContext db = new(options, new TestBudgetContext(budgetA)))
        {
            Account account = Account.Create(budgetA, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            accountA = account.Id;
        }

        // Act — this deliberately bypasses CreateTransactionHandler. Going through the handler would
        // make the test pass while proving nothing: the BudgetIsolation-filtered repository returns
        // null for budget A's account and the handler throws ValidationException before PostgreSQL
        // ever sees the row. Only a direct DbSet.Add proves the composite
        // (AccountId, BudgetId) foreign key is what refuses the write.
        await using BudgetoidDbContext crossBudgetDb = new(options, new TestBudgetContext(budgetB));
        crossBudgetDb.Transactions.Add(Transaction.Create(
            budgetB,
            accountA,
            1m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            "Should Fail",
            UtcNow()));
        DbUpdateException? caught = null;
        try
        {
            await crossBudgetDb.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That((caught!.InnerException as PostgresException)?.SqlState)
            .IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Test]
    public async Task Database_RejectsATransactionReferencingACategoryInAnotherBudget()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        Guid budgetA = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid budgetB = await host.SeedBudgetAsync("google-b", "b@example.com");
        Guid categoryA;
        await using (BudgetoidDbContext db = new(options, new TestBudgetContext(budgetA)))
        {
            CategoryGroup categoryGroup = CategoryGroup.Create(budgetA, "Essentials", null, 0, UtcNow());
            db.CategoryGroups.Add(categoryGroup);
            Category category = Category.Create(budgetA, categoryGroup.Id, "Groceries", null, 0, UtcNow());
            db.Categories.Add(category);
            await db.SaveChangesAsync();
            categoryA = category.Id;
        }

        // Budget B gets its own account so the account foreign key is satisfied and the category
        // foreign key is unambiguously the constraint under test.
        Guid accountB;
        await using (BudgetoidDbContext db = new(options, new TestBudgetContext(budgetB)))
        {
            Account account = Account.Create(budgetB, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            accountB = account.Id;
        }

        // Act — this deliberately bypasses CreateTransactionHandler. Going through the handler would
        // make the test pass while proving nothing: the BudgetIsolation-filtered repository returns
        // null for budget A's category and the handler throws ValidationException before PostgreSQL
        // ever sees the row. Only a direct DbSet.Add proves the composite
        // (CategoryId, BudgetId) foreign key is what refuses the write.
        await using BudgetoidDbContext crossBudgetDb = new(options, new TestBudgetContext(budgetB));
        Transaction transaction = Transaction.Create(
            budgetB,
            accountB,
            1m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            "Should Fail",
            UtcNow());
        transaction.AssignCategory(categoryA);
        crossBudgetDb.Transactions.Add(transaction);
        DbUpdateException? caught = null;
        try
        {
            await crossBudgetDb.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That((caught!.InnerException as PostgresException)?.SqlState)
            .IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Test]
    public async Task Database_RejectsATransactionReferencingAPayeeInAnotherBudget()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        Guid budgetA = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid budgetB = await host.SeedBudgetAsync("google-b", "b@example.com");
        Guid payeeA;
        await using (BudgetoidDbContext db = new(options, new TestBudgetContext(budgetA)))
        {
            Payee payee = Payee.Create(budgetA, "Corner Shop", UtcNow());
            db.Payees.Add(payee);
            await db.SaveChangesAsync();
            payeeA = payee.Id;
        }

        // Budget B gets its own account so the account foreign key is satisfied and the payee
        // foreign key is unambiguously the constraint under test.
        Guid accountB;
        await using (BudgetoidDbContext db = new(options, new TestBudgetContext(budgetB)))
        {
            Account account = Account.Create(budgetB, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            accountB = account.Id;
        }

        // Act — this deliberately bypasses CreateTransactionHandler. Going through the handler would
        // make the test pass while proving nothing: the BudgetIsolation-filtered repository returns
        // null for budget A's payee and the handler throws ValidationException before PostgreSQL
        // ever sees the row. Only a direct DbSet.Add proves the composite
        // (PayeeId, BudgetId) foreign key is what refuses the write.
        await using BudgetoidDbContext crossBudgetDb = new(options, new TestBudgetContext(budgetB));
        Transaction transaction = Transaction.Create(
            budgetB,
            accountB,
            1m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            "Should Fail",
            UtcNow());
        transaction.AssignPayee(payeeA);
        crossBudgetDb.Transactions.Add(transaction);
        DbUpdateException? caught = null;
        try
        {
            await crossBudgetDb.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That((caught!.InnerException as PostgresException)?.SqlState)
            .IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Test]
    public async Task Database_AcceptsATransactionWithoutAPayeeOrCategory()
    {
        // Arrange — regression guard for the composite foreign keys: payee_id and category_id must
        // stay nullable, and PostgreSQL MATCH SIMPLE must keep skipping a multi-column foreign key
        // check whenever any of its columns is NULL. Making the references non-nullable, or forcing
        // the check, would break every uncategorised transaction.
        await using RepositoryTestHost host = await StartHostAsync();
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid transactionId;

        // Act
        await using (BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId)))
        {
            Account account = Account.Create(budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());
            db.Accounts.Add(account);
            await db.SaveChangesAsync();

            Transaction transaction = Transaction.Create(
                budgetId,
                account.Id,
                1m,
                UsdMinorUnit,
                new DateOnly(2026, 6, 12),
                "No payee, no category",
                UtcNow());
            db.Transactions.Add(transaction);
            await db.SaveChangesAsync();
            transactionId = transaction.Id;
        }

        await using BudgetoidDbContext readDb = new(options, new TestBudgetContext(budgetId));
        Transaction? stored = await readDb.Transactions
            .SingleOrDefaultAsync(transaction => transaction.Id == transactionId);

        // Assert
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.PayeeId).IsNull();
        await Assert.That(stored.CategoryId).IsNull();
        await Assert.That(stored.BudgetId).IsEqualTo(budgetId);
    }

    [Test]
    [Arguments("1000000000.01")]
    [Arguments("-1000000000.01")]
    public async Task Database_RejectsAnAmountBeyondTheMagnitudeLimit(string amount)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid accountId = await SeedAccountAsync(host, budgetId);
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act — raw Npgsql on purpose: Transaction.Create refuses this amount client-side, so an
        // EF-based write proves nothing about the schema. Both signs, because the rule is on the
        // magnitude and a constraint written without abs() would refuse only one of them.
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            connection, budgetId, accountId, Money(amount));

        // Assert — the constraint name is asserted next to the SQLSTATE because any other check on
        // this table would raise 23514 too, and the test would then pass on the wrong rejection.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.CheckViolation);
        await Assert.That(exception.ConstraintName).IsEqualTo("CK_transactions_amount");
    }

    [Test]
    [Arguments("1000000000")]
    [Arguments("-1000000000")]
    public async Task Database_AcceptsAnAmountAtTheMagnitudeLimit(string amount)
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid accountId = await SeedAccountAsync(host, budgetId);
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act — the domain refuses only above this value, so the limit itself is legitimate data.
        // Without this case a constraint written with < instead of <= would look correct.
        await InsertTransactionAsync(connection, budgetId, accountId, Money(amount));

        // Assert
        await Assert.That(await CountTransactionsAsync(connection, budgetId)).IsEqualTo(1L);
    }

    [Test]
    public async Task Database_DeletesEveryTransactionInTheAmbientBudget()
    {
        // Arrange — two rows, because a delete written to take one would still empty a single-row
        // table and look correct.
        await using RepositoryTestHost host = await StartHostAsync();
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid accountId = await SeedAccountAsync(host, budgetId);
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await InsertTransactionAsync(connection, budgetId, accountId, Money("-10"));
        await InsertTransactionAsync(connection, budgetId, accountId, Money("-20"));
        await Assert.That(await CountTransactionsAsync(connection, budgetId)).IsEqualTo(2L);

        // Act
        await using (BudgetoidDbContext db = new(options, new TestBudgetContext(budgetId)))
        {
            await new TransactionRepository(db).DeleteAllForAmbientBudgetAsync();
        }

        // Assert — counted on the container account, not through the query filter that did the
        // scoping, so the observation cannot inherit the mistake it is looking for.
        await Assert.That(await CountTransactionsAsync(connection, budgetId)).IsEqualTo(0L);
    }

    [Test]
    public async Task Database_LeavesAnotherBudgetsTransactionsInPlace()
    {
        // Arrange — two tenants with a row each. This is the test that proves the BudgetIsolation
        // filter is what scopes the delete: without it, a repository method that simply emptied the
        // table would pass the test above.
        await using RepositoryTestHost host = await StartHostAsync();
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        Guid budgetA = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid budgetB = await host.SeedBudgetAsync("google-b", "b@example.com");
        Guid accountA = await SeedAccountAsync(host, budgetA);
        Guid accountB = await SeedAccountAsync(host, budgetB);
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await InsertTransactionAsync(connection, budgetA, accountA, Money("-10"));
        await InsertTransactionAsync(connection, budgetB, accountB, Money("-20"));
        await Assert.That(await CountTransactionsAsync(connection, budgetA)).IsEqualTo(1L);
        await Assert.That(await CountTransactionsAsync(connection, budgetB)).IsEqualTo(1L);

        // Act — the ambient budget is A's, and the method takes no budget id at all.
        await using (BudgetoidDbContext db = new(options, new TestBudgetContext(budgetA)))
        {
            await new TransactionRepository(db).DeleteAllForAmbientBudgetAsync();
        }

        // Assert
        await Assert.That(await CountTransactionsAsync(connection, budgetA)).IsEqualTo(0L);
        await Assert.That(await CountTransactionsAsync(connection, budgetB)).IsEqualTo(1L);
    }

    /// <summary>
    /// The losing side of two concurrent erasures of one account, which lands here first whenever the
    /// budget holds any movement.
    /// </summary>
    /// <remarks>
    /// Both requests read these rows; the loser blocks on the winner's locks until it commits and
    /// then deletes nothing, so EF counts zero affected rows where it expected one per row and raises
    /// a conflict. The budget holds no transactions, which is the entire post-condition — reporting a
    /// failure would answer a completed erasure with a 500.
    /// <para>
    /// The vanishing is staged rather than raced, for the reason
    /// <see cref="ConcurrentDeleteInterceptor" /> gives: the state is what the code responds to, and
    /// this way it is the state on every run rather than on a lucky one. That the state really does
    /// raise the conflict is proved in
    /// <c>UserRepositoryTests.Database_WhenTheRowIsDeletedBetweenTheReadAndTheSave_RaisesAConcurrencyConflict</c>,
    /// and again by the escape test below, which reaches it here and watches it come out.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DeleteAllForAmbientBudgetAsync_WhenAnotherRequestDeletedTheRowsFirst_Completes()
    {
        // Arrange — two rows, both taken by the winner after this call has read them.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid accountId = await SeedAccountAsync(host, budgetId);
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await InsertTransactionAsync(connection, budgetId, accountId, Money("-10"));
        await InsertTransactionAsync(connection, budgetId, accountId, Money("-20"));
        ConcurrentDeleteInterceptor winner = new(
            host.ConnectionString, "delete from transactions where budget_id = @id", budgetId);
        await using BudgetoidDbContext db = new(
            CreateOptions(host, winner), new TestBudgetContext(budgetId));
        var repository = new TransactionRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.DeleteAllForAmbientBudgetAsync());

        // Assert — nothing escaped, the winner really did take both rows, and the budget holds no
        // transactions, which is the entire post-condition.
        await Assert.That(escaped).IsNull();
        await Assert.That(winner.Deleted).IsEqualTo(2);
        await Assert.That(await CountTransactionsAsync(connection, budgetId)).IsEqualTo(0L);
    }

    /// <summary>
    /// The detach inside that catch, which is what lets the enclosing unit of work carry on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Leave a conflicting entry Deleted and the next save on this request-scoped context re-flushes a
    /// delete that has already been answered — and erasure's next step is exactly such a save,
    /// <c>UserRepository.DeleteAsync</c> on the same context. So without the detach the swallow buys
    /// nothing.
    /// </para>
    /// <para>
    /// <b>Two rows, and the second row is the whole test.</b> EF reports only the first mismatching
    /// command's entries on <c>DbUpdateConcurrencyException.Entries</c>, so a detach driven by the
    /// exception rather than by the change tracker sweeps one entry and leaves every surplus one
    /// Deleted. One row passes under either sweep and therefore proves nothing; from two rows up, only
    /// the tracker sweep leaves the context clean.
    /// </para>
    /// <para>
    /// Asserted on the tracker rather than on nothing having been thrown, because a surplus entry
    /// throws nothing here: it waits for the next save. What that costs is pinned by the test below.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DeleteAllForAmbientBudgetAsync_WhenAnotherRequestDeletedTheRowsFirst_LeavesNothingToReplay()
    {
        // Arrange — two rows, one more than the exception can report.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid accountId = await SeedAccountAsync(host, budgetId);
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await InsertTransactionAsync(connection, budgetId, accountId, Money("-10"));
        await InsertTransactionAsync(connection, budgetId, accountId, Money("-20"));
        ConcurrentDeleteInterceptor winner = new(
            host.ConnectionString, "delete from transactions where budget_id = @id", budgetId);
        await using BudgetoidDbContext db = new(
            CreateOptions(host, winner), new TestBudgetContext(budgetId));

        // Act
        await new TransactionRepository(db).DeleteAllForAmbientBudgetAsync();

        // Assert — the winner really did take both rows, and the context is left holding neither of
        // them. Counted over every tracked transaction and not only the Deleted ones: this call is the
        // only thing that has tracked a transaction on this context, so any survivor at all is one the
        // catch failed to sweep.
        await Assert.That(winner.Deleted).IsEqualTo(2);
        await Assert.That(db.ChangeTracker.Entries<Transaction>().Count()).IsEqualTo(0);
    }

    /// <summary>
    /// What a surplus Deleted entry actually costs, which is worse than the 500 it looks like: the
    /// account is not erased at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Erasure's next step is <c>UserRepository.DeleteAsync</c> on this same context. A transaction
    /// left Deleted re-flushes there, EF finds a row already gone and raises a conflict naming a
    /// <see cref="Transaction" /> — which that method's <c>when</c> clause correctly refuses to
    /// swallow, since it swallows only conflicts over <c>users</c> rows it marked itself. The
    /// transaction rolls back and the <c>users</c> row survives, so the account whose erasure was
    /// requested twice is erased neither time.
    /// </para>
    /// <para>
    /// The real next call rather than a bare <c>SaveChangesAsync</c> standing in for it, and the
    /// surviving-row count rather than the absence of an exception: a test that only watched for a
    /// throw would go green again the day a surplus entry came back and merely moved which call
    /// raised it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DeleteAllForAmbientBudgetAsync_WhenAnotherRequestDeletedTheRowsFirst_LetsTheErasureFinish()
    {
        // Arrange — the same two rows. The interceptor fires once, so the users delete below is not
        // preceded by an out-of-band delete of its own: the row it removes is really there to remove.
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid userId, Guid budgetId) = await host.SeedOwnerAsync("google-a", "a@example.com");
        Guid accountId = await SeedAccountAsync(host, budgetId);
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await InsertTransactionAsync(connection, budgetId, accountId, Money("-10"));
        await InsertTransactionAsync(connection, budgetId, accountId, Money("-20"));
        ConcurrentDeleteInterceptor winner = new(
            host.ConnectionString, "delete from transactions where budget_id = @id", budgetId);
        await using BudgetoidDbContext db = new(
            CreateOptions(host, winner), new TestBudgetContext(budgetId));
        await new TransactionRepository(db).DeleteAllForAmbientBudgetAsync();

        // Act — the step EraseAccountHandler takes next, unabridged and on the same context.
        Exception? escaped = await CaptureAsync(() => new UserRepository(db).DeleteAsync(userId));

        // Assert — counted on the container superuser, not through the context that was asked to do
        // the erasing, so the observation cannot inherit the failure it is looking for.
        await Assert.That(winner.Deleted).IsEqualTo(2);
        await Assert.That(escaped).IsNull();
        await Assert.That(await CountUsersAsync(connection, userId)).IsEqualTo(0L);
    }

    /// <summary>
    /// The narrowing on that catch, which is the half a widened <c>when</c> clause would take away.
    /// </summary>
    /// <remarks>
    /// A conflict carries no SQLSTATE, so the entries are what the method filters on: every
    /// conflicting row must be a transaction it marked Deleted itself. A conflict over anything else
    /// riding along on the same <c>SaveChangesAsync</c> is a failure this method does not model, and
    /// swallowing it would report an erasure that the rolled-back transaction did not perform.
    /// </remarks>
    [Test]
    public async Task DeleteAllForAmbientBudgetAsync_WhenTheConflictNamesAnotherEntity_LetsItEscape()
    {
        // Arrange — an empty category group is removed out of band and then marked Deleted here, so
        // the one delete that finds nothing is a category_groups row. The transaction delete this
        // method actually makes is sound and affects its row. No interceptor: the conflicting row is
        // already gone before the call starts, which is all the mismatch needs.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid accountId = await SeedAccountAsync(host, budgetId);
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await InsertTransactionAsync(connection, budgetId, accountId, Money("-10"));
        Guid categoryGroupId = await SeedCategoryGroupAsync(host, budgetId);
        await using BudgetoidDbContext db = new(CreateOptions(host), new TestBudgetContext(budgetId));
        CategoryGroup categoryGroup = await db.CategoryGroups.SingleAsync(
            group => group.Id == categoryGroupId);
        await DeleteRowAsync(connection, "delete from category_groups where id = @id", categoryGroupId);
        db.CategoryGroups.Remove(categoryGroup);
        var repository = new TransactionRepository(db);

        // Act
        Exception? escaped = await CaptureAsync(() => repository.DeleteAllForAmbientBudgetAsync());

        // Assert
        await Assert.That(escaped).IsTypeOf<DbUpdateConcurrencyException>();
    }

    /// <summary>
    /// An account that went out from under the caller is reported as the missing account it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One of the two translation halves, and neither existed before.</b> Without them the escape
    /// test below is satisfied by a repository whose <c>catch</c> clauses have been deleted outright,
    /// which would answer the same race with an untranslated <see cref="DbUpdateException" /> — a 500
    /// naming a foreign key, on a request whose only fault is that somebody removed the account while
    /// the edit was in flight.
    /// </para>
    /// <para>
    /// <b>The transaction is read back through <see cref="TransactionRepository.GetByIdAsync" /></b>,
    /// which is the query the handler uses and the one that puts a real row into the change tracker.
    /// The account it is then pointed at is an id nothing answers to, which is the same state a
    /// concurrent account deletion leaves behind and is arranged the way
    /// <c>RepositoryConstraintAttributionTests.AddCategory_WithAnUnknownCategoryGroup_TranslatesItsOwnForeignKey</c>
    /// arranges its own: a fresh <see cref="Guid.CreateVersion7" />, rather than a row deleted out of
    /// band, because the two are indistinguishable to the constraint and only one of them can fail for
    /// an unrelated reason.
    /// </para>
    /// <para>
    /// The key is asserted, not just the type. A <see cref="ValidationException" /> carrying the wrong
    /// field renders against the wrong input on the client, and the two <c>catch</c> clauses in this
    /// method differ in nothing else — swap their bodies and only this assertion and its neighbour
    /// notice.
    /// </para>
    /// </remarks>
    [Test]
    public async Task UpdateAsync_WhenTheAccountIsGone_TranslatesItsOwnForeignKey()
    {
        // Arrange — a real transaction, read back through the repository.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid accountId = await SeedAccountAsync(host, budgetId);
        Guid transactionId = await SeedTransactionAsync(host, budgetId, accountId);
        await using BudgetoidDbContext db = new(CreateOptions(host), new TestBudgetContext(budgetId));
        var repository = new TransactionRepository(db);
        Transaction transaction = await repository.GetByIdAsync(transactionId)
            ?? throw new InvalidOperationException(
                "The seeded transaction was not readable through the repository before the act.");

        // The edit a caller made against an account that has since gone.
        transaction.Update(
            Guid.CreateVersion7(), Money("-15"), UsdMinorUnit, new DateOnly(2026, 6, 13), "Edited");

        // Act
        Exception? escaped = await CaptureAsync(() => repository.UpdateAsync(transaction));

        // Assert
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<ValidationException>();
        await Assert.That(((ValidationException)escaped!).Errors.ContainsKey(nameof(Transaction.AccountId)))
            .IsTrue();
    }

    /// <summary>
    /// A category that went out from under the caller is reported as the missing category it is.
    /// </summary>
    /// <remarks>
    /// The other translation half. It sits beside the account one rather than being folded into it
    /// because the two are separate <c>catch</c> clauses naming separate constraints, and a single test
    /// could only reach one of them — the account is resolved first, so a transaction pointed at two
    /// missing rows at once would never exercise this clause at all.
    /// </remarks>
    [Test]
    public async Task UpdateAsync_WhenTheCategoryIsGone_TranslatesItsOwnForeignKey()
    {
        // Arrange — the account stays real, so the category foreign key is unambiguously the
        // constraint under test.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid accountId = await SeedAccountAsync(host, budgetId);
        Guid transactionId = await SeedTransactionAsync(host, budgetId, accountId);
        await using BudgetoidDbContext db = new(CreateOptions(host), new TestBudgetContext(budgetId));
        var repository = new TransactionRepository(db);
        Transaction transaction = await repository.GetByIdAsync(transactionId)
            ?? throw new InvalidOperationException(
                "The seeded transaction was not readable through the repository before the act.");

        transaction.AssignCategory(Guid.CreateVersion7());

        // Act
        Exception? escaped = await CaptureAsync(() => repository.UpdateAsync(transaction));

        // Assert
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<ValidationException>();
        await Assert.That(((ValidationException)escaped!).Errors.ContainsKey(nameof(Transaction.CategoryId)))
            .IsTrue();
    }

    /// <summary>
    /// A foreign-key violation this repository does not model is not dressed up as a missing account or
    /// a missing category.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The narrowing half for <c>UpdateAsync</c>, and the reason its two <c>catch</c> clauses carry
    /// constraint names at all.</b> Widen either <c>when</c> clause to the bare <c>23503</c> and this
    /// arrangement — a payee pointed at a budget that does not exist — comes back to the caller as
    /// "Account was not found.", keyed on <see cref="Transaction.AccountId" />, and the client renders
    /// it against an account the test can still read. A confident, specific, false 400.
    /// </para>
    /// <para>
    /// <b>The intruder is the sharpest one available on this table</b>, and it is
    /// <c>RepositoryConstraintAttributionTests</c>' own: <c>payees</c> is budget-owned, so a payee
    /// naming a budget nobody created raises <c>23503</c> from
    /// <c>FK_payees_budgets_budget_id</c> — the same SQLSTATE both of this method's clauses filter,
    /// from a table the repository has no port for. Nothing about the transaction is wrong: it is
    /// re-pointed at the account it already had, so both constraints this method does model are
    /// satisfied and exactly one rule in the batch is broken.
    /// </para>
    /// <para>
    /// The transaction is genuinely modified rather than merely tracked, so the batch really does carry
    /// the <c>UPDATE</c> this method exists to send. A test that only tracked it would be measuring the
    /// payee insert through a method that happened to be holding the door.
    /// </para>
    /// <para>
    /// The SQLSTATE is asserted beside the constraint name, which is what keeps this a narrowing test
    /// rather than a test that any failure escapes: a violation of some entirely different kind would
    /// satisfy "neither of the transaction foreign keys" without ever exercising the filters. The
    /// expected behaviour is that an unmodelled violation <b>propagates</b> — a 500 naming the real
    /// constraint beats a 400 that lies.
    /// </para>
    /// </remarks>
    [Test]
    public async Task UpdateAsync_WhenATrackedRowBreaksAnotherForeignKey_LetsTheViolationEscape()
    {
        // Arrange — a transaction that is entirely healthy, and a payee filed against a budget that
        // was never created.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid accountId = await SeedAccountAsync(host, budgetId);
        Guid transactionId = await SeedTransactionAsync(host, budgetId, accountId);
        await using BudgetoidDbContext db = new(CreateOptions(host), new TestBudgetContext(budgetId));
        var repository = new TransactionRepository(db);
        Transaction transaction = await repository.GetByIdAsync(transactionId)
            ?? throw new InvalidOperationException(
                "The seeded transaction was not readable through the repository before the act.");

        db.Payees.Add(Payee.Create(Guid.CreateVersion7(), "Corner Shop", UtcNow()));

        // A real edit, against the account it already has — so the UPDATE is in the batch and is
        // beyond reproach.
        transaction.Update(accountId, Money("-15"), UsdMinorUnit, new DateOnly(2026, 6, 13), "Edited");

        // Act
        Exception? escaped = await CaptureAsync(() => repository.UpdateAsync(transaction));

        // Assert
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();

        // And it really was a foreign-key violation — on a rule that is not this repository's to speak
        // for.
        await Assert.That(SqlStateOf(escaped)).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(ConstraintNameOf(escaped)).IsEqualTo(PayeeBudgetForeignKey);
        await Assert.That(ConstraintNameOf(escaped))
            .IsNotEqualTo(TransactionConfiguration.AccountForeignKeyName);
        await Assert.That(ConstraintNameOf(escaped))
            .IsNotEqualTo(TransactionConfiguration.CategoryForeignKeyName);
    }

    /// <summary>
    /// The foreign key <c>payees.budget_id</c> carries, spelled out rather than read off the
    /// configuration that renders it — a test taking its expectation from the code under test agrees
    /// with that code by construction. <c>RepositoryConstraintAttributionTests</c> spells the same name
    /// as a literal for the same reason.
    /// </summary>
    private const string PayeeBudgetForeignKey = "FK_payees_budgets_budget_id";

    /// <summary>
    /// Writes one transaction through the domain factory and returns its id, which the raw-SQL
    /// <see cref="InsertTransactionAsync" /> above cannot hand back.
    /// </summary>
    /// <remarks>
    /// Through a context of its own, so the row is committed before the act rather than travelling
    /// inside the unit of work under test — and through <see cref="Transaction.Create" />, so a seeded
    /// row is one the application could really have written.
    /// </remarks>
    private static async Task<Guid> SeedTransactionAsync(
        RepositoryTestHost host,
        Guid budgetId,
        Guid accountId)
    {
        await using BudgetoidDbContext db = new(CreateOptions(host), new TestBudgetContext(budgetId));
        Transaction transaction = Transaction.Create(
            budgetId,
            accountId,
            Money("-10"),
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            "Seeded",
            UtcNow());
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        return transaction.Id;
    }

    /// <summary>
    /// Names the constraint PostgreSQL actually refused on, or <see langword="null" /> when the
    /// escaping exception never reached the database at all.
    /// </summary>
    private static string? ConstraintNameOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.ConstraintName
            : null;

    /// <summary>
    /// The SQLSTATE PostgreSQL refused with, or <see langword="null" /> when nothing did. Read beside
    /// the constraint name so a narrowing test can say the violation it staged really is the kind the
    /// filter has to tell apart.
    /// </summary>
    private static string? SqlStateOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.SqlState
            : null;

    /// <summary>
    /// Writes an empty category group into <paramref name="budgetId" /> and returns its id. Empty on
    /// purpose: nothing references it, so its row can be removed out of band without a RESTRICT edge
    /// refusing the removal.
    /// </summary>
    private static async Task<Guid> SeedCategoryGroupAsync(RepositoryTestHost host, Guid budgetId)
    {
        await using BudgetoidDbContext db = new(CreateOptions(host), new TestBudgetContext(budgetId));
        CategoryGroup categoryGroup = CategoryGroup.Create(budgetId, "Essentials", null, 0, UtcNow());
        db.CategoryGroups.Add(categoryGroup);
        await db.SaveChangesAsync();
        return categoryGroup.Id;
    }

    /// <summary>
    /// Runs one parameterised delete on the container superuser, standing in for a row another
    /// request took.
    /// </summary>
    private static async Task DeleteRowAsync(NpgsqlConnection connection, string sql, Guid id)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" />
    /// when nothing did. Deliberately untyped: the question every test that uses it asks is
    /// <i>which</i> exception surfaces — a <see cref="ValidationException" /> where a
    /// <see cref="DbUpdateException" /> was expected, or the reverse — so catching a specific one here
    /// would decide the answer in the helper.
    /// </summary>
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Fixed UTC instant for every row these tests write. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static DateTime UtcNow() =>
        new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Parses a money literal the culture-invariant way. The values arrive as strings because
    /// <c>decimal</c> is not a legal attribute argument type.
    /// </summary>
    private static decimal Money(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes the account a transaction needs to satisfy the composite account reference, and
    /// returns its id.
    /// </summary>
    private static async Task<Guid> SeedAccountAsync(RepositoryTestHost host, Guid budgetId)
    {
        await using BudgetoidDbContext db = new(CreateOptions(host), new TestBudgetContext(budgetId));
        Account account = Account.Create(budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, UtcNow());
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private static async Task InsertTransactionAsync(
        NpgsqlConnection connection,
        Guid budgetId,
        Guid accountId,
        decimal amount)
    {
        await using NpgsqlCommand command = BuildInsert(connection, budgetId, accountId, amount);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(
        NpgsqlConnection connection,
        Guid budgetId,
        Guid accountId,
        decimal amount)
    {
        await using NpgsqlCommand command = BuildInsert(connection, budgetId, accountId, amount);

        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected PostgresException.");
    }

    private static NpgsqlCommand BuildInsert(
        NpgsqlConnection connection,
        Guid budgetId,
        Guid accountId,
        decimal amount)
    {
        NpgsqlCommand command = new(
            """
            insert into transactions (id, budget_id, account_id, amount, date, description, created_at_utc)
            values (@id, @budget_id, @account_id, @amount, @date, @description, @created_at_utc)
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("budget_id", budgetId);
        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.AddWithValue("amount", amount);
        command.Parameters.AddWithValue("date", new DateOnly(2026, 6, 12));
        command.Parameters.AddWithValue("description", "At the limit");
        command.Parameters.AddWithValue("created_at_utc", UtcNow());
        return command;
    }

    private static Task<long> CountTransactionsAsync(NpgsqlConnection connection, Guid budgetId) =>
        CountAsync(connection, "select count(*) from transactions where budget_id = @id", budgetId);

    /// <summary>
    /// Counts the rows <paramref name="userId" /> still has in <c>users</c>, which is how a test says
    /// "the erasure completed" without asking the context that performed it.
    /// </summary>
    private static Task<long> CountUsersAsync(NpgsqlConnection connection, Guid userId) =>
        CountAsync(connection, "select count(*) from users where id = @id", userId);

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql, Guid id)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", id);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the
        // query changed shape, and that should fail loudly here instead of at the assertion.
        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{sql}', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Builds the context options these tests connect through.
    /// </summary>
    /// <param name="host">The container this context connects to.</param>
    /// <param name="interceptors">
    /// Interceptors to attach, for the one test that needs something to happen inside a
    /// <c>SaveChangesAsync</c>. Empty for every other caller, which is why it is a
    /// <see langword="params" /> tail rather than a second factory.
    /// </param>
    private static DbContextOptions<BudgetoidDbContext> CreateOptions(
        RepositoryTestHost host,
        params IInterceptor[] interceptors)
    {
        return new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .AddInterceptors(interceptors)
            .Options;
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
