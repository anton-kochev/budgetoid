# Budgetoid

Personal budget management app. .NET 10 backend + Angular 21 frontend.

## Project Layout

- `BudgetoidApp/` — backend solution (.NET 10)
  - `AppHost/` — Aspire orchestrator for local development and `azd` deployment
  - `ServiceDefaults/` — shared Aspire service defaults
  - `Domain/` — entities and domain rules, no infrastructure dependencies
  - `Application/` — CQRS commands/queries with plain handler interfaces (no MediatR)
  - `Infrastructure/` — EF Core 10 + Npgsql PostgreSQL persistence
  - `Api/` — ASP.NET Core minimal API
  - `Tools/DbProvision/` — deploy-time console tool: migrate, provision the app role, verify RLS coverage
  - `tests/UnitTests/`, `tests/IntegrationTests/` — TUnit tests
- `ClientApp/angular-budgetoid/` — frontend (Angular 21)

## Build & Run

### Backend (from `BudgetoidApp/`)

```sh
dotnet build BudgetoidApp.sln
dotnet test
aspire run # or F5 AppHost
```

Aspire starts PostgreSQL and the API. The connection name is `budgetoid` and must match
AppHost, API registration, and test overrides.

### Frontend (from `ClientApp/angular-budgetoid/`)

```sh
npm start        # ng serve (dev server)
npm run build    # production build
npm test         # Vitest unit tests (single run)
npm run test:watch     # Vitest watch mode
npm run test:coverage  # Vitest with coverage
npm run lint     # ESLint with --fix
npm run format   # Prettier
```

Specs live next to their subject as `*.spec.ts` and run on the `@angular/build:unit-test`
builder (Vitest, jsdom). Import test globals explicitly from `vitest` — no ambient globals
are configured. Use `// Arrange // Act // Assert` comments.

## Backend Architecture

Clean Architecture with CQRS. Commands/queries live under `Application/Transactions/*` and
are injected as plain handlers (`ICommandHandler`/`IQueryHandler`); no MediatR dispatcher
until decorators are needed. The API is ASP.NET Core minimal API on Azure Container Apps.
Auth is live Google OAuth.

**The budget is the unit of tenancy.** `UserProvisioningMiddleware` resolves the Google
`sub` (via `EnsureUserHandler`) into a user id and default budget id on the scoped
`CurrentUser`; `IBudgetContext` exposes the ambient budget. Read
[data isolation](docs/engineering/data-isolation.md) before touching budget-scoped queries.
Load-bearing rules, each explained there or in the linked decision:

- Budget-owned rows are isolated twice: PostgreSQL `budget_isolation` RLS policies enforce,
  EF `BudgetIsolation` query filters turn a foreign row into the API's 404/400. Neither is
  duplication — do not delete either. See [ADR 0005](docs/decisions/0005-isolate-budget-owned-rows-with-row-level-security.md).
- EF escape hatches (`IgnoreQueryFilters`, `FromSql*`, `ExecuteSql*`, `Find`/`FindAsync`,
  `ExecuteUpdate`/`ExecuteDelete`) are compile errors via `BudgetoidApp/BannedSymbols.txt`.
- A new budget-owned table needs a grant **and** a policy. Grants fail closed (`42501`),
  RLS fails open — `RlsCoverageTests` exists to catch the silent case.
- `BudgetSessionInterceptor` must stay a **connection-opened** interceptor, and
  `No Reset On Close=true` / `Multiplexing=true` are forbidden in any connection string.
  See [ADR 0008](docs/decisions/0008-read-the-ambient-budget-inside-the-policy.md).
- The app connects as `budgetoid_app` on `ConnectionStrings:budgetoid`; the elevated
  `budgetoid-admin` is read only by the Development startup block. Migrations never run on
  the app role. See [ADR 0004](docs/decisions/0004-connect-as-a-least-privilege-role.md).
- Immutable columns are enforced by **omission from a `GRANT UPDATE` column list** — never
  by `REVOKE`, and never widen a list to table-wide. Grants and policies live together in
  `Infrastructure/Persistence/Provisioning/app-role-grants.sql`, never in a migration.
- **In production the app role has no password** — the missing `Password=` is what makes
  Aspire fetch an Entra token for the API's managed identity. Do not "complete" it.
  See [ADR 0007](docs/decisions/0007-authenticate-to-postgres-with-managed-identity.md).

## Frontend Architecture

- Angular 21 standalone components (no NgModules)
- Slice-1 transaction state uses an Angular signal-based service; NgRx remains for existing
  auth/profile scaffolding only
- `+core/` — API services, guards, interceptors, app-wide providers
- `+shared/` — shared components and utilities
- `+state/` — NgRx actions, effects, selectors, reducers
- Path aliases: `@app-core/*`, `@app-shared/*`, `@app-state/*` (baseUrl is `./src`)
- Auth: Google OAuth via `angular-oauth2-oidc`
- UI: Angular Material + Angular CDK, styled with SCSS

## Documentation

- **Design system** — `docs/design/_overview.md`. Read the relevant chapter before building
  or changing UI. Every visible value comes from design tokens (`--bud-*` / `--mat-sys-*`);
  a hard-coded hex, px gap, or duration in component styles is a defect unless the book
  names it. Brand mark rules stay in `branding/BRAND.md`.
- **Product** — `docs/product/problem.md` defines the problem; features are measured
  against it. Read it before making product or UX decisions.
- **Business logic** — start at `docs/business-logic/_overview.md`. Read the relevant file
  before modifying business rules; if none exists for the domain area, create one following
  the structure of the others.
- **Engineering invariants** — [data isolation](docs/engineering/data-isolation.md) and
  [migrations](docs/engineering/migrations.md). Each names the tests that lock it: removing
  a `HasQueryFilter` line or a policy must fail one.
- A change to a design rule, business rule, or invariant updates the owning doc **in the
  same commit**.
- **`docs/` documents only what is true today.** Agreed-but-unbuilt design lives in the
  private `budgetoid-specs` repository (SRS documents at the root, `product-research/` for
  rationale) and moves into `docs/` the day it ships. Never state an unbuilt capability in
  the present tense. The hardening backlog lives there too — a public list of unclosed
  weaknesses is a map.

## Rule Enforcement

Every rule is owned by the lowest layer that can enforce it **declaratively** — database,
then application, then client. Upper layers may restate a rule for error quality and UX,
never for enforcement. "The database enforces it" means it *rejects*, not that it coerces.
Two boundaries: no procedural logic (triggers, PL/pgSQL) pushed down just to satisfy
"lowest layer"; and domain invariants go down while product policy stays up, because the
bottom is the most expensive layer to change. When a rule deliberately sits above its
lowest capable layer, the doc describing it says why. See
[ADR 0002](docs/decisions/0002-enforce-rules-at-the-lowest-capable-layer.md).

## Deploy Notes

`DEPLOYMENT.md` is the runbook; ADRs [0006](docs/decisions/0006-automate-migrations-and-provisioning-in-the-pipeline.md),
[0009](docs/decisions/0009-route-database-traffic-over-a-private-endpoint.md) and
[0010](docs/decisions/0010-serve-the-app-from-a-custom-domain.md) hold the reasoning.

- Use `azd init` / `azd up` from AppHost; do not mix with `aspire deploy`.
- **The AppHost owns the Container Apps environment**, not azd — that ownership is what
  attaches the virtual network and sets scale-to-zero in the app model. Recreating it
  changes the API hostname, which also means editing `app-config.json` and the Google
  OAuth client.
- The publish branch of `AppHost/Program.cs` deliberately does **not** `WithReference` the
  database for the API: that would register the API's managed identity as a full Entra
  administrator, and administrators are not subject to RLS. Do not add it back.
- `.ClearDefaultRoleAssignments()` on the Postgres resource is load-bearing, not tidying —
  without it `azd provision` fails on an empty ARM resource name.
- **The database has no standing firewall rule.** `az postgres flexible-server
  firewall-rule list` must come back empty outside a deploy.
- **`budgetoid.app` is registered but not yet wired up.** The generated Azure hostnames are
  still the live ones; treat any doc claiming otherwise as wrong.
- **The baseline migration is frozen.** Schema changes are additive migrations from here
  on; the `migrations-guard` CI job fails any edit to an existing migration file.
- Production migrations run from the pipeline, never at API startup: migrate **then**
  provision **then** verify, an order that lives in `DeploymentDatabaseProvisioning` rather
  than in a runbook. Verification is not optional.
- Append `Maximum Pool Size=5` to production PostgreSQL connection strings.
- `Api.csproj` uses `<ContainerFamily>noble-chiseled</ContainerFamily>`; no Dockerfile.

## Code Conventions

### Backend

- .NET 10, nullable enabled, implicit usings
- Primary constructors for DI
- File-scoped namespaces
- TUnit for tests
- Date/time values stored as UTC; PostgreSQL `timestamptz` rejects non-UTC `DateTime`

### Frontend

- `inject()` function over constructor DI
- OnPush change detection
- Mobile-first: start every layout at phone width, enhance upward with `min-width`
  breakpoints; CSS Grid as the default layout tool
- ESLint 9 flat config (`eslint.config.js`) with `angular-eslint` + `typescript-eslint`
- Prettier: single quotes, trailing commas, 80 char width, 2-space indent
- camelCase JSON serialization

## Workflow

- Backend: run `dotnet build BudgetoidApp.sln` and `dotnet test` before committing
- Frontend: run `npm test`, `npm run lint`, and `npm run format` before committing
- Commits follow Conventional Commits (`feat:`, `fix:`, `ci:`, `chore:`, …)
- One logical change per commit
