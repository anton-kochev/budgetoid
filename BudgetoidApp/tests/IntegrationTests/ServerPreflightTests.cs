namespace IntegrationTests;

/// <summary>
/// Pins what the suite demands of a PostgreSQL server before any test runs, and that a server falling
/// short stops the run with one message saying what is wrong and where the server came from.
/// </summary>
/// <remarks>
/// A server the suite did not start can be anything. Without this check a too-old major shows up as
/// assertion failures about SQLSTATEs, a missing superuser as hundreds of <c>42501</c>s, and a small
/// connection limit as random <c>53300</c>s half-way through — each a red run that points everywhere
/// but at the server.
/// </remarks>
public sealed class ServerPreflightTests
{
    private static readonly ServerProbe Good = new(Major: 18, IsSuperuser: true, HasPostgresDatabase: true, MaxConnections: 500);

    [Test]
    public async Task Problems_ForAServerThatFits_IsEmpty() =>
        await Assert.That(ServerPreflight.Problems(Good)).IsEmpty();

    [Test]
    public async Task Problems_ListEveryShortfall()
    {
        // Arrange
        ServerProbe probe = new(Major: 17, IsSuperuser: false, HasPostgresDatabase: false, MaxConnections: 100);

        // Act
        IReadOnlyList<string> problems = ServerPreflight.Problems(probe);

        // Assert
        await Assert.That(problems).IsEquivalentTo([
            "PostgreSQL 17, production runs 18",
            "the user is not a superuser",
            "there is no `postgres` database",
            "max_connections is 100, the suite needs at least 500",
        ]);
    }

    [Test]
    public async Task Ensure_NamesTheSourceAndEveryProblem()
    {
        // Arrange
        ServerProbe probe = Good with { Major = 17, MaxConnections = 100 };

        // Act
        InvalidOperationException? exception = await Assert.That(() => ServerPreflight.Ensure(probe, "BUDGETOID_TEST_DATABASE_URL"))
            .Throws<InvalidOperationException>();

        // Assert
        await Assert.That(exception!.Message).Contains("BUDGETOID_TEST_DATABASE_URL");
        await Assert.That(exception.Message).Contains("PostgreSQL 17, production runs 18");
        await Assert.That(exception.Message).Contains("max_connections is 100");
    }

    [Test]
    public async Task Ensure_ForAServerThatFits_DoesNotThrow() =>
        await Assert.That(() => ServerPreflight.Ensure(Good, "the test container")).ThrowsNothing();
}
