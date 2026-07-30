using Infrastructure.Persistence.Provisioning;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IntegrationTests;

public sealed class PostgresTestHost : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("budgetoid")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    /// <summary>
    /// Password the grants script assigns to the application role inside this test container. A
    /// constant is fine: the container lives for one test and is unreachable from outside it.
    /// </summary>
    private const string AppRolePassword = "app-test-password";

    public ApiFactory Factory { get; private set; } = null!;

    /// <summary>
    /// The container account — a superuser. Kept as <c>ConnectionString</c> because existing
    /// callers (schema assertions, seeding, out-of-band SQL) rely on that meaning: they need to
    /// observe and set up state the application role is deliberately not allowed to touch.
    /// </summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// The least-privilege role the application itself connects as.
    /// </summary>
    /// <remarks>
    /// PostgreSQL skips every privilege check for a superuser, so a test that runs on the
    /// container's admin connection measures nothing about the grants. The raw-SQL probes in
    /// <c>TenancySchemaTests</c>/<c>AppRoleGrantsTests</c> prove the grant matrix is correct but
    /// not that it is <b>sufficient</b> — nothing in them exercises a real request through the
    /// role. Running the whole API suite on this connection string is what makes an over-tight
    /// grant fail a feature test instead of shipping.
    /// </remarks>
    public string AppConnectionString => new NpgsqlConnectionStringBuilder(ConnectionString)
    {
        Username = DatabaseProvisioning.AppRoleName,
        Password = AppRolePassword,
    }.ConnectionString;

    public async Task StartAsync()
    {
        await _container.StartAsync();
        Factory = CreateFactory();
    }

    // Builds a factory over the same database container. Caller owns disposal (use `await using`).
    // `configureServices` is applied inside the test host's service configuration, so callers can
    // substitute application services without touching the production composition root.
    //
    // Both connection strings are handed over deliberately, and the host applies no grants of its
    // own: provisioning the role is the application's Development-startup job, and doing it here
    // instead would hide the very thing running under the role exists to prove.
    public ApiFactory CreateFactory(
        string? defaultSubject = "test-subject",
        Action<IServiceCollection>? configureServices = null) =>
        new(
            AppConnectionString,
            defaultSubject,
            configureServices: configureServices,
            adminConnectionString: ConnectionString);

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
    }
}
