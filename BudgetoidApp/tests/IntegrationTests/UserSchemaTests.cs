using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Transactions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Covers what deleting a user row does to everything hanging off it. The rule the pair pins is
/// that a user is erasable exactly as far as their recorded money movement allows: structure goes
/// with them, transactions hold them in place. Both deletes are raw Npgsql on purpose — under
/// Restrict, EF raises a client-side <see cref="InvalidOperationException" /> before any statement
/// reaches PostgreSQL, so an EF-based delete would prove nothing about the schema. Only real SQL
/// exercises the constraint.
/// </summary>
public sealed class UserSchemaTests
{
    /// <summary>
    /// Minor unit of the USD account these tests seed. Precision is not what either of them is
    /// about; the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task Database_RefusesToDeleteAUserWhoseBudgetHoldsTransactions()
    {
        // Arrange — one user, one budget, one account and the transaction that makes the budget
        // unerasable. The refusal is two foreign keys deep: users -> budgets cascades, and the
        // cascade is what runs into the Restrict on transactions.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid budgetId = await host.SeedAdditionalBudgetAsync(userId, "Household");
        await SeedTransactionAsync(host, budgetId);

        // Act — raw Npgsql on purpose. Under Restrict, EF raises a client-side
        // InvalidOperationException before any statement reaches PostgreSQL, so an EF-based delete
        // would prove nothing about the schema. Only real SQL exercises the constraint.
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            host,
            "delete from users where id = @id",
            userId);

        // Assert — deliberately no ConstraintName assertion. The delete cascades into budgets, and
        // from there several Restrict foreign keys are eligible to refuse at once; which one
        // PostgreSQL names is decided by foreign-key creation order in the baseline migration, not
        // by anything the caller did. The refusal is the rule, the name is an artifact — pinning it
        // would not strengthen this test, it would make a migration reshuffle look like a
        // regression.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);

        // The rows below are not an extra: a statement that fails rolls back whole, and asserting
        // that is half the rule. A refusal that had already destroyed the budget on its way to
        // failing would be a catastrophe, and a SQLSTATE-only assertion cannot see it.
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await Assert.That(await CountRowsAsync(connection, "users", "id", userId)).IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "budgets", "user_id", userId)).IsEqualTo(1L);

        // The credential is on the same rolled-back statement, and it is the row whose survival
        // matters most: it is the only thing that resolves a sign-in to this account, so a refusal
        // that had taken it out would leave the user permanently unreachable while still holding
        // their email and their transactions.
        await Assert.That(await CountRowsAsync(connection, "credentials", "user_id", userId)).IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "accounts", "budget_id", budgetId)).IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "transactions", "budget_id", budgetId)).IsEqualTo(1L);
    }

    [Test]
    public async Task Database_AllowsDeletingAUserWhoseBudgetHoldsNoTransactions()
    {
        // Arrange — structure only: no recorded money movement, so nothing anchors the user and the
        // whole tree goes with them.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid budgetId = await host.SeedAdditionalBudgetAsync(userId, "Household");
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            seed.Accounts.Add(Account.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed("Checking"), AccountType.Checking, 0m, "USD", UsdMinorUnit, SeedInstant));
            CategoryGroup group = CategoryGroup.Create(
                Guid.CreateVersion7(),
                budgetId,
                SealedNarrative.Indexed("Everyday"),
                null,
                0,
                SeedInstant);
            seed.CategoryGroups.Add(group);
            seed.Categories.Add(Category.Create(
                Guid.CreateVersion7(),
                budgetId,
                group.Id,
                SealedNarrative.Indexed("Groceries"),
                null,
                0,
                SeedInstant));
            seed.Payees.Add(Payee.Create(Guid.CreateVersion7(), budgetId, SealedNarrative.Indexed("Corner Shop"), SeedInstant));
            await seed.SaveChangesAsync();
        }

        // Act — raw Npgsql for the same reason as above: the cascade is a schema behaviour, and
        // EF's own delete would never send the statement that exercises it.
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using (NpgsqlCommand delete = new("delete from users where id = @id", connection))
        {
            delete.Parameters.AddWithValue("id", userId);
            await delete.ExecuteNonQueryAsync();
        }

        // Assert — what this proves is that the delete propagates rather than being refused
        // part-way. categories -> category_groups is Restrict while both cascade from the budget, so
        // the cascade has to reach the categories before the group is removed for any of this to
        // succeed. It holds today, but at the user level it is one cascade hop further than the
        // budget-level equivalent and worth re-proving there.
        await Assert.That(await CountRowsAsync(connection, "users", "id", userId)).IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "budgets", "user_id", userId)).IsEqualTo(0L);

        // Credentials cascade rather than restrict: they are how the account is reached, not
        // something it owes anyone, so nothing about them should be able to hold an erasure up.
        // Left behind, they would also be a stranded record of which Google account this was.
        await Assert.That(await CountRowsAsync(connection, "credentials", "user_id", userId)).IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "accounts", "budget_id", budgetId)).IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "category_groups", "budget_id", budgetId)).IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "categories", "budget_id", budgetId)).IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "payees", "budget_id", budgetId)).IsEqualTo(0L);
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Writes one transaction into <paramref name="budgetId" />, together with the account it needs
    /// to satisfy the composite account reference.
    /// </summary>
    private static async Task SeedTransactionAsync(RepositoryTestHost host, Guid budgetId)
    {
        await using BudgetoidDbContext db = CreateDb(host, budgetId);
        Account account = Account.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed("Checking"), AccountType.Checking, 0m, "USD", UsdMinorUnit, SeedInstant);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        db.Transactions.Add(Transaction.Create(
            Guid.CreateVersion7(),
            budgetId,
            account.Id,
            -10m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            SealedNarrative.Description("Groceries"),
            SeedInstant));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Counts rows keyed on whichever column the caller is reasoning about. The column is explicit
    /// rather than derived from the table because these tests straddle two keys: the user's own id
    /// on <c>users</c> and <c>budgets</c>, the budget id on everything the budget owns.
    /// </summary>
    private static async Task<long> CountRowsAsync(
        NpgsqlConnection connection,
        string table,
        string column,
        Guid id)
    {
        await using NpgsqlCommand command = new(
            $"select count(*) from {table} where {column} = @id",
            connection);
        command.Parameters.AddWithValue("id", id);

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
        Guid userId)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", userId);

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
    /// Builds a context bound to an ambient budget, which the budget-isolated sets these tests seed
    /// — <c>Accounts</c>, <c>CategoryGroups</c>, <c>Categories</c>, <c>Payees</c>,
    /// <c>Transactions</c> — all require.
    /// </summary>
    private static BudgetoidDbContext CreateDb(RepositoryTestHost host, Guid budgetId) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options,
        new TestBudgetContext(budgetId));

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
