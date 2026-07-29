# Budgetoid

Personal budget management app. .NET 10 backend + Angular 21 frontend.

## Project Layout

- `BudgetoidApp/` — current backend solution (.NET 10)
  - `AppHost/` — Aspire orchestrator for local development and future `azd` deployment
  - `ServiceDefaults/` — shared Aspire service defaults
  - `Domain/` — entities and domain rules, no infrastructure dependencies
  - `Application/` — CQRS commands/queries with plain handler interfaces (no MediatR)
  - `Infrastructure/` — EF Core 10 + Npgsql PostgreSQL persistence
  - `Api/` — ASP.NET Core minimal API
  - `Tools/DbProvision/` — deploy-time console tool: migrate, provision the app role, verify RLS coverage
  - `tests/UnitTests/`, `tests/IntegrationTests/` — TUnit tests
- `ClientApp/angular-budgetoid/` — current frontend (Angular 21)

Legacy `Budgetoid/`, Vue, and Elm projects have been removed.

## Build & Run

### Backend (from `BudgetoidApp/`)

```sh
dotnet build BudgetoidApp.sln
dotnet test
aspire run # or F5 AppHost
```

Aspire starts PostgreSQL and the API. The connection name is `budgetoid` and must match AppHost, API registration, and test overrides.

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

Tests run via the `@angular/build:unit-test` builder (Vitest runner, Node/jsdom).
Specs live next to their subject as `*.spec.ts`. Import test globals explicitly
from `vitest` (`import { describe, it, expect } from 'vitest'`) — no ambient
globals are configured for ESLint. Use `// Arrange // Act // Assert` comments.

## Backend Architecture

Clean Architecture with CQRS. Commands/queries live under `Application/Transactions/*` and are handled by directly injected plain handlers (`ICommandHandler`/`IQueryHandler` shape); no MediatR dispatcher until decorators are needed. Infrastructure uses EF Core 10 with PostgreSQL via Npgsql. The API layer is ASP.NET Core minimal API, intended for Azure Container Apps.

Auth is live Google OAuth. The budget is the unit of tenancy: `UserProvisioningMiddleware` resolves the authenticated principal (via `EnsureUserHandler`, keyed on the Google `sub`) into an internal user id and that user's default budget id, both held on the scoped `CurrentUser`. The ambient budget is exposed through `IBudgetContext`, implemented by `HttpContextBudgetContext` in prod (`TestBudgetContext` in tests); `Account`, `CategoryGroup`, `Category`, `Payee`, and `Transaction` are isolated per budget at two depths: PostgreSQL `budget_isolation` row-level security policies are the enforcement, and the `BudgetIsolation` query filters above them turn another budget's row into the 404 or 400 the API answers with. Do not delete either as duplication. See `docs/business-logic/budgets.md` and `docs/business-logic/users-and-ownership.md`.

The app connects to PostgreSQL as `budgetoid_app`, a least-privilege role, on `ConnectionStrings:budgetoid`. `ConnectionStrings:budgetoid-admin` is elevated and is read only by the Development startup block, which migrates and then applies the grants; migrations can never run on the application role. Immutable columns (`budget_id` everywhere, all of `budgets`, `accounts.currency_code`, `users.google_subject`) are expressed by **omission from a `GRANT UPDATE` column list** — PostgreSQL column privileges are additive, so `REVOKE` cannot subtract a column from a table-wide grant, and widening any list to table-wide silently reopens every hole. A blocked write is `42501` and is deliberately untranslated: it means the domain was bypassed. Grants live in `Infrastructure/Persistence/Provisioning/app-role-grants.sql`, never in a migration. See `docs/decisions/0004-connect-as-a-least-privilege-role.md`.

The same script carries the row-level security policies, for the same reason — they are written `TO budgetoid_app`, so one file cannot be applied without the other. `BudgetSessionInterceptor` puts the ambient budget on every connection the context opens as the session setting `app.current_budget_id`; it must stay a **connection-opened** interceptor, because EF opens and closes the connection per operation, so a value set in middleware evaporates on return to the pool and one set inside a transaction is reverted by a rollback. `No Reset On Close=true` and `Multiplexing=true` are forbidden in any connection string. A new budget-owned table needs a grant **and** a policy: grants are fail-closed (`42501`), RLS is fail-open — a granted table with no policy is readable across every tenant, silently, which is what `RlsCoverageTests` exists to catch. A session naming no budget fails with `22P02`. See `docs/decisions/0005-isolate-budget-owned-rows-with-row-level-security.md`.

## Frontend Architecture

- Angular 21 standalone components (no NgModules)
- Slice-1 transaction state uses an Angular signal-based service; NgRx remains for existing auth/profile scaffolding only
- `+core/` — API services, guards, interceptors, app-wide providers
- `+shared/` — shared components and utilities
- `+state/` — NgRx actions, effects, selectors, reducers
- Path aliases: `@app-core/*`, `@app-shared/*`, `@app-state/*` (configured in tsconfig, baseUrl is `./src`)
- Auth: Google OAuth via `angular-oauth2-oidc`
- UI: Angular Material + Angular CDK
- Styling: SCSS

## Design System Documentation

The design system lives in `docs/design/` — start with `docs/design/_overview.md`.
Read the relevant chapter before building or changing UI. Every visible value comes
from design tokens (`--bud-*` / `--mat-sys-*`); a hard-coded hex, px gap, or duration
in component styles is a defect unless the book names it. When a change affects a
design rule, update the chapter in the same commit. Brand mark rules stay in
`branding/BRAND.md`.

## Business Logic Documentation

The product problem definition lives in `docs/product/problem.md` — read it before making
product or UX decisions; features are measured against it.
Before modifying business logic, read the relevant file in `docs/business-logic/`.
When your changes affect business rules, update the corresponding doc in the same commit.
If no file exists for the domain area, create one following the structure of existing files.
Start with `docs/business-logic/_overview.md` for domain orientation.
Rules marked `[SOURCE: code-audit — unconfirmed]` need human confirmation before relying on them.

## Rule Enforcement

Every rule is owned by the lowest layer that can enforce it **declaratively** — database first,
then application, then client. Upper layers may restate a rule for error quality and UX, never for
enforcement. Two boundaries: no procedural logic (triggers, PL/pgSQL) pushed into the database just
to satisfy "lowest layer"; and domain invariants go down while product policy stays up, because the
bottom is the most expensive layer to change. "The database enforces it" means it *rejects*, not
that it coerces. When a rule deliberately sits above its lowest capable layer, the doc that
describes the rule says why. Full reasoning in
`docs/decisions/0002-enforce-rules-at-the-lowest-capable-layer.md`.

## Deploy Notes

- Use `azd init` / `azd up` from AppHost later; do not mix with `aspire deploy`.
- Scale-to-zero needs explicit Azure Container App publish settings with `MinReplicas = 0` and max 2.
- `Api.csproj` uses `<ContainerFamily>noble-chiseled</ContainerFamily>`; no handwritten Dockerfile.
- Append `Maximum Pool Size=5` to production PostgreSQL connection strings.
- Production migrations run from the deploy pipeline, never at API startup. `.github/workflows/deploy.yml` runs `Tools/DbProvision` between `azd provision` and `azd deploy`, so new code never starts against an old schema.
- Deploying is migrate **then** provision **then** verify, and that ordering now lives in
  `DeploymentDatabaseProvisioning` rather than in a runbook: the grants and policies name individual
  tables, so the schema has to exist first, and the password alphabet is checked before the first
  statement so a typo cannot leave a half-migrated database. Verification is not optional — grants
  are fail-closed, RLS is fail-open. See `DEPLOYMENT.md` and `docs/decisions/0006-automate-migrations-and-provisioning-in-the-pipeline.md`.
- **The baseline migration is frozen.** Production's `__EFMigrationsHistory` references the current migration id, and the pipeline applies migrations unattended, so regenerating the single baseline would make the next push try to re-create every table. Schema changes are additive migrations from here on.
- The deployed container is handed one connection string, the least-privilege one. The publish
  branch of `AppHost/Program.cs` deliberately does **not** `WithReference` the database for the API:
  that reference injects the admin identity as `BUDGETOID_URI`/`_USERNAME`/`_PASSWORD` as well as a
  connection string. Do not add it back.

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
- Mobile-first: start every layout at phone width, enhance upward with `min-width` breakpoints; CSS Grid as the default layout tool
- ESLint 9 flat config (`eslint.config.js`) with `angular-eslint` + `typescript-eslint`
- Prettier: single quotes, trailing commas, 80 char width, 2-space indent
- camelCase JSON serialization

## Workflow

- Backend: run `dotnet build BudgetoidApp.sln` and `dotnet test` before committing
- Frontend: run `npm test`, `npm run lint`, and `npm run format` before committing
- Commits follow Conventional Commits (`feat:`, `fix:`, `ci:`, `chore:`, …)
- One logical change per commit
