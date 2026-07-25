using Domain.Budgets;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace IntegrationTests;

public sealed class RepositoryTestHost : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("budgetoid")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task StartAsync()
    {
        await _container.StartAsync();
        await using var db = new BudgetoidDbContext(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(ConnectionString)
                .Options);
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// Persists a user together with its default budget and returns the <b>budget</b> id, so tests
    /// can satisfy the budgets foreign key on every owned entity with a real tenant row.
    /// </summary>
    /// <remarks>
    /// The seeding context is built without an <c>IBudgetContext</c>, which is only safe because
    /// <c>Budget</c> deliberately carries no global query filter — its owner scoping is explicit at
    /// every call site instead.
    /// </remarks>
    public async Task<Guid> SeedBudgetAsync(string googleSubject, string email)
    {
        Guid userId = await SeedUserAsync(googleSubject, email);
        await using var db = CreateSeedingDbContext();
        Budget budget = Budget.CreateDefault(userId, SeedInstant);
        db.Budgets.Add(budget);
        await db.SaveChangesAsync();
        return budget.Id;
    }

    /// <summary>
    /// Persists a user and returns its generated id, for the few tests whose subject is the
    /// user-to-budget link itself. Tests of budget-owned entities want <see cref="SeedBudgetAsync"/>.
    /// </summary>
    public async Task<Guid> SeedUserAsync(string googleSubject, string email)
    {
        await using var db = CreateSeedingDbContext();
        User user = User.Create(googleSubject, email, displayName: null, SeedInstant);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>
    /// Adds another budget to an existing owner and returns its id, so a test can exercise two
    /// tenants without inventing a second user.
    /// </summary>
    public async Task<Guid> SeedAdditionalBudgetAsync(Guid userId, string name)
    {
        await using var db = CreateSeedingDbContext();
        Budget budget = Budget.Create(userId, name, SeedInstant);
        db.Budgets.Add(budget);
        await db.SaveChangesAsync();
        return budget.Id;
    }

    /// <summary>
    /// Fixed UTC instant for all seeded rows. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing, not decoration.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    private BudgetoidDbContext CreateSeedingDbContext() => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
