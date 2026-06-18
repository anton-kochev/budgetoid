using Testcontainers.PostgreSql;

namespace IntegrationTests;

public sealed class PostgresTestHost : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("budgetoid")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public ApiFactory Factory { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    public async Task StartAsync()
    {
        await _container.StartAsync();
        Factory = new ApiFactory(ConnectionString);
    }

    // Builds a factory whose requests run as the given user, over the same database container.
    // Caller owns disposal (use `await using`).
    public ApiFactory CreateFactory(Guid userId) => new(ConnectionString, userId);

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }
}
