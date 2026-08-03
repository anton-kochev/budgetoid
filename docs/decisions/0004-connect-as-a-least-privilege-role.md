# ADR 0004 — Connect to PostgreSQL as a least-privilege role

- **Status:** Accepted
- **Date:** 2026-07-29
- **Area:** Persistence / Deployment (PostgreSQL roles and grants, rule enforcement)

## Context

[ADR 0001](0001-postgres-password-authentication.md) settled how the application *authenticates* to
Azure Database for PostgreSQL, and settled it as the **server administrator**: Aspire built the
connection string from the admin login, and migrations ran on the same credentials. Nothing about
that authentication choice is reopened here — password auth stays, and so does the hardening path
back to passwordless. What ADR 0001 left standing is a question it never asked: the process serving
an HTTP request held every privilege the database has, so a code path that got a raw `UPDATE` past
the domain could rewrite anything, and one that got a `CREATE`/`DROP` past it could reshape the
schema.

That mattered because three families of rules were held **only by the shape of the C# domain**:

- **X1** — a budget-owned row never changes `budget_id`. No method on `Account`, `CategoryGroup`,
  `Category`, `Payee` or `Transaction` reaches `BudgetId`.
- **B2** — a `budgets` row is never updated at all. `Budget` exposes no mutator of any kind.
- **A1** — `accounts.currency_code` never changes after creation. `UpdateAccountCommand` does not
  carry it, and `Account` has no path that sets it.

`users.google_subject` — the key the whole sign-in resolves through — was in the same position.

Under [ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md) a rule is owned by the lowest
layer that can enforce it **declaratively**, and "no method exists" is the highest layer there is: it
holds for the paths that go through the domain and for no others. The schema carried part of X1 and
nothing else. Composite foreign keys default to `ON UPDATE NO ACTION`, so moving a parent out from
under a child is refused with `23503` on any connection — which covers a transaction and a category
completely, since each always references a parent, and covers an account, a category group and a
payee only while something references *them*. An empty account, an empty category group and an
unreferenced payee had no child to object, and every payee is unreferenced between being created and
being used. B2, A1 and `google_subject` had no bottom layer at all.

ADR 0002 also named where the missing layer would have to live: roles and grants cannot go in the
single regenerated baseline migration, because `dotnet ef migrations add` discards hand-added
`migrationBuilder.Sql(...)`; they belong to **provisioning**. It closed that paragraph with "this is
the first thing whoever introduces a least-privilege application role will hit."

## Decision

**The application serves every request as `budgetoid_app`, a role holding exactly the privileges the
domain's write surface needs, and PostgreSQL refuses the rest.** The grant matrix is
`BudgetoidApp/Infrastructure/Persistence/Provisioning/app-role-grants.sql`, applied by
`DatabaseProvisioning.ApplyGrantsAsync`
(`BudgetoidApp/Infrastructure/Persistence/Provisioning/DatabaseProvisioning.cs`) over an admin
connection. That file is the single source of truth for what the application may write.

**Two connection strings, and the elevated one is unreachable from the request path.**
`ConnectionStrings:budgetoid` is the least-privilege role and is what `AddDbContext` binds
`BudgetoidDbContext` to, so it serves every request. `ConnectionStrings:budgetoid-admin` is the
elevated identity and is read in exactly one place: the `IsDevelopment()` block in
`BudgetoidApp/Api/Program.cs:118`, which migrates and *then* applies the grants. The order is
load-bearing rather than tidy — the grants name individual tables, so the schema has to exist before
they can be applied. In the deployed application that key has no value to find, because migration
and provisioning are operator steps at deploy time (`DEPLOYMENT.md`, Steps 3 and 4).

**The role's password is read out of the application connection string, not from a key of its own.**
Provisioning assigns the role whatever password the application is already configured to connect
with, so the two cannot disagree; a third setting would be a third thing to keep in sync and a fourth
way to be locked out. The password is spliced into the script as a single-quoted SQL literal, which
is safe only because `DatabaseProvisioning` first rejects anything outside a conservative ASCII
alphabet — no quotes, backslashes or dollar signs. Provisioning owns the password, so restricting its
alphabet is legitimate, and it is both simpler and stronger than escaping. `CREATE ROLE` cannot take
a parameter placeholder, which is why there is a token to substitute at all.

**Immutability is expressed by omission from an explicit column list, because PostgreSQL column
privileges are additive.** This is the single most important thing about the file.
`REVOKE UPDATE (col) ON t` cannot subtract a column from a table-wide `GRANT UPDATE ON t` — it only
removes a column-level privilege that was granted column-level. So every `UPDATE` grant names its
columns, and an immutable column is one that is simply not on the list: `budget_id` appears on none
of the five owned tables, `currency_code` is absent from `accounts`, `created_at_utc` is absent
everywhere, and `budgets` and `credentials` have no `UPDATE` grant of any shape — the whole content
of B2, and of the rule that a credential's **identity** (`user_id`, `type`, `provider`, `subject`,
`created_at_utc`) is written once and never edited. On `credentials` the empty grant is the present
state of that list rather than a property of the table: a passkey signature counter and a last-used
timestamp are both specified, and each joins the list while the identity columns stay off it.
**"Simplifying" any column-list grant into a table-wide one silently re-opens every hole the list
exists to close**, and nothing fails at the time it is done.

**The script is idempotent and convergent, and is deliberately not an EF migration.** Each table's
block is `REVOKE ALL` followed by the grants it should have, so a re-run converges the role to
exactly what is written — deleting a line removes the privilege on the next run rather than leaving
it behind on a database that already has it. It must never become a migration: the repository keeps a
single regenerated baseline, hand-added SQL in it is lost on every regeneration, and grants target a
**role** rather than the schema, so they belong to a step that re-runs rather than to a history that
applies once.

**Migrations always run as admin, and that was measured rather than assumed.** `SELECT` on
`__EFMigrationsHistory` is not enough to run `MigrateAsync` even against an already-migrated
database: the no-op path still issues `CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory"`, which is
refused with `42501` because PostgreSQL checks `CREATE` on the schema whether or not the table
exists, and it then takes its migration lock as `LOCK TABLE … IN ACCESS EXCLUSIVE MODE`, which needs
`UPDATE`, `DELETE` or `TRUNCATE` on that table. Granting either — `CREATE` on the schema, or a write
privilege on the history table — would re-open precisely the holes the role exists to close, so the
role keeps `SELECT` alone and migration stays an admin step everywhere, including the Development
startup path.

**Grants name every table explicitly, so the role is fail-closed for anything nobody granted.** A new
table is invisible to the application until a line is added here, and the feature that needs it fails
with `42501` on its first write. That is the intended failure: the right response is the narrowest
grant that unblocks it, never `GRANT ALL`.

**A refused write surfaces as `42501` and a 500, and is deliberately not translated into a validation
error.** Every column the role cannot write is one no domain method reaches, so a `42501` arriving at
a user means the domain was bypassed — by raw SQL, an `ExecuteUpdate`, or a write path built without
the entity. A 400 would be a lie about whose mistake it was; a 500 naming the real failure is the
honest answer, and `GlobalExceptionHandler` already produces it.

**The tests run under the role, because a test on the admin connection measures nothing.** PostgreSQL
skips every privilege check for a superuser, and the test containers' account is one — so a
permissions assertion sent on `RepositoryTestHost.ConnectionString` passes no matter what the grants
say, including against an empty grants file. Both test hosts therefore expose a separate
`AppConnectionString`, and `AppRoleGrantsTests` and the three grant-backed cases in
`TenancySchemaTests` send their statements over it. Each refusal is paired in the same test with a
write that must succeed on the same connection, so the assertion says *this column is immutable*
rather than *the role cannot write*. Beyond that, `PostgresTestHost` hands the API test factory the
role's connection string for the application and the admin one only for startup, so the whole API
suite exercises real requests under the role — which is what turns "the grants are correct" into "the
grants are correct **and** sufficient". An over-tight grant now fails a feature test instead of
shipping.

**The deployed container is handed exactly one connection string, and `api.WithReference(db)` is
deliberately absent from the publish branch.** For this resource a reference injects the *admin*
identity into the container: not only `ConnectionStrings__budgetoid`, which the explicit override
would win over, but also `BUDGETOID_URI`, `BUDGETOID_USERNAME` and `BUDGETOID_PASSWORD`, all built
from the administrator login — the server admin password in a request-serving container, under a
second set of names, with no consumer. This was read out of the generated Aspire manifest before and
after rather than reasoned about. Dropping the reference is what actually keeps the admin credential
out; the connection string the application reads is built explicitly at
`BudgetoidApp/AppHost/Program.cs:93` from the server host output and the `postgres-app-password`
parameter, and it needs no reference to exist. Nothing was lost by removing it: the `budgetoid`
database and the Key Vault secret the operator migrates with are both emitted by `AddDatabase` into
the server's Bicep module. **Record kept here because the missing reference looks like an omission,
and someone will otherwise "fix" it back in.**

## Alternatives considered

- **Keep connecting as the administrator and rely on the domain to hold X1, B2, A1 and
  `google_subject`.** Rejected — this is the status quo restated, and it is the case ADR 0002 rules
  out in general terms: a rule that lives in the absence of a method holds only for callers that go
  through that method, and every future write path that does not — an importer, a bulk endpoint, a
  maintenance script, an `ExecuteUpdate` — loses it silently, with no failure signal to notice it by.
  The three characterization gaps were not hypothetical either: an empty account, an empty category
  group and an unreferenced payee could each be moved into another budget by one raw `UPDATE` that
  every constraint in the schema would accept.
- **A table-wide `GRANT UPDATE ON t` with `REVOKE UPDATE (col)` for the immutable columns. Rejected
  because it does not work at all**, and it is the alternative most worth recording, because it is
  what a reader assumes was available and it fails silently. PostgreSQL column privileges are
  additive: a table-level `UPDATE` privilege already covers every column, and revoking one column
  removes a *column-level* grant that was never issued, so the statement succeeds, changes nothing,
  and leaves the column writable. The role would look constrained in the script and be unconstrained
  in the database. The explicit column list is not a stylistic preference over `REVOKE`; it is the
  only construct that expresses the rule.
- **Row-Level Security instead.** Rejected as a substitute, and this decision deliberately does not
  settle it either way. RLS answers a different question: it is about *which rows* a connection may
  see and touch, while the grant matrix is about *which columns* may ever change. The role holds
  plain `SELECT` on every budget-owned table, so it can read any budget's rows, and read-side
  isolation remains the application-layer `BudgetIsolation` query filters exactly as
  [ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md) recorded when it deferred RLS over
  `SET LOCAL` under connection pooling. That deferral stands untouched, and reads must still not be
  assumed protected at the bottom. What this decision does change for a future RLS adoption is the
  prerequisite: the application no longer connects as a superuser or as the table owner, both of
  which bypass policies, so the role RLS would need already exists.
- **Enforce the same rules with triggers.** Rejected on ADR 0002's Boundary A: no procedural logic is
  pushed into the database merely to satisfy "lowest layer". A `BEFORE UPDATE` trigger raising on a
  changed `budget_id` would be business logic invisible to the type system, untested by the unit
  suite and versioned only by migrations — and it would be strictly more machinery than a column list
  that states the same rule declaratively and costs nothing at runtime.

## Consequences

- **A new table is unreachable until someone grants it.** The first write fails with `42501` and a
  500. This is the designed failure mode, and the fix is a narrow grant in the script — never
  `GRANT ALL`, and never a table-wide `UPDATE` on a table that has an immutable column.
- **A new mutable column must be added to its table's `UPDATE` list, or writing it fails the same
  way.** The API suite runs under the role, so this is caught by a feature test rather than in
  production — but it is a second file every schema change has to be checked against, and that cost
  is permanent.
- **Migrations and provisioning are admin-only steps, in that order.** Locally the Development
  startup block does both on every boot, which also re-passwords the role on a persistent data
  volume. In production they are two operator steps at deploy time, and provisioning must be re-run
  on every deploy: a changed grant matrix takes effect only when the script is applied.
- **Two database identities now exist and are independently rotatable**, which is the point of the
  split. The administrator's credential lives in Key Vault and is used by an operator; the role's
  password is an azd parameter that becomes the container's connection string. Getting them confused
  produces a deployment that cannot reach its database, which is why `DEPLOYMENT.md` states which is
  which at each step.
- **The application role's password is constrained to a conservative ASCII alphabet**, because it is
  spliced into SQL as a literal. That is enforced in `DatabaseProvisioning` on the code path and by
  the operator's own care on the deploy path.
- **`ADR 0001`'s record that the application connects with admin credentials no longer describes the
  application.** Per this repository's convention an ADR is amended by a later ADR and never edited:
  ADR 0001 continues to own the authentication decision, and this record owns the authorization one.
