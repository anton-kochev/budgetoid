// This assembly deliberately sets no ParallelLimiter. The absence is the point of this file.
//
// TUnit's default is one test in flight per processor, and that default is wrong for this assembly
// on its face: what a test here costs is not a processor. Nearly every one of the 402 test methods
// in this project stands up its own PostgreSQL container through PostgresTestHost or
// RepositoryTestHost, so the limit that binds is Docker's — image layers, memory, port bindings and
// the daemon's own start-up serialisation — and a machine's core count says nothing about it. That
// reasoning once justified a ParallelLimiter here, capping concurrency at half the processor count.
//
// The symptom it was written for was never a wrong result. Roughly one run in six lost a single
// test to a container that never reported healthy inside its start-up timeout, and which test that
// was moved from run to run: a failure that says nothing whatever about the code, and the most
// expensive kind to read, because the first thing anyone does with a red test is look at the diff.
//
// The cap treated that as flat resource pressure. It was not. PostgresTestHost and
// RepositoryTestHost leaked their container whenever start-up threw — the caller binds the
// `await using` variable only after StartAsync returns, so a container Docker had already created
// was never released, and each leak made the next start likelier to time out. A feedback loop, not
// a constant load, which is why capping concurrency reduced the rate without ever eliminating it.
// Both hosts now dispose the container before rethrowing; the remarks on PostgresTestHost.StartAsync
// carry that reasoning, at the code which implements it.
//
// The cap was kept in the same change as the leak fix on purpose — removing both at once would have
// confounded the measurement, since a surviving flake could then be a failed leak fix or a genuinely
// load-bearing cap and nothing in the run output separates those — and was then re-measured on its
// own. Five consecutive `dotnet test` runs with no limiter, on an 11-processor machine where the cap
// had been resolving to 5: 771 tests across both test projects, 0 failed, every run, at 109s / 106s
// / 104s / 110s / 102s wall clock. Median 106s, against the ~2m15s the capped runs had been taking.
// So the cap was costing roughly a fifth of the suite's wall clock to suppress a symptom whose cause
// was somewhere else entirely. Hence no limiter.
//
// If an intermittently missing container ever comes back, re-adding a cap is the wrong first move:
// that is what hid this bug for as long as it was hidden. Look for a second leak path first.
//
// Deliberately NOT container-per-class. Respawn 7.0.0 is already referenced and unused, and sharing
// one container across a class with a reset between tests is the obvious next step — but
// PostgresTestHost's isolation is per test today, several classes here provision roles and read the
// grant matrix, and rewriting that is a far larger change than an intermittently missing container
// justifies.
//
// Nothing but comment remains, and the file keeps its place on that basis: what it records is an
// absence, which has nowhere else to live. Assembly attributes are what someone greps for when a
// suite misbehaves under parallelism, so this is where they will look — and where they should find
// the measurement, before re-adding the limiter it removed.
