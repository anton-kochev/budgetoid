using Infrastructure.Persistence.Provisioning;

namespace UnitTests;

/// <summary>
/// Covers the one string a deploy operator reads when the reach verifier stops a deploy: the
/// exception's <c>Message</c>, which an uncaught throw out of the deploy step prints and nothing else.
/// </summary>
/// <remarks>
/// Two properties of it. Each problem stands on its own line, because a run of a dozen sentences
/// joined by spaces is one paragraph nobody can scan for the one they need to act on. And the lead
/// sentence says nothing the problems do not bear out: a missing role or a role that cannot log in
/// is not a role reaching <i>beyond</i> anything, and a lead that says so sends the operator looking
/// for a grant that is not there. The problems for those two cases are produced by
/// <see cref="AppRoleReach.FindProblems" /> itself, so they are the sentences a deploy would carry.
/// </remarks>
public sealed class AppRoleReachExceptionTests
{
    private const string RoleName = "budgetoid_app";

    /// <summary>
    /// The lead's claim that a missing or non-login role must not make. Written out rather than read
    /// from the class, because it is the phrase under test.
    /// </summary>
    private const string BeyondTheGrantMatrixClaim = "beyond its grant matrix";

    [Test]
    public async Task Message_WithSeveralProblems_PutsEachOnItsOwnLine()
    {
        // Arrange
        string[] problems =
        [
            "Schema public grants CREATE to budgetoid_app.",
            "Role budgetoid_app has BYPASSRLS.",
            "Routine public.sabotage() grants EXECUTE to PUBLIC.",
        ];

        // Act
        AppRoleReachException exception = new(problems);
        string[] lines = exception.Message.Split('\n');

        // Assert — every problem is the whole of some line, bar a list marker in front of it.
        List<string> notOnALineOfTheirOwn = problems
            .Where(problem => !lines.Any(line => IsTheLineOf(line.TrimEnd('\r'), problem)))
            .ToList();
        await Assert.That(notOnALineOfTheirOwn).IsEmpty();
    }

    [Test]
    public async Task Message_ForAMissingRole_DoesNotClaimReachBeyondTheGrantMatrix()
    {
        // Arrange — the snapshot DiscoverAsync returns when no role of that name exists.
        AppRoleReachSnapshot snapshot = EmptySnapshot(attributes: null);
        IReadOnlyList<string> problems = AppRoleReach.FindProblems(snapshot);

        // Act
        AppRoleReachException exception = new(problems);

        // Assert — the problem is there, and the lead does not call it reach.
        await Assert.That(problems.Count).IsEqualTo(1);
        await Assert.That(exception.Message).Contains(problems[0]);
        await Assert.That(exception.Message).DoesNotContain(BeyondTheGrantMatrixClaim);
    }

    [Test]
    public async Task Message_ForARoleThatCannotLogIn_DoesNotClaimReachBeyondTheGrantMatrix()
    {
        // Arrange — NOLOGIN and nothing else wrong.
        AppRoleReachSnapshot snapshot = EmptySnapshot(
            new AppRoleAttributes(
                CanLogin: false,
                Superuser: false,
                CreateRole: false,
                CreateDatabase: false,
                Replication: false,
                BypassRowLevelSecurity: false));
        IReadOnlyList<string> problems = AppRoleReach.FindProblems(snapshot);

        // Act
        AppRoleReachException exception = new(problems);

        // Assert
        await Assert.That(problems.Count).IsEqualTo(1);
        await Assert.That(exception.Message).Contains(problems[0]);
        await Assert.That(exception.Message).DoesNotContain(BeyondTheGrantMatrixClaim);
    }

    // A line is a problem's own when it ends with the problem and carries nothing before it but
    // whitespace and a list marker — no letter of another sentence.
    private static bool IsTheLineOf(string line, string problem) =>
        line.EndsWith(problem, StringComparison.Ordinal)
        && !line[..^problem.Length].Any(char.IsLetter);

    private static AppRoleReachSnapshot EmptySnapshot(AppRoleAttributes? attributes) => new(
        RoleName,
        attributes,
        MemberOf: [],
        SchemaGrants: [],
        DefaultPrivileges: [],
        OwnedObjects: [],
        PublicRelationGrants: [],
        OutsidePublicRelationGrants: [],
        ParameterGrants: [],
        ExecutableRoutines: [],
        DatabaseGrants: [],
        NonOwnerGrants: []);
}
