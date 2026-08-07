// The namespace this file's own type is declared in, imported because an assembly-level attribute
// has to precede every namespace declaration and so is written outside all of them.
using IntegrationTests;
using TUnit.Core.Interfaces;

// Caps how many tests this assembly runs at once. Without it TUnit's default is one test per
// processor, and what a test here costs is not a processor: nearly every one of the ~360 test
// methods in this project stands up its own PostgreSQL container through PostgresTestHost or
// RepositoryTestHost, so the limit that matters is Docker's — image layers, memory, port bindings
// and the daemon's own start-up serialisation — and a machine's core count says nothing about it.
//
// The symptom this addresses is not a wrong result. Roughly one run in six lost a single test to a
// container that never reported healthy inside its start-up timeout, and which test that was moved
// from run to run: a failure that says nothing whatever about the code, and the most expensive kind
// to read, because the first thing anyone does with a red test is look at the diff.
//
// This limiter was originally written as if flat resource pressure were the whole cause. That
// attribution is now known to be incomplete: PostgresTestHost and RepositoryTestHost leaked their
// container whenever start-up threw, because the caller binds the `await using` variable only after
// StartAsync returns, so a container Docker had already created was never released. Each leak made
// the next start likelier to time out, which is a feedback loop rather than a constant load — and
// that is why capping concurrency reduced the rate without eliminating it. Both hosts now dispose
// the container before rethrowing.
//
// The limiter stays for now anyway, deliberately. Removing it in the same change would confound the
// measurement: a flake surviving both changes could be a leak fix that did not work or a concurrency
// cap that was genuinely load-bearing, and nothing in the run output would tell those apart. Re-
// measure over several clean runs, and if the suite stays green, remove this in a separate change.
//
// Halving the processor count rather than picking a number: the machines this runs on differ, and
// the floor of two keeps a single-core CI agent from serialising the suite outright — Math.Max, not
// a comment asking nobody to write one.
//
// Deliberately NOT container-per-class. Respawn 7.0.0 is already referenced and unused, and sharing
// one container across a class with a reset between tests is the obvious next step — but
// PostgresTestHost's isolation is per test today, several classes here provision roles and read the
// grant matrix, and rewriting that is a far larger change than an intermittently missing container
// justifies.
[assembly: ParallelLimiter<ContainerBudget>]

namespace IntegrationTests;

/// <summary>
/// How many container-backed tests may be in flight at once.
/// </summary>
/// <remarks>
/// A record rather than a class only because it is a value with one member and no behaviour;
/// <see cref="ParallelLimiterAttribute{TParallelLimit}" /> constructs it, so it needs the
/// parameterless constructor a positional record would take away.
/// </remarks>
public sealed record ContainerBudget : IParallelLimit
{
    /// <summary>
    /// The floor. One test at a time would turn a suite this size into a serial run measured in
    /// tens of minutes, and a single-core agent is not a reason to accept that.
    /// </summary>
    private const int MinimumConcurrency = 2;

    public int Limit { get; } = Math.Max(MinimumConcurrency, Environment.ProcessorCount / 2);
}
