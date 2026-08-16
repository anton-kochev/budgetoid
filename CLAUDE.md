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
npm test         # Vitest unit tests (single run); needs a prior `npm run build`
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
`sub` (via `ResolveUserHandler`) into a user id and default budget id on the scoped
`CurrentUser`; `IBudgetContext` exposes the ambient budget. **Only a route carrying
`ProvisionsUser` metadata may create an account** — every other authenticated route resolves
or answers 401 and writes nothing at all, so a token outliving an erasure cannot resurrect
the row. An account, its first credential and its default budget are created in **one**
`SaveChanges`; there is no heal, and a resolved account with no budget throws. The marker is
opt-in on six route groups; adding it anywhere else needs the argument in
[users-and-ownership.md](docs/business-logic/users-and-ownership.md). Read
[data isolation](docs/engineering/data-isolation.md) before touching budget-scoped queries.
Load-bearing rules, each explained there or in the linked decision:

- Budget-owned rows are isolated twice: PostgreSQL `budget_isolation` RLS policies enforce,
  EF `BudgetIsolation` query filters turn a foreign row into the API's 404/400. Neither is
  duplication — do not delete either. See [ADR 0005](docs/decisions/0005-isolate-budget-owned-rows-with-row-level-security.md).
- `users`, `budgets`, `sessions` and `passkey_signature_counters` are policed on the **user** by
  `user_isolation`, not on a budget. `credentials`, `passkey_public_keys`, `webauthn_challenges`,
  `recovery_code_hashes` and `session_tokens` are exempt, each because it is read *before* the request
  has an identity a
  policy could be keyed on — so the credential lookup must never join `users`, and a recovery code is
  found by the hash of the verifier on a request that has said nothing about who is asking. The last
  is the same argument reached from the opposite end: not somebody who lost their authenticator, but
  **every authenticated request there is**, which is why the session's *discovery key* is split onto
  its own exempt table while the expiry and revocation instant it decides nothing without stay on the
  policed `sessions` row ([ADR 0019](docs/decisions/0019-authenticate-a-request-from-a-first-party-session-cookie.md)).
  **An
  exempt table scopes nothing**: only the discovery lookup may omit an owner filter, and every other
  **read** of one must carry its own `where user_id = …`. The destructive statements are an exception
  to that sentence and not to the rule — EF emits each `DELETE` by primary key, so none of them
  carries an owner predicate at all. What scopes one is the owner-bearing read that produced its
  entity, in the same transaction, which is why a delete takes the loaded entity rather than an id,
  and why `credentials.user_id` immutability is load-bearing twice over. Do not read the port's
  shape as a narrowing that discriminates: a lookup handed an owner id taken off the row it is
  about to select by primary key constrains nothing today, and exists to keep the rule literally
  true and to bite the first caller that takes that id from somewhere else.
  What holds each exemption to its reason is its **pinned column set**, not the
  grant matrix — a write-once secret passes any append-only rule — so a new column there means *move
  the column*, never widen the pin. See
  [ADR 0011](docs/decisions/0011-police-the-user-owned-tables.md),
  [ADR 0012](docs/decisions/0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md)
  and [ADR 0014](docs/decisions/0014-scope-the-credential-delete-in-the-application.md).
- **The passkey assertion path publishes the identity only after the signature verifies, and opens
  its transaction only after that.** A transaction opened earlier configures the connection while
  `app.current_user_id` is still empty, so every policed statement inside it fails with `22P02`.
- EF escape hatches (`IgnoreQueryFilters`, `FromSql*`, `ExecuteSql*`, `Find`/`FindAsync`,
  `ExecuteUpdate`/`ExecuteDelete`) are compile errors via `BudgetoidApp/BannedSymbols.txt`.
- **A credential type has exactly one spelling and it is written out, never derived from the member
  name.** `Domain/Users/CredentialTypeSpelling.cs` owns it; the `type` column, every copy of that
  column, `CK_credentials_type` and the `type` member of `GET /api/me/credentials` all read that one
  definition, which is what makes "the wire agrees with the column" a fact rather than a
  coincidence — it stopped being one the moment a two-word member was declared and camel-case
  produced `recoveryCodes` for a column holding `recovery_codes`. Do not answer that with a global
  `JsonNamingPolicy`: it would silently respell every other enum the API emits. Which spellings a
  *table* accepts is a separate rule each configuration keeps on top — `passkey_public_keys` and
  `passkey_signature_counters` take `passkey` or `federated` only, and folding that into the shared
  type would impose it on everything reading it. `CredentialType` is ordered so `default` is
  `Federated`, whose shape the database refuses; `SessionKind` made the same choice for the same
  reason.
- **The dependency direction is pinned, not described.** Between rings MSBuild's cycle detection
  already refuses the outward `ProjectReference`; what nothing caught until now is the outward edge
  that closes no loop — a package on `Application`, a `FrameworkReference` or `Sdk` attribute on
  `Domain`, `UnitTests` reaching `Api`, a whole new project. `ProjectReferenceGraphTests` renders
  every csproj declaration as one of fifty-nine rows and pins the set, dropping package *versions*
  so a routine bump never reddens it. Two guards sit beside it for rules the graph provably cannot
  express: `CompositionBoundaryTests` (Api may **compose** Infrastructure, never **consume** it — no
  route delegate takes a persistence port) and `OwnershipKeyImmutabilityTests` (every `UserId` and
  `BudgetId` the Domain declares is written once, `init` included). Each ships permanent negative
  controls, so none can pass by having nothing to find. Read
  [dependency direction](docs/engineering/dependency-direction.md) before adding a project or a
  reference of any kind.
- A new tenant-owned table needs a grant **and** a policy — `budget_isolation` if it carries
  `budget_id`, `user_isolation` if it carries `user_id`. Grants fail closed (`42501`),
  RLS fails open. `RlsCoverageTests` and the deploy-time verifier read one shared classifier
  (`RowLevelSecurityCoverage`); a table carrying neither column fails both.
- **A recovery code never reaches the server.** The client mints it, derives a verifier from it, and
  sends only that; `recovery_code_hashes` stores `SHA-256(verifier)`. The code is what the next epic
  derives a key-encryption key from, so a code on the wire would hand the operator the keys. Two
  consequences a reader will try to "fix": the server **cannot** check the 128-bit entropy rule — it
  sees fixed-width opaque bytes, and pins width, set size and distinctness only — and a code is
  consumed by **deleting** its row, never by stamping it, which is why the table holds no `UPDATE`
  grant of any shape. One `credentials` row per **set**, at most one set per account. The generation
  path must never materialise the old set's hash rows: EF would then delete them itself instead of
  the database's cascade, and unlike `sessions` the role *has* `DELETE` here, so it would succeed
  silently. See [recovery-codes.md](docs/business-logic/recovery-codes.md) and
  ADRs [0015](docs/decisions/0015-mint-recovery-codes-on-the-client-and-store-only-a-hash-of-a-verifier.md),
  [0016](docs/decisions/0016-give-recovery-code-hashes-their-own-exempt-table.md),
  [0017](docs/decisions/0017-consume-a-recovery-code-by-deleting-its-row.md).
- **An account owns one content key and one index key, and every recovery factor stores its own
  wrapped copy of both.** Deriving either from a credential would give a second passkey a second index
  key — two blind index values for one name, and a uniqueness constraint that appears to work while
  enforcing nothing. `wrapped_account_keys` is **policed** by `user_isolation`, not exempt: it is read
  after the request has an identity, and it is keyed on **`factor_id`** — a **factor is not a
  credential**: a passkey is one factor, a set of recovery codes is **ten**, because each code derives
  its own key-encryption key and a person redeems whichever one they still have. Registering a passkey
  and issuing a set each **require** `factorId` and both envelopes **per factor**, written in the
  **same** `SaveChanges` as the credential. Read what that buys precisely: the two `NOT NULL` envelope
  columns make "a row carries both keys or neither" a schema fact, but **"every factor has a row" is
  not one** — one-to-optional needs a trigger, which ADR 0002 forbids — it is held by there being
  exactly two write paths, so a third would create a keyless factor and redden nothing.
  `factor_id` is client-minted and
  deliberately **not** `credentials.id` — letting a client choose that id retires ADR 0014's first leg.
  It arrives in **one spelling**: the lower-case 36-character hyphenated form, no surrounding
  whitespace, which is what a `Guid` renders as and therefore what every later read hands back. Both
  write paths call one `CanonicalFactorId.TryParse`, which compares the text **ordinally against
  `parsed.ToString("D")`** — `Guid.TryParseExact(…, "D")` alone pins nothing, because it admits
  upper-case and mixed-case hex and trims before it looks at the format. That comparison is not
  redundant with the parse and is the whole rule: the value is the associated data both envelopes were
  sealed with, so a client binding to the spelling it sent and handed back another has **both**
  envelopes stop opening, permanently, with no error naming the cause. This repository's client is safe
  only because it lower-cases before sealing; the contract is cross-client.
  Three consequences a reader will try to "fix": the role holds **no `UPDATE` and no `DELETE`** here,
  so a replaced set's row must leave by the cascade and `GenerateRecoveryCodesHandler`'s
  never-materialise rule now binds a second table that fails **loudly** with `42501`; the wrapped keys
  are **not** the verifiable PRF evidence `CompleteRegistrationHandler`'s remarks ask for, because the
  server cannot tell a key-encryption key derived through PRF from one derived out of a constant; and
  the client crypto in `+core/security/account-keys.ts` **has no caller** while the server already
  demands its output — that asymmetry is deliberate and argued in the decision log. See
  [account-keys.md](docs/business-logic/account-keys.md) and
  [ADR 0018](docs/decisions/0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md).
- **The schema carries no remnant of an erasure and the route table offers no way back** — no
  soft-delete flag, tombstone, deletion record, anonymized remnant or archived copy, and no route
  that restores, undeletes or reactivates an account. Three gates, and each holds a different half:
  `ErasureRemnantVocabulary` refuses the column and relation *names it recognises*,
  `ErasureAtomicityTests` counts the rows of every relation that stores any, and
  `ErasureIrreversibilityTests` pins the `/api/me/erasure` resource to the one destructive route.
  A remnant with no name — an overwritten column, a message payload, a materialized view — is caught
  by the row count alone; a log line is caught by nothing but `ErasureLoggingTests`. All of them
  carry arguments for their deliberate omissions — read
  [erasure.md](docs/business-logic/erasure.md) before widening any.
- **The export refuses rather than truncates.** `GET /api/me/export` reads `budgets` scoped by
  `user_id` and the five owned collections through the ambient-budget filters, and **throws** unless
  the owned set is *exactly* the ambient budget — **set equality, both directions**: a budget owned
  outside the ambient one means rows are missing, and an ambient budget the user does not own means
  one budget's rows filed under another's id. Do not simplify it to `Count > 1`; that passes the
  second direction, which has its own test. A silent single-budget export is exactly the truncation
  the requirement forbids, and the throw is what a future reader will be tempted to "fix" into a
  quiet success. It writes no row, logs no identifier, and carries no `ProvisionsUser`.
  See [export.md](docs/business-logic/export.md).
- `SessionContextInterceptor` must stay a **connection-opened** interceptor, and
  `No Reset On Close=true` / `Multiplexing=true` are forbidden in any connection string —
  now for two settings, `app.current_user_id` and `app.current_budget_id`, which raises the
  cost of ever flipping them. See [ADR 0008](docs/decisions/0008-read-the-ambient-budget-inside-the-policy.md).
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
  auth scaffolding only
- `+core/` — API services, guards, interceptors, app-wide providers
- `+shared/` — shared components and utilities
- `+state/` — NgRx actions, effects, selectors, reducers
- Path aliases: `@app-core/*`, `@app-shared/*`, `@app-state/*` (baseUrl is `./src`)
- Auth: Google OAuth via `angular-oauth2-oidc`
- UI: Angular Material + Angular CDK, styled with SCSS
- **Nothing loads from another origin** — no CDN script, stylesheet, typeface, icon, or
  image, and no identity-provider profile picture. Typefaces live in `public/fonts/`.
  `src/no-external-origins.spec.ts` reads the production bundle, so `npm test` needs a
  `npm run build` first. See [no third-party origins](docs/engineering/no-third-party-origins.md).
- **The production build registers no state-inspection provider.** `provideStoreDevtools` lives
  in `src/app/devtools.providers.ts`, which the production `fileReplacements` in `angular.json`
  swaps for an empty module — a runtime `isDevMode()` branch leaves the code in the bundle.
  `src/no-devtools.spec.ts` reads the bundle and fails if it comes back.
- **No button shows a focus ring unless `src/styles.scss` puts one there.** Material sets
  `outline: none` on `.mdc-button`, so the book's `2px solid var(--bud-focus-ring)` at
  `outline-offset: 2px` lives in one global `:focus-visible` block — element selectors, not
  `:where()`, because it has to out-specify Material. `src/focus-ring.spec.ts` reads the
  emitted CSS; it proves the rule ships, not that it wins the cascade, and the keyboard
  walkthrough in [accessibility.md](docs/design/accessibility.md) holds the rest.
- **The test runner's time zone is pinned** to `Pacific/Kiritimati` in `src/test-setup.ts`,
  because CI runs at UTC and a date test comparing UTC against local discriminates nothing
  there. `export-filename.spec.ts` asserts the offset is non-zero, so deleting the pin fails.
- **`/app/settings` ships with no navigation entry** — reachable by URL only, on purpose; a
  later epic gives it one. It shows the email from `GET /api/me`, a working export that writes
  the response bytes to disk **unread** (`responseType: 'blob'` — a JSON round-trip would turn
  exact `numeric(14,4)` amounts into doubles), and an erasure control that is present and
  **disabled** because the client cannot register a passkey yet. It also lists every credential
  from `GET /api/me/credentials` — **type and day only**, never an id or a provider subject —
  with registration present and **disabled** for the same reason. **Revoke renders only on a row
  something can revoke**, which is the passkeys: a federated credential is replaced by an email
  change and a recovery-code set is unrevocable by construction, so neither draws even a disabled
  button. Every other disabled control on this screen promises a release; one that never could be
  enabled is the worse lie. Revocability is carried per kind beside that kind's word and caption,
  in a map declared exhaustive over `CredentialKind`, so a member added without one fails to
  compile. The day is formatted in the reader's own zone by `credential-registration-date.ts`,
  never by `DatePipe`: nothing provides `LOCALE_ID`, so `DatePipe` would silently pin every date
  to `en-US`, and a UTC-formatted day is wrong for fourteen hours of every day under the
  `Pacific/Kiritimati` test pin. A **Recovery codes** section reads the count from
  `GET /api/me/recovery-codes` and never writes: six states that no two of which are
  interchangeable, `null` and `0` never collapsed — a `catchError` returning `of(0)` would tell
  somebody whose request failed that they have no way back — loading and failure in an
  unconditional `role="status"` region that is empty at rest, the count rendered outside it, and
  pluralisation as three template branches because `I18nPluralPipe` would pin plural rules to
  `en-US` the way `DatePipe` pins days. Generate is present and **disabled**: generation is gated
  on a fresh passkey assertion. `+core/security/recovery-codes.ts` mints codes and derives
  verifiers with Web Crypto and **has no caller** — its spec is the only place in the system that
  can check the 128-bit entropy rule, because the server sees fixed-width opaque bytes. Key
  rotation and email change render nothing today and are owned by later stories — do not
  "complete" the screen. See [export.md](docs/business-logic/export.md),
  [erasure.md](docs/business-logic/erasure.md),
  [recovery-codes.md](docs/business-logic/recovery-codes.md) and the credential-list and
  recovery-codes chapters in [components.md](docs/design/components.md).

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
- **Engineering invariants** — [data isolation](docs/engineering/data-isolation.md),
  [migrations](docs/engineering/migrations.md), and
  [no third-party origins](docs/engineering/no-third-party-origins.md). Each names the tests
  that lock it: removing a `HasQueryFilter` line, a policy, or a self-hosted font must fail one.
- A change to a design rule, business rule, or invariant updates the owning doc **in the
  same commit**.
- **`docs/` documents only what is true today.** Agreed-but-unbuilt design lives in the
  private `budgetoid-specs` repository (SRS documents at the root, `product-research/` for
  rationale) and moves into `docs/` the day it ships. Never state an unbuilt capability in
  the present tense. The hardening backlog lives there too — a public list of unclosed
  weaknesses is a map.
- **`docs/design/**` is the one carve-out: it is a specification, not a report.** The book
  states what a surface *shall* look like — `components.md` says so itself ("if a component
  isn't specified here, specify it here before building it") — so a chapter describing an
  unbuilt control is doing its job, not going stale. What it may never do is describe the
  built thing wrongly: where what ships departs from the book, the chapter says so in the
  same commit and names the gap as work. Do not "clean up" the target out of the book to
  make it match the code; correct the code, or record the departure.

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
- **There is no production environment right now, and `budgetoid.app` is the name the next one
  answers on.** `rg-budgetoid-prod` does not exist; only `rg-budgetoid-msi` and its pipeline
  identity survive a teardown, by design. The repository already names the target — `budgetoid.app`
  for the frontend, `api.budgetoid.app` for the API — and **`passkey-relying-party-id` is frozen at
  `budgetoid.app`**, never a generated `*.azurestaticapps.net` hostname. That value is hashed into
  every passkey an authenticator stores, so a later change invalidates all of them with no
  migration; it is decided here rather than at cutover precisely so no account can be created under
  a throwaway name. Binding the DNS is part of bringing the environment up, not a follow-up.
- **The baseline migration is frozen, but its rebaseline window is open.** Schema changes are
  additive migrations and the `migrations-guard` CI job fails any edit to an existing migration
  file — except while `REBASELINE_WINDOW` in that job is `open`, which it is, because the
  production database holds no data. Regenerating the baseline under that window means resetting
  production's `__EFMigrationsHistory` in the same deploy. Read
  [migrations](docs/engineering/migrations.md) before touching the folder.
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
- Frontend: run `npm run build && npm test`, `npm run lint`, and `npm run format` before
  committing — the build must come first, one spec reads its output
- Commits follow Conventional Commits (`feat:`, `fix:`, `ci:`, `chore:`, …)
- One logical change per commit
