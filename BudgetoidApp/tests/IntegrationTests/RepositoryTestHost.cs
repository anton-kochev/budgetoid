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
                .Options,
            new TestUserContext(Guid.Empty));
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
