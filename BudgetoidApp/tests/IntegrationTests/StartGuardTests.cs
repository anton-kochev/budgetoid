namespace IntegrationTests;

/// <summary>
/// What <see cref="StartGuard" /> does when a start fails, when it succeeds, and when the disposal it
/// answers the failure with fails too.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests start no container and need no Docker.</b> That is the whole reason the guard was
/// extracted: the branch it exists for is reachable only by a Docker daemon that has gone away, which
/// is a state no test can arrange and — until the guard took a delegate — no test could stand in for.
/// Against a fake whose start throws on demand, the branch is ordinary code with two observable
/// effects, and both are asserted rather than argued.
/// </para>
/// <para>
/// <b>The success case is not filler.</b> Without it, "disposed exactly once on failure" is satisfied
/// by a guard that disposes unconditionally — which would hand every caller in this assembly a stopped
/// container and turn a leak into a suite that cannot run at all. The two tests are the two halves of
/// one claim, and the failure directions they catch are opposite.
/// </para>
/// <para>
/// <b>Nothing here asserts on <c>PostgreSqlContainer</c>.</b> The claim being pinned is about the
/// guard, not about Testcontainers, and the container's behaviour under a failed start — created,
/// <c>Up</c>, and released only by <c>DisposeAsync</c> — is a fact about version 4.12.0 recorded on
/// <see cref="StartGuard" /> and in <c>AssemblyInfo.cs</c>. A test asserting it here would be a test
/// of somebody else's library that needed a broken daemon to run.
/// </para>
/// </remarks>
public sealed class StartGuardTests
{
    /// <summary>
    /// A failed start disposes the resource exactly once and lets the original failure through
    /// untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Exactly once, not at least once.</b> A guard that disposed twice would call
    /// <c>DisposeAsync</c> on a resource it had already released, which for a container is a second
    /// stop against a daemon that has just failed one — noise on top of the failure being reported.
    /// </para>
    /// <para>
    /// <b>The same exception instance, not merely the same type.</b> A guard that caught the failure
    /// and threw a new <see cref="TimeoutException" /> of its own would satisfy a type assertion and
    /// discard the stack trace that says which readiness check gave up — the one piece of information
    /// the person reading the red run needs.
    /// </para>
    /// </remarks>
    [Test]
    public async Task StartAsync_WhenTheStartThrows_DisposesOnceAndLetsTheFailureThrough()
    {
        // Arrange — a start that fails the way a readiness check does: asynchronously, after yielding.
        TimeoutException startFailure = new("The container never reported healthy.");
        FakeResource resource = new(async () =>
        {
            await Task.Yield();
            throw startFailure;
        });

        // Act
        Exception? escaped = await CaptureAsync(
            () => StartGuard.StartAsync(resource, started => started.StartAsync()));

        // Assert — the resource was released, and released once.
        await Assert.That(resource.Disposals).IsEqualTo(1);

        // And the caller is told what actually went wrong, by the object that knows.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(ReferenceEquals(escaped, startFailure)).IsTrue();
    }

    /// <summary>
    /// A start that succeeds hands the resource back and disposes nothing.
    /// </summary>
    /// <remarks>
    /// The control the failure test cannot supply. It also pins the identity of what comes back:
    /// returning a different instance would leave the caller's <c>await using</c> holding something
    /// other than the container that was started, and the started one leaked.
    /// </remarks>
    [Test]
    public async Task StartAsync_WhenTheStartSucceeds_ReturnsTheResourceUndisposed()
    {
        // Arrange
        FakeResource resource = new(() => Task.CompletedTask);

        // Act
        FakeResource started = await StartGuard.StartAsync(resource, candidate => candidate.StartAsync());

        // Assert
        await Assert.That(ReferenceEquals(started, resource)).IsTrue();
        await Assert.That(resource.Disposals).IsEqualTo(0);
        await Assert.That(resource.Starts).IsEqualTo(1);
    }

    /// <summary>
    /// When the disposal fails too, the start failure is not lost behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failure mode this pins is a misdiagnosis, not a leak.</b> An unguarded
    /// <c>await resource.DisposeAsync()</c> inside the <c>catch</c> discards the exception being
    /// handled, so a Docker daemon that has gone away surfaces as whatever the disposal happened to
    /// throw — and the reader is sent to look at cleanup code while the actual problem is that nothing
    /// can talk to Docker. The container leaks in that case as well, so the guard would have bought
    /// nothing and cost the diagnosis.
    /// </para>
    /// <para>
    /// <b>Both failures travel, and the order is asserted.</b> Swallowing the disposal failure and
    /// rethrowing the original is the shorter fix and is wrong in the other direction: a resource that
    /// refused to be released is still out there, and nothing would say so. The start failure is first
    /// because it is the diagnosis; the disposal failure is the consequence.
    /// </para>
    /// <para>
    /// The disposal is still asserted to have been attempted, or this test would also pass against a
    /// guard that had stopped disposing altogether and thrown for some other reason.
    /// </para>
    /// </remarks>
    [Test]
    public async Task StartAsync_WhenTheDisposalAlsoThrows_ReportsBothAndKeepsTheStartFailureFirst()
    {
        // Arrange — a start that fails, and a resource that cannot be released either.
        TimeoutException startFailure = new("The container never reported healthy.");
        InvalidOperationException disposalFailure = new("The daemon is unreachable.");
        FakeResource resource = new(
            () => Task.FromException(startFailure),
            onDispose: () => throw disposalFailure);

        // Act
        Exception? escaped = await CaptureAsync(
            () => StartGuard.StartAsync(resource, started => started.StartAsync()));

        // Assert — the disposal really was attempted.
        await Assert.That(resource.Disposals).IsEqualTo(1);

        // And neither failure was dropped, with the start failure named first.
        await Assert.That(escaped).IsTypeOf<AggregateException>();
        IReadOnlyList<Exception> reported = ((AggregateException)escaped!).InnerExceptions;
        await Assert.That(reported.Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(reported[0], startFailure)).IsTrue();
        await Assert.That(ReferenceEquals(reported[1], disposalFailure)).IsTrue();
    }

    /// <summary>
    /// A resource whose start and disposal are both the caller's to decide, and which counts what
    /// happened to it.
    /// </summary>
    /// <remarks>
    /// A traditional constructor with readonly fields rather than a primary one, and counters rather
    /// than booleans: "was it disposed" cannot tell a single disposal from a double one, which is
    /// exactly the distinction the failure test above is written to make.
    /// </remarks>
    private sealed class FakeResource : IAsyncDisposable
    {
        private readonly Func<Task> _start;
        private readonly Action? _onDispose;

        public FakeResource(Func<Task> start, Action? onDispose = null)
        {
            _start = start;
            _onDispose = onDispose;
        }

        /// <summary>How many times the guard asked this resource to start.</summary>
        public int Starts { get; private set; }

        /// <summary>How many times the guard released this resource.</summary>
        public int Disposals { get; private set; }

        public Task StartAsync()
        {
            Starts++;
            return _start();
        }

        public ValueTask DisposeAsync()
        {
            // Counted before the callback can throw, so a disposal that fails is still a disposal
            // that was attempted — which is what the both-failed test asserts on.
            Disposals++;
            _onDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" /> when
    /// nothing did. Deliberately untyped, as everywhere else in this folder: the question these tests
    /// ask is <i>which</i> exception surfaces, so catching a specific one here would decide the answer
    /// before the assertion reads it.
    /// </summary>
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
