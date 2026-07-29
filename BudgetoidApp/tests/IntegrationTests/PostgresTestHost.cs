using Microsoft.Extensions.DependencyInjection;
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

    // Builds a factory over the same database container. Caller owns disposal (use `await using`).
    // `configureServices` is applied inside the test host's service configuration, so callers can
    // substitute application services without touching the production composition root.
    public ApiFactory CreateFactory(
        string? defaultSubject = "test-subject",
        Action<IServiceCollection>? configureServices = null) =>
        new(ConnectionString, defaultSubject, configureServices: configureServices);

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }
}
