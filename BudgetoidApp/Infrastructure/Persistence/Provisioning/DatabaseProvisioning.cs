using System.Buffers;
using Npgsql;

namespace Infrastructure.Persistence.Provisioning;

/// <summary>
/// Creates the least-privilege application role and applies its grant matrix and row-level
/// security policies by executing the embedded <c>app-role-grants.sql</c> script on an admin
/// connection. The script is the single source of truth for the role's write surface: the test
/// hosts run it after <c>MigrateAsync</c>, local dev runs it on every boot, and the production
/// deploy step runs the same SQL.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not an EF migration and must never become one: the repo regenerates
/// its single baseline migration, and hand-added SQL inside it is lost on every regeneration.
/// Grants also target a role, not the schema — they belong to provisioning, which re-runs and
/// converges, not to a migration history that applies once.
/// </para>
/// <para>
/// <b>What</b> the role may do and <b>how</b> it proves who it is are separate concerns here, and
/// the split is what lets one script run unchanged against a container, a dev machine and Azure.
/// <see cref="ApplyGrantsAsync"/> produces a role that is allowed to log in and has no credential
/// of any kind; a credential is attached afterwards by whichever environment-specific path
/// applies — <see cref="AttachAppRolePasswordAsync"/> locally and in tests,
/// <see cref="AttachAppRoleIdentityAsync"/> in production, where the role is bound to the API's
/// managed identity. The consequence worth knowing: a re-provision can never reset a credential
/// the environment owns, because provisioning does not know one.
/// </para>
/// </remarks>
public static class DatabaseProvisioning
{
    /// <summary>Name of the least-privilege PostgreSQL role the application connects as.</summary>
    public const string AppRoleName = "budgetoid_app";

    private const string GrantsResourceName =
        "Infrastructure.Persistence.Provisioning.app-role-grants.sql";

    /// <summary>
    /// The database Azure Database for PostgreSQL Flexible Server exposes the <c>pgaadauth</c>
    /// security label provider in. It is not reachable from the application database, so
    /// <see cref="AttachAppRoleIdentityAsync"/> has to connect here regardless of which database
    /// the admin connection string names.
    /// </summary>
    private const string LabelProviderDatabase = "postgres";

    /// <summary>
    /// Characters a role password may consist of. Provisioning owns the password, so restricting
    /// its alphabet is legitimate — and simpler and stronger than escaping. The set excludes
    /// single quotes and backslashes (SQL string literal syntax) and dollar signs (dollar-quoting),
    /// so a password that passes this check cannot alter the meaning of the SQL it is spliced into.
    /// </summary>
    private static readonly SearchValues<char> AllowedPasswordCharacters = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~!@#%^*+=");

    /// <summary>
    /// Ensures the <see cref="AppRoleName"/> role exists and holds exactly the grants and isolation
    /// policies the script defines. Idempotent; safe to run on every boot and every deploy.
    /// </summary>
    /// <remarks>
    /// The role is provisioned <b>credential-free</b>: <c>LOGIN</c>, no password, no Entra label. The
    /// script executes verbatim and carries no secret, so it is identical in every environment.
    /// Attaching a credential is a separate, per-environment step — see
    /// <see cref="AttachAppRolePasswordAsync"/> and <see cref="AttachAppRoleIdentityAsync"/>. A role
    /// this method just created cannot authenticate until one of them has run, which is loud rather
    /// than silent: the first login attempt fails with <c>28P01</c>.
    /// </remarks>
    /// <param name="adminConnectionString">
    /// Connection string for a role allowed to create roles and grant privileges on the
    /// application schema.
    /// </param>
    /// <param name="cancellationToken">Cancels the provisioning round-trip.</param>
    public static async Task ApplyGrantsAsync(
        string adminConnectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(adminConnectionString);

        string sql = await ReadGrantsScriptAsync(cancellationToken);

        await using NpgsqlConnection connection = new(adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Gives the application role a password, which is how it authenticates locally and in tests.
    /// Production uses <see cref="AttachAppRoleIdentityAsync"/> instead.
    /// </summary>
    /// <remarks>
    /// The password is validated <i>before</i> a connection is opened, and that ordering is the
    /// contract rather than an implementation detail: <c>ALTER ROLE</c> cannot take a bound
    /// parameter, so the value is spliced into a SQL literal, and a caller must be able to learn its
    /// secret is unusable without having touched the database at all. A rejected call therefore
    /// leaves the role exactly as <see cref="ApplyGrantsAsync"/> left it rather than
    /// half-credentialed.
    /// </remarks>
    /// <param name="adminConnectionString">
    /// Connection string for a role allowed to alter <see cref="AppRoleName"/>.
    /// </param>
    /// <param name="appRolePassword">
    /// Password to assign to the application role. Restricted to a conservative ASCII alphabet;
    /// see <see cref="EnsureValidAppRolePassword"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the round-trip.</param>
    /// <exception cref="ArgumentException">
    /// The password is empty or contains a character outside the allowed alphabet.
    /// </exception>
    public static async Task AttachAppRolePasswordAsync(
        string adminConnectionString,
        string appRolePassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(adminConnectionString);
        EnsureValidAppRolePassword(appRolePassword);

        await using NpgsqlConnection connection = new(adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(
            $"ALTER ROLE {AppRoleName} WITH PASSWORD '{appRolePassword}';", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Binds the application role to a Microsoft Entra managed identity, which is how it
    /// authenticates in production, and drops whatever password it had.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs against the cluster's <c>postgres</c> database rather than the application one: Azure
    /// exposes the <c>pgaadauth</c> label provider only there, so the supplied connection string's
    /// <c>Database</c> is rewritten and every other option preserved. Anything else — a hardcoded
    /// string, or dropping options — would either miss the provider or lose the TLS and timeout
    /// settings the admin connection needs.
    /// </para>
    /// <para>
    /// Deliberately not part of <c>DeploymentDatabaseProvisioning.ProvisionAsync</c>: an identity
    /// that was never attached fails loudly at the first login (<c>28P01</c>), so it needs no
    /// ordering guarantee inside a single call the way the fail-open isolation policies do.
    /// </para>
    /// </remarks>
    /// <param name="adminConnectionString">
    /// Connection string for a role allowed to alter <see cref="AppRoleName"/> and set security
    /// labels on it. Its <c>Database</c> is ignored; see the remarks.
    /// </param>
    /// <param name="appIdentityObjectId">
    /// Object id of the managed identity in the Entra tenant — for the deployed API, its container
    /// app's user-assigned or system-assigned identity.
    /// </param>
    /// <param name="cancellationToken">Cancels the round-trip.</param>
    public static async Task AttachAppRoleIdentityAsync(
        string adminConnectionString,
        Guid appIdentityObjectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(adminConnectionString);

        string labelProviderConnectionString =
            new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Database = LabelProviderDatabase,
            }.ConnectionString;

        await using NpgsqlConnection connection = new(labelProviderConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(
            BuildAppRoleIdentitySql(appIdentityObjectId), connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Builds the two statements that make the application role an Entra principal: the
    /// <c>pgaadauth</c> security label naming <paramref name="appIdentityObjectId"/>, then
    /// <c>PASSWORD NULL</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="AttachAppRoleIdentityAsync"/> because the statements can only be
    /// executed on Azure — vanilla PostgreSQL has no <c>pgaadauth</c> label provider — so pinning the
    /// emitted text is the only verification available anywhere else.
    /// </para>
    /// <para>
    /// The label comes first and the order is load-bearing: nulling the password before the label is
    /// attached would leave a window in which the role has no credential of either kind, and on a
    /// re-provision that window is a production API that cannot authenticate. <c>type=service</c>
    /// because a managed identity is a service principal; <c>user</c> would send Azure looking for
    /// the object id in the wrong directory object class.
    /// </para>
    /// </remarks>
    /// <param name="appIdentityObjectId">Object id of the managed identity, rendered lowercase and
    /// hyphenated — the only form <c>pgaadauth</c> accepts in the <c>oid</c> field.</param>
    /// <returns>Both statements, semicolon-terminated, in the order they must run.</returns>
    public static string BuildAppRoleIdentitySql(Guid appIdentityObjectId) =>
        // The object id is spliced into a single-quoted literal because the SECURITY LABEL grammar
        // takes no bound parameter. Typing the parameter as Guid rather than string is the whole
        // defence: a Guid renders as 32 hex digits and four hyphens and can no more contain a quote
        // than a semicolon, so there is no injection to escape against.
        $"""
         SECURITY LABEL for "pgaadauth" on role {AppRoleName} is 'aadauth,oid={appIdentityObjectId:D},type=service';
         ALTER ROLE {AppRoleName} WITH PASSWORD NULL;
         """;

    /// <summary>
    /// Throws unless <paramref name="appRolePassword"/> is a password this class can splice into an
    /// <c>ALTER ROLE</c> literal: non-empty, and made only of ASCII letters, digits and
    /// <c>-_.~!@#%^*+=</c>.
    /// </summary>
    /// <remarks>
    /// The restricted alphabet <i>is</i> the safety argument. <c>ALTER ROLE ... WITH PASSWORD</c>
    /// cannot take a bound parameter, so <see cref="AttachAppRolePasswordAsync"/> has to write the
    /// value into a single-quoted literal; excluding the quote, the backslash and the dollar sign
    /// means a password that passes this check cannot end the literal or start a new statement.
    /// Public so that a caller holding a configured secret can reject it at the point it reads it,
    /// rather than at the point a connection is opened — and
    /// <see cref="AttachAppRolePasswordAsync"/> calls it too, so the two entry points cannot drift
    /// over what a legal password is.
    /// </remarks>
    /// <param name="appRolePassword">Candidate password for the application role.</param>
    /// <exception cref="ArgumentException">
    /// The password is empty or contains a character outside the allowed alphabet.
    /// </exception>
    public static void EnsureValidAppRolePassword(string appRolePassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(appRolePassword);

        if (appRolePassword.AsSpan().ContainsAnyExcept(AllowedPasswordCharacters))
        {
            throw new ArgumentException(
                "The application role password may only contain ASCII letters, digits, and "
                + "-_.~!@#%^*+= — it is spliced into an ALTER ROLE literal, and the restricted "
                + "alphabet is what makes that safe.",
                nameof(appRolePassword));
        }
    }

    private static async Task<string> ReadGrantsScriptAsync(CancellationToken cancellationToken)
    {
        await using Stream stream =
            typeof(DatabaseProvisioning).Assembly.GetManifestResourceStream(GrantsResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{GrantsResourceName}' was not found. "
                + "Check the <EmbeddedResource> item in Infrastructure.csproj.");
        using StreamReader reader = new(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
