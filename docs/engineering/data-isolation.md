# Data Isolation Invariant

> Read this before touching budget-scoped queries.

**A row must never be visible to a budget it doesn't belong to, nor to a person who does not own
it.** Two axes, because two things own rows: the money data belongs to a budget, and the identity
rows belong to a user. Both are enforced in layers; the bottom one is PostgreSQL row-level
security, and the layers above it exist for error quality. Keep all of the following true.

Enforced today:
- **Row-level security, on both axes.** A `budget_isolation` policy on `accounts`,
  `category_groups`, `categories`, `payees` and `transactions` compares `budget_id` against the
  session's ambient budget, and a `user_isolation` policy on `users`, `budgets` and `sessions`
  compares `id` and `user_id` against the session's authenticated user — each in both `USING` and
  `WITH CHECK`, so the connection every request is served by reaches no other tenant's rows and can insert into
  no tenant but its own, whatever produced the statement. `SessionContextInterceptor` puts both
  `app.current_user_id` and `app.current_budget_id` on each connection the context opens, in one
  round-trip. The policies live in `Infrastructure/Persistence/Provisioning/app-role-grants.sql`,
  never in a migration ([ADR 0005](../decisions/0005-isolate-budget-owned-rows-with-row-level-security.md),
  [ADR 0011](../decisions/0011-police-the-user-owned-tables.md)).
  **A new tenant-owned table needs a grant *and* a policy**: the grants are fail-closed, so a
  missing one fails loudly with `42501`, but RLS is fail-**open** — a granted table with no policy
  is readable across every tenant, silently. `tests/IntegrationTests/RlsCoverageTests.cs` reads the
  live schema and requires **every relation in `public` that can hold or expose rows** to be
  accounted for: an ordinary or partitioned table policed with the policy its ownership calls for,
  or an explicit exemption carrying its reason. Two refusals rather than one. A table carrying
  neither `budget_id` nor `user_id` is refused because "we forgot" and "it needs nothing" produce
  the identical catalog. A **view, materialized view or foreign table is refused outright** — a
  view runs with its *owner's* privileges unless `security_invoker` is set, and an owner bypasses
  RLS, so a granted view over a policed table reads every tenant; a materialized view cannot be
  policed at all. Index, sequence, composite type and TOAST table stay out because they expose no
  rows of their own, which is the test any future narrowing must pass. That direction is the whole
  point and must not be inverted — a list of *policed* relations fails open, because the one nobody
  added to it keeps the suite green. Exempt today: `credentials` (read to discover *who is asking*,
  so a policy keyed on the identity it resolves would refuse the query that resolves it),
  `currencies` (reference data owned by no tenant), and `__EFMigrationsHistory`. The same list and
  the same classification are what the deploy-time verifier reads, so the gate and the test cannot
  drift apart.
- **An exemption records the columns its reason was argued over, and `credentials` growing one goes
  red.** The exemption is granted to a *query* — the one that discovers who is asking — but
  PostgreSQL applies it to a whole *table*, so without this a column read only after authentication
  could land beside the discovery columns and be readable by every session. Going red means **move
  the column** to a table carrying `user_id`, which the coverage rule then polices by itself; it
  never means appending the name to the pinned list. `currencies` and `__EFMigrationsHistory` pin
  nothing on purpose — the first belongs to no tenant whatever columns it grows, the second has its
  shape owned by EF.
- **The column that decides tenancy must be `NOT NULL`.** Under `budget_id = current_budget` a row
  whose owner is NULL is invisible to every session — fail-closed, so not a leak, but a row that
  exists, that nobody can reach, and that nothing explains. The coverage gate refuses it.
- **The deploy gate reads a policy's content, not only its name.** It requires the single policy on
  a policed relation to be permissive and `FOR ALL`, and its `USING` expression to name both the
  session setting it is keyed on and the ownership column it turns on; a `WITH CHECK` may be absent
  (PostgreSQL then reuses `USING`) but if present must be identical to it. Identity alone was not
  enough: `FOR SELECT` instead of `FOR ALL` carries the right name and leaves every write
  unconstrained, and so does `USING (true)`. The column is matched on a **word boundary**, not as a
  substring — `users`' ownership column is `id`, which is a substring of `budget_id` and of
  `app.current_user_id`, so a substring test would be vacuous on exactly that table.
- **Read-side filter.** `BudgetoidDbContext` defines a global query filter named
  `BudgetIsolation` on `Transaction`, `Account`, `Payee`, `CategoryGroup`, and `Category`, scoped
  to the current `IBudgetContext.BudgetId`. Every LINQ query against those sets is auto-scoped —
  you cannot forget it. Each filter lambda **must** read the DbContext's primary-constructor
  parameter (`budgetContext!.BudgetId`) directly: Roslyn lowers that parameter to an instance
  field, so the lambda closes over `this` and EF re-roots the closure to the context instance
  running the query. A captured local, a static, or a service-locator call would bake the first
  request's budget into EF's cached model. The context is registered **non-pooled**
  (`AddDbContext` + `EnrichNpgsqlDbContext` in `Api/Program.cs`) because pooling forbids scoped
  constructor injection. Repositories deliberately do *not* re-filter by budget
  (`ITransactionRepository.GetAllAsync`), so these filters are the only thing above the policies —
  they turn another budget's row into a correct empty result and the 404 or 400 the API answers
  with. That is error quality, not enforcement; do not delete either layer as duplication.
- **Write-side schema guard.** The filter enforces nothing on a write, so every reference between
  two budget-owned rows is a composite foreign key carrying `budget_id` — `categories →
  category_groups` and `transactions → accounts | categories | payees`, each against an
  `(Id, BudgetId)` alternate key. PostgreSQL rejects a cross-budget reference whatever code path
  wrote it. This proves internal consistency only; *which* budget a write lands in is still the
  filter's and `IBudgetContext`'s job alone.
- **`Budget`, `User`, `Credential` and `Session` have no query filter.** The provisioning lookup
  runs before a budget id exists, so every query over `Budgets` must scope by owner explicitly
  (`FindFirstForUserAsync`); a session names no budget at all, so there is none to filter it by.
  That is a statement about the *read-side filter* only, and it no longer travels with the coverage
  exemption: `users`, `budgets` and `sessions` are policed on the user, and only `credentials` is
  still exempt. It has to be — reading it is how the request discovers who is
  asking, so it is the one table reached with no identity on the session at all
  ([ADR 0011](../decisions/0011-police-the-user-owned-tables.md)). That is also why the credential
  lookup projects to `credentials.user_id` and never joins `users`: the join would touch the table
  policed on the very id being resolved.
- **Immutable ownership.** `Transaction.BudgetId` has no public setter and is set only via the
  factory. The query filter is read-side only — `SaveChanges` ignores it — so that immutability is
  what stops the *application* from moving a row between budgets. The database holds the same rule
  independently: `budget_id` is absent from every `UPDATE` column list granted to the application
  role, so a raw `UPDATE` fails with `42501` whatever issued it
  ([ADR 0004](../decisions/0004-connect-as-a-least-privilege-role.md)).
- **Server-assigned ownership.** `BudgetId` comes only from `IBudgetContext`, never from a request
  DTO or route. `CreateTransactionCommand` has no `BudgetId` field; keep it that way. The policies'
  `WITH CHECK` half holds the same rule underneath: an insert can only land in the ambient budget,
  so a future importer or bulk endpoint cannot stamp a foreign `budget_id` even consistently.

Escape hatches the filter does **not** cover. These no longer leak — each one now meets the
policies instead, and a cross-budget read comes back empty rather than populated. Still do not
introduce them on budget-scoped data: an empty result where the code expects a row is a bug, and a
connection that names no ambient budget — or no ambient user, for `users`, `budgets` and `sessions`
— fails with `22P02` rather than answering. The build enforces this list: `BannedSymbols.txt` (referenced by
`Infrastructure` and `Api`, the only projects with an EF reference) turns each API below into an
RS0030 compile error.
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

Tests that lock this: `tests/IntegrationTests/RlsIsolationTests.cs` (raw SQL on the application
role, on both axes, every negative paired with the same statement against the session's own budget
or own user), `tests/IntegrationTests/RlsCoverageTests.cs` (schema-derived, so any new table
without the policy its ownership calls for, or without a stated exemption, fails — including one
carrying neither ownership column, and one that is a view or materialized view),
`tests/IntegrationTests/DeploymentProvisioningTests.cs` (the same rule at the deploy gate,
sabotaged once per failure mode: a dropped policy, a *renamed* one, row security switched off, an
unclassifiable table, a policy narrowed to `FOR SELECT`, one with a trivial `USING`, one keyed on
the wrong session setting, one on `users` naming no ownership column, one whose `WITH CHECK` is
wider than its `USING`, a restrictive one, and a granted view over a policed table),
`tests/IntegrationTests/BudgetIsolationTests.cs` (DbContext-level two-budgets-same-process +
endpoint-level two-factory) and the `BudgetId` immutability unit test in
`tests/UnitTests/TransactionTests.cs`. Removing a `HasQueryFilter` line must make the
DbContext-level test fail; removing — or renaming — a policy must make the RLS ones fail, and must
also refuse the next deploy.
