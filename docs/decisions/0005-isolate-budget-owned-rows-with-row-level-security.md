# ADR 0005 — Isolate budget-owned rows with Row-Level Security

- **Status:** Accepted
- **Date:** 2026-07-29
- **Area:** Persistence / Security (PostgreSQL row-level security, tenancy, rule enforcement)

## Context

[ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md) settled that every rule belongs to the
lowest layer that can enforce it declaratively, and in the same breath recorded the one place the
principle was not applied. Composite foreign keys had pushed the *write* side of the budget boundary
into the schema; the *read* side stayed in the `BudgetIsolation` global query filters in
`BudgetoidApp/Infrastructure/Persistence/BudgetoidDbContext.cs`, which are application code and
therefore hold exactly as long as the application remembers them. Raw SQL, `IgnoreQueryFilters`, an
`ExecuteUpdate`, a repository written in a hurry and a maintenance script all walk straight past.
ADR 0002 named Row-Level Security as the genuinely lower option for reads and deferred it on three
stated grounds: it "needs `SET LOCAL` per request under connection pooling, complicates the Aspire
wiring, and is materially harder to test".

Two of those three were real. One was not, and which one matters, because the deferral rested on it.

**`SET LOCAL` was the wrong mechanism, so the obstacle it named never existed.** Measured against
PostgreSQL 17: most EF operations in this codebase run in autocommit with no explicit transaction,
and `SET LOCAL` outside a transaction block sets nothing at all — it emits
`WARNING: SET LOCAL can only be used in transaction blocks` and reports success. The deferral was
written around a mechanism that would not have worked, so the pooling difficulty it anticipated was a
difficulty of a design nobody could have shipped. The session-scoped `set_config` adopted here has a
lifetime problem of its own, and it is answered by *where* the statement is issued rather than by
anything about the pool.

**The Aspire wiring cost one argument.** `AddDbContext` switches to the `(serviceProvider, options)`
overload, whose `optionsLifetime` defaults to `Scoped` and is therefore what lets a scoped
interceptor resolve from the request scope; the options gain one `AddInterceptors` call, and
`AddInfrastructure` one scoped registration. `EnrichNpgsqlDbContext`, called afterwards, re-applies
Aspire's retry, health and telemetry defaults over the top without disturbing the interceptor.

**"Materially harder to test" was true, and the price is paid in the shape of the tests rather than
in their number.** PostgreSQL skips row-level security for a superuser, so every probe has to run on
the application role's own connection — a test on the container account passes against no policies
at all — while every read-back of the form "the other budget's row is untouched" has to run on the
admin connection, because that is a question no policed connection can answer. Every negative has to
be paired, in the same test, with the identical statement aimed at the session's own budget, or a
policy of `USING (false)` would satisfy it. `RlsIsolationTests` and `RlsCoverageTests` are that shape.

What [ADR 0004](0004-connect-as-a-least-privilege-role.md) got right is the prerequisite this record
depends on: the application serves every request as `budgetoid_app`, which is neither a superuser nor
the owner of any table, and PostgreSQL skips policies for both. The role a policy can bind to already
existed.

**This record takes up ADR 0002's deferral and supersedes two sentences of ADR 0004.** Per this
repository's convention an ADR is amended by a later ADR and never edited, so the sentences that no
longer describe the system are named here rather than corrected there. ADR 0002's "Adopt Row-Level
Security now" alternative said "deferred, not discarded"; this is the record that takes it. ADR
0004's "Row-Level Security instead" alternative says
read-side isolation "remains the application-layer `BudgetIsolation` query filters" and that "reads
must still not be assumed protected at the bottom". Both are now false. The rest of that entry stands
untouched, including its main point: RLS is not a substitute for the grant matrix, because a policy
decides *which rows* a connection may reach and a column-list grant decides *which columns* may ever
change, and neither answers the other's question.

## Decision

**Exactly five tables carry a `budget_isolation` policy — `accounts`, `category_groups`,
`categories`, `payees`, `transactions` — mirroring the `BudgetIsolation` query-filter scope**,
because the same sentence is being enforced twice at two depths and a difference between the two
lists would be a rule with two meanings. `budgets`, `users` and `currencies` are deliberately out. A
budget is the tenant rather than a tenant's row, so there is nothing for a policy to compare;
`currencies` belongs to no tenant at all; and provisioning reads `users` and `budgets` **before** an
ambient budget exists — `EnsureUserHandler` resolves the principal and then find-or-creates the
budget — so a policy on either would refuse the very query that decides which budget is ambient, and
break sign-in for everyone.

**The policies live in `app-role-grants.sql` itself, not in a migration and not in a sibling
script.** They are written `TO budgetoid_app`, so they are part of that role's privilege story rather
than part of the schema, and ADR 0004's reasons for keeping grants out of migrations apply unchanged:
the repository keeps a single regenerated baseline and `dotnet ef migrations add` discards hand-added
`migrationBuilder.Sql(...)`. One file rather than two is a separate point and a deliberate one — a
deploy that applied the grants and forgot the policies would hand the role every tenant's rows, and
there is no such deploy if there is no second file to forget. Same applier
(`DatabaseProvisioning.ApplyGrantsAsync`), same deploy step, same convergence contract:
`DROP POLICY IF EXISTS` precedes each `CREATE POLICY`, because `CREATE POLICY` has no `OR REPLACE`
and a changed policy body would otherwise never take effect on a re-run.

**One permissive `FOR ALL` policy per table, with `USING` and `WITH CHECK` both
`budget_id = current_setting('app.current_budget_id')::uuid`.** `FOR ALL` rather than four
verb-specific policies: the rule is one sentence about which rows belong to this session, and
splitting it into four would create four places for it to drift.

**`WITH CHECK` is a strengthening, not parity with the query filter.** The filters are read-side
only, so an `INSERT` landed in whatever `budget_id` it named; the composite foreign keys proved that
the new row and everything it referenced agreed on *a* budget, never that the budget was the
requesting tenant's. With `WITH CHECK`, an insert can only land in the ambient budget. That is a
guarantee no layer held before — the grant matrix answers what a row may become after it
exists, and this answers where it may come into existence.

**The ambient budget travels as a session-level setting written when the connection opens.**
`BudgetSessionInterceptor` (`BudgetoidApp/Infrastructure/Persistence/BudgetSessionInterceptor.cs`)
issues `select set_config('app.current_budget_id', @budget, false)` — session scope, deliberately
not `SET LOCAL`, for the reason measured in the Context above. The value is bound as text because
`set_config` takes text: bind the `Guid` and Npgsql infers `uuid`, which no `set_config` overload
accepts.

**A `DbConnectionInterceptor` on connection-opened is the only correct placement, not merely an
adequate one.** EF Relational opens and closes the connection per operation and nothing in this
codebase pins it open, so a value written once in middleware evaporates the moment the connection
returns to the pool. A `set_config` issued inside a transaction is undone by a rollback, which is
precisely the path that must not lose it. And a retry re-opens, so connection-opened is also what
survives `NpgsqlRetryingExecutionStrategy`. Both the sync and the async overload are implemented:
some EF paths still open connections synchronously, and an unoverridden one would hand a policed
query a session naming no budget.

**`ALTER ROLE budgetoid_app SET app.current_budget_id = ''` — a session default, and it is
load-bearing rather than tidy.** Without it, one bug — a session that names no budget — raises a
different SQLSTATE according to the connection's history. Both measured: `42704` ("unrecognized
configuration parameter") on a backend that has never seen the setting, and `22P02` once
`set_config` has run and Npgsql's pool reset has left the parameter defined but empty. Making every
session start defined-and-empty collapses that to one failure, the `''::uuid` cast inside the
policy, which is a thing a test can pin —
`RlsIsolationTests.Database_RefusesToReadAnythingWhenTheSessionNamesNoBudget` does exactly that, and
could not have been written against a nondeterministic code.

**`IBudgetContext` gains `ResolvedBudgetId`, and the strict accessor is derived from it.** The
interceptor runs on every connection the context opens, including on paths that legitimately have no
ambient budget: user provisioning queries `users` and `budgets` before one exists, and infrastructure
scopes such as health checks never resolve a principal at all. Neither touches a policed table.
`ResolvedBudgetId` is the nullable accessor for exactly those callers; `BudgetId` stays the strict
one the query filters read, because a filter comparing against a null budget would match nothing or
— worse, on the write side — scope nothing. `BudgetId` is a default interface member computed as
`ResolvedBudgetId` with null rejected, so no implementation can make the two name different budgets:
the session setting reads one accessor and the filters read the other, and two separately written
members disagreeing would fail nothing. Catching `BudgetId`'s throw inside the interceptor was
rejected — exception-as-control-flow for a state that is not exceptional.

**No table gets `FORCE ROW LEVEL SECURITY`, and that is a decision rather than an oversight.** Owner
and superuser bypass is load-bearing here: the schema is created and migrated on the admin
connection, test seeding writes both tenants through it, and every read-back asserting "the other
budget's row is untouched" is a question no policed connection could answer. `FORCE` is the obvious
hardening a reader reaches for, which is why its absence is recorded — it would break all of that
and protect nothing, because the role these policies exist to constrain is not the owner.

**The policies are permissive, and the trap that follows is worth stating before someone falls into
it.** Permissive policies are OR-ed, so a second permissive policy on one of these tables can only
ever *widen* what `budget_isolation` allows. Anything meant to narrow isolation has to be written
`AS RESTRICTIVE`, or it will quietly do the opposite of what it says.
`RlsCoverageTests.Database_GivesEveryBudgetOwnedTableExactlyOneIsolationPolicy` asserts exactly one
policy per budget-owned table rather than at least one, for that reason.

**Two Npgsql connection-string options are now forbidden**, and they are recorded at
`BuildConnectionString` in `BudgetoidApp/Api/Program.cs`, the one place a connection string is
rebuilt. `No Reset On Close=true` would keep a returned connection's `app.current_budget_id`, which
turns the pool reset from hygiene into a security control that is no longer running — it is what
clears one tenant's budget before the next borrower. `Multiplexing=true` interleaves logical
sessions over one physical connection, which no session-setting design survives at all. Be exact
about the strength of the first, because overstating it would be the same mistake as trusting the
reset: the interceptor writes `''` explicitly on the unresolved path rather than skipping the
statement, so a borrowed connection is overwritten on open whatever the pool does. The ban is
defence in depth, not the thing correctness rests on.

## Alternatives considered

- **Leave read isolation in the query filters and treat the deferral as permanent.** Rejected — this
  is the status quo restated, and it is the case ADR 0002 rules out in general terms: a rule that
  lives in application code holds only for the code paths that remember to ask. The gap was not
  hypothetical. Every budget-owned table was fully readable by the application role across every
  tenant, and one `FromSql`, one `IgnoreQueryFilters`, or one repository that queried `Set<T>()` past
  the filter would have read another person's money with nothing to notice it by.
- **`current_setting('app.current_budget_id', true)` with `NULLIF`, so a missing setting yields NULL
  instead of an error. Rejected, and it is the alternative most worth recording**, because it looks
  like defensive engineering and is the exact opposite. The `true` argument makes `current_setting`
  return NULL rather than raise, and `budget_id = NULL` is NULL, so every policed table reads as
  empty and every write is refused by a policy that simply matched nothing. A session that forgot to
  name a budget would then be indistinguishable from a tenant that owns no data — which is precisely
  the bug class these policies exist to catch, made invisible. The strict form fails with `22P02` and
  names itself.
- **A PL/pgSQL helper that reads the setting and raises a legible error.** Rejected on ADR 0002's
  Boundary A: no procedural logic is pushed into the database merely to satisfy "lowest layer". The
  error text is the only thing it buys, and it would be bought with a second language in the schema,
  invisible to the type system and versioned only by whatever applies it. A `22P02` naming the cast
  is already unambiguous to the one audience that ever sees it, which is whoever is debugging a
  connection that named no budget.
- **Policies in an EF migration.** Rejected: it splits one privilege story across two mechanisms and
  two deploy steps, so a database could hold the grants without the policies or the reverse.
  Hand-added `migrationBuilder.Sql(...)` is discarded on every baseline regeneration besides, which
  is the same reason grants are not migrations.
- **`FORCE ROW LEVEL SECURITY`.** Rejected for the reasons in the Decision, and listed separately
  because a reader who knows the feature will otherwise read its absence as an omission rather than
  as an answer.
- **Rely on Npgsql's pool reset to clear the setting, instead of writing `''` on the unresolved
  path.** Rejected: it makes tenancy correctness a property of a connection-string option any future
  edit can flip, and the failure would be silent — a health-check or provisioning connection borrowed
  straight after a request would still be carrying that request's budget. One extra round-trip on
  two non-tenant paths buys independence from how the pool is configured.

## Consequences

- **The grants and the policies fail in opposite directions, and this is the single most important
  thing for a future contributor to carry away.** A table nobody grants is invisible to the role,
  and the first feature to touch it fails loudly with `42501` — fail-closed. A granted table nobody
  writes a policy for is fully readable and writable by the role across every tenant, silently —
  fail-**open**, and indistinguishable from working. A new budget-owned table therefore needs a
  grant *and* a policy. `RlsCoverageTests` derives its subject from the live schema — every ordinary
  table in `public` carrying a `budget_id` column — so a missing policy fails a test instead of
  shipping. Hardcoding the five known names there would defeat the point: the test would keep
  passing on the one day it matters, the day someone adds the sixth table.
- **The EF query filters stay, and neither layer is redundant cover for the other.** The filters
  give a correct empty result and the good error — a 404 for a by-id target, a 400 for a bad
  reference — where the policies give a refusal or an absence; the policies make the rule true
  regardless of application code. This is ADR 0002's "upper layers may restate a rule for error
  quality, never for enforcement" applied to tenancy, and the failure mode to guard against is a
  future reader deleting either half as duplication.
- **The deploy flow gains nothing new.** `DEPLOYMENT.md`'s provisioning step already re-runs
  `app-role-grants.sql` on the admin connection after the migration bundle, and the policies are in
  that file. A changed policy takes effect when the script is applied, exactly as a changed grant
  does.
- **What this does not decide is *which* budget is ambient.** That is still resolved
  application-side at provisioning, from the Google subject, onto `CurrentUser.BudgetId`. The
  policies enforce consistency with that choice; they say nothing about its correctness.
  Ambient-budget resolution remains the whole protection for whose data a request reaches, and a bug
  that resolved the wrong budget would produce a session RLS confines faithfully to the wrong tenant.
- **`budgets` is uncovered, so every query over it must still scope by owner explicitly.** The
  warning in [budgets.md](../business-logic/budgets.md#edge-cases--known-gotchas) survives unchanged,
  and it is now the sharpest gap in the picture: `db.Budgets` is the one budget-related set with
  neither a query filter nor a policy behind it.
- **Superuser and owner paths bypass the policies by design**: migrations, test seeding, and an
  operator at a `psql` prompt all see every tenant. That is what makes the schema deployable and the
  isolation testable, and it is the reason a script run by hand is not covered by any of this.
- **A policy qual is only evaluated when there are candidate rows.** A session naming no budget that
  queries an **empty** policed table gets an empty result rather than an error, because nothing
  reached the `''::uuid` cast. The loud failure is real and worth relying on, but it does not reach
  that case, and `RlsIsolationTests` says so beside the test that would otherwise be read as a
  guarantee it is not.
- **Two wiring facts were verified rather than assumed, and neither is visible at wiring time.**
  Aspire's `EnrichNpgsqlDbContext` does **not** drop interceptors registered in the `AddDbContext`
  options; and EF does **not** discover an `IInterceptor` registered in the application service
  collection, so `AddInterceptors` on the options is required rather than merely convenient. Either
  being otherwise leaves the interceptor unattached, which compiles, boots, passes startup, and
  announces itself only as `22P02` on the first policed query in every request the API serves.
