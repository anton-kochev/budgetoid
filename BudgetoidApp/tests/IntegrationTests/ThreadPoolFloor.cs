using System.Runtime.CompilerServices;

namespace IntegrationTests;

/// <summary>
/// Raises the pool's minimum worker threads before the first test runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is for.</b> Every API boot passes through <see cref="SharedPostgresCluster.UnderRoleGate{T}" />,
/// which blocks on <c>RoleGate.Wait()</c> because <c>CreateHost</c> is a synchronous override. With no
/// ParallelLimiter, the start of a run boots dozens of hosts at once, and each one waiting at the gate
/// parks a pool thread. Stack samples on an 11-core host showed up to 40 of them parked there at the
/// same moment.
/// </para>
/// <para>
/// <b>Why that broke unrelated tests.</b> Above its minimum the pool adds threads at roughly one a
/// second, so every other test's continuations queued behind the parked ones — a work item waited up
/// to 1.9 s for a thread, on the host and inside Pi alike. Opening a Npgsql connection crosses several
/// awaits, and now and then their waits added up past the 15 s connect timeout. That surfaced as
/// <c>NpgsqlException: The operation has timed out</c> thrown from <c>NpgsqlTimeout.Check()</c> before
/// the TCP connect had even started — a failure that reads like the network or the server, and was
/// neither.
/// </para>
/// <para>
/// <b>Why a floor and not a fix at the gate.</b> The blocking cannot move off the pool: the override is
/// synchronous and the callers are tests on pool threads. A floor lets the pool hand out a thread at
/// once instead of injecting them slowly. Threads are created only on demand, so the number is a
/// ceiling on how fast the pool may grow, not a cost paid up front. With 200, runs on the host and in
/// Pi showed no wait of 250 ms or more (from 30 and 22 such waits), and the suite finished about a
/// fifth faster. If the parked count ever approaches the floor, raise it; adding a ParallelLimiter
/// would trade this for the problem AssemblyInfo.cs describes.
/// </para>
/// </remarks>
internal static class ThreadPoolFloor
{
    public const int WorkerThreads = 200;

    [ModuleInitializer]
    internal static void Raise()
    {
        ThreadPool.GetMinThreads(out int workers, out int completionPorts);
        if (workers < WorkerThreads)
        {
            ThreadPool.SetMinThreads(WorkerThreads, completionPorts);
        }
    }
}
