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

    /// <summary>
    /// Starts the container and builds the factory over it.
    /// </summary>
    /// <remarks>
    /// The failure path disposes the container here rather than leaving it to the caller, and that
    /// is the whole reason for the try/catch. Every call site has the shape
    /// <c>await using PostgresTestHost host = await StartHostAsync();</c>, so the variable is bound
    /// only <b>after</b> this method returns: when the start throws, nothing is ever disposed. The
    /// container object is created in a field initializer, so Docker may already hold a container
    /// by then, and one that never reported healthy would keep its memory and its port binding for
    /// the rest of the run — making the next start more likely to time out in turn. That feedback
    /// loop, not flat resource pressure, is what a suite losing a different single test every few
    /// runs looks like. Owning the cleanup here also means a call site added later cannot forget it.
    /// </remarks>
    public async Task StartAsync()
    {
        try
        {
            await _container.StartAsync();
            Factory = CreateFactory();
        }
        catch
        {
            await _container.DisposeAsync();
            throw;
        }
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

    /// <summary>
    /// Releases the factory and then the container, and is safe on a host that never started.
    /// </summary>
    /// <remarks>
    /// Both halves of this are about disposal of a <b>half-started</b> host, which is the state a
    /// failed <see cref="StartAsync" /> leaves behind. <see cref="Factory" /> is declared
    /// <c>null!</c> and assigned only on the last line of the start, so dereferencing it
    /// unconditionally turned any such disposal into a <see cref="NullReferenceException" /> that
    /// replaced the real start-up failure in the run output — that substitution is why the
    /// intermittently failing test was never identifiable. And the container is released in a
    /// <c>finally</c> because a factory that fails to dispose must not take the container down with
    /// it: a leaked container outlives the run, while a failed factory disposal is confined to it.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Factory is not null)
            {
                await Factory.DisposeAsync();
            }
        }
        finally
        {
            await _container.DisposeAsync();
        }
    }
}
