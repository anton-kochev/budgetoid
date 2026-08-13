using Domain.Budgets;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Provisioning;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

public sealed class RepositoryTestHost : IAsyncDisposable
{
    /// <summary>
    /// Superuser connection string for this host's own database inside the shared cluster, or
    /// <see langword="null" /> until <see cref="StartAsync" /> has produced one.
    /// </summary>
    private string? _connectionString;

    public string ConnectionString => _connectionString
        ?? throw new InvalidOperationException(
            $"{nameof(RepositoryTestHost)} has no database until {nameof(StartAsync)} has run.");

    /// <summary>
    /// Connects as the least-privilege application role instead of the container account. This
    /// property exists because <see cref="ConnectionString" /> cannot measure privileges at all:
    /// the host's own connection is the container superuser, and PostgreSQL skips every privilege
    /// check for a superuser — a permissions test run on the admin connection passes no matter
    /// what the grants say, including with no grants script at all. Only a statement sent through
    /// this connection string observes the role's real write surface.
    /// </summary>
    public string AppConnectionString => new NpgsqlConnectionStringBuilder(ConnectionString)
    {
        Username = DatabaseProvisioning.AppRoleName,
        Password = SharedPostgresCluster.AppRolePassword,
    }.ConnectionString;

    /// <summary>
    /// Opens a connection as the least-privilege application role <b>with the signed-in user and
    /// the ambient budget already on the session</b>, so that "an app-role connection" and "an
    /// app-role connection carrying the session state production puts on it" are the same thing
    /// rather than two states a caller can get wrong. Callers own the returned connection and
    /// dispose it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Row-level security polices these tables on two axes: <c>budget_isolation</c> on the
    /// budget-owned tables keys its policies to <c>app.current_budget_id</c>, and
    /// <c>user_isolation</c> on <c>users</c> and <c>budgets</c> keys its policies to
    /// <c>app.current_user_id</c>. That is why this overload takes two arguments and not one. The
    /// two axes fail in opposite ways, and which is which is worth knowing before reading a red run.
    /// A wrong or missing budget is silent: an UPDATE that should affect one row affects zero and
    /// reports success, so an assertion on the affected count fails while an assertion on a refusal
    /// passes for entirely the wrong reason. A wrong user is loud — the row is simply invisible, and
    /// an unset <c>app.current_user_id</c> reaches the policy as <c>''::uuid</c> and raises
    /// <c>22P02</c>. <paramref name="budgetId" /> must therefore be the budget the statements on
    /// this connection target rows in, not just any real budget — and <paramref name="userId" />
    /// must be the user that owns it.
    /// </para>
    /// <para>
    /// Both settings, always, which is why this takes two arguments rather than keeping a
    /// one-argument overload beside them. The production interceptor writes both on every connection
    /// it opens, so a session naming only a budget is a state production cannot produce and a test
    /// running in it measures a database no request ever reaches. An overload would also be quietly
    /// dangerous in the other direction: an existing one-argument call site would keep compiling and
    /// silently rebind its budget id to <paramref name="userId" />, which in a security test is the
    /// difference between a green run and a green run that proves nothing.
    /// <see cref="OpenAppConnectionForUserAsync" /> is the deliberate exception, for the tables that
    /// have no ambient budget at all.
    /// </para>
    /// <para>
    /// <c>set_config(..., false)</c> — not <c>SET LOCAL</c>. These tests send statements in
    /// autocommit, and <c>SET LOCAL</c> outside a transaction sets nothing and warns. Both calls
    /// travel in one statement, so the session is never observable half-configured. The value is
    /// passed as text because <c>set_config</c> takes text: bind the <see cref="Guid" /> itself and
    /// Npgsql infers <c>uuid</c>, which no <c>set_config</c> overload accepts. Setting a custom GUC
    /// — one with a dotted namespace — needs no privilege, so the statement succeeds whatever value
    /// it names; the policies that read those settings are what decide the cost of naming the wrong
    /// one, and both policies exist.
    /// </para>
    /// </remarks>
    public Task<NpgsqlConnection> OpenAppConnectionAsync(Guid userId, Guid budgetId) =>
        OpenConfiguredAppConnectionAsync(
            "select set_config('app.current_user_id', @user, false), "
                + "set_config('app.current_budget_id', @budget, false)",
            [("user", userId), ("budget", budgetId)]);

    /// <summary>
    /// Opens an app-role connection carrying <b>only</b> the signed-in user, which is what every
    /// app-role statement against <c>users</c> and <c>budgets</c> has to go through. Both tables are
    /// policed on <c>app.current_user_id</c>, so this is a requirement and not a convenience: a bare
    /// app-role connection does not quietly read the wrong rows there, it fails outright with
    /// <c>22P02</c>, because an unset setting reaches the policy as <c>''::uuid</c>. Neither table is
    /// budget-owned — a user owns budgets rather than belonging to one, and a budget is the tenant
    /// rather than a tenant's row — so there is no ambient budget for such a session to carry, and
    /// naming one would only suggest there was. Callers own the returned connection and dispose it.
    /// </summary>
    /// <remarks>
    /// Everything <see cref="OpenAppConnectionAsync" /> says about <c>set_config(..., false)</c> and
    /// about passing the value as text holds here unchanged; the only difference is which settings
    /// the session declares.
    /// </remarks>
    public Task<NpgsqlConnection> OpenAppConnectionForUserAsync(Guid userId) =>
        OpenConfiguredAppConnectionAsync(
            "select set_config('app.current_user_id', @user, false)",
            [("user", userId)]);

    /// <summary>
    /// Opens an app-role connection and applies one <c>set_config</c> statement to it. Shared so the
    /// two openers above cannot drift on the half that is not about which settings they declare.
    /// </summary>
    private async Task<NpgsqlConnection> OpenConfiguredAppConnectionAsync(
        string setConfigSql,
        (string Name, Guid Value)[] settings)
    {
        NpgsqlConnection connection = new(AppConnectionString);

        try
        {
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(setConfigSql, connection);
            foreach ((string name, Guid value) in settings)
            {
                command.Parameters.AddWithValue(name, value.ToString());
            }

            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // The caller never receives the connection on this path, so nothing else can close it.
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }

    /// <summary>
    /// Takes a database out of the shared cluster, already migrated and already provisioned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The migration and the grants script used to run here, once per test, against a container of
    /// this host's own. They now run once for the whole assembly, into the template every database
    /// here is cloned from — and the clone inherits the result rather than reproducing it. That is
    /// sound because the two halves of provisioning live in different places: the schema, the grant
    /// matrix and the isolation policies are all per-<b>database</b> catalogs (<c>pg_class.relacl</c>,
    /// <c>pg_policy</c>) and are copied by <c>CREATE DATABASE ... TEMPLATE</c>, while the role itself
    /// is cluster-level and is therefore already in place. A test that revokes a grant or drops a
    /// policy still affects nothing but its own database.
    /// </para>
    /// <para>
    /// The failure path drops the database here rather than leaving it to the caller, and that is the
    /// whole reason for the try/catch. Every call site has the shape
    /// <c>await using RepositoryTestHost host = await StartHostAsync();</c>, so the variable is bound
    /// only <b>after</b> this method returns: when the start throws, nothing is ever disposed, and a
    /// database that was created and then abandoned keeps its files and its catalog entry for the
    /// rest of the run. Owning the cleanup here also means a call site added later cannot forget it.
    /// </para>
    /// </remarks>
    public async Task StartAsync() =>
        _connectionString = await SharedPostgresCluster.CreateDatabaseAsync();

    /// <summary>
    /// The user and the default budget one call to <see cref="SeedOwnerAsync" /> created, paired
    /// because the two ids are only meaningful together: an app-role session names both, and a test
    /// that holds one without the other cannot open one.
    /// </summary>
    /// <remarks>
    /// A <see langword="readonly" /> <see langword="record" /> <see langword="struct" /> rather than
    /// a class: this is a pair of ids with no identity of its own, it is destructured at nearly
    /// every call site, and it is never stored, mutated or compared by reference.
    /// </remarks>
    public readonly record struct SeededOwner(Guid UserId, Guid BudgetId);

    /// <summary>
    /// Persists a user together with its default budget and returns <b>both</b> ids, so a test can
    /// open an app-role connection — which names a user and a budget — from one seeding call.
    /// </summary>
    /// <remarks>
    /// The seeding context is built without an <c>IBudgetContext</c>, which is only safe because
    /// <c>Budget</c> deliberately carries no global query filter — its owner scoping is explicit at
    /// every call site instead.
    /// </remarks>
    public async Task<SeededOwner> SeedOwnerAsync(string googleSubject, string email)
    {
        Guid userId = await SeedUserAsync(googleSubject, email);
        await using var db = CreateSeedingDbContext();
        Budget budget = Budget.CreateDefault(userId, SeedInstant);
        db.Budgets.Add(budget);
        await db.SaveChangesAsync();
        return new SeededOwner(userId, budget.Id);
    }

    /// <summary>
    /// Persists a user together with its default budget and returns the <b>budget</b> id, so tests
    /// can satisfy the budgets foreign key on every owned entity with a real tenant row. Tests that
    /// also need the owner want <see cref="SeedOwnerAsync" />, which this delegates to.
    /// </summary>
    public async Task<Guid> SeedBudgetAsync(string googleSubject, string email) =>
        (await SeedOwnerAsync(googleSubject, email)).BudgetId;

    /// <summary>
    /// Persists a user together with the federated Google credential that resolves to it, and
    /// returns the <b>user</b> id. Tests of budget-owned entities want
    /// <see cref="SeedBudgetAsync"/>.
    /// </summary>
    /// <remarks>
    /// Both rows go in one <c>SaveChangesAsync</c>, mirroring the shape <c>UserRepository</c>
    /// inserts them in: a seeded user without its credential would be a state production can never
    /// produce, so tests written against it would be testing a schema nobody ships.
    /// <paramref name="googleSubject"/> is still the caller's handle on the identity, which is why
    /// this signature outlived the column it used to write.
    /// </remarks>
    public async Task<Guid> SeedUserAsync(string googleSubject, string email)
    {
        await using var db = CreateSeedingDbContext();
        User user = User.Create(email, SeedInstant);
        db.Users.Add(user);
        db.Credentials.Add(Credential.CreateFederated(
            user.Id, Credential.GoogleProvider, googleSubject, SeedInstant));
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>
    /// Persists a whole passkey onto an existing account — the <c>credentials</c> row an
    /// authenticator's key hangs off, the public key that verifies its signatures, and the signature
    /// counter a clone gives itself away against — and returns the <b>credential</b> id, which is the
    /// primary key of both dependent rows and therefore the handle every probe needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All three rows in one <c>SaveChangesAsync</c>, mirroring the shape a registration writes them
    /// in. A credential without its key, or a key without its counter, is a state no ceremony can
    /// produce: the assertion path reads the key to verify the signature and the counter immediately
    /// after, so a test written against a half-seeded passkey would be measuring a schema nobody
    /// ships. Tests that deliberately want a bare passkey credential — a probe row aimed at the empty
    /// primary key — build one with raw SQL instead, which is the honest way to say that the gap is
    /// the point.
    /// </para>
    /// <para>
    /// Through the domain factories rather than raw SQL, unlike the passkey credentials the schema
    /// tests seed by hand. These three now have factories, and going through them means a seeded row
    /// is one the application could really have written — so a probe that lands beside it is measured
    /// against production's own shape rather than against whatever column list a test typed out.
    /// </para>
    /// </remarks>
    public async Task<Guid> SeedPasskeyAsync(
        Guid userId,
        byte[] webAuthnCredentialId,
        byte[]? coseKey = null,
        CoseAlgorithm algorithm = CoseAlgorithm.Es256,
        uint signatureCounter = 0)
    {
        ArgumentNullException.ThrowIfNull(webAuthnCredentialId);

        await using var db = CreateSeedingDbContext();
        Credential credential = Credential.CreatePasskey(userId, SeedInstant);
        db.Credentials.Add(credential);
        db.PasskeyPublicKeys.Add(PasskeyPublicKey.Register(
            credential, webAuthnCredentialId, coseKey ?? DefaultCoseKey, algorithm));
        db.PasskeySignatureCounters.Add(PasskeySignatureCounter.Start(credential, signatureCounter));
        await db.SaveChangesAsync();
        return credential.Id;
    }

    /// <summary>
    /// The COSE key seeded passkeys carry when a caller does not name one. Four bytes: nothing here
    /// verifies a signature, and the only rule the column holds is that the key is between one byte
    /// and <see cref="PasskeyPublicKey.MaxCoseKeyLength" />. A caller testing that bound passes its
    /// own.
    /// </summary>
    private static readonly byte[] DefaultCoseKey = [0xA5, 0x01, 0x02, 0x03];

    /// <summary>
    /// Files the account's two wrapped keys against an existing credential — one row in
    /// <c>wrapped_account_keys</c> — and returns the <b>factor</b> identifier it carries, which is the
    /// only column of that row a caller cannot already name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Through <see cref="WrappedAccountKeys.For" /> rather than raw SQL, for the reason
    /// <see cref="SeedPasskeyAsync" /> gives: a seeded row is then one the application could really
    /// have written, so a probe landing beside it is measured against production's own shape. The
    /// factory takes the loaded <see cref="Credential" />, so this reads it back rather than accepting
    /// three loose ids — which is the whole argument that factory makes.
    /// </para>
    /// <para>
    /// <paramref name="factorId" /> is the caller's to choose and is required. It is minted by the
    /// client in production, <c>IX_wrapped_account_keys_factor_id</c> is unique across the whole table
    /// rather than per account, and a default would therefore turn two seeded rows anywhere in one
    /// database into a <c>23505</c> — which is exactly the refusal one test here is reading and no
    /// other test wants to meet by accident.
    /// </para>
    /// </remarks>
    public async Task<Guid> SeedWrappedAccountKeysAsync(Guid credentialId, Guid factorId)
    {
        await using var db = CreateSeedingDbContext();
        Credential credential = await db.Credentials
            .SingleAsync(candidate => candidate.Id == credentialId);
        db.WrappedAccountKeys.Add(WrappedAccountKeys.For(
            credential,
            factorId,
            WrappedKeyEnvelope(SeededContentKeyFiller),
            WrappedKeyEnvelope(SeededIndexKeyFiller),
            SeedInstant));
        await db.SaveChangesAsync();

        return factorId;
    }

    /// <summary>
    /// The fillers the two seeded envelopes carry. Different from each other so a read-back naming the
    /// wrong column is visible by eye, and neither is the filler a probe writes.
    /// </summary>
    public const byte SeededContentKeyFiller = 0xC0;

    public const byte SeededIndexKeyFiller = 0x1D;

    /// <summary>
    /// Builds a well-formed wrapped-key envelope: the one version byte the contract defines, then
    /// <paramref name="filler" /> to the column's exact width.
    /// </summary>
    /// <remarks>
    /// The filler is neither a nonce nor a ciphertext, and nothing in these tests opens either — no
    /// unlock path exists and this server holds no value that could. What a row has to satisfy is the
    /// width and the version, which <see cref="WrappedAccountKeys.For" /> and two check constraints
    /// per column both refuse to bend; an envelope of any other shape would be refused by one of those
    /// instead of by the grant or the policy a test is reading.
    /// </remarks>
    public static byte[] WrappedKeyEnvelope(byte filler)
    {
        byte[] envelope = new byte[WrappedAccountKeys.EnvelopeLength];
        Array.Fill(envelope, filler);
        envelope[0] = WrappedAccountKeys.EnvelopeVersion;

        return envelope;
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

    /// <summary>
    /// Drops this host's database, and is safe on a host that never started.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_connectionString is not null)
        {
            await SharedPostgresCluster.DropDatabaseAsync(_connectionString);
        }
    }
}
