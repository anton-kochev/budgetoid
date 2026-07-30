using Domain.Accounts;
using Domain.CategoryGroups;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Real-PostgreSQL behaviour for the two rules the budget re-scope moves off the user: name
/// uniqueness and ordering are per budget.
/// </summary>
/// <remarks>
/// Deliberate coverage judgement: uniqueness is proven end-to-end for <c>Account</c> only. Category
/// groups, categories and payees carry a byte-for-byte identical <c>(BudgetId, Name)</c> unique
/// index on the same collation, so <c>Model_ScopesNameUniquenessToTheBudget</c> covers them and a
/// fourth Testcontainer would buy nothing. This is not an oversight.
/// </remarks>
public sealed class BudgetScopingTests
{
    /// <summary>
    /// Minor unit of the USD accounts these tests seed. Precision is not what any of them is
    /// about; the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task Accounts_WithTheSameNameInDifferentBudgets_BothPersist()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetA = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid budgetB = await host.SeedBudgetAsync("google-b", "b@example.com");

        // Act
        await using (BudgetoidDbContext dbA = CreateDb(host, budgetA))
        {
            dbA.Accounts.Add(CreateAccount(budgetA, "Checking"));
            await dbA.SaveChangesAsync();
        }

        await using BudgetoidDbContext dbB = CreateDb(host, budgetB);
        dbB.Accounts.Add(CreateAccount(budgetB, "Checking"));
        await dbB.SaveChangesAsync();

        // Assert
        await Assert.That(await dbB.Accounts.CountAsync()).IsEqualTo(1);
        await Assert.That(await CountAccountsAsync(host)).IsEqualTo(2L);
    }

    [Test]
    public async Task Accounts_WithTheSameNameDifferingOnlyByCaseInOneBudget_AreRejected()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetId = await host.SeedBudgetAsync("google-a", "a@example.com");
        await using BudgetoidDbContext db = CreateDb(host, budgetId);
        db.Accounts.Add(CreateAccount(budgetId, "Checking"));
        await db.SaveChangesAsync();

        // Act
        db.Accounts.Add(CreateAccount(budgetId, "checking"));
        DbUpdateException? caught = null;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException exception)
        {
            caught = exception;
        }

        // Assert
        await Assert.That(caught).IsNotNull();
        await Assert.That((caught!.InnerException as PostgresException)?.SqlState)
            .IsEqualTo(PostgresErrorCodes.UniqueViolation);
    }

    [Test]
    public async Task CategoryGroups_PositionsAreIndependentPerBudget()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid budgetA = await host.SeedBudgetAsync("google-a", "a@example.com");
        Guid budgetB = await host.SeedBudgetAsync("google-b", "b@example.com");

        await using (BudgetoidDbContext dbA = CreateDb(host, budgetA))
        {
            dbA.CategoryGroups.Add(CategoryGroup.Create(budgetA, "Essentials", null, 0, UtcNow()));
            dbA.CategoryGroups.Add(CategoryGroup.Create(budgetA, "Lifestyle", null, 1, UtcNow()));
            await dbA.SaveChangesAsync();
        }

        // Act — budget B starts its own sequence at 0; positions in budget A must not interfere.
        await using BudgetoidDbContext dbB = CreateDb(host, budgetB);
        dbB.CategoryGroups.Add(CategoryGroup.Create(budgetB, "Essentials", null, 0, UtcNow()));
        await dbB.SaveChangesAsync();
        List<CategoryGroup> groupsOfB = await dbB.CategoryGroups
            .OrderBy(group => group.Position)
            .ToListAsync();

        // Assert
        await Assert.That(groupsOfB.Count).IsEqualTo(1);
        await Assert.That(groupsOfB[0].Position).IsEqualTo(0);
        await Assert.That(groupsOfB[0].BudgetId).IsEqualTo(budgetB);

        await using BudgetoidDbContext dbA2 = CreateDb(host, budgetA);
        List<int> positionsOfA = await dbA2.CategoryGroups
            .OrderBy(group => group.Position)
            .Select(group => group.Position)
            .ToListAsync();
        await Assert.That(positionsOfA).IsEquivalentTo(new[] { 0, 1 });
    }

    private static Account CreateAccount(Guid budgetId, string name) => Account.Create(
        budgetId,
        name,
        AccountType.Checking,
        0m,
        "USD",
        UsdMinorUnit,
        UtcNow());

    private static async Task<long> CountAccountsAsync(RepositoryTestHost host)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("select count(*) from accounts", connection);
        object? scalar = await command.ExecuteScalarAsync();

        return scalar is long count
            ? count
            : throw new InvalidOperationException($"Expected a count, got '{scalar ?? "null"}'.");
    }

    private static BudgetoidDbContext CreateDb(RepositoryTestHost host, Guid budgetId) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options,
        new TestBudgetContext(budgetId));

    private static DateTime UtcNow() =>
        new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
