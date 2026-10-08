namespace Infrastructure.Persistence.Provisioning;

/// <summary>
/// Thrown when verifying the application role's reach finds something to act on — a missing or
/// non-login role, or a widening — an extra privilege, attribute, membership, ownership or write
/// path — which is fail-open and silent.
/// </summary>
/// <remarks>
/// It derives from <see cref="InvalidOperationException"/>, as
/// <see cref="RowLevelSecurityCoverageException"/> does, so anything handling provisioning failures
/// by the base type keeps working. The deploy tool does not branch on it: it catches every exception,
/// prints <see cref="Exception.Message"/> and exits 1, so the message is all an operator gets.
/// </remarks>
public sealed class AppRoleReachException : InvalidOperationException
{
    /// <summary>Creates the exception from one human-readable sentence per finding.</summary>
    /// <param name="problems">Each finding, naming the object it is on and how to clear it.</param>
    public AppRoleReachException(IReadOnlyList<string> problems)
        : base(BuildMessage(problems)) => Problems = problems;

    /// <summary>Each finding, naming the object it is on and how to clear it.</summary>
    public IReadOnlyList<string> Problems { get; }

    // Composed here rather than by the caller: an uncaught throw out of a deploy step shows the
    // operator Message and nothing else. One finding per line, joined with '\n' whatever the
    // platform, so a dozen of them can be scanned; the lead claims nothing a missing or non-login
    // role would contradict.
    private static string BuildMessage(IReadOnlyList<string> problems) =>
        $"Verifying the application role's reach found {problems.Count} problem(s), so the deploy "
        + "stops here; clear each one as its line says, then deploy again:\n"
        + string.Join('\n', problems.Select(problem => $"- {problem}"));
}
