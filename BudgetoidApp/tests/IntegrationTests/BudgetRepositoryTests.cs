using Domain.Accounts;
using Domain.Budgets;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Transactions;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

public sealed class BudgetRepositoryTests
{
    [Test]
    public async Task Budgets_WithTheSameNameForOneUser_AreRejected()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new BudgetRepository(db);
        bool firstAdded = await repository.TryAddAsync(Budget.CreateDefault(userId, UtcAt(hour: 10)));

        // Act — the real PostgreSQL unique violation must surface as false, not as an escaping
        // DbUpdateException, because the provisioning handler's re-read path depends on it.
        bool secondAdded = await repository.TryAddAsync(Budget.CreateDefault(userId, UtcAt(hour: 11)));

        // Assert
        await Assert.That(firstAdded).IsTrue();
        await Assert.That(secondAdded).IsFalse();
        await Assert.That(await db.Budgets.CountAsync(budget => budget.UserId == userId)).IsEqualTo(1);
    }

    [Test]
    public async Task FindFirstForUserAsync_ReturnsTheEarliestBudget()
    {
        // Arrange — inserted newest-first, so a repository ordering by insertion or by Guid.CompareTo
        // over UUID v7 ids would return the wrong row. The ordering is a documented contract on
        // IBudgetRepository, which is what keeps InMemoryBudgetRepository honest.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new BudgetRepository(db);
        Budget later = Budget.Create(userId, "Later", UtcAt(hour: 18));
        Budget earlier = Budget.Create(userId, "Earlier", UtcAt(hour: 6));
        await repository.TryAddAsync(later);
        await repository.TryAddAsync(earlier);

        // Act
        Budget? found = await repository.FindFirstForUserAsync(userId);

        // Assert
        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Id).IsEqualTo(earlier.Id);
        await Assert.That(found.Name).IsEqualTo("Earlier");
    }

    [Test]
    public async Task FindFirstForUserAsync_WithNoBudget_ReturnsNull()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new BudgetRepository(db);

        // Act
        Budget? found = await repository.FindFirstForUserAsync(userId);

        // Assert
        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task HasTransactionsAsync_ReturnsTrue_WhenTheBudgetHasATransaction()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await SeedTransactionAsync(host, budgetId);
        await using BudgetoidDbContext db = CreateDb(host, budgetId);
        var repository = new BudgetRepository(db);

        // Act — no budgetId argument by design: tenancy comes from the ambient IBudgetContext
        // through the BudgetIsolation filter, the only authorization this system has. A caller-
        // supplied budget id would be an unchecked claim of ownership.
        bool hasTransactions = await repository.HasTransactionsAsync();

        // Assert
        await Assert.That(hasTransactions).IsTrue();
    }

    [Test]
    public async Task HasTransactionsAsync_ReturnsFalse_WhenTheBudgetHasNoTransactions()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using BudgetoidDbContext db = CreateDb(host, budgetId);
        var repository = new BudgetRepository(db);

        // Act
        bool hasTransactions = await repository.HasTransactionsAsync();

        // Assert
        await Assert.That(hasTransactions).IsFalse();
    }

    [Test]
    public async Task HasTransactionsAsync_ReturnsFalse_WhenOnlyAnotherBudgetHasTransactions()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid budgetWithMovement = await host.SeedAdditionalBudgetAsync(userId, "Household");
        Guid emptyBudget = await host.SeedAdditionalBudgetAsync(userId, "Side Project");
        await SeedTransactionAsync(host, budgetWithMovement);
        await using BudgetoidDbContext db = CreateDb(host, emptyBudget);
        var repository = new BudgetRepository(db);

        // Act
        bool hasTransactions = await repository.HasTransactionsAsync();

        // Assert — this is the test that goes red the day anyone adds IgnoreQueryFilters() to the
        // implementation: without the BudgetIsolation filter the query sees the other budget's row
        // and reports true, which would let a delete be refused for someone else's money movement.
        await Assert.That(hasTransactions).IsFalse();
    }

    [Test]
    public async Task Database_RefusesToDeleteABudgetThatHoldsTransactions()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await SeedTransactionAsync(host, budgetId);

        // Act — raw Npgsql on purpose. Under Restrict, EF raises a client-side
        // InvalidOperationException before any statement reaches PostgreSQL, so an EF-based delete
        // would prove nothing about the schema. Only real SQL exercises the constraint.
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            host,
            "delete from budgets where id = @id",
            budgetId);

        // Assert
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Test]
    public async Task Database_AllowsDeletingABudgetWithStructureButNoTransactions()
    {
        // Arrange — structure only: no recorded money movement, so the budget was a mistake and
        // its accounts, groups, categories and payees go with it.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-1", "person@example.com");
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            seed.Accounts.Add(Account.Create(
                budgetId, "Checking", AccountType.Checking, 0m, "USD", SeedInstant));
            CategoryGroup group = CategoryGroup.Create(budgetId, "Everyday", null, 0, SeedInstant);
            seed.CategoryGroups.Add(group);
            seed.Categories.Add(Category.Create(budgetId, group.Id, "Groceries", null, 0, SeedInstant));
            seed.Payees.Add(Payee.Create(budgetId, "Corner Shop", SeedInstant));
            await seed.SaveChangesAsync();
        }

        // Act — raw Npgsql for the same reason as above: the cascade is a schema behaviour, and
        // EF's own delete would never send the statement that exercises it.
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using (NpgsqlCommand delete = new("delete from budgets where id = @id", connection))
        {
            delete.Parameters.AddWithValue("id", budgetId);
            await delete.ExecuteNonQueryAsync();
        }

        // Assert
        await Assert.That(await CountRowsAsync(connection, "budgets", budgetId)).IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "accounts", budgetId)).IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "category_groups", budgetId)).IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "categories", budgetId)).IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "payees", budgetId)).IsEqualTo(0L);
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime"/>, so <see cref="DateTimeKind.Utc"/> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Writes one transaction into <paramref name="budgetId"/>, together with the account it needs
    /// to satisfy the composite account reference.
    /// </summary>
    private static async Task SeedTransactionAsync(RepositoryTestHost host, Guid budgetId)
    {
        await using BudgetoidDbContext db = CreateDb(host, budgetId);
        Account account = Account.Create(
            budgetId, "Checking", AccountType.Checking, 0m, "USD", SeedInstant);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        db.Transactions.Add(Transaction.Create(
            budgetId,
            account.Id,
            -10m,
            new DateOnly(2026, 6, 12),
            "Groceries",
            SeedInstant));
        await db.SaveChangesAsync();
    }

    private static async Task<long> CountRowsAsync(
        NpgsqlConnection connection,
        string table,
        Guid budgetId)
    {
        string column = table == "budgets" ? "id" : "budget_id";
        await using NpgsqlCommand command = new(
            $"select count(*) from {table} where {column} = @id",
            connection);
        command.Parameters.AddWithValue("id", budgetId);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the
        // query changed shape, and that should fail loudly here instead of at the assertion.
        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{table}', got '{unexpected ?? "null"}'."),
        };
    }

    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(
        RepositoryTestHost host,
        string sql,
        Guid budgetId)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", budgetId);

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

    /// <summary>
    /// Builds a context bound to an ambient budget. The parameterless overload leaves
    /// <c>IBudgetContext</c> null, which is fine for <c>Budget</c> (no query filter) but throws the
    /// moment a query touches a budget-isolated set such as <c>Transactions</c>.
    /// </summary>
    private static BudgetoidDbContext CreateDb(RepositoryTestHost host, Guid budgetId) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options,
        new TestBudgetContext(budgetId));

    private static BudgetoidDbContext CreateDb(RepositoryTestHost host) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options);

    private static DateTime UtcAt(int hour) =>
        new(2026, 7, 14, hour, 0, 0, DateTimeKind.Utc);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
