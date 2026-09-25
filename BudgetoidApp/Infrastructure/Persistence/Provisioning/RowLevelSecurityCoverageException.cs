namespace Infrastructure.Persistence.Provisioning;

/// <summary>
/// Thrown when at least one table is not protected by row-level security — because it has it
/// switched off, because it does not carry exactly one isolation policy, because the policy it does
/// carry is not the one its ownership requires or does not bind the application role, or because
/// nobody has decided which tenant the table's rows belong to at all.
/// </summary>
/// <remarks>
/// It derives from <see cref="InvalidOperationException"/> so that anything already handling
/// provisioning failures by the base type keeps working, and it is the coverage failure specifically
/// so that a deploy pipeline can tell "the tenancy boundary is missing, here is where" from "the
/// database was unreachable".
/// </remarks>
public sealed class RowLevelSecurityCoverageException : InvalidOperationException
{
    /// <summary>
    /// Creates the exception from the offending table names and one human-readable problem per
    /// table.
    /// </summary>
    /// <param name="tables">Names of the tables that are not accounted for.</param>
    /// <param name="problems">
    /// Per-table descriptions of what is wrong, each naming its table.
    /// </param>
    public RowLevelSecurityCoverageException(
        IReadOnlyList<string> tables,
        IReadOnlyList<string> problems)
        : base(BuildMessage(tables, problems)) => Tables = tables;

    /// <summary>Names of the tables that are not accounted for.</summary>
    public IReadOnlyList<string> Tables { get; }

    // The message is composed here, from the same list Tables exposes, rather than by the caller:
    // an uncaught throw out of a deploy step shows the operator Message and nothing else, so a
    // coverage failure whose table names lived only in a property would be unreadable in exactly
    // the situation this exception exists for.
    private static string BuildMessage(
        IReadOnlyList<string> tables,
        IReadOnlyList<string> problems) =>
        $"Row-level security does not account for {tables.Count} table(s): "
        + $"{string.Join(", ", tables)}. {string.Join(" ", problems)} "
        + "A tenant-owned table the application role can reach without an enforced isolation policy "
        + "is readable and writable across every tenant, so this is a tenancy breach rather than a "
        + "missing feature. Re-apply app-role-grants.sql on the admin connection.";
}
