using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Transactions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the rule that a budget-owned row never moves between budgets. This is tenancy, not
/// tidiness: a row that changes <c>budget_id</c> carries its history into someone else's ledger.
/// The domain makes it true by construction — no method on <see cref="Account" />,
/// <see cref="CategoryGroup" />, <see cref="Category" />, <see cref="Payee" /> or
/// <see cref="Transaction" /> reaches <c>BudgetId</c> — so every statement here is raw Npgsql, the
/// only way to send the UPDATE the domain will not produce.
/// </summary>
/// <remarks>
/// <para>
/// The database enforces the rule only unevenly, and these five tests are split along that seam.
/// Composite foreign keys default to <c>ON UPDATE NO ACTION</c>, so moving a parent out from under
/// a child is refused with <c>23503</c>. That covers a transaction (its <c>(account_id,
/// budget_id)</c> reference always exists) and a category (its <c>(category_group_id, budget_id)</c>
/// reference always exists) completely. It covers an account, a category group and a payee only
/// while something references them — an <b>empty</b> account, an <b>empty</b> category group and an
/// <b>unreferenced</b> payee have no child to object, and today a raw UPDATE moves them.
/// </para>
/// <para>
/// The three tests named <c>CurrentlyAllows…</c> are characterization tests over that gap, not
/// endorsements of it. Each says so at its assertion.
/// </para>
/// </remarks>
public sealed class TenancySchemaTests
{
    /// <summary>
    /// Minor unit of the USD account these tests seed. Precision is not what any of them is about;
    /// the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task Database_RefusesToMoveATransactionToAnotherBudget()
    {
        // Arrange — one account and the transaction on it. The transaction's account reference is
        // composite and mandatory, so there is no shape of transaction this refusal misses.
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid budgetId, Guid otherBudgetId) = await SeedTwoBudgetsAsync(host);
        Guid transactionId;
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            Account account = Account.Create(
                budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, SeedInstant);
            seed.Accounts.Add(account);
            await seed.SaveChangesAsync();

            Transaction transaction = Transaction.Create(
                budgetId,
                account.Id,
                -10m,
                UsdMinorUnit,
                new DateOnly(2026, 6, 12),
                "Groceries",
                SeedInstant);
            seed.Transactions.Add(transaction);
            await seed.SaveChangesAsync();
            transactionId = transaction.Id;
        }

        // Act
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            host, "transactions", transactionId, otherBudgetId);

        // Assert — the constraint name is asserted next to the SQLSTATE so the refusal has to come
        // from the composite account reference, which is the tenancy rule, rather than from
        // FK_transactions_budgets_budget_id, which would only mean the destination did not exist.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(exception.ConstraintName)
            .IsEqualTo("FK_transactions_accounts_account_id_budget_id");

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // The destination row is asserted rather than assumed, and on this table it is not
        // redundant with the constraint name above: point the move at a nonexistent budget and
        // PostgreSQL still names the composite account key first, so the name assertion alone stays
        // green while the test has stopped being about tenancy. Only this line notices. See
        // SeedTwoBudgetsAsync.
        await Assert.That(await CountRowsAsync(connection, "budgets", "id", otherBudgetId))
            .IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "transactions", "budget_id", budgetId))
            .IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "transactions", "budget_id", otherBudgetId))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesToMoveACategoryToAnotherBudget()
    {
        // Arrange — a category always sits in a group, and the reference to that group is composite,
        // so this refusal has no gap either.
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid budgetId, Guid otherBudgetId) = await SeedTwoBudgetsAsync(host);
        Guid categoryId;
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            CategoryGroup group = CategoryGroup.Create(budgetId, "Everyday", null, 0, SeedInstant);
            seed.CategoryGroups.Add(group);
            Category category = Category.Create(
                budgetId, group.Id, "Groceries", null, 0, SeedInstant);
            seed.Categories.Add(category);
            await seed.SaveChangesAsync();
            categoryId = category.Id;
        }

        // Act
        PostgresException exception = await ThrowsPostgresExceptionAsync(
            host, "categories", categoryId, otherBudgetId);

        // Assert — the group reference is the tenancy rule, and here, unlike on transactions, naming
        // it does rule out a refusal that came from the destination budget not existing: with a
        // nonexistent destination PostgreSQL names FK_categories_budgets_budget_id instead. Which
        // constraint gets named first is an ordering artifact though, not a guarantee, so the
        // destination row is still asserted below rather than inferred from the name.
        await Assert.That(exception.SqlState).IsEqualTo(PostgresErrorCodes.ForeignKeyViolation);
        await Assert.That(exception.ConstraintName)
            .IsEqualTo("FK_categories_category_groups_category_group_id_budget_id");

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await Assert.That(await CountRowsAsync(connection, "budgets", "id", otherBudgetId))
            .IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "categories", "budget_id", budgetId))
            .IsEqualTo(1L);
        await Assert.That(await CountRowsAsync(connection, "categories", "budget_id", otherBudgetId))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_CurrentlyAllowsMovingAnEmptyAccountToAnotherBudget()
    {
        // Arrange — an account with no transactions on it, which is the entire gap: the transactions
        // foreign key is what refuses the move, and there is nothing here for it to refuse on.
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid budgetId, Guid otherBudgetId) = await SeedTwoBudgetsAsync(host);
        Guid accountId;
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            Account account = Account.Create(
                budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, SeedInstant);
            seed.Accounts.Add(account);
            await seed.SaveChangesAsync();
            accountId = account.Id;
        }

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        await MoveToBudgetAsync(connection, "accounts", accountId, otherBudgetId);

        // Assert — this is a gap, not a rule. Nothing in the schema forbids the move, so the row
        // lands in a budget it was never opened in. The rule's lowest capable layer is
        // REVOKE UPDATE (budget_id) ON accounts from a least-privilege application role, and that
        // role does not exist yet — the app connects as admin, so a REVOKE today would have no
        // effect and testing it would be theatre. When the role lands, this test flips to expect a
        // rejection.
        await Assert.That(await CountRowsAsync(connection, "accounts", "budget_id", budgetId))
            .IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "accounts", "budget_id", otherBudgetId))
            .IsEqualTo(1L);
    }

    [Test]
    public async Task Database_CurrentlyAllowsMovingAnEmptyCategoryGroupToAnotherBudget()
    {
        // Arrange — a group with no categories in it, for the same reason: the categories foreign
        // key is the only thing that would object, and it has no row to object with.
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid budgetId, Guid otherBudgetId) = await SeedTwoBudgetsAsync(host);
        Guid groupId;
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            CategoryGroup group = CategoryGroup.Create(budgetId, "Everyday", null, 0, SeedInstant);
            seed.CategoryGroups.Add(group);
            await seed.SaveChangesAsync();
            groupId = group.Id;
        }

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        await MoveToBudgetAsync(connection, "category_groups", groupId, otherBudgetId);

        // Assert — a gap, not a rule, on the same terms as the empty account above: it closes with
        // REVOKE UPDATE (budget_id) ON category_groups once a least-privilege application role
        // exists, and this test then flips to expect a rejection.
        await Assert.That(await CountRowsAsync(connection, "category_groups", "budget_id", budgetId))
            .IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "category_groups", "budget_id", otherBudgetId))
            .IsEqualTo(1L);
    }

    [Test]
    public async Task Database_CurrentlyAllowsMovingAnUnreferencedPayeeToAnotherBudget()
    {
        // Arrange — a payee no transaction names. A payee is referenced optionally, so this is not
        // an exotic state: every payee is unreferenced between being created and being used.
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid budgetId, Guid otherBudgetId) = await SeedTwoBudgetsAsync(host);
        Guid payeeId;
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            Payee payee = Payee.Create(budgetId, "Corner Shop", SeedInstant);
            seed.Payees.Add(payee);
            await seed.SaveChangesAsync();
            payeeId = payee.Id;
        }

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();

        // Act
        await MoveToBudgetAsync(connection, "payees", payeeId, otherBudgetId);

        // Assert — a gap, not a rule, on the same terms as the two above: it closes with
        // REVOKE UPDATE (budget_id) ON payees once a least-privilege application role exists, and
        // this test then flips to expect a rejection.
        await Assert.That(await CountRowsAsync(connection, "payees", "budget_id", budgetId))
            .IsEqualTo(0L);
        await Assert.That(await CountRowsAsync(connection, "payees", "budget_id", otherBudgetId))
            .IsEqualTo(1L);
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Seeds one owner with two budgets and returns both ids: the one every row starts in, and the
    /// one every UPDATE moves it to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The destination is a <b>real, persisted</b> budget, and that is the point of this helper. A
    /// freshly generated <see cref="Guid" /> would be the obvious shortcut and would ruin the file:
    /// every table carries a single-column foreign key to <c>budgets</c>, so a move to a
    /// nonexistent budget raises <c>23503</c> for a reason that has nothing to do with tenancy —
    /// the two refusal tests would go green on the wrong constraint, and the three
    /// characterization tests would report a gap as closed when it is wide open.
    /// </para>
    /// <para>
    /// The destination is also left empty. Accounts, category groups, categories and payees each
    /// carry a case-insensitive unique index on <c>(budget_id, name)</c>, so a same-named row
    /// waiting in the destination would make the UPDATE fail with <c>23505</c> instead of doing
    /// what the test is about. The two budget names differ for the same reason, against
    /// <c>IX_budgets_user_id_name</c>.
    /// </para>
    /// </remarks>
    private static async Task<(Guid BudgetId, Guid OtherBudgetId)> SeedTwoBudgetsAsync(
        RepositoryTestHost host)
    {
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid budgetId = await host.SeedAdditionalBudgetAsync(userId, "Household");
        Guid otherBudgetId = await host.SeedAdditionalBudgetAsync(userId, "Holiday Fund");
        return (budgetId, otherBudgetId);
    }

    private static async Task MoveToBudgetAsync(
        NpgsqlConnection connection,
        string table,
        Guid rowId,
        Guid destinationBudgetId)
    {
        await using NpgsqlCommand command = BuildMove(connection, table, rowId, destinationBudgetId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(
        RepositoryTestHost host,
        string table,
        Guid rowId,
        Guid destinationBudgetId)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = BuildMove(connection, table, rowId, destinationBudgetId);

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

    private static NpgsqlCommand BuildMove(
        NpgsqlConnection connection,
        string table,
        Guid rowId,
        Guid destinationBudgetId)
    {
        // The table name is interpolated because every caller passes a literal; the ids are
        // parameters, as they must be.
        NpgsqlCommand command = new(
            $"update {table} set budget_id = @budget_id where id = @id",
            connection);
        command.Parameters.AddWithValue("budget_id", destinationBudgetId);
        command.Parameters.AddWithValue("id", rowId);
        return command;
    }

    /// <summary>
    /// Counts rows keyed on whichever column the caller is reasoning about. The column is explicit
    /// rather than derived from the table because these tests straddle two keys: the budget's own
    /// id on <c>budgets</c>, the owning budget id on everything else.
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
