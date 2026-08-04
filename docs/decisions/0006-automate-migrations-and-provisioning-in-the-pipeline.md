# ADR 0006 — Automate migrations and role provisioning in the deploy pipeline

- **Status:** Accepted
- **Date:** 2026-07-30
- **Area:** Deployment / CI-CD (GitHub Actions, PostgreSQL migration and provisioning, Key Vault)

## Context

`DEPLOYMENT.md` Steps 3 and 4 were two manual operations run in a fixed order by whoever was
deploying. Step 3 built an EF migration bundle and ran it on the admin connection; Step 4
`sed`-substituted the application role's password into `app-role-grants.sql` and piped the result
through `psql`. The order between them is load-bearing rather than tidy — the grants name individual
tables, so the schema has to exist first — and that order lived entirely in prose.

[ADR 0005](0005-isolate-budget-owned-rows-with-row-level-security.md) put the `budget_isolation`
policies into that same script, and doing so changed what forgetting the second step costs. **The
grant matrix is fail-closed**: a privilege nobody granted announces itself as `42501` at the first
statement that needs it, so a deploy that migrated and skipped provisioning broke a feature loudly
and was repaired within the hour. **Row-level security is fail-open.** A migrated table with no
enforced policy is readable and writable by the application role across every tenant, nothing raises,
and the application behaves exactly as it does when the policy is present. "The operator ran Step 3
and skipped Step 4" therefore moved from an inconvenience to a silent tenancy breach, and an ordering
that lives in a runbook is only as good as the person reading it on the day they read it.

[ADR 0001](0001-postgres-password-authentication.md) closed by tracking **CI-automated migrations**
among the production follow-ups. This record is that follow-up, and taking it reverses one specific
sentence of ADR 0001. Per this repository's convention an ADR is amended by a later ADR and never
edited, so the sentence is named here rather than corrected there. Gotcha #1 of ADR 0001 ends: *"The
repair step and the pipeline identity's Key Vault data-plane grant are no longer needed."* The first
half stands — the connection string is built explicitly in the AppHost and no post-deploy repair
exists. The second half is now false: the pipeline reads the admin connection string out of Key Vault
on every deploy, and therefore holds **Key Vault Secrets User** on the vault. Nothing else about ADR
0001 moves; password authentication stays, and so does the hardening path back to passwordless.

Two sentences of [ADR 0004](0004-connect-as-a-least-privilege-role.md) are superseded for the same
reason and named here on the same convention. Its Decision says the deployed application finds no
value under `ConnectionStrings:budgetoid-admin` "because migration and provisioning are operator
steps at deploy time (`DEPLOYMENT.md`, Steps 3 and 4)", and its Consequences repeat that "in
production they are two operator steps at deploy time". The conclusion still holds and the reason no
longer does: the deployed container is still handed exactly one connection string and still the
least-privilege one, but migration and provisioning are now a single automated step performed by the
pipeline rather than two performed by a person. Everything else in ADR 0004 stands, including the part
this record depends on completely — the grants and the policies apply to a role, converge on re-run,
and must never become a migration.

## Decision

**One tool performs the whole database step, not two mechanisms coordinated by YAML.**
`BudgetoidApp/Tools/DbProvision` calls `DeploymentDatabaseProvisioning.ProvisionAsync`, which
validates the role password, logs the pending migration count, migrates, applies the grants and the
policies through `DatabaseProvisioning.ApplyGrantsAsync`, and then verifies row-level security
coverage. The ordering that used to live in the runbook now lives inside one method, in the layer that
already owns the grants: the logic sits in `Infrastructure` beside `DatabaseProvisioning`, and the
console project is a shim that reads two environment variables, calls the method, and maps an
exception to an exit code. It has no logic of its own, which is why nothing tests it directly —
`tests/IntegrationTests/DeploymentProvisioningTests.cs` tests the method the shim calls.

**Password validation happens before the first statement, and that is the whole reason
`EnsureValidAppRolePassword` was made public.** The alphabet check belongs to
`ApplyGrantsAsync`, because that is where the password is spliced into SQL as a literal — and
`ApplyGrantsAsync` runs *after* the migration. Writing the two calls in their natural order would
therefore migrate first and reject the password second, leaving production half-migrated because a
deploy secret had a typo in it. `ProvisionAsync` calls the check itself, before it opens a context;
`ApplyGrantsAsync` still calls it too, so the two entry points cannot drift over what a legal password
is. `ProvisionAsync_InvalidPasswordAlphabet_ThrowsBeforeTouchingTheDatabase` pins the ordering by
asserting that `__EFMigrationsHistory` does not exist after a rejected password — the earliest trace a
migration attempt leaves, and therefore the strongest available statement that nothing ran.

**Coverage is verified rather than assumed, and the subject of the check is derived from the live
schema.** `VerifyRowLevelSecurityCoverageAsync` reads every ordinary table in `public` through
`RowLevelSecurityCoverage`, which is the same discovery and classification `RlsCoverageTests` runs.
Sharing it is the one place this repository does not prefer deliberate restatement: a second
*executed* list of which tables must be policed has no adjudicator when the two disagree, and the one
that loses stops noticing a table. That is not hypothetical — the verifier used to find its own
subjects by looking for a `budget_id` column, so it could not see `users` at all and reported full
coverage on a schema where every person's row was reachable by a session that named somebody else.
A table is required to carry the isolation policy its own ownership calls for, and every table that
owes none is excused by a written-down exemption rather than by the shape of a query. Each failure
below fails on its own: row-level security switched off (the policy stays in `pg_policy` and is never
enforced), a policy count other than **exactly one** — permissive policies OR together, so a second
can only widen what the first allows — a policy whose *name* is not the one that table owes, a policy
binding neither `budgetoid_app` nor `public`, and a table carrying neither ownership column, which is
refused rather than waved through because "we forgot" and "it needs nothing" produce the identical
catalog. A schema with no table needing a policy at all is a plain `InvalidOperationException` saying
the database is not migrated, because otherwise every check would pass with nothing in it.

**A coverage failure throws `RowLevelSecurityCoverageException`, carrying the offending table names in
both a property and the message.** It derives from `InvalidOperationException` so anything already
handling provisioning failures by the base type keeps working, and it is a distinct type so a caller
can tell "the tenancy boundary is missing, here is where" from "the database was unreachable". The
message is composed inside the exception from the same list the property exposes, because an uncaught
throw out of a deploy step shows the operator `Message` and nothing else: table names living only in a
property would be unreadable in precisely the situation the exception exists for. The verifier logs
what it is about to inspect **before** it renders a verdict, so an operator reading a refusal can see
that the check ran against the database they think it did.

**The database step sits between `azd provision` and `azd deploy`.** Provisioning first, because the
firewall rule and the server have to exist; deploying last, because new application code must never
start against an old schema. The window in which the two disagree is the window in which the API is
serving requests against a database it was not built for, and that window is what this ordering
closes.

**The runner opens a transient single-IP firewall window and closes it in an `if: always()` step.**
Azure Postgres accepts no external connection without a rule, so the step resolves the runner's public
IP and creates a rule for that one address. The rule name is the fixed `gh-deploy-migration`, which
makes `create` behave as an upsert: a rule stranded by a runner that died between opening and closing
is overwritten by the next deploy and then removed by that deploy's cleanup, so the hole self-heals
rather than accumulating. The cleanup is `always()` and keyed on the step's recorded server name, so a
failed migration closes the window it opened instead of leaving the database reachable from a retired
runner's address, and a `|| true` on the delete keeps cleanup from masking the real failure.

**The pipeline reads the admin connection string from Key Vault; the tool does not.** `DbProvision`
takes plain strings and references nothing but `Infrastructure`, so it gains no Azure SDK dependency,
no credential-chain behaviour and no second way to authenticate. Three handling rules go with that.
The value is masked with `::add-mask::` the moment it is read, because it is not a `secrets.*` value
and Actions does not mask it automatically. It is never written to `GITHUB_ENV`, which would persist an
administrator credential to a file on the runner that every later step inherits. And it reaches the
tool as an environment variable rather than as an argument, because argv is visible to other processes
on the machine. `Ssl Mode=Require` is appended for the reason `Api/Program.cs` appends it: the Key
Vault string carries host, user, password and database and no SSL mode, and Azure refuses unencrypted
connections.

**One secret, two consumers.** `AZURE_POSTGRES_APP_PASSWORD` feeds both `azd provision`, which bakes
it into the container's connection string, and the provisioning step, which assigns it to the role.
This dissolves a footgun the runbook used to warn about — two values that had to match, with nothing
in the system reconciling them and a login failure as the only signal that they did not. A guard step
fails the run when the secret is empty, because an unset GitHub secret resolves to the empty string
rather than failing: without the guard, `azd` would provision a container whose connection string has
no password and the tool would try to assign an empty one, and both would look like a green deploy
until the first request.

**`concurrency: group: deploy` with `cancel-in-progress: false`.** Two overlapping runs would have one
run's `always()` firewall cleanup sever the other's migration mid-statement. Cancelling rather than
queueing would do the same thing to whichever run was interrupted, so the choice is to serialize, not
to preempt.

**The step runs on every deploy, ungated.** `MigrateAsync` shares migration ids with the bundle that
was applied by hand, so it no-ops against the current production schema, and the logged pending-count
line is the evidence: a first automated run reporting no pending migrations is what says the two
histories agree. Gating the step behind a manual dispatch or an approval would reintroduce exactly the
drift the automation exists to remove — a schema change merged, deployed, and waiting for somebody to
remember the database half.

## Alternatives considered

- **Keep `dotnet ef migrations bundle` for the schema and add a grants-only tool beside it.** Rejected,
  and it is the alternative worth recording first, because it looks like the smaller change and it
  preserves the actual defect. Two mechanisms means the ordering between them is expressed as two
  workflow steps, and a reordering, an early `exit`, a `continue-on-error`, or a copied job that keeps
  one step and not the other all reintroduce "migrated but not provisioned" — which is silent. One
  process that cannot perform the second half before the first, and refuses to report success without
  verifying the outcome, is the only version where the guarantee is not a property of the YAML.
- **Validate the role password where it is used, in `ApplyGrantsAsync` alone.** Rejected: it is the
  natural reading order and it fails in the worst available way. The grants run after the migration, so
  a password outside the permitted alphabet would be caught only once the schema had already changed,
  leaving a migrated database whose application role was never re-provisioned, on a deploy that failed
  and will be retried by a human under time pressure.
- **A permanent "allow all Azure services" firewall rule instead of a transient one.** Rejected: that
  rule admits every Azure tenant's egress, forever, in exchange for removing two steps from one
  workflow. The transient rule admits one address for a couple of minutes and deletes itself. The
  comparison is not close enough to be a trade-off.
- **Hardcode the five known budget-owned tables in the verifier.** Rejected on the same grounds ADR
  0005 rejected it for `RlsCoverageTests`: a written-down list is correct on every day except the day
  someone adds the sixth table, which is the only day the check has anything to catch. Deriving the
  list from `budget_id` means the schema itself nominates what has to be protected.
- **Assert at least one policy per table.** Rejected: permissive policies OR together, so a second
  policy can only widen what `budget_isolation` allows. "At least one" would pass on a table whose
  isolation had been quietly reopened by an addition, which is the failure a coverage check is for.
- **Let the tool read Key Vault itself with `DefaultAzureCredential`.** Rejected: it pulls the Azure
  SDK and a credential chain into `Infrastructure`'s dependency graph to save one `az` invocation, and
  it makes the tool untestable against a bare container without stubbing a cloud. Plain strings in, no
  ambient identity, no second authentication path to reason about.
- **Pass the connection string and password as command-line arguments.** Rejected: argv is readable by
  other processes on the machine and is echoed by shell tracing, and a tool that accepts a credential
  as an argument invites being run that way by hand. Environment-only, with a usage message and a
  distinct exit code when either variable is missing.
- **Export the Key Vault value to `GITHUB_ENV` so later steps can reuse it.** Rejected: `GITHUB_ENV` is
  a file on the runner, and everything written to it is inherited by every subsequent step in the job.
  One step needs the admin credential; giving it to all of them costs nothing to write and cannot be
  taken back within the run.
- **`cancel-in-progress: true` on the concurrency group.** Rejected: it looks like the standard deploy
  setting and it is unsafe for this job specifically. Cancelling a run mid-migration kills the process
  holding EF's migration lock, and the `always()` cleanup of the cancelled run can close the firewall
  window the surviving run is still using.
- **Gate the database step behind `workflow_dispatch` or a manual approval, and keep pushes
  deploy-only.** Rejected: it is the status quo with a nicer interface. Application code would still
  ship without its schema whenever nobody pressed the button, and the failure would appear as
  application errors in production rather than as a red deploy.
- **Give the role a second, pipeline-specific password secret.** Rejected: it recreates the two-values
  problem the single secret removes. Whichever of the two the container is built from and whichever the
  role is assigned, nothing compares them, and a mismatch surfaces as `28P01` on a cold start rather
  than as a failed deploy.

## Consequences

- **The pipeline identity gains Key Vault Secrets User on the vault holding the admin connection
  string**, on top of the management-plane access `azd pipeline config` grants it. That is the
  privilege automation costs, and it is worth stating plainly rather than leaving it to be discovered:
  the alternative was a human holding the same credential and running the same commands, which is
  strictly more exposure spread over more machines. The passwordless hardening path in ADR 0001 would
  remove this grant again, because there would be no admin password to read.
- **The baseline migration is frozen.** The repository previously regenerated its single baseline
  freely — `DatabaseProvisioning`'s own remarks still explain the grants are not a migration partly for
  that reason. Production's `__EFMigrationsHistory` now references the current migration id and
  migrations apply unattended on every push, so a regenerated baseline gets a new id, and the next push
  finds nothing applied and tries to re-create every table against a populated database. The human
  checkpoint that would have caught that is gone by design. The invariant is recorded in
  `docs/engineering/migrations.md`, and the `migrations-guard` CI job holds it by failing any
  change to a migration file that is not an addition. That job also carries the one recorded way
  out — a rebaseline window, open while the production database holds no data, which suspends the
  guard and nothing else.
  `Migrations_KeepTheBaselineFrozen` holds that line in the suite: it pins the first migration id
  to the literal `20260804230129_InitialCreate` and deliberately not the count, so additive
  migrations pass and only a regenerated or back-dated baseline fails. The assertion that
  `GetMigrations()` returns ids in apply order is what makes "first" mean "earliest", so a
  back-dated migration lands at index 0 and fails on the id rather than slipping in ahead of the
  baseline. That inversion is the stronger guard: a count of one fails on the additive migration
  that is now the wanted thing and stays green through the regeneration that is the dangerous one,
  which keeps the count at one and changes nothing but the id. The id is hardcoded for the same
  reason — a test that read it off the migrations directory would agree with whatever file is
  present, replacement included — so editing that line by hand is the checkpoint the
  manual deploy step no longer supplies.
- **A coverage failure aborts the deploy after the schema has already changed.** `ProvisionAsync`
  migrates, provisions, and then verifies, so a new budget-owned table added without a policy leaves
  the database migrated, the deploy red, and the new application code undeployed. That is the right
  order of events — the still-running previous code has no queries against a table it does not know
  about — but the recovery is to add the policy to `app-role-grants.sql` and re-run, not to reach for
  the database.
- **Rotating the application role's password has a window.** `azd provision` writes the container's
  connection string before the tool re-passwords the role, so a genuinely new value leaves a minute or
  two in which a cold-start replica presents the new password to a role that still has the old one — a
  transient `28P01`, and reachable rather than theoretical because the API scales to zero. Setting the
  secret to the value the role already has avoids it entirely; a deliberate rotation should expect one
  possible blip and is best done when nobody is using the app.
- **Residual risk: a runner destroyed between creating and deleting the firewall rule leaves a
  single-IP rule behind** until the next deploy overwrites and removes it. Manual deletion is
  documented in `DEPLOYMENT.md`. A scheduled cleanup workflow was considered and judged more machinery
  than the risk warrants — the exposure is one retired runner address against a server whose only
  accounts are password-protected.
- **The deploy path now depends on a third party for one fact.** The runner's public IP comes from
  `api.ipify.org`, with `ifconfig.me` as a fallback; if both are unreachable the step fails loudly
  rather than proceeding without a firewall rule. That is a deliberate dependency on a trivial service
  in exchange for not maintaining an IP allow-list for GitHub-hosted runners.
- **A first bootstrap still runs the tool by hand**, because the pipeline that would run it is wired
  in `DEPLOYMENT.md` Step 5, after the environment exists. The same recipe — firewall rule, two
  environment variables, `dotnet run --project BudgetoidApp/Tools/DbProvision` — is the break-glass
  path when the pipeline is unavailable, which is why it stays documented rather than being deleted as
  superseded.
- **The tool's log is the pipeline's only window into the step.** A run that reported nothing would
  read identically to a run that did nothing, so the pending-migration count, the role being
  provisioned, and the tables verified are all printed, and the tests assert that a supplied log
  delegate is actually called. The message wording is not a contract; the trace existing is.
