using System.Globalization;
using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Transactions;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

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

    private static async Task<long> CountTransactionsAsync(NpgsqlConnection connection, Guid budgetId)
    {
        await using NpgsqlCommand command = new(
            "select count(*) from transactions where budget_id = @id",
            connection);
        command.Parameters.AddWithValue("id", budgetId);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the
        // query changed shape, and that should fail loudly here instead of at the assertion.
        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from 'transactions', got '{unexpected ?? "null"}'."),
        };
    }

    private static DbContextOptions<BudgetoidDbContext> CreateOptions(RepositoryTestHost host)
    {
        return new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options;
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
