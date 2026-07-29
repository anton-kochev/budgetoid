using System.Buffers;
using Npgsql;

namespace Infrastructure.Persistence.Provisioning;

/// <summary>
/// Creates the least-privilege application role and applies its grant matrix by executing the
/// embedded <c>app-role-grants.sql</c> script on an admin connection. The script is the single
/// source of truth for the role's write surface: the test hosts run it after
/// <c>MigrateAsync</c>, local dev runs it on every boot, and the production deploy step runs
/// the same SQL.
/// </summary>
/// <remarks>
/// This is deliberately not an EF migration and must never become one: the repo regenerates
/// its single baseline migration, and hand-added SQL inside it is lost on every regeneration.
/// Grants also target a role, not the schema — they belong to provisioning, which re-runs and
/// converges, not to a migration history that applies once.
/// </remarks>
public static class DatabaseProvisioning
{
    /// <summary>Name of the least-privilege PostgreSQL role the application connects as.</summary>
    public const string AppRoleName = "budgetoid_app";

    /// <summary>
    /// Token in the grants script that the password is substituted for. CREATE ROLE cannot take
    /// a parameter placeholder, so the substitution happens in C#; <see cref="EnsurePasswordAlphabet"/>
    /// is what makes splicing the value into SQL safe.
    /// </summary>
    private const string PasswordToken = "__APP_PASSWORD__";

    private const string GrantsResourceName =
        "Infrastructure.Persistence.Provisioning.app-role-grants.sql";

    /// <summary>
    /// Characters a role password may consist of. Provisioning owns the password, so restricting
    /// its alphabet is legitimate — and simpler and stronger than escaping. The set excludes
    /// single quotes and backslashes (SQL string literal syntax) and dollar signs (the script's
    /// dollar-quoted DO block), so a password that passes this check cannot alter the meaning of
    /// the SQL it is spliced into.
    /// </summary>
    private static readonly SearchValues<char> AllowedPasswordCharacters = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~!@#%^*+=");

    /// <summary>
    /// Ensures the <see cref="AppRoleName"/> role exists with <paramref name="appRolePassword"/>
    /// and holds exactly the grants the script defines. Idempotent; safe to run on every boot
    /// and every deploy.
    /// </summary>
    /// <param name="adminConnectionString">
    /// Connection string for a role allowed to create roles and grant privileges on the
    /// application schema.
    /// </param>
    /// <param name="appRolePassword">
    /// Password to assign to the application role. Restricted to a conservative ASCII alphabet;
    /// see <see cref="EnsurePasswordAlphabet"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the provisioning round-trip.</param>
    /// <exception cref="ArgumentException">
    /// The password is empty or contains a character outside the allowed alphabet.
    /// </exception>
    public static async Task ApplyGrantsAsync(
        string adminConnectionString,
        string appRolePassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(adminConnectionString);
        ArgumentException.ThrowIfNullOrEmpty(appRolePassword);
        EnsurePasswordAlphabet(appRolePassword);

        string script = await ReadGrantsScriptAsync(cancellationToken);
        string sql = script.Replace(
            PasswordToken, $"'{appRolePassword}'", StringComparison.Ordinal);

        await using NpgsqlConnection connection = new(adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void EnsurePasswordAlphabet(string appRolePassword)
    {
        if (appRolePassword.AsSpan().ContainsAnyExcept(AllowedPasswordCharacters))
        {
            throw new ArgumentException(
                "The application role password may only contain ASCII letters, digits, and "
                + "-_.~!@#%^*+= — it is spliced into the grants script as a literal, and the "
                + "restricted alphabet is what makes that safe.",
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
