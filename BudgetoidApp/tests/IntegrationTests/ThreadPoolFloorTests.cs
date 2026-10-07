namespace IntegrationTests;

/// <summary>
/// Pins that the pool already has its floor by the time any test runs, so a burst of API boots
/// parked in <see cref="SharedPostgresCluster.UnderRoleGate{T}" /> cannot starve every other test's
/// continuations.
/// </summary>
public sealed class ThreadPoolFloorTests
{
    [Test]
    public async Task MinWorkerThreads_AreRaisedToTheFloor_BeforeAnyTestRuns()
    {
        // Act
        ThreadPool.GetMinThreads(out int workers, out _);

        // Assert
        await Assert.That(workers).IsGreaterThanOrEqualTo(ThreadPoolFloor.WorkerThreads);
    }
}
