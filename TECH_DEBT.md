# Tech Debt & Hardening Backlog

Tracked improvements and the invariants future code must preserve. Add entries as they're
discovered; remove them as they're done.

---

## Data isolation invariant (read this before touching budget-scoped queries)

**A row must never be visible to a budget it doesn't belong to.** This is enforced in layers; the
bottom one is PostgreSQL row-level security, and the layers above it exist for error quality. Keep
all of the following true.

Enforced today:
- **Row-level security.** A `budget_isolation` policy on `accounts`, `category_groups`,
  `categories`, `payees` and `transactions` compares `budget_id` against the session's ambient
  budget in both `USING` and `WITH CHECK`, so the connection every request is served by reaches no
  other budget's rows and can insert into no budget but the ambient one — whatever produced the
  statement. `BudgetSessionInterceptor` puts the budget on each connection the context opens. The
  policies live in `Infrastructure/Persistence/Provisioning/app-role-grants.sql`, never in a
  migration ([ADR 0005](docs/decisions/0005-isolate-budget-owned-rows-with-row-level-security.md)).
  **A new budget-owned table needs a grant *and* a policy**: the grants are fail-closed, so a
  missing one fails loudly with `42501`, but RLS is fail-**open** — a granted table with no policy
  is readable across every tenant, silently. `tests/IntegrationTests/RlsCoverageTests.cs` derives
  its subject from the live schema so that drift fails a test instead of shipping.
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
- **`Budget` itself has no filter.** The provisioning lookup runs before a budget id exists, so
  every query over `Budgets` must scope by owner explicitly (`FindFirstForUserAsync`).
- **Immutable ownership.** `Transaction.BudgetId` has no public setter and is set only via the
  factory. The query filter is read-side only — `SaveChanges` ignores it — so that immutability is
  what stops the *application* from moving a row between budgets. The database holds the same rule
  independently: `budget_id` is absent from every `UPDATE` column list granted to the application
  role, so a raw `UPDATE` fails with `42501` whatever issued it
  ([ADR 0004](docs/decisions/0004-connect-as-a-least-privilege-role.md)).
- **Server-assigned ownership.** `BudgetId` comes only from `IBudgetContext`, never from a request
  DTO or route. `CreateTransactionCommand` has no `BudgetId` field; keep it that way. The policies'
  `WITH CHECK` half holds the same rule underneath: an insert can only land in the ambient budget,
  so a future importer or bulk endpoint cannot stamp a foreign `budget_id` even consistently.

Escape hatches the filter does **not** cover. These no longer leak — each one now meets the
policies instead, and a cross-budget read comes back empty rather than populated. Still do not
introduce them on budget-scoped data: an empty result where the code expects a row is a bug, the
policies do not cover `budgets`, and a connection that names no ambient budget fails with `22P02`
rather than answering. The build enforces this list: `BannedSymbols.txt` (referenced by
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
role, every negative paired with the same statement against the session's own budget),
`tests/IntegrationTests/RlsCoverageTests.cs` (schema-derived, so a new budget-owned table without a
policy fails), `tests/IntegrationTests/BudgetIsolationTests.cs` (DbContext-level
two-budgets-same-process + endpoint-level two-factory) and the `BudgetId` immutability unit test in
`tests/UnitTests/TransactionTests.cs`. Removing a `HasQueryFilter` line must make the
DbContext-level test fail; removing a policy must make the RLS ones fail.

---

## Migration invariant (read this before touching `Infrastructure/Migrations/`)

**The baseline migration is frozen.** The repo used to keep a single baseline it regenerated freely.
Production's `__EFMigrationsHistory` now references the current migration id, and the deploy pipeline
applies migrations unattended on every push to `main` — so regenerating the baseline gives it a new
id, and the next push would find nothing applied and try to re-create every table against a
populated database. The human checkpoint that used to catch this is gone by design.

Schema changes are **additive migrations** from here on. CI enforces this: the `migrations-guard`
job in `.github/workflows/ci.yml` fails when any migration file from the frozen baseline onward is
modified, deleted, or renamed — only additions pass. The model snapshot is exempt because EF
rewrites it on every `migrations add`.

---

## Backlog

### SPA ships without security headers
**Why:** `ClientApp/angular-budgetoid/public/staticwebapp.config.json` sets only a navigation
fallback — no `Content-Security-Policy`, `Strict-Transport-Security`, `X-Frame-Options`, or
`Referrer-Policy`. Add a `globalHeaders` block. A strict CSP also requires the fonts item below.

### Fonts and icons load from a third-party CDN
**Why:** `src/index.html` pulls Google Fonts and Material Symbols from `fonts.googleapis.com` /
`fonts.gstatic.com`, leaking the user's IP and user agent to a third party on every page load —
at odds with the no-third-parties stance in `docs/product/privacy.md`. Self-host both.

### API→database TLS does not validate the server certificate
**Why:** `Api/Program.cs` forces `SslMode=Require` outside Development, which encrypts but skips
certificate validation (documented inline). Move to `VerifyFull` with the platform CA bundle.

### NgRx StoreDevtools registered in production builds
**Why:** `app.config.ts` calls `provideStoreDevtools` unconditionally; `logOnly` still exposes
state (including the user's email) to the Redux DevTools extension. Gate it to dev builds.

### "No PII in logs" is an iOS-only requirement
**Why:** NFR-SEC-002 covers the iOS client, but no equivalent rule binds the API or the Angular
app. OTel logging (`ServiceDefaults/Extensions.cs`) has `IncludeScopes = true` with no redaction
processor. State the NFR for both and add a log-record processor that drops sensitive attributes.

### `users` and `budgets` have no RLS policy
**Why:** `app-role-grants.sql` deliberately leaves both tables unpoliced; cross-user isolation
there rests on application code alone, unlike every budget-owned table. Either add policies or
write an ADR owning the exception explicitly.

### Erasure needs grants the application role doesn't have
**Why:** `docs/product/privacy.md` commits to one-action account deletion, but `app-role-grants.sql`
grants only `SELECT, INSERT` (+ column-limited `UPDATE`) on `users` and `budgets` — no `DELETE`.
The erasure feature must add the grants and the coverage verification alongside the endpoint.

### DesignTimeDbContextFactory hardcodes a connection string
**Why:** `Infrastructure/Persistence/DesignTimeDbContextFactory.cs` uses
`Host=localhost;Port=5432;...;Password=postgres`, which doesn't match the Aspire-managed
Postgres (random host port + generated password in user secrets). It works for
`dotnet ef migrations add` (offline, model-only) but `dotnet ef database update` from the CLI
can't connect. Consider reading the connection from configuration/user-secrets, or document
that migrations are applied at startup / via bundles rather than the CLI.
