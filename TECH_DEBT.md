# Tech Debt & Hardening Backlog

Tracked improvements and the invariants future code must preserve. Add entries as they're
discovered; remove them as they're done.

---

## Data isolation invariant (read this before touching transaction queries)

**A transaction must never be visible to a user it doesn't belong to.** This is enforced in
layers; the EF global query filter is the primary control, but it has known gaps. Keep all of
the following true.

Enforced today:
- **Read-side filter.** `BudgetoidDbContext` defines a global query filter
  (`HasQueryFilter("UserIsolation", t => t.UserId == _userId)`) scoped to the current
  `IUserContext.UserId`. Every LINQ query against `Transactions` is auto-scoped — you cannot
  forget it. The user id is captured into a constructor field (not referenced as
  `userContext.UserId` inside the lambda) so the current user is never baked into EF's cached
  model. The context is registered **non-pooled** (`AddDbContext` + `EnrichNpgsqlDbContext` in
  `Api/Program.cs`) because pooling forbids scoped constructor injection. This global filter is
  the **sole** read-side guard — repositories deliberately do *not* re-filter by user
  (`ITransactionRepository.GetAllAsync`), so the escape hatches below are especially load-bearing.
- **Immutable ownership.** `Transaction.UserId` has no public setter and is set only via the
  factory. The query filter is read-side only — `SaveChanges` ignores it — so immutability is
  what stops a row from ever moving between users. Guarded by a unit test.
- **Server-assigned ownership.** `UserId` comes only from `IUserContext`, never from a request
  DTO or route. `CreateTransactionCommand` has no `UserId` field; keep it that way.

Escape hatches the filter does **not** cover — do not introduce these on user-scoped data:
- `IgnoreQueryFilters()` — never on `BudgetoidDbContext`.
- Raw SQL (`FromSqlRaw` / `FromSqlInterpolated` / `ExecuteSql...`) — bypasses the filter; if
  unavoidable, scope by user explicitly in the SQL.
- `Find` / `FindAsync` — by-key loads bypass the filter and can even return an already-tracked
  other-user entity with no SQL. Use a filtered LINQ lookup
  (`FirstOrDefaultAsync(t => t.Id == id)`), which returns null for non-owners.
- `Remove(new Transaction { Id = x })` and other by-id mutations on stub entities — delete by
  PK with no owner check. Load through the filtered set first, then mutate.
- `ExecuteUpdate` / `ExecuteDelete` — honor the filter only when started from
  `dbContext.Transactions` without `IgnoreQueryFilters`.
- Future user-scoped related/owned entities must each carry their **own** query filter.
  (Required navigations can otherwise silently *under*-return via INNER JOIN — a correctness
  bug, not a leak, but worth knowing.)

Tests that lock this: `tests/IntegrationTests/TransactionIsolationTests.cs` (DbContext-level
two-user-same-process + endpoint-level two-factory) and the `UserId` immutability unit test in
`tests/UnitTests/TransactionTests.cs`. Removing the `HasQueryFilter` line must make the
DbContext-level test fail.

**Prerequisite, not yet done — real authentication.** `FakeUserContext` returns a constant
user id for every request, so isolation is only meaningful once Google OIDC populates a
verified `IUserContext.UserId`. Wire real auth before any multi-user deployment.

---

## Backlog

### Layer 4 — Postgres Row-Level Security (database-level backstop)
**Why:** the query filter and conventions above all live in application code. RLS is the only
control that holds even when app code is buggy, uses raw SQL, or forgets a filter — the
database itself refuses to return other users' rows. Add before hosting real users' financial
data.

**Sketch:**
- In a migration: `ALTER TABLE transactions ENABLE ROW LEVEL SECURITY;` plus a policy keyed to
  a per-connection GUC, e.g. `USING (user_id = current_setting('app.current_user_id')::uuid)`.
- Per request, set the GUC on the connection inside the request's transaction via a
  `DbConnection`-opened EF interceptor: `SET LOCAL app.current_user_id = '<user>'`.
- **Tradeoffs / risks:** the interceptor must run on *every* connection open (connection
  pooling reuses physical connections); the migration owns the policy; the app's DB role must
  not be `BYPASSRLS` / table owner. Worth prototyping the connection-opened interceptor early
  so the design isn't found pooling-incompatible later.

### Enforce the escape-hatch rules in CI (not just prose)
**Why:** the "do not use `IgnoreQueryFilters` / `Find` / `FromSql*`" rules above are only as
strong as code review. Convert them into a build-failing guard:
- Lightweight: a test that scans the `Application` / `Infrastructure` source (or IL) and fails
  if the forbidden APIs appear on the transactions path.
- Stronger: a Roslyn analyzer, or `ArchUnitNET` architecture tests.

### DesignTimeDbContextFactory hardcodes a connection string
**Why:** `Infrastructure/Persistence/DesignTimeDbContextFactory.cs` uses
`Host=localhost;Port=5432;...;Password=postgres`, which doesn't match the Aspire-managed
Postgres (random host port + generated password in user secrets). It works for
`dotnet ef migrations add` (offline, model-only) but `dotnet ef database update` from the CLI
can't connect. Consider reading the connection from configuration/user-secrets, or document
that migrations are applied at startup / via bundles rather than the CLI.
