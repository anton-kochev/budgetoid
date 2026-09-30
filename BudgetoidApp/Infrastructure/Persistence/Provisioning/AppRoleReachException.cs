namespace Infrastructure.Persistence.Provisioning;

/// <summary>
/// Thrown when the application role can reach more than its grant matrix gives it — an extra
/// privilege, attribute, membership or ownership, each of which is fail-open and silent.
/// </summary>
/// <remarks>
/// It derives from <see cref="InvalidOperationException"/>, as
/// <see cref="RowLevelSecurityCoverageException"/> does, so anything handling provisioning failures
/// by the base type keeps working, while a deploy pipeline can still tell "the role is wider than
/// declared" from "the database was unreachable".
/// </remarks>
public sealed class AppRoleReachException : InvalidOperationException
{
    /// <summary>Creates the exception from one human-readable sentence per widening.</summary>
    /// <param name="problems">Each widening, naming the object it is on and how to remove it.</param>
    public AppRoleReachException(IReadOnlyList<string> problems)
        : base(BuildMessage(problems)) => Problems = problems;

    /// <summary>Each widening found, naming the object it is on and how to remove it.</summary>
    public IReadOnlyList<string> Problems { get; }

    // Composed here rather than by the caller: an uncaught throw out of a deploy step shows the
    // operator Message and nothing else.
    private static string BuildMessage(IReadOnlyList<string> problems) =>
        $"The application role reaches {problems.Count} thing(s) beyond its grant matrix: "
        + $"{string.Join(" ", problems)} "
        + "An extra privilege is fail-open and silent, so the deploy stops here; remove each one as "
        + "its sentence says, then deploy again.";
}
