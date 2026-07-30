# ADR 0008 — Read the ambient budget inside the policy, not from a role default

- **Status:** Accepted
- **Date:** 2026-07-30
- **Area:** Persistence / Security (PostgreSQL row-level security, configuration parameters, managed
  PostgreSQL, provisioning tests)

## Context

[ADR 0005](0005-isolate-budget-owned-rows-with-row-level-security.md) wanted one bug to have one
name. A session that names no budget is always a defect — the interceptor did not run, or ran on a
path that had no ambient budget and then touched a policed table anyway — and a defect that reports
itself differently depending on whether the physical connection had ever seen `set_config` is a
defect nobody can write a test around. Two SQLSTATEs were measured for the same mistake: `42704`
("unrecognized configuration parameter") on a backend that had never seen the setting, and `22P02`
once the parameter existed but the pool reset had emptied it. The fix chosen there was to make every
session start defined-and-empty, with `ALTER ROLE budgetoid_app SET app.current_budget_id = ''`, so
that the only reachable failure was the `''::uuid` cast inside the policy.

**That goal is untouched and is restated here as still binding.** What does not survive is the
mechanism, and it does not survive for a reason no amount of configuration can reach.

`app.current_budget_id` is a *placeholder* configuration parameter: it lives in a namespace no loaded
extension has registered, which is precisely why an application is allowed to invent it and write it
with `set_config` at runtime. PostgreSQL treats writing one **as a role default** as a different act
from writing one in a session. `ALTER ROLE … SET` validates the option name against the known GUC
table, finds a placeholder, and requires superuser to proceed — the check is in `guc.c`, routine
`validate_option_array_item`, and it refuses with `42501: permission denied to set parameter
"app.current_budget_id"`. The reasoning behind that refusal is sound: the server cannot know what an
unregistered parameter means, so it cannot know whether persisting one is safe, and it defers to the
only principal that is trusted with everything.

Azure Database for PostgreSQL Flexible Server grants no principal superuser. The ceiling is
`azure_pg_admin`, which is the role the Entra administrator of the server receives, and it is the
most privileged identity that can ever run the provisioning script under
[ADR 0007](0007-authenticate-to-postgres-with-managed-identity.md). So the statement fails for the
pipeline's service principal, fails for a human administrator, and would fail for any identity the
project could plausibly create. This is not a privilege that was overlooked and can be granted later;
on this platform it is unobtainable, and a design that needs it is a design that cannot be deployed.

Three facts were reproduced on a stock PostgreSQL container against a non-superuser `CREATEROLE`
role, because "the cloud is stricter" is a story and a measurement is not:

- `ALTER ROLE budgetoid_app SET app.current_budget_id = ''` raises `42501`, from `guc.c`, routine
  `validate_option_array_item`. Vanilla PostgreSQL refuses it too. The Azure ceiling is what makes
  the refusal permanent, not what causes it.
- The **same role, same server**, `ALTER ROLE budgetoid_app SET statement_timeout = '5s'` succeeds.
  The refusal is about placeholder parameters, not about this role's authority over
  `budgetoid_app`. That pairing is what rules out "grant it something and move on".
- A `CREATEROLE` role that did not itself create `budgetoid_app` cannot alter it at all — PostgreSQL
  answers that only roles with `CREATEROLE` **and the `ADMIN` option** on the target may. The
  provisioning script's own `DO` block is what creates the role, and creating it is what confers that
  admin option, so the script's structure is quietly load-bearing: an environment where somebody else
  created `budgetoid_app` by hand is an environment where the script cannot finish.

## What this supersedes

Per this repository's convention an ADR is amended by a later ADR and never edited, so the passage
that no longer describes the system is named here rather than corrected there.

- **[ADR 0005](0005-isolate-budget-owned-rows-with-row-level-security.md) — one Decision paragraph
  superseded, its purpose retained verbatim.** The paragraph beginning "**`ALTER ROLE budgetoid_app
  SET app.current_budget_id = ''` — a session default, and it is load-bearing rather than tidy**" no
  longer describes anything: no such statement is issued and none can be. Everything it argues for
  stands — the two measured SQLSTATEs, the requirement that one bug collapse to one failure, the
  `''::uuid` cast as the single reachable failure point, and
  `RlsIsolationTests.Database_RefusesToReadAnythingWhenTheSessionNamesNoBudget` as the test that can
  only exist because the code is deterministic. Only the sentence naming *where* the default comes
  from is replaced.
- **[ADR 0005](0005-isolate-budget-owned-rows-with-row-level-security.md) — the policy expression in
  the "One permissive `FOR ALL` policy per table" paragraph is widened.** `USING` and `WITH CHECK`
  were `budget_id = current_setting('app.current_budget_id')::uuid`; they are now
  `budget_id = COALESCE(current_setting('app.current_budget_id', true), '')::uuid`. The set of tables,
  the single `FOR ALL` shape, the `WITH CHECK` strengthening and the permissive-versus-restrictive
  warning are all untouched.
- **[ADR 0005](0005-isolate-budget-owned-rows-with-row-level-security.md) — the `NULLIF` rejection is
  *not* superseded, and confusing the two would undo the whole design.** Its rejected alternative,
  "`current_setting('app.current_budget_id', true)` with `NULLIF`, so a missing setting yields NULL",
  remains rejected on exactly its original grounds. The two shapes share the `true` argument and
  nothing else. `NULLIF` yields NULL, `budget_id = NULL` is NULL, and every policed table then reads
  as empty while every write is refused by a policy that simply matched nothing — a session that
  forgot to name a budget becomes indistinguishable from a tenant that owns no data. `COALESCE(…, '')`
  substitutes the empty string and then casts it, and `''::uuid` throws. The design still buys the
  loud failure; it has stopped paying for it with a role attribute.

## Decision

**Every `budget_isolation` policy reads the ambient budget as
`COALESCE(current_setting('app.current_budget_id', true), '')::uuid`, in both `USING` and
`WITH CHECK`.** The `true` argument makes `current_setting` return NULL instead of raising `42704`
when the parameter has never been defined on this backend; `COALESCE` immediately turns that NULL
into the empty string; the cast then fails with `22P02` exactly as it did when a role default
supplied the same empty string. The expression is doing what the role default did, in the one place
it cannot be separated from the thing it protects.

**All three reachable states behave identically to the design ADR 0005 specified**, and all three
were exercised rather than reasoned about: a backend that has never seen the setting raises `22P02`;
a pool-recycled connection where the parameter is defined as `''` raises `22P02`; a session carrying
a real budget id filters rows correctly. The first of those is the state the role default existed to
eliminate, and it is now eliminated by the policy body.

**The invariant moves down, and that is the argument for this shape rather than a consolation for
losing the other one.** A role default is a property of a role: a re-provision that skipped one
statement, an operator who ran `ALTER ROLE … RESET ALL`, or a role recreated by a different path
would remove it, and nothing in the policies would notice — the isolation would keep working and only
the *quality of the failure* would silently regress to the nondeterministic pair. The `COALESCE` form
cannot be separated from the policy, because it **is** the policy: the same `CREATE POLICY` statement
that establishes the isolation establishes how a missing budget is answered, and the two converge or
fail together on every run of `app-role-grants.sql`. That is
[ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md)'s direction applied to the error
contract itself — the rule now sits in the lowest artifact that can hold it declaratively, one level
below the role attribute it left.

**Nothing else about the mechanism changes.** `BudgetSessionInterceptor` still issues
`select set_config('app.current_budget_id', @budget, false)` on connection-opened, still writes `''`
explicitly on the unresolved path rather than skipping the statement, and both connection-string bans
(`No Reset On Close=true`, `Multiplexing=true`) stand for the reasons ADR 0005 gives. The policy is
now tolerant of a parameter that was never defined; it is not tolerant of a session that failed to
name a budget, and the interceptor is still what keeps the second case from happening.

## Testing consequence

**Provisioning had only ever been exercised over a superuser connection, which made every privilege
check in the script vacuous.** A superuser passes every check by definition, so the suite could stay
green over a script containing a statement that no production identity is permitted to execute — and
did. The class of bug is what matters here: a test environment that silently grants more than
production does not produce weak tests, it produces tests that cannot fail, and the failure surfaces
in the deploy pipeline against a real server, after the migration has already applied.

**There is now an integration test that provisions as a non-superuser `CREATEROLE` role**, which is
the closest analogue stock PostgreSQL offers to `azure_pg_admin`: enough authority to create roles
and apply grants and policies, no authority over anything reserved to superuser. It carries two
controls, and both are required:

- The removed `ALTER ROLE … SET app.current_budget_id` statement, re-sent by that same role, is
  asserted to **fail** with `42501`. Without it, the test would pass on any environment — including a
  superuser one — and prove nothing about the constraint it exists to pin.
- `ALTER ROLE budgetoid_app SET statement_timeout = '5s'`, sent by the same role in the same test, is
  asserted to **succeed**. Without it, a role that had lost all authority over `budgetoid_app` — or a
  target role it never created — would satisfy the first control for entirely the wrong reason, and
  the test would report "placeholder parameters are refused" when the truth was "this role can do
  nothing here at all".

Neither control tests the product. Together they test the test, which is the only way a negative
assertion about privilege is worth anything.

## Alternatives considered

- **Grant the provisioning identity superuser, or find an Azure privilege that permits it.** Rejected
  because it is not available: Azure Database for PostgreSQL Flexible Server exposes no superuser to
  any principal, and `azure_pg_admin` is the ceiling for the Entra administrator that ADR 0007's
  pipeline already runs as. Even on a self-hosted server this would invert ADR 0004's entire premise —
  provisioning as superuser to make a least-privilege role behave — and would carry the vacuous-test
  problem above into every future privilege check.
- **`NULLIF` instead of `COALESCE`, so a missing setting yields NULL.** Rejected, still, on ADR 0005's
  grounds, and listed again because this record makes the two look adjacent. Both use
  `current_setting(…, true)`; only one of them still fails loudly. NULL compares to nothing, so a
  session that named no budget would read as an empty tenant and write as a refused-by-nothing policy
  — fail-closed but mute, which is the bug class the whole design exists to make audible.
- **A `SET` on every connection issued from the interceptor before the first policed query, in place
  of the default.** Rejected as a restatement of what already happens: the interceptor *does* write
  the parameter on connection-opened, including `''` on unresolved paths. The `42704` case was never
  about the interceptor's ordinary path; it was about a policed query reaching a backend the
  interceptor never touched, and no amount of additional interceptor work can cover a connection it
  did not open.
- **`ALTER DATABASE budgetoid SET app.current_budget_id = ''` instead of `ALTER ROLE`.** Rejected: it
  is the same `validate_option_array_item` check with the same superuser requirement for a
  placeholder, so it fails identically while looking like it might not. Recorded because it is the
  first thing a reader will reach for.
- **Register the parameter properly, so it is no longer a placeholder — a custom variable class via
  an extension or `postgresql.conf`.** Rejected: it needs a server restart and file-level access on a
  managed server that offers neither, and it would make the tenancy design depend on server
  configuration that no artifact in this repository can converge. The policy expression converges on
  every provisioning run by construction.
- **Accept the two SQLSTATEs and assert either one in the test.** Rejected, and it is the honest
  cheap option. It costs one `or` in a test and buys back the ambiguity ADR 0005 removed on purpose:
  the SQLSTATE would then depend on the physical connection's history rather than on the bug, so a
  future failure in production could not be matched to a known cause by its code alone. The
  `COALESCE` costs nothing and keeps the guarantee.

## Consequences

- **`app-role-grants.sql` is now executable end to end by every identity that will ever run it**, and
  that property is asserted rather than assumed. It is also the property most likely to be broken by a
  future addition — any statement reserved to superuser is a deploy-time failure that a
  superuser-connected test suite would never see. The non-superuser provisioning test is the guard,
  and it belongs in the same conversation as any new statement added to that script.
- **The `22P02` contract is unchanged and remains the documented behaviour.** Business-logic docs, the
  CLAUDE.md architecture note, and `RlsIsolationTests` all describe the same failure they described
  before; nothing downstream of the policies needed to change, which is the evidence that the seam was
  in the right place.
- **The evaluation caveat from ADR 0005 survives, with one more reason to remember it.** A policy qual
  is only evaluated when there are candidate rows, so a session naming no budget that queries an
  **empty** policed table still gets an empty result rather than an error. The loud failure now lives
  entirely inside the qual, so this is the *only* place it does not reach, and there is no longer a
  role-level artifact that could be mistaken for covering the gap.
- **A re-provision can no longer regress the error contract.** There is no role attribute to reapply,
  so the failure mode "isolation is intact, diagnostics silently degraded" has no mechanism left. That
  mode was never going to be caught by a test, because the isolation it leaves behind is correct.
- **One class of environment is now known-hostile: a `budgetoid_app` created outside the provisioning
  script.** The script's `DO` block confers the `ADMIN` option on whoever runs it, and a role created
  by another identity leaves the script unable to alter it at all. Nothing in the current deploy path
  creates the role any other way; a manual `CREATE ROLE` during an incident would.
