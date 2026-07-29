# Tech Debt & Hardening Backlog

Tracked improvements and the invariants future code must preserve. Add entries as they're
discovered; remove them as they're done.

---

## Data isolation invariant (read this before touching budget-scoped queries)

**A row must never be visible to a budget it doesn't belong to.** This is enforced in layers; the
EF global query filter is the primary control, but it has known gaps. Keep all of the following
true.

Enforced today:
- **Read-side filter.** `BudgetoidDbContext` defines a global query filter named
  `BudgetIsolation` on `Transaction`, `Account`, `Payee`, `CategoryGroup`, and `Category`, scoped
  to the current `IBudgetContext.BudgetId`. Every LINQ query against those sets is auto-scoped —
  you cannot forget it. Each filter lambda **must** read the DbContext's primary-constructor
  parameter (`budgetContext!.BudgetId`) directly: Roslyn lowers that parameter to an instance
  field, so the lambda closes over `this` and EF re-roots the closure to the context instance
  running the query. A captured local, a static, or a service-locator call would bake the first
  request's budget into EF's cached model and leak rows across tenants. The context is registered
  **non-pooled** (`AddDbContext` + `EnrichNpgsqlDbContext` in `Api/Program.cs`) because pooling
  forbids scoped constructor injection. These global filters are the **sole** read-side guard —
  repositories deliberately do *not* re-filter by budget (`ITransactionRepository.GetAllAsync`),
  so the escape hatches below are especially load-bearing.
- **Write-side schema guard.** The filter enforces nothing on a write, so every reference between
  two budget-owned rows is a composite foreign key carrying `budget_id` — `categories →
  category_groups` and `transactions → accounts | categories | payees`, each against an
  `(Id, BudgetId)` alternate key. PostgreSQL rejects a cross-budget reference whatever code path
  wrote it. This proves internal consistency only; *which* budget a write lands in is still the
  filter's and `IBudgetContext`'s job alone.
- **`Budget` itself has no filter.** The provisioning lookup runs before a budget id exists, so
  every query over `Budgets` must scope by owner explicitly (`FindFirstForUserAsync`).
- **Immutable ownership.** `Transaction.BudgetId` has no public setter and is set only via the
  factory. The query filter is read-side only — `SaveChanges` ignores it — so that immutability is
  what stops the *application* from moving a row between budgets. The database holds the same rule
  independently: `budget_id` is absent from every `UPDATE` column list granted to the application
  role, so a raw `UPDATE` fails with `42501` whatever issued it
  ([ADR 0004](docs/decisions/0004-connect-as-a-least-privilege-role.md)).
- **Server-assigned ownership.** `BudgetId` comes only from `IBudgetContext`, never from a request
  DTO or route. `CreateTransactionCommand` has no `BudgetId` field; keep it that way.

Escape hatches the filter does **not** cover — do not introduce these on budget-scoped data:
- `IgnoreQueryFilters()` — never on `BudgetoidDbContext`.
- Raw SQL (`FromSqlRaw` / `FromSqlInterpolated` / `ExecuteSql...`) — bypasses the filter; if
  unavoidable, scope by budget explicitly in the SQL.
- `Find` / `FindAsync` — by-key loads bypass the filter and can even return an already-tracked
  other-budget entity with no SQL. Use a filtered LINQ lookup
  (`FirstOrDefaultAsync(t => t.Id == id)`), which returns null for non-owners.
- `Remove(new Transaction { Id = x })` and other by-id mutations on stub entities — delete by
  PK with no owner check. Load through the filtered set first, then mutate.
- `ExecuteUpdate` / `ExecuteDelete` — honor the filter only when started from
  `dbContext.Transactions` without `IgnoreQueryFilters`.
- Future budget-scoped related/owned entities must each carry their **own** query filter.
  (Required navigations can otherwise silently *under*-return via INNER JOIN — a correctness
  bug, not a leak, but worth knowing.)

Tests that lock this: `tests/IntegrationTests/BudgetIsolationTests.cs` (DbContext-level
two-budgets-same-process + endpoint-level two-factory) and the `BudgetId` immutability unit test in
`tests/UnitTests/TransactionTests.cs`. Removing a `HasQueryFilter` line must make the
DbContext-level test fail.

---

## Backlog

### Layer 4 — Postgres Row-Level Security (database-level backstop)
**Why:** the query filter and conventions above all live in application code. RLS is the only
control that holds even when app code is buggy, uses raw SQL, or forgets a filter — the
database itself refuses to return other budgets' rows. Add before hosting real users' financial
data.

**Sketch:**
- In a migration: `ALTER TABLE transactions ENABLE ROW LEVEL SECURITY;` plus a policy keyed to
  a per-connection GUC, e.g. `USING (budget_id = current_setting('app.current_budget_id')::uuid)`.
  Repeat for every budget-scoped table, not just `transactions`.
- Per request, set the GUC on the connection inside the request's transaction via a
  `DbConnection`-opened EF interceptor: `SET LOCAL app.current_budget_id = '<budget>'`.
- **Tradeoffs / risks:** the interceptor must run on *every* connection open (connection
  pooling reuses physical connections); the migration owns the policy. Worth prototyping the
  connection-opened interceptor early so the design isn't found pooling-incompatible later.
- **One prerequisite is already met.** RLS is silently skipped for a table owner or a
  `BYPASSRLS` role, and the application connects as `budgetoid_app`, which is neither
  ([ADR 0004](docs/decisions/0004-connect-as-a-least-privilege-role.md)). Whoever picks this up
  gets a role the policies would actually apply to — and, for the same reason, must apply the
  policies through the admin identity, not the application one.

### Enforce the escape-hatch rules in CI (not just prose)
**Why:** the "do not use `IgnoreQueryFilters` / `Find` / `FromSql*`" rules above are only as
strong as code review. Convert them into a build-failing guard:
- Lightweight: a test that scans the `Application` / `Infrastructure` source (or IL) and fails
  if the forbidden APIs appear on the budget-scoped data path.
- Stronger: a Roslyn analyzer, or `ArchUnitNET` architecture tests.

### DesignTimeDbContextFactory hardcodes a connection string
**Why:** `Infrastructure/Persistence/DesignTimeDbContextFactory.cs` uses
`Host=localhost;Port=5432;...;Password=postgres`, which doesn't match the Aspire-managed
Postgres (random host port + generated password in user secrets). It works for
`dotnet ef migrations add` (offline, model-only) but `dotnet ef database update` from the CLI
can't connect. Consider reading the connection from configuration/user-secrets, or document
that migrations are applied at startup / via bundles rather than the CLI.
