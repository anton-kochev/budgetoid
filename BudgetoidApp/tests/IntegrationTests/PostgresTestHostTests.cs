namespace IntegrationTests;

/// <summary>
/// Covers <see cref="PostgresTestHost" />'s own disposal contract. A test host is test code, so it
/// normally earns no tests of its own — but this one is the single object ~360 integration tests
/// depend on to release a container, and a defect in it is invisible in every one of their results.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no test for the container leak itself — the case where
/// <c>_container.StartAsync()</c> throws and the container has to be disposed before the exception
/// leaves <c>StartAsync</c>. Forcing that failure means giving the host a configuration seam
/// (an injectable container, a fault switch) that exists only so a test can reach it, and adding
/// production-shaped indirection to a test host in order to test the test host costs more than the
/// four lines it would guard. The observable proof is the flake rate of the suite over subsequent
/// clean runs, which is the same signal that surfaced the defect.
/// </para>
/// <para>
/// This test, by contrast, needs no seam at all: an unstarted host is a state a caller reaches by
/// construction, and it is exactly the state a failed start leaves behind.
/// </para>
/// </remarks>
public sealed class PostgresTestHostTests
{
    /// <summary>
    /// A host whose <c>StartAsync</c> threw is disposed in the state this test constructs: no
    /// factory, no container. Before the guard, disposal dereferenced <c>Factory</c> — declared
    /// <c>null!</c> and assigned only on the last line of <c>StartAsync</c> — and raised a
    /// <see cref="NullReferenceException" /> that replaced the real start-up failure in the run
    /// output. That substitution is why the intermittently failing test could never be identified.
    /// </summary>
    [Test]
    public async Task DisposeAsync_OnAHostThatNeverStarted_DoesNotThrow()
    {
        // Arrange — construction alone, which is the state a start-up failure leaves behind.
        PostgresTestHost host = new();

        // Act
        Func<Task> dispose = async () => await host.DisposeAsync();

        // Assert
        await Assert.That(dispose).ThrowsNothing();
    }
}
