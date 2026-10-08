using System.Text.RegularExpressions;

namespace Infrastructure.Persistence.Provisioning;

/// <summary>
/// The application role a provisioning call acts on: <see cref="Production" /> everywhere the
/// application runs, and a role of its own per test in the integration suite.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A role is server-wide, so on one shared PostgreSQL server every deployment
/// test that sabotages <c>budgetoid_app</c> — alters it, renames it, grants it a parameter — would be
/// sabotaging every other test's role too. Taking the role as a value lets each test provision one
/// nobody else touches. Internal, with internal overloads, so the application has no way to
/// provision anything but production's role: Infrastructure.csproj carries the argument.
/// </para>
/// <para>
/// <b>Why the name is so narrow.</b> A role name cannot be a bound parameter in <c>CREATE ROLE</c>,
/// <c>GRANT</c> or a policy's <c>TO</c>, so it is spliced into SQL as written. A lowercase letter or
/// underscore, then up to 62 lowercase letters, digits or underscores, is an identifier PostgreSQL
/// reads exactly as spelled without quoting, fits its 63-byte limit, and can carry no quote,
/// semicolon or space — so a name that passes <see cref="For" /> cannot change what the SQL means.
/// </para>
/// <para>
/// <b>Why rendering is a whole-word replace.</b> <c>app-role-grants.sql</c> stays the single source of
/// truth and names <c>budgetoid_app</c> literally, as the deploy runs it. Rendering for another role
/// replaces that name wherever it stands as a whole word — <c>\b</c> counts the underscore as part of
/// a word, so <c>budgetoid_app_renamed_away</c> is left alone — and rendering for
/// <see cref="Production" /> returns the script byte for byte.
/// </para>
/// </remarks>
internal sealed partial record AppRole
{
    private AppRole(string name) => Name = name;

    /// <summary>The role the application connects as, <c>budgetoid_app</c>.</summary>
    public static AppRole Production { get; } = new(DatabaseProvisioning.AppRoleName);

    /// <summary>The role's name, safe to splice into SQL unquoted.</summary>
    public string Name { get; }

    /// <summary>A role called <paramref name="name" />.</summary>
    /// <exception cref="ArgumentException">
    /// The name is not a plain lowercase identifier of at most 63 characters.
    /// </exception>
    public static AppRole For(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!PlainIdentifier().IsMatch(name))
        {
            throw new ArgumentException(
                "A role name must be a lowercase letter or underscore followed by up to 62 lowercase "
                + "letters, digits or underscores — it is spliced into SQL unquoted.",
                nameof(name));
        }

        return name == Production.Name ? Production : new AppRole(name);
    }

    /// <summary>
    /// <paramref name="script" /> with every whole-word <c>budgetoid_app</c> replaced by this role.
    /// </summary>
    public string Render(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return this == Production ? script : ProductionName().Replace(script, Name);
    }

    [GeneratedRegex(@"\A[a-z_][a-z0-9_]{0,62}\z")]
    private static partial Regex PlainIdentifier();

    [GeneratedRegex(@"\bbudgetoid_app\b")]
    private static partial Regex ProductionName();
}
