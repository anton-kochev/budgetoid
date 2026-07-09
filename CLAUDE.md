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

Auth is deferred behind `IUserContext`; slice 1 uses `FakeUserContext`.

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

## Deploy Notes

- Use `azd init` / `azd up` from AppHost later; do not mix with `aspire deploy`.
- Scale-to-zero needs explicit Azure Container App publish settings with `MinReplicas = 0` and max 2.
- `Api.csproj` uses `<ContainerFamily>noble-chiseled</ContainerFamily>`; no handwritten Dockerfile.
- Append `Maximum Pool Size=5` to production PostgreSQL connection strings.
- Production migrations should run from CI/CD migration bundles, not API startup.

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
- ESLint 9 flat config (`eslint.config.js`) with `angular-eslint` + `typescript-eslint`
- Prettier: single quotes, trailing commas, 80 char width, 2-space indent
- camelCase JSON serialization

## Workflow

- Backend: run `dotnet build BudgetoidApp.sln` and `dotnet test` before committing
- Frontend: run `npm test`, `npm run lint`, and `npm run format` before committing
- Commits follow Conventional Commits (`feat:`, `fix:`, `ci:`, `chore:`, …)
- One logical change per commit
