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

Clean Architecture with CQRS. Commands/queries live under `Application/Transactions/*` and are
injected as plain handlers (`ICommandHandler`/`IQueryHandler`); no MediatR dispatcher until
decorators are needed. The API is ASP.NET Core minimal API on Azure Container Apps. Auth is live
Google OAuth.

**The budget is the unit of tenancy**, and **exactly one path creates an account** —
`POST /api/registration`, which writes ~30 rows in one `SaveChanges` and creates nothing without a
passkey and a card of recovery codes. Nothing in the compiler holds that: a second creating path is
one line that would redden nothing. Read [registration.md](docs/business-logic/registration.md) and
[data isolation](docs/engineering/data-isolation.md) before touching budget-scoped queries.

Load-bearing rules. Each links the doc that argues it — **read that doc before changing the rule**,
because every one of these is something a reader will otherwise simplify away.

- **Budget-owned rows are isolated twice** — PostgreSQL `budget_isolation` RLS policies enforce, EF
  `BudgetIsolation` query filters turn a foreign row into the API's 404/400. Not duplication; delete
  neither. [ADR 0005](docs/decisions/0005-isolate-budget-owned-rows-with-row-level-security.md)
- **`users`, `budgets`, `sessions`, `passkey_signature_counters` are policed on the user**, not a
  budget. Six tables are exempt because each is read *before* the request has an identity — so the
  credential lookup must never join `users`. **An exempt table scopes nothing**: only the discovery
  lookup may omit an owner filter. Each exemption is held by its **pinned column set** — a new column
  there means *move the column*, never widen the pin.
  [ADR 0011](docs/decisions/0011-police-the-user-owned-tables.md),
  [ADR 0012](docs/decisions/0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md),
  [ADR 0014](docs/decisions/0014-scope-the-credential-delete-in-the-application.md)
- **The passkey assertion path publishes the identity only after the signature verifies, and opens
  its transaction only after that** — earlier, every policed statement inside it fails with `22P02`.
- **A request authenticates from `__Host-budgetoid-session` in three steps whose order is the
  security property**, all owned by `AuthenticateSessionHandler`, and **no transaction may wrap any
  of it**. Reversed, every request in the product answers `22P02`. Four rules beside it — the
  `X-Budgetoid-Client` 403, the single ended-session route, the explicitly-named default scheme, and
  the session-and-token pair — are argued in
  [sessions.md](docs/business-logic/sessions.md) and
  [ADR 0019](docs/decisions/0019-authenticate-a-request-from-a-first-party-session-cookie.md).
- **A session opened by a federated credential reaches one route, and the gate is opt-out.**
  `FullSessionRequirement` rides the fallback policy; a route escapes with
  `AllowsLockedSessionAttribute`, and the opted-out set is exactly `POST /api/me/session/revocation`.
  Polarity follows from which mistake is audible. [sessions.md](docs/business-logic/sessions.md)
- **Registration is one act and one transaction, and the account id is derived rather than chosen.**
  Two routes authenticated by the provider scheme and nothing else — **the only reason `JwtBearer` is
  still registered**. No `ITransactionalExecutor` may wrap the finish leg. The ladder's order is the
  rule. [registration.md](docs/business-logic/registration.md),
  [ADR 0021](docs/decisions/0021-make-registration-one-consented-act-and-derive-the-account-id-from-its-own-challenge.md)
- **EF escape hatches are compile errors** via `BudgetoidApp/BannedSymbols.txt` —
  `IgnoreQueryFilters`, `FromSql*`, `ExecuteSql*`, `Find`/`FindAsync`, `ExecuteUpdate`/`ExecuteDelete`.
- **A credential type has exactly one spelling, written out, never derived from the member name.**
  `Domain/Users/CredentialTypeSpelling.cs` owns it. Do not answer that with a global
  `JsonNamingPolicy`. [users-and-ownership.md](docs/business-logic/users-and-ownership.md)
- **The dependency direction is pinned, not described.** `ProjectReferenceGraphTests` pins every
  csproj edge as a row, including the solution's one `InternalsVisibleTo`;
  `CompositionBoundaryTests` and `OwnershipKeyImmutabilityTests` hold what the graph cannot express.
  Read [dependency direction](docs/engineering/dependency-direction.md) before adding a project or a
  reference of any kind.
- **Every column carries exactly one classification** — *narrative*, *arithmetic* or *excluded* —
  and the words are about what the product owes the person, not what the column holds. `DataInventory`
  names all 93; a unit-tier gate fails on one nobody classified. **Narrative is derived** from the
  `NarrativeField` properties, so a column cannot be re-classified to green a coverage test. The
  schema is enumerated in exactly one place (`MappedSchema`). It replaces none of the ten censuses
  beside it. [data inventory](docs/engineering/data-inventory.md),
  [ADR 0024](docs/decisions/0024-key-the-data-inventory-on-the-model-and-reconcile-it-against-the-catalog.md)
- **A new tenant-owned table needs a grant *and* a policy** — `budget_isolation` for `budget_id`,
  `user_isolation` for `user_id`. Grants fail closed (`42501`), RLS fails open. A table carrying
  neither column fails both.
- **A recovery code never reaches the server.** The client mints it and sends only a verifier;
  `recovery_code_hashes` stores `SHA-256(verifier)`. The server therefore **cannot** check the
  128-bit entropy rule, and a code is consumed by **deleting** its row, never by stamping it.
  [recovery-codes.md](docs/business-logic/recovery-codes.md),
  ADRs [0015](docs/decisions/0015-mint-recovery-codes-on-the-client-and-store-only-a-hash-of-a-verifier.md),
  [0016](docs/decisions/0016-give-recovery-code-hashes-their-own-exempt-table.md),
  [0017](docs/decisions/0017-consume-a-recovery-code-by-deleting-its-row.md)
- **An account owns one content key and one index key, and every recovery factor stores its own
  wrapped copy of both.** `wrapped_account_keys` is policed, keyed on **`factor_id`** — a factor is
  not a credential, so a set of recovery codes is ten. Every path creating a factor requires
  `factorId` and both envelopes in the credential's own `SaveChanges`; **"every factor has a row" is
  not a schema fact**, so a path that skipped them would redden nothing.
  `GET /api/me/account-keys` is keyed on the **account**, never on a credential — narrowing it was
  tried and was wrong. [account-keys.md](docs/business-logic/account-keys.md),
  [ADR 0018](docs/decisions/0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md)
- **One AEAD envelope serves both consumers, and its associated data is never carried inside it.**
  `version(1) ‖ nonce(12) ‖ ciphertext ‖ tag(16)`, unpadded base64url; 29 bytes is a **floor**.
  **All eight narrative columns are typed for an envelope, four carry a blind index, and the set is
  closed.** `NarrativeField` has no constructor taking a `string` — **that absent member, not a
  test, is what holds "no narrative value is ever server-readable"**. The blind index normalizes
  trim → NFKC → **full case fold** → UTF-8 against a table this repository ships; do not relax any
  step to make a later screen easier. Sealing took a *capability* away on `payees.name` and
  surrendered uniqueness on `budgets.name`; both are decisions, not gaps.
  [ciphertext-envelope.md](docs/business-logic/ciphertext-envelope.md),
  [ADR 0022](docs/decisions/0022-mint-narrative-row-identifiers-on-the-client.md)
- **The schema carries no remnant of an erasure and the route table offers no way back.** Three
  gates each hold a different half. Read [erasure.md](docs/business-logic/erasure.md) before
  widening any.
- **The export refuses rather than truncates** — it throws unless the owned set is *exactly* the
  ambient budget, **set equality in both directions**. Do not simplify to `Count > 1`.
  [export.md](docs/business-logic/export.md)
- `SessionContextInterceptor` must stay a **connection-opened** interceptor, and
  `No Reset On Close=true` / `Multiplexing=true` are forbidden in any connection string.
  [ADR 0008](docs/decisions/0008-read-the-ambient-budget-inside-the-policy.md)
- The app connects as `budgetoid_app`; the elevated `budgetoid-admin` is read only by the
  Development startup block. Migrations never run on the app role.
  [ADR 0004](docs/decisions/0004-connect-as-a-least-privilege-role.md)
- **Immutable columns are enforced by omission from a `GRANT UPDATE` column list** — never by
  `REVOKE`, never widened to table-wide. Grants and policies live in
  `Infrastructure/Persistence/Provisioning/app-role-grants.sql`, never in a migration.
- **In production the app role has no password** — the missing `Password=` is what makes Aspire
  fetch an Entra token for the API's managed identity. Do not "complete" it.
  [ADR 0007](docs/decisions/0007-authenticate-to-postgres-with-managed-identity.md)
- **The four security headers are written from `Response.OnStarting`, never before `await next(…)`**
  — the exception handler's `Response.Clear()` discards a direct write, so a 500 would ship bare.
  [security headers](docs/engineering/security-headers.md)

## Frontend Architecture

- Angular 21 standalone components (no NgModules); `inject()` over constructor DI; OnPush.
- `+core/` — API services, guards, interceptors, app-wide providers. `+shared/` — shared components
  and utilities. Path aliases `@app-core/*`, `@app-shared/*`, `@app-state/*` (baseUrl is `./src`).
- Auth: Google OAuth via `angular-oauth2-oidc`. UI: Angular Material + Angular CDK, SCSS.
- Slice-1 transaction state uses an Angular signal-based service. **NgRx is registered and empty**:
  `provideStore()`/`provideEffects()` take no arguments and `@app-state/*` resolves to nothing. It is
  kept as the store the next slice reaches for, and because removing it would take
  `devtools.providers.ts`, the `angular.json` `fileReplacements` entry and `no-devtools.spec.ts`.

Load-bearing rules. Each links the doc that argues it — **read that doc before changing the rule**.

- **Two interceptors, one predicate.** `apiCredentialsInterceptor` answers "is this going to our
  API?" once and attaches `withCredentials`, `X-Budgetoid-Client`, and — **on the two registration
  routes only** — the provider's bearer. `sessionExpiryInterceptor` imports that same predicate,
  never restates it. It compares **origins**, not `startsWith`, and the bearer is narrowed by origin
  first and path second. Neither interceptor's own spec can see whether it is registered, so
  `app.config.spec.ts` carries one pin per interceptor.
  [sessions.md](docs/business-logic/sessions.md)
- **The client learns who it is by asking, once, before the first route activates.**
  `SessionService.probe()` runs in the `APP_INITIALIZER` after `config.load()` and is **awaited**,
  which is what keeps every guard synchronous. `status` is **four-valued**: a network failure, a 500
  or a timeout is `unreachable`, and **both guards admit `unreachable` and `unknown`** — only
  `anonymous` may bounce anybody. `sessionExpiryInterceptor` is the single owner of "the session
  ended", acts on **401 only**, and skips requests carrying `EXPECTS_UNAUTHENTICATED`.
  [sessions.md](docs/business-logic/sessions.md)
- **Nothing loads from another origin** — no CDN script, stylesheet, typeface, icon, image, or
  identity-provider picture. `src/no-external-origins.spec.ts` reads the production bundle, so
  `npm test` needs a `npm run build` first.
  [no third-party origins](docs/engineering/no-third-party-origins.md)
- **The production build registers no state-inspection provider.** `provideStoreDevtools` lives in
  `src/app/devtools.providers.ts`, which production `fileReplacements` swaps for an empty module — a
  runtime `isDevMode()` branch would leave the code in the bundle. `src/no-devtools.spec.ts` holds it.
- **The browser is told what the app may load, and `script-src 'self'` is literal.** The four headers
  ship in `globalHeaders` of `public/staticwebapp.config.json`, **never on a route rule**, which
  Azure skips for every deep link. Critical-CSS inlining is **off**, which is why the theme pre-paint
  lives in `public/theme-prepaint.js`. [security headers](docs/engineering/security-headers.md),
  [ADR 0020](docs/decisions/0020-trade-inlined-critical-css-for-a-literal-script-src-self.md)
- **No button shows a focus ring unless `src/styles.scss` puts one there** — Material sets
  `outline: none` on `.mdc-button`, so one global `:focus-visible` block with element selectors,
  never `:where()`, has to out-specify it. [accessibility.md](docs/design/accessibility.md)
- **The test runner's time zone is pinned** to `Pacific/Kiritimati`, and **`npm test` needs a prior
  `npm run build`**. [frontend testing](docs/engineering/frontend-testing.md)
- **`/app/settings` is reached from the shell navigation**, a layout on the `app` route — so
  **which routes carry a bar is a fact about the route table**. **Three different reasons hold the
  disabled controls off across four sites and the screen says all three**; do not paste one sentence
  over all of them. Home and Add are specified and not built — do not "complete" the screen.
  [export.md](docs/business-logic/export.md), [erasure.md](docs/business-logic/erasure.md),
  [recovery-codes.md](docs/business-logic/recovery-codes.md),
  [components.md](docs/design/components.md)
- **The browser runs both halves of the front door.** `webauthn-encoding.ts` is pure translation over
  a **strict** decoder — a lenient one must never appear beside it. `webauthn-ceremony.service.ts`
  runs **three** ceremonies and sends two; the third mints its own challenge and spends neither nonce
  pool. **The PRF output never crosses the module boundary**, the registration payload **projects**
  `getClientExtensionResults()` rather than forwarding it, `create()` returning no PRF output is not
  a refusal but `enabled: false` is, and `isArrayBuffer` is a **brand check, never `instanceof`**.
  [passkeys.md](docs/business-logic/passkeys.md),
  [account-keys.md](docs/business-logic/account-keys.md)
- **The account's keys are held per tab by one root-provided service.**
  `AccountKeyCustodyService` tries **every** entry under its own `factorId`, never `entries[0]`, and
  keeps what opened on `#` fields with **no accessor, and there never may be one** — which is what
  forced the three codecs onto this class. **Nothing is persisted and nothing is shared across tabs**:
  a non-extractable `CryptoKey` is structured-cloneable, so IndexedDB and `BroadcastChannel` both
  *work* and both are refused. A page reload therefore locks the **account**, and the way back is
  Unlock on `/app/settings`. `'unopened'`, `'unreachable'` and `'unauthenticated'` are three
  different next steps for a person — collapsing any two sends somebody down a road that cannot help
  them. Clearing has **one owner**, `SessionService.ended()`, never an `effect()`.
  [account-keys.md](docs/business-logic/account-keys.md),
  [sessions.md](docs/business-logic/sessions.md)
- **The recovery-code hand-off is the one screen that shows a secret, and it still mints and posts
  nothing.** The codes never enter a live region; what is saved or copied is the grouped codes and
  nothing else; **the acknowledgement gate is in the click handler, not only in the attribute**; and
  the consequence is its own block, never the checkbox's label.
  [components.md](docs/design/components.md), "A secret shown once" in
  [voice.md](docs/design/voice.md),
  [recovery-codes.md](docs/business-logic/recovery-codes.md)
- **Registration is one screen, one route and one *creating* request.** `/register` declares no
  `children` and provides `RegisterService` **on the component** — custody, not lifetime. The one
  thing that outlives it is a **created** account's key pair, handed over by `adopt` on the 201.
  A 409 has two readings told apart by what the previous request *ended as*, never by whether a
  button was pressed. Both requests carry `EXPECTS_UNAUTHENTICATED`.
  [registration.md](docs/business-logic/registration.md),
  [components.md](docs/design/components.md)
- **Welcome carries two actions and exactly one of them is Primary**, and there is **no `/sign-in`
  route** — one control, no fields, because the authenticator is the form. **The screen says one
  thing however a sign-in was refused**, or a varying sentence would rebuild the
  credential-enumeration oracle the server refuses to be. `refused` is not `unknown`.
  [passkeys.md](docs/business-logic/passkeys.md), [components.md](docs/design/components.md)

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
  [data inventory](docs/engineering/data-inventory.md),
  [migrations](docs/engineering/migrations.md),
  [no third-party origins](docs/engineering/no-third-party-origins.md), and
  [security headers](docs/engineering/security-headers.md). Each names the tests that lock it:
  removing a `HasQueryFilter` line, a policy, a self-hosted font, or a directive from the
  shipped `Content-Security-Policy` must fail one.
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
