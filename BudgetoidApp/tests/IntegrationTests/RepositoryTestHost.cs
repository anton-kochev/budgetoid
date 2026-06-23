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
    /// Persists a user and returns its generated id, so transaction tests can satisfy the
    /// users foreign key with a real owner row.
    /// </summary>
    public async Task<Guid> SeedUserAsync(string googleSubject, string email)
    {
        await using var db = new BudgetoidDbContext(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(ConnectionString)
                .Options);
        User user = User.Create(
            googleSubject,
            email,
            displayName: null,
            new DateTime(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc));
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
