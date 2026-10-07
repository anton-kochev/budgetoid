using Infrastructure.Persistence;
using Infrastructure.Persistence.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IntegrationTests;

/// <summary>
/// The one PostgreSQL container this assembly runs against, and the per-test databases carved out
/// of it. Both test hosts sit on top of it, which is why it lives here rather than in a file of its
/// own: the two hosts are the only callers, and a shared cluster with two owners is a thing to keep
/// in one place.
/// </summary>
/// <remarks>
/// <para>
/// Isolation is per <b>database</b> rather than per container. A database is where grants, policies
/// and schema live — <c>pg_class.relacl</c>, <c>pg_policy</c> and the tables themselves are all
/// per-database catalogs — so a test that revokes a privilege, drops a policy or adds a table is as
/// invisible to its neighbours as it was when each had a container. <c>CREATE DATABASE ... TEMPLATE</c>
/// costs ~30 ms against ~2.7 s for a container start, and both leave the same isolated schema behind.
/// </para>
/// <para>
/// <b>Roles are the exception, and the exception is load-bearing.</b> A role is a cluster-level
/// object, so <c>budgetoid_app</c> is now one role shared by every database here rather than one per
/// test. Two consequences, and neither is cosmetic. It is created and given its password exactly
/// once, below, because the <c>IF NOT EXISTS ... CREATE ROLE</c> block in <c>app-role-grants.sql</c>
/// is not atomic — two concurrent runs raise <c>42710</c>, and two concurrent
/// <c>ALTER ROLE ... WITH LOGIN</c> raise <c>XX000 tuple concurrently updated</c>. And every later
/// run of that script has to be serialised through <see cref="RoleGate" /> for the same reason.
/// Measured at 4-, 8- and 16-way concurrency, every degree lost all but one caller to
/// <c>XX000</c>; serialised, the same call costs ~10 ms.
/// </para>
/// <para>
/// The container is released by Testcontainers' resource reaper when the test process exits, not by
/// a hook here. That is deliberate: an assembly-level teardown attribute would be the first one in
/// this suite, and the reaper already covers the case that matters — a run killed part-way through,
/// which no teardown hook would survive either.
/// </para>
/// </remarks>
internal static class SharedPostgresCluster
{
    /// <summary>
    /// The PostgreSQL image every test container starts from: production's major, and the exact
    /// version the project's <c>.pithos</c> gives the agent, so a run inside Pi and a run on the host
    /// measure the same server.
    /// </summary>
    public const string Image = "postgres:18.6";

    /// <summary>
    /// Password the application role authenticates with inside this cluster. A constant is fine: a
    /// container lives for one run and is unreachable from outside it, and a named server is a
    /// throwaway test server too (a Pithos session's database lives in memory for that session).
    /// </summary>
    /// <remarks>
    /// The role is server-wide, so every run on one server sets this same password, and they agree. The
    /// developer's own API booting against that server sets its own, and a run that overlaps it can
    /// see the application role's logins refused until the run's next boot resets it.
    /// </remarks>
    public const string AppRolePassword = "app-test-password";

    /// <summary>
    /// This run's prefix, lease and sweep. The template every per-test database is cloned from is
    /// <see cref="ClusterRun.TemplateDatabase" />: it holds the migrated schema, the grant matrix and
    /// the isolation policies — all per-database objects, so a clone inherits them whole and neither
    /// the migration nor the grants script has to run again per test.
    /// </summary>
    public static ClusterRun Run { get; } = ClusterRun.Create();

    /// <summary>
    /// The connection holding this run's lease, kept open for the life of the process. Never disposed:
    /// the lease must outlive every test, and the process ending is what releases it.
    /// </summary>
    private static NpgsqlConnection? _lease;

    /// <summary>
    /// Serialises every run of <c>app-role-grants.sql</c> in this process. The script's first
    /// statement writes <c>pg_authid</c>, which is cluster-level and therefore shared by every
    /// database here; concurrent writers of one <c>pg_authid</c> tuple fail with
    /// <c>XX000 tuple concurrently updated</c> rather than blocking.
    /// </summary>
    /// <remarks>
    /// The semaphore only orders this process. Every holder also takes
    /// <see cref="ClusterRun.HoldRoleLock" />, a server-wide advisory lock, because a second run on the
    /// same server is a second writer of the same row. The developer's own API, booting against that
    /// server outside the suite, takes neither and can still collide with a run; that is a
    /// <c>XX000</c> on one boot, and a restart clears it.
    /// </remarks>
    private static readonly SemaphoreSlim RoleGate = new(1, 1);

    /// <summary>
    /// Starts the container and builds the template on the first call and no later one.
    /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication" /> is what makes that true under the
    /// suite's unbounded parallelism: every caller awaits the same task, and a failure is observed by
    /// all of them rather than leaving a second container behind.
    /// </summary>
    private static readonly Lazy<Task<string>> ClusterConnectionString =
        new(StartClusterAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Names each per-test database. Only uniqueness matters, not the value.</summary>
    private static int _databaseCounter;

    /// <summary>
    /// Creates a database for one test out of the template and returns the <b>superuser</b>
    /// connection string that reaches it.
    /// </summary>
    public static async Task<string> CreateDatabaseAsync()
    {
        string cluster = await ClusterConnectionString.Value;
        string database = Run.TestDatabase(Interlocked.Increment(ref _databaseCounter));

        // CREATE DATABASE ... TEMPLATE refuses a source that any session is connected to (55006). The
        // template is migrated over an unpooled connection precisely so no session lingers on it, so
        // this retry is the second line of defence rather than the first — but it is cheap, and the
        // failure it covers would otherwise be an unreproducible red test in a suite that has already
        // paid for one of those. Measured clean at 4-, 8- and 16-way concurrency with zero 55006.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await ExecuteAsync(
                    MaintenanceConnectionString(cluster),
                    $"create database {database} template {Run.TemplateDatabase}");
                break;
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.ObjectInUse && attempt < 10)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }

        return WithDatabase(cluster, database);
    }

    /// <summary>
    /// The cluster reached through its <c>postgres</c> database, once the cluster is up. For tests of
    /// the run machinery itself; a test that wants a database of its own uses
    /// <see cref="CreateDatabaseAsync" />.
    /// </summary>
    public static async Task<string> MaintenanceConnectionStringAsync() =>
        MaintenanceConnectionString(await ClusterConnectionString.Value);

    /// <summary>
    /// Drops a per-test database and forgets the connection pool that reached it. <c>FORCE</c>
    /// because the caller's pools may still hold idle connections to it, and a database nobody can
    /// drop is a database that accumulates for the rest of the run.
    /// </summary>
    /// <remarks>
    /// Failures are swallowed. This runs from a disposal path, where the alternative is replacing a
    /// test's real failure with a cleanup one — the same substitution that once made an intermittent
    /// failure in this suite unidentifiable. A database that survives costs disk in a container that
    /// is thrown away minutes later.
    /// </remarks>
    public static async Task DropDatabaseAsync(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string database = builder.Database!;

        try
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(connectionString));
            await ExecuteAsync(
                MaintenanceConnectionString(connectionString),
                $"drop database if exists {database} with (force)");
        }
        catch (Exception)
        {
            // Deliberately ignored; see the remarks.
        }
    }

    /// <summary>
    /// Runs one API host boot at a time, because booting the API runs
    /// <c>app-role-grants.sql</c> and its first statement writes the shared <c>pg_authid</c> tuple.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single caller is <see cref="ApiFactory" />'s <c>CreateHost</c> override, and that is the
    /// only place it can be. A boot reaches the grants script from three directions — both test
    /// hosts, and <c>PasskeyCeremonyTests</c>' own <c>CreateApiFactory</c> helper, which builds an
    /// <see cref="ApiFactory" /> straight over a <see cref="RepositoryTestHost" /> and so passes
    /// through neither host's seam. <see cref="ApiFactory" /> is the one object all three construct.
    /// </para>
    /// <para>
    /// Blocking rather than <c>await</c> because <c>CreateHost</c> is a synchronous override.
    /// <see cref="SemaphoreSlim" /> is not reentrant, so nothing above this may hold the gate on the
    /// way in — an outer gate around <c>CreateFactory</c> deadlocked the suite outright, which is why
    /// the gate exists here and nowhere else.
    /// </para>
    /// </remarks>
    public static T UnderRoleGate<T>(Func<T> action)
    {
        RoleGate.Wait();
        try
        {
            // The semaphore orders this process; the server-wide lock orders every process on the
            // server. A boot only happens after a host has its database, so the cluster is up and
            // reading its task cannot block.
            string maintenance = MaintenanceConnectionString(ClusterConnectionString.Value.GetAwaiter().GetResult());
            using NpgsqlConnection roleLock = ClusterRun.HoldRoleLock(maintenance);
            return action();
        }
        finally
        {
            RoleGate.Release();
        }
    }

    /// <summary>
    /// Finds the server — the one the environment names, or a container started here — and prepares
    /// this run on it.
    /// </summary>
    /// <remarks>
    /// A named server is used as it is: the suite never starts, stops or reconfigures it, which is why
    /// <see cref="ServerPreflight" /> has to check it instead. A container is started with what the
    /// preflight demands, so it always passes.
    /// </remarks>
    private static async Task<string> StartClusterAsync()
    {
        TestDatabaseSource source = TestDatabaseSource.FromProcess();
        if (!source.StartsContainer)
        {
            return await PrepareClusterAsync(source.ConnectionString!, source.Variable!);
        }

        PostgreSqlContainer container = new PostgreSqlBuilder(Image)
            .WithDatabase("budgetoid")
            .WithUsername("postgres")
            .WithPassword("postgres")

            // One cluster now serves every test in the assembly, so the connection budget that used
            // to be per container is shared. The default of 100 is a limit the old shape could never
            // reach and this one can.
            .WithCommand("-c", $"max_connections={ServerPreflight.RequiredConnections}")
            .Build();

        try
        {
            await container.StartAsync();
            return await PrepareClusterAsync(container.GetConnectionString(), "the test container");
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    /// <param name="cluster">A superuser connection string to the server; its database is ignored.</param>
    /// <param name="source">Where the server came from, for the preflight's message.</param>
    private static async Task<string> PrepareClusterAsync(string cluster, string source)
    {
        string maintenance = MaintenanceConnectionString(cluster);
        string template = UnpooledConnectionString(WithDatabase(cluster, Run.TemplateDatabase));

        // Fatal on any shortfall, before this run writes anything to the server.
        await using (NpgsqlConnection probe = await OpenAsync(UnpooledConnectionString(maintenance)))
        {
            ServerPreflight.Ensure(await ServerProbe.ReadAsync(probe), source);
        }

        // Lease first, then sweep: this run is live before it reclaims anyone else's leftovers,
        // so a sweep running in another process at the same moment cannot take it for dead.
        _lease = await Run.AcquireLeaseAsync(maintenance);
        await ClusterRun.SweepAsync(maintenance);

        // template0, not the default template1: on a server this suite does not own, template1 is
        // whatever someone left in it, and a clone of it would carry that into every test.
        await ExecuteAsync(maintenance, $"create database {Run.TemplateDatabase} template template0");

        await using (BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>().UseNpgsql(template).Options))
        {
            await db.Database.MigrateAsync();
        }

        // The role is created and credentialed exactly once, here, and never again. Everything
        // this call writes to pg_authid is cluster-level and therefore already correct for every
        // database cloned afterwards; what the clone needs from the script is the per-database
        // half — grants and policies — and it inherits that from the template's catalogs.
        // Under the server-wide role lock: another run on this server may be writing the same row.
        await using (await ClusterRun.HoldRoleLockAsync(maintenance))
        {
            await DatabaseProvisioning.ApplyGrantsAsync(template);
            await DatabaseProvisioning.AttachAppRolePasswordAsync(template, AppRolePassword);
        }

        return cluster;
    }

    /// <summary>
    /// The same cluster reached through the <c>postgres</c> database. <c>CREATE DATABASE</c> and
    /// <c>DROP DATABASE</c> cannot be sent from the database they name.
    /// </summary>
    private static string MaintenanceConnectionString(string connectionString) =>
        WithDatabase(connectionString, "postgres");

    private static string WithDatabase(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;

    /// <summary>
    /// Turns pooling off. Used for every statement sent to the template: a pooled connection stays
    /// open after it is disposed, and a template with a live session on it cannot be cloned (55006).
    /// </summary>
    private static string UnpooledConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = await OpenAsync(UnpooledConnectionString(connectionString));
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Opens a connection, retrying only a refused or timed-out TCP connect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every per-test database is created and dropped through this path, so it opens thousands of
    /// unpooled connections in bursts. When Docker runs in a VM behind the host's network (Pithos runs
    /// Pi in Docker Desktop and Testcontainers in a separate Lima VM), a rare connect in such a burst
    /// is refused or never answered while the cluster itself is healthy: one in 3,372 was refused in
    /// an otherwise green run, and a stress test reproduced the drops only on that path, never inside
    /// the VM or on Docker Desktop alone.
    /// </para>
    /// <para>
    /// <b>Only the connect is retried.</b> Nothing has reached the server yet, so a second attempt
    /// cannot run a statement twice. A failure after the socket is open — authentication, a server
    /// error, a statement — is not a transport hiccup and is thrown as it was.
    /// </para>
    /// </remarks>
    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        for (int attempt = 1; ; attempt++)
        {
            NpgsqlConnection connection = new(connectionString);
            try
            {
                await connection.OpenAsync();
                return connection;
            }
            catch (NpgsqlException exception)
                when (attempt < 3
                      && exception.InnerException is System.Net.Sockets.SocketException
                      {
                          SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused
                              or System.Net.Sockets.SocketError.TimedOut
                      })
            {
                await connection.DisposeAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }
}

/// <param name="usesApplicationAuthentication">
/// Leaves the application's own authentication defaults standing on <see cref="Factory" />, instead of
/// naming <see cref="TestAuthHandler" /> the default authenticate and challenge scheme.
/// </param>
/// <param name="repointsProviderSchemeToTestHandler">
/// Makes the identity provider's bearer scheme answer with <see cref="TestAuthHandler" /> inside
/// <see cref="Factory" />, so the two registration routes can be driven from a test.
/// </param>
/// <remarks>
/// <para>
/// <b>Both default to off, so no existing caller changes at all</b>, and both exist because the factory
/// this host builds is otherwise unreachable for a cookie: naming <see cref="TestAuthHandler" /> as the
/// default is exactly what stops the cookie handler being asked, so a session-carrying client would 401
/// on every one of the ~300 call sites here with nothing saying why. The flags are forwarded to
/// <see cref="ApiFactory" /> unchanged and are argued for over there — restating either argument here
/// would be a second opinion about a rule that already has an owner.
/// </para>
/// <para>
/// They sit on the <b>host</b> rather than only on <see cref="CreateFactory" /> because
/// <see cref="Factory" /> is what almost every test uses, and it is built by <see cref="StartAsync" />
/// before any test line runs. A per-factory flag alone would mean a second host boot for every migrated
/// test — a real cost against a shared cluster, and one paid ~300 times.
/// </para>
/// </remarks>
public sealed class PostgresTestHost(
    bool usesApplicationAuthentication = false,
    bool repointsProviderSchemeToTestHandler = false) : IAsyncDisposable
{
    /// <summary>
    /// Superuser connection string for this host's own database inside the shared cluster, or
    /// <see langword="null" /> until <see cref="StartAsync" /> has produced one.
    /// </summary>
    private string? _connectionString;

    public ApiFactory Factory { get; private set; } = null!;

    /// <summary>
    /// The container account — a superuser — pointed at this host's own database. Kept as
    /// <c>ConnectionString</c> because existing callers (schema assertions, seeding, out-of-band SQL)
    /// rely on that meaning: they need to observe and set up state the application role is
    /// deliberately not allowed to touch.
    /// </summary>
    public string ConnectionString => _connectionString
        ?? throw new InvalidOperationException(
            $"{nameof(PostgresTestHost)} has no database until {nameof(StartAsync)} has run.");

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
        Password = SharedPostgresCluster.AppRolePassword,
    }.ConnectionString;

    /// <summary>
    /// Takes a database out of the shared cluster and builds the factory over it.
    /// </summary>
    /// <remarks>
    /// The failure path drops the database here rather than leaving it to the caller, and that is the
    /// whole reason for the try/catch. Every call site has the shape
    /// <c>await using PostgresTestHost host = await StartHostAsync();</c>, so the variable is bound
    /// only <b>after</b> this method returns: when the start throws, nothing is ever disposed. A
    /// database that was created and then abandoned would keep its files and its catalog entry for
    /// the rest of the run. Owning the cleanup here also means a call site added later cannot forget
    /// it.
    /// </remarks>
    public async Task StartAsync()
    {
        _connectionString = await SharedPostgresCluster.CreateDatabaseAsync();

        try
        {
            Factory = CreateFactory();
        }
        catch
        {
            await SharedPostgresCluster.DropDatabaseAsync(_connectionString);
            _connectionString = null;
            throw;
        }
    }

    // Builds a factory over the same database. Caller owns disposal (use `await using`).
    // `configureServices` is applied inside the test host's service configuration, so callers can
    // substitute application services without touching the production composition root.
    //
    // Both connection strings are handed over deliberately, and the host applies no grants of its
    // own: provisioning the role is the application's Development-startup job, and doing it here
    // instead would hide the very thing running under the role exists to prove.
    //
    // That job is now also a shared-cluster hazard, and it is deliberately NOT handled here. The
    // Development startup block runs app-role-grants.sql, whose first statement writes the
    // cluster-level pg_authid tuple for budgetoid_app — one tuple shared by every database in the
    // assembly — and two boots racing on it fail with XX000. Serialising the boot at this seam would
    // miss the boots that never come through it, so the gate lives in ApiFactory instead. See
    // SharedPostgresCluster.UnderRoleGate.
    //
    // The two authentication flags come off the host rather than off this signature, so that `Factory`
    // and every factory built beside it answer the same way. A caller that needed one host serving two
    // authentication shapes would be a caller whose test spans two applications.
    public ApiFactory CreateFactory(
        string? defaultSubject = "test-subject",
        Action<IServiceCollection>? configureServices = null) =>
        new(
            AppConnectionString,
            defaultSubject,
            configureServices: configureServices,
            adminConnectionString: ConnectionString,
            usesApplicationAuthentication: usesApplicationAuthentication,
            repointsProviderSchemeToTestHandler: repointsProviderSchemeToTestHandler);

    /// <summary>
    /// Releases the factory and then the database, and is safe on a host that never started.
    /// </summary>
    /// <remarks>
    /// Both halves of this are about disposal of a <b>half-started</b> host, which is the state a
    /// failed <see cref="StartAsync" /> leaves behind. <see cref="Factory" /> is declared
    /// <c>null!</c> and assigned only on the last line of the start, so dereferencing it
    /// unconditionally turned any such disposal into a <see cref="NullReferenceException" /> that
    /// replaced the real start-up failure in the run output — that substitution is why the
    /// intermittently failing test was never identifiable. And the database is dropped in a
    /// <c>finally</c> because a factory that fails to dispose must not take the cleanup down with
    /// it: a leaked database outlives the test, while a failed factory disposal is confined to it.
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
            if (_connectionString is not null)
            {
                await SharedPostgresCluster.DropDatabaseAsync(_connectionString);
            }
        }
    }
}
