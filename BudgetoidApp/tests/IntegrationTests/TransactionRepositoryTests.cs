using Domain.Transactions;
using Infrastructure.Persistence;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

public sealed class TransactionRepositoryTests
{
    [Test]
    public async Task AddAsync_StoresCreatedAtUtcAsTimestampWithTimeZone()
    {
        await using RepositoryTestHost host = await StartHostAsync();
        DbContextOptions<BudgetoidDbContext> options = CreateOptions(host);
        Guid userId = Guid.CreateVersion7();

        await using (BudgetoidDbContext db = new(options, new TestUserContext(userId)))
        {
            await new TransactionRepository(db).AddAsync(Transaction.Create(userId, 1m, new DateOnly(2026, 6, 12),
                "Test"));
        }

        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("select pg_typeof(created_at_utc)::text from transactions limit 1",
            connection);
        string? type = (string?)await command.ExecuteScalarAsync();

        await Assert.That(type).IsEqualTo("timestamp with time zone");
    }

    [Test]
    public async Task AmountColumn_UsesNumeric14Scale2()
    {
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
        await Assert.That(reader.GetInt32(1)).IsEqualTo(2);
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
