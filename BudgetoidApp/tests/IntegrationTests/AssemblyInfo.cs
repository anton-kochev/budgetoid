// This assembly deliberately sets no ParallelLimiter. The absence is the point of this file.
//
// TUnit's default is one test in flight per processor. That default was wrong for this assembly on
// its face back when every test stood up its own PostgreSQL container: what a test cost was not a
// processor, it was Docker — image layers, memory, port bindings and the daemon's own start-up
// serialisation — and a machine's core count says nothing about that. It is no longer wrong in that
// way, because the per-test containers are gone. The limiter is still absent, for a reason that
// outlived the shape it was first argued against.
//
// The shape in place
//
// One postgres:17 container serves the whole assembly. Migrations and the grants script are applied
// once, to a template database; each test then takes a database of its own out of it with
// CREATE DATABASE ... TEMPLATE and drops it WITH (FORCE) afterwards. SharedPostgresCluster, in
// PostgresTestHost.cs, owns all of that, and the implementation detail belongs there rather than
// here.
//
// Isolation is unchanged, and that was checked class by class rather than assumed. Grants, policies
// and schema are per-DATABASE catalogs — pg_class.relacl, pg_policy, and the tables themselves — so
// CREATE DATABASE ... TEMPLATE copies them whole, and a test that revokes a privilege or drops a
// policy is as invisible to its neighbours as it was with a container to itself.
//
// What the shared cluster costs is not isolation but the concurrency-safety of the provisioning
// script. Roles are cluster-level: budgetoid_app is a single pg_authid tuple that every host boot
// writes, and concurrent writers of one tuple do not queue, they fail with
// XX000 tuple concurrently updated. Measured, 3 of 4, 7 of 8 and 15 of 16 callers were lost at
// those thread counts. One gate serialises them.
//
// That gate must exist in exactly one place. SemaphoreSlim is not reentrant, so a gate in
// ApiFactory.CreateHost plus a second one on a host method deadlocks the whole suite — observed,
// not theorised. It lives in ApiFactory.CreateHost, because PasskeyCeremonyTests builds an
// ApiFactory straight over a RepositoryTestHost and so passes through no seam either host could
// offer. Worth carrying away: the seam for this concern is ApiFactory, not the hosts.
//
// DeploymentProvisioningTests and NonSuperuserDeploymentProvisioningTests sit outside all of this,
// deliberately, and build their own containers through PostgreSqlBuilder directly. What they assert
// on is the creation of cluster-level roles that must not already exist, which a shared cluster
// would break the instant they joined it.
//
// The template race — 55006, "source database is being accessed by other users" — is handled twice
// over. The template is migrated over a Pooling=false connection so that no idle session lingers on
// it, and the create retries on a bound on top of that. Measured at 4, 8 and 16 concurrent creates:
// zero occurrences. PostgreSQL 17 clones with the WAL_LOG strategy and does not hold the source
// exclusively.
//
// The numbers, measured on an 11-processor machine
//
// Container-per-test: five runs, median 107.4 s, peak 44 containers. Shared cluster with a database
// per test: five runs plus two confirmations, median 68.8 s, peak 19 containers — the remainder
// being the two deployment classes above and Testcontainers' reaper.
//
// The fixed cost a test pays before its first assertion went from ~3.4 s to ~31 ms. Container start
// at 2696 ms, MigrateAsync at 701 ms and ApplyGrantsAsync at 11.3 ms are now paid once for the
// assembly instead of once per test; what a test pays in their place is CREATE DATABASE ...
// TEMPLATE at 30.8 ms.
//
// The cluster runs with -c max_connections=500. A connection budget that used to be per container
// is now shared by every test in the assembly, so that is the number to raise if the suite grows —
// not the default of 100, which the old shape could never reach and this one can.
//
// Why there is no ParallelLimiter
//
// There was one, capping concurrency at half the processor count. The symptom it was written for
// was never a wrong result. Roughly one run in six lost a single test to a container that never
// reported healthy inside its start-up timeout, and which test that was moved from run to run: a
// failure that says nothing whatever about the code, and the most expensive kind to read, because
// the first thing anyone does with a red test is look at the diff.
//
// The cap treated that as flat resource pressure. It was not. Both test hosts leaked their
// container whenever start-up threw — the caller binds the `await using` variable only after
// StartAsync returns, so a container Docker had already created was never released, and each leak
// made the next start likelier to time out. A feedback loop, not a constant load, which is why
// capping concurrency reduced the rate without ever eliminating it. The remarks on
// PostgresTestHost.StartAsync carry that reasoning, at the code which implements it.
//
// The cap was kept in the same change as the leak fix on purpose — removing both at once would have
// confounded the measurement, since a surviving flake could then be a failed leak fix or a genuinely
// load-bearing cap and nothing in the run output separates those — and was then re-measured on its
// own. Five consecutive runs with no limiter, on the machine above, where the cap had been resolving
// to 5: 771 tests across both test projects, 0 failed, every run, at a median of 106 s against the
// ~2m15s the capped runs had been taking. So the cap was costing roughly a fifth of the suite's wall
// clock to suppress a symptom whose cause was somewhere else entirely.
//
// That argument survives the change of shape, and the pressure it was about has since dropped
// further: peak containers are 19 rather than 44, and the per-test Docker work the cap was rationing
// does not happen at all any more. If an intermittently missing container ever comes back, re-adding
// a cap is still the wrong first move — it is what hid the leak for as long as it was hidden.
//
// The second leak path this file used to send the next reader looking for
//
// It was looked for, and it was there. The two classes above — DeploymentProvisioningTests and
// NonSuperuserDeploymentProvisioningTests — hold the only container starts left outside
// SharedPostgresCluster, and both StartBareContainerAsync helpers carried the unguarded shape
// verbatim: build, await StartAsync, return, with the caller's `await using` variable bound only
// afterwards. Both now start through StartGuard, which disposes before rethrowing — what
// SharedPostgresCluster.StartClusterAsync already did inline, over a wider region of its own that it
// keeps. StartGuard.cs carries the reasoning at the code that implements it, and StartGuardTests
// executes the branch over a fake: a catch reachable only by a broken Docker daemon is a catch no
// test in either of those classes can enter, which is why the guard sat unexecuted for as long as it
// was written out twice.
//
// What the mechanism is was checked rather than assumed, against Testcontainers 4.12.0: a readiness
// check that can never pass throws only after Docker has created and started the container, which is
// then Up at the moment StartAsync raises, and stays Up until DisposeAsync stops it. So an abandoned
// start holds memory and a port binding for the rest of the run — not a container that failed to
// exist, which is how the symptom reads from the test output.
//
// What was NOT established is that this is the cause of any particular red run. The evidence was the
// shape plus a leaked postgres:17 container, Testcontainers-labelled and days old, found sitting on a
// developer machine. One test in DeploymentProvisioningTests was lost once under load and did not
// reproduce over three full suite runs, which is what a one-in-six flake looks like when it does not
// fire. A closed leak path, then; not a diagnosed and cured flake, and the next reader should not
// treat the question as retired.
//
// If it comes back again: what is left to leak per test is now a database rather than a container,
// and both test hosts already drop theirs on the failure path (PostgresTestHost.StartAsync,
// RepositoryTestHost.DisposeAsync). A cap is still the wrong first move.
//
// Why not container-per-class
//
// Still deliberate, and the arithmetic is worth restating against today rather than against the
// container-per-test suite it was first written for. Container-per-class serialises the tests within
// a class, so wall clock stops being total work divided by cores and becomes the longest single
// class: PasskeyCeremonyTests alone is 36 tests. A database per test costs 30.8 ms and keeps every
// one of them in flight, so there is nothing left for the trade to buy.
//
// Sharing one database across a class would also need a reset between tests, and Respawn —
// referenced at 7.0.0, still unused — is not one. It deletes rows. It does not reset schema, roles
// or grants, which is exactly what the schema and grant classes here assert on: they revoke
// privileges, drop policies and read the grant matrix, and a row-level reset leaves every one of
// those changes standing. The reference should be dropped. It is the last trace of an alternative
// these measurements closed off, and a package referenced for an approach nobody will take reads as
// an approach somebody might; removing it is a change of its own, and this file is comment only.
//
// Nothing but comment remains, and the file keeps its place on that basis: what it records is an
// absence, which has nowhere else to live. Assembly attributes are what someone greps for when a
// suite misbehaves under parallelism, so this is where they will look — and where they should find
// the measurement, before re-adding the limiter it removed.
