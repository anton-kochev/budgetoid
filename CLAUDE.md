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

**The budget is the unit of tenancy.** `AuthenticateSessionHandler` resolves the session cookie into
a user id and default budget id on the scoped `CurrentUser`; `IBudgetContext` exposes the ambient
budget. **Exactly one path creates an account**: `POST /api/registration`, which creates nothing
without a passkey and a card of recovery codes, and writes ~30 rows in **one** `SaveChanges`. There
is no heal, and a resolved account with no budget throws. `RegisterAccountHandler` is the only code
that brings an account into existence — but **nothing in the compiler holds that**: a second
creating path is one line that would redden nothing, so it is held by review, not by a build error.
**An authenticated principal naming no account is not a state this pipeline can be in**: a cookie is
only ever issued over a session row, so the handler answers `NoResult` and the request is challenged
— a 401 indistinguishable from an anonymous one. See
[registration.md](docs/business-logic/registration.md). Read
[data isolation](docs/engineering/data-isolation.md) before touching budget-scoped queries.
Load-bearing rules, each explained there or in the linked decision:

- Budget-owned rows are isolated twice: PostgreSQL `budget_isolation` RLS policies enforce,
  EF `BudgetIsolation` query filters turn a foreign row into the API's 404/400. Neither is
  duplication — do not delete either. See [ADR 0005](docs/decisions/0005-isolate-budget-owned-rows-with-row-level-security.md).
- `users`, `budgets`, `sessions` and `passkey_signature_counters` are policed on the **user** by
  `user_isolation`, not on a budget. `credentials`, `passkey_public_keys`, `webauthn_challenges`,
  `recovery_code_hashes` and `session_tokens` are exempt, each because it is read *before* the request
  has an identity a policy could be keyed on — so the credential lookup must never join `users`.
  **An exempt table scopes nothing**: only the discovery lookup may omit an owner filter, and every
  other **read** of one must carry its own `where user_id = …`. Deletes are the exception to that
  sentence and not to the rule — EF emits each `DELETE` by primary key, so what scopes one is the
  owner-bearing read that produced its entity in the same transaction, which is why a delete takes
  the loaded entity rather than an id. What holds each exemption to its reason is its **pinned
  column set**, not the grant matrix — a write-once secret passes any append-only rule — so a new
  column there means *move the column*, never widen the pin. See
  [ADR 0011](docs/decisions/0011-police-the-user-owned-tables.md),
  [ADR 0012](docs/decisions/0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md)
  and [ADR 0014](docs/decisions/0014-scope-the-credential-delete-in-the-application.md).
- **The passkey assertion path publishes the identity only after the signature verifies, and opens
  its transaction only after that.** A transaction opened earlier configures the connection while
  `app.current_user_id` is still empty, so every policed statement inside it fails with `22P02`.
- **A request authenticates from the `__Host-budgetoid-session` cookie in three steps whose order is
  the security property**, and `AuthenticateSessionHandler` owns all three: read the **exempt**
  `session_tokens` row by the token's digest on a connection naming nobody, *then* `ResolveUser`,
  *then* read the **policed** `sessions` row. Reversed, the policy meets `''::uuid` and every request
  in the product answers `22P02`; **no transaction may wrap any of it**, for the same reason. The API
  decodes the cookie and looks nothing up — `CompositionBoundaryTests` holds that line. `ResolveBudget`
  comes last because `ResolveUser` clears it. Four further rules a reader will try to simplify: every
  request but `GET /health` must carry a non-empty `X-Budgetoid-Client` header or answer **403** — the
  CSRF control, unchecked by value on purpose and covering the **anonymous** routes because those are
  the ones that set a cookie; an **ended** session authenticates on exactly one route, the one that
  ends sessions, marked with `AcceptsEndedSessionAttribute` and reaching no ambient budget even there;
  the default scheme is the cookie's, named **explicitly** by the fallback policy so it is readable off
  the route table; and **the session and its token are written as a pair** — `ISessionRepository.AddAsync`
  has no overload taking a session alone, and `IRegistrationRepository.RegisterAsync`, the second
  writer, carries both. Each of the **four** establishing paths mints a handle and sets the cookie; on
  `POST /api/me/recovery-codes` that is only the branch whose sweep ended a live session, because **a
  first issue sets no cookie**. **No response body carries the handle or a session id**, held by a
  census over every type a route returns.
  `session_tokens` is one of the tables `GenerateRecoveryCodesHandler`'s never-materialise rule binds,
  and the only one where it fails loudly (`42501`) — but only when a read materialises entities, so
  turning one into a projection makes the trap stop biting without making the rule stop applying. See
  [sessions.md](docs/business-logic/sessions.md) and
  [ADR 0019](docs/decisions/0019-authenticate-a-request-from-a-first-party-session-cookie.md).
- **A session opened by a federated credential reaches one route, and the gate is opt-out.**
  `FullSessionRequirement` rides the **fallback** authorization policy beside `RequireAuthenticatedUser`,
  so it covers every route declaring no policy of its own — everything outside the anonymous surface
  and the registration group, which declares one naming the provider scheme and nothing else —
  and a route escapes with `AllowsLockedSessionAttribute`. The opted-out set is exactly
  `POST /api/me/session/revocation`, read whole off the route table. **Opt-out, unlike its
  neighbour**: `AcceptsEndedSession` is opt-in because a forgotten marker there refuses something and
  is loud, while a forgotten opt-*in* here would hand budget content to a locked session with nothing
  going red — polarity follows from which mistake is audible. It sits in the application because no
  declarative database rule reaches it, the
  [ADR 0002](docs/decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) statement the rule owes.
  Two things a reader will simplify away: the kind claim is judged by a **round trip** (parse, then
  compare the text ordinally against what the parsed member renders as) because `Enum.TryParse` admits
  `"full"` case-insensitively and `"1"` under every overload; and `SessionKindReach.ReadsBudgetContent`
  is the **one** definition that `Session.ReadsBudgetContent` calls, so a kind added later cannot be
  admitted by one caller and refused by the other. Nothing establishes a locked session today, so the
  gate is unreachable from any live route and is held entirely by tests that seed one through the
  database.
- **Registration is one act and one transaction, and the account id is derived rather than chosen.**
  Two routes under `/api/registration`, authenticated by the **provider scheme and nothing else** —
  not anonymous, because an account may not exist without a completed provider exchange. **That policy
  is the only reason `JwtBearer` is still registered.** The `sub`/`email` and `email_verified` gates
  ride an `IEndpointFilter`, `RegistrationClaimGate`, on that group — **not** in the Application ring,
  because judging `email_verified` there needs either a `ClaimsPrincipal` in that project or a member
  on the command for the answer to land in, and the ownership rules refuse both. The finish leg writes
  ~30 rows across nine relations in **one `SaveChanges`**, and **no `ITransactionalExecutor` may wrap
  it**, for the `22P02` reason the handler states inline. Five more a reader will simplify:
  **`RegistrationAccountId.For(challenge)` is called by both legs over the same nonce** — `user.id` in
  the creation options is the WebAuthn user handle, compared byte-for-byte against `users.id`, so a
  disagreement refuses every later sign-in from that authenticator, permanently and with no error
  naming the cause — and it is derived **after** `ConsumeAsync`, or the account id is one the caller
  chose. The ladder's order is the rule (consume before verify, prf gate after verification,
  key-custody payload after the prf gate). The passkey's factor id must **differ from all ten codes'**.
  The session opens over the **passkey** credential, never the recovery-codes one — invisible to any
  constraint, decisive for a later revocation sweep. And **the options leg refuses a subject that
  already holds an account, above its own `IssueAsync`**, because the browser mints the passkey the
  instant the device agrees and a later refusal costs a credential nobody can delete; it closes the
  common case and not the class, so the finish leg keeps both checks for the race.
  See [registration.md](docs/business-logic/registration.md) and
  [ADR 0021](docs/decisions/0021-make-registration-one-consented-act-and-derive-the-account-id-from-its-own-challenge.md).
- EF escape hatches (`IgnoreQueryFilters`, `FromSql*`, `ExecuteSql*`, `Find`/`FindAsync`,
  `ExecuteUpdate`/`ExecuteDelete`) are compile errors via `BudgetoidApp/BannedSymbols.txt`.
- **A credential type has exactly one spelling and it is written out, never derived from the member
  name.** `Domain/Users/CredentialTypeSpelling.cs` owns it; the `type` column, every copy of that
  column, `CK_credentials_type` and the `type` member of `GET /api/me/credentials` all read that one
  definition, which is what makes "the wire agrees with the column" a fact rather than a coincidence —
  camel-case would produce `recoveryCodes` for a column holding `recovery_codes`. Do not answer that
  with a global `JsonNamingPolicy`: it would silently respell every other enum the API emits. Which
  spellings a *table* accepts is a separate rule each configuration keeps on top — folding it into the
  shared type would impose it on everything reading it. `CredentialType` is ordered so `default` is
  `Federated`, whose shape the database refuses; `SessionKind` made the same choice for the same
  reason.
- **The dependency direction is pinned, not described.** MSBuild's cycle detection already refuses
  the outward `ProjectReference` between rings; what it cannot catch is the outward edge that closes
  no loop — a package on `Application`, a `FrameworkReference` or `Sdk` attribute on `Domain`,
  `UnitTests` reaching `Api`, a whole new project. `ProjectReferenceGraphTests` renders every csproj
  declaration as a row and pins the set, dropping package *versions* so a routine bump never reddens
  it. Two guards sit beside it for rules the graph provably cannot express:
  `CompositionBoundaryTests` (Api may **compose** Infrastructure, never **consume** it — no route
  delegate takes a persistence port) and `OwnershipKeyImmutabilityTests` (every `UserId` and
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
  the database's cascade, and the role *has* `DELETE` here, so it would succeed **silently**.
  See [recovery-codes.md](docs/business-logic/recovery-codes.md) and
  ADRs [0015](docs/decisions/0015-mint-recovery-codes-on-the-client-and-store-only-a-hash-of-a-verifier.md),
  [0016](docs/decisions/0016-give-recovery-code-hashes-their-own-exempt-table.md),
  [0017](docs/decisions/0017-consume-a-recovery-code-by-deleting-its-row.md).
- **An account owns one content key and one index key, and every recovery factor stores its own
  wrapped copy of both.** Deriving either from a credential would give a second passkey a second index
  key — two blind index values for one name, and a uniqueness constraint that appears to work while
  enforcing nothing. `wrapped_account_keys` is **policed** by `user_isolation`, not exempt: it is read
  after the request has an identity, and it is keyed on **`factor_id`** — a **factor is not a
  credential**: a passkey is one factor, a set of recovery codes is **ten**, because each code derives
  its own key-encryption key and a person redeems whichever one they still have. Every path that
  creates a factor **requires `factorId` and both envelopes, written in the credential's own
  `SaveChanges`** — registration writes **eleven** rows at once. That property, not a count, is the
  rule: the two `NOT NULL` columns make "a row carries both keys or neither" a schema fact, but
  **"every factor has a row" is not one**, so a path that skipped them would create a keyless factor
  and redden nothing. Four more a reader will try to "fix":
  `factor_id` is **client-minted and not `credentials.id`**, arriving in **one spelling** that both
  write paths check with `CanonicalFactorId.TryParse` — an **ordinal** compare against
  `parsed.ToString("D")`, because `Guid.TryParseExact(…, "D")` admits upper- and mixed-case hex and
  trims first; the value is the associated data both envelopes were sealed with, so a mismatch stops
  **both** opening, permanently, with no error naming the cause, and the contract is cross-client.
  The role holds **no `UPDATE` and no `DELETE`** here, so a replaced set's row leaves by the cascade
  and `GenerateRecoveryCodesHandler`'s never-materialise rule binds a second table that fails
  **loudly** (`42501`). The wrapped keys are **not** verifiable PRF evidence — the server cannot tell
  a key-encryption key derived through PRF from one derived out of a constant. And the client crypto
  in `+core/security/account-keys.ts` keeps the key-encryption keys as **locals that never touch the
  service instance** and zero-fills every copy where it is consumed, each wipe pinned by a spec that
  asserts the buffer was non-zero at the moment of the call. The *unwrapping* is still uncalled,
  because no route hands `wrapped_account_keys` back. See
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
  quiet success. It writes no row and logs no identifier.
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
- **The four security headers are written from `Response.OnStarting`, never before `await next(…)`** —
  the exception handler's `Response.Clear()` discards a direct write, so a 500 would ship bare. The
  registration sits **above** `FirstPartyRequestMiddleware`, which answers 403 without calling `next`;
  ordering against `UseExceptionHandler()` decides nothing. `SecurityHeaderTests` holds both. See
  [security headers](docs/engineering/security-headers.md).

## Frontend Architecture

- Angular 21 standalone components (no NgModules)
- Slice-1 transaction state uses an Angular signal-based service. **NgRx is registered and empty**:
  `provideStore()` and `provideEffects()` take no arguments and no action, reducer, effect or
  selector remains; `@app-state/*` resolves to nothing. It is kept as the store the next slice reaches
  for, and because removing it would take `devtools.providers.ts`, the `angular.json`
  `fileReplacements` entry and `no-devtools.spec.ts` with it.
- `+core/` — API services, guards, interceptors, app-wide providers
- `+shared/` — shared components and utilities
- Path aliases: `@app-core/*`, `@app-shared/*`, `@app-state/*` (baseUrl is `./src`)
- Auth: Google OAuth via `angular-oauth2-oidc`
- UI: Angular Material + Angular CDK, styled with SCSS
- **Two interceptors, one predicate.** `apiCredentialsInterceptor` answers "is this going to our
  API?" **once** and attaches three things: `withCredentials: true`, the `X-Budgetoid-Client`
  header, and — **on the two registration routes only** — the provider's bearer.
  `sessionExpiryInterceptor` reads the same question on the way back. The predicate is **exported
  from the first and imported by the second** — never restated — because two copies of "is this our
  API?" drift, and the drift is silent in both directions. It compares **origins**:
  `url.startsWith(apiBaseUrl)` admits `https://api.budgetoid.app.attacker.example`, a name anybody
  can register, and hands it this app's credentials. **The bearer is narrowed by origin first and
  path second, in that order** — a route test on the path alone would hand the provider's token to
  `…attacker.example/api/registration`. It reaches those two routes and nowhere else: a credential
  travelling further than it is needed is the defect whether or not anything reads it. The cookie and
  the header are unconditional. An empty `apiBaseUrl` classifies nothing as this API. **Neither
  interceptor's own spec can see whether it is registered** — both call their function directly — so
  `src/app/app.config.spec.ts` carries one pin per interceptor, each reddening on its own half only.
  Without them either registration is deletable with a green suite.
  See [sessions.md](docs/business-logic/sessions.md).
- **The client learns who it is by asking, once, before the first route activates.** The session
  cookie is `HttpOnly`, so nothing in the browser can read it. `SessionService.probe()` runs in
  the `APP_INITIALIZER` **after** `config.load()` — you cannot ask an address you have not read
  yet — and is **awaited**, which is what keeps every guard synchronous and means no guard ever
  runs against `'unknown'`. `BaseApiService` resolves `apiBaseUrl` **per request**, never copying it
  into a field, so no service can hold a base that was empty when it was built. `status` is
  **four-valued and the fourth is the one a reader will collapse**: 401/403 → `anonymous`, but a
  network failure, a 500 or a timeout → `unreachable`, and **both guards admit `unreachable` and
  `unknown`** — only `anonymous` may bounce anybody. Reading silence as a refusal throws a person
  holding a good session out of their own account over one blinked request.
  `sessionExpiryInterceptor` is the **single owner of "the session ended"**: it acts on **401 only**
  (403 is the CSRF and locked-session refusal, answered to a browser whose session is intact), always
  re-throws, and skips any request carrying the `EXPECTS_UNAUTHENTICATED` context token — a token,
  not a URL list, because a URL list is a second definition of the anonymous surface kept
  client-side. **The probe carries it and `getMe()` does not, on one route**: the probe asks whether
  there is a session and a 401 is its answer, while a 401 on the Settings read is a session that
  ended. Unmarked, the probe navigates every anonymous cold load to `/welcome` from inside the
  initializer, making `/register` unreachable by URL. The token lives in
  `expects-unauthenticated.token.ts` rather than in the interceptor that reads it, or it closes a
  three-module cycle the moment the probe sets it. See
  [sessions.md](docs/business-logic/sessions.md).
- **Nothing loads from another origin** — no CDN script, stylesheet, typeface, icon, or
  image, and no identity-provider profile picture. Typefaces live in `public/fonts/`.
  `src/no-external-origins.spec.ts` reads the production bundle, so `npm test` needs a
  `npm run build` first. See [no third-party origins](docs/engineering/no-third-party-origins.md).
- **The production build registers no state-inspection provider.** `provideStoreDevtools` lives
  in `src/app/devtools.providers.ts`, which the production `fileReplacements` in `angular.json`
  swaps for an empty module — a runtime `isDevMode()` branch leaves the code in the bundle.
  `src/no-devtools.spec.ts` reads the bundle and fails if it comes back.
- **The browser is told what the app may load, and `script-src 'self'` is literal.** The
  `Content-Security-Policy`, `Strict-Transport-Security`, `Referrer-Policy` and
  `X-Content-Type-Options` ship in
  `globalHeaders` of `public/staticwebapp.config.json` — never on a route rule, which Azure skips
  for every `navigationFallback` rewrite, i.e. every deep link. Critical-CSS inlining is **off**
  (`"inlineCritical": false`, which is the only reason the `optimization` object exists — its other
  keys restate defaults) because it emits an
  inline `<style>`, an `onload=` handler and a `<noscript>` twin; the theme pre-paint therefore
  lives in `public/theme-prepaint.js`, loaded with no `defer` and no `type="module"`.
  `src/security-headers.spec.ts` reads the emitted config and `index.html`. See
  [security headers](docs/engineering/security-headers.md) and
  [ADR 0020](docs/decisions/0020-trade-inlined-critical-css-for-a-literal-script-src-self.md).
- **No button shows a focus ring unless `src/styles.scss` puts one there** — Material sets
  `outline: none` on `.mdc-button`, so one global `:focus-visible` block with element selectors,
  never `:where()`, has to out-specify it. See [accessibility.md](docs/design/accessibility.md).
- **The test runner's time zone is pinned** to `Pacific/Kiritimati`, and **`npm test` needs a prior
  `npm run build`** because several specs read the emitted bundle. See
  [frontend testing](docs/engineering/frontend-testing.md).
- **`/app/settings` is reached from the shell navigation**, `ShellComponent`, a layout on the `app`
  route — **which routes carry a bar is a fact about the route table**, so `/welcome` and `/register`
  cannot draw one however a session status reads. Working today: the email from `GET /api/me`, the
  export, **Sign out**, the credential list from `GET /api/me/credentials` and the recovery-code
  count. Present and **disabled**: erasure, passkey registration, code generation. Rules the chapters
  argue and a reader will undo:
  **Home and Add are specified and not built**; the export writes the response bytes **unread**
  (`responseType: 'blob'`); the credential list shows **type and day only**, never an id or provider
  subject; **two different reasons hold the disabled controls off and the screen says both** — do not
  paste one sentence over all of them, and do not "correct" the erasure copy into saying the browser
  is incapable; **Revoke renders only on a row something can revoke**, carried per kind in a map
  declared exhaustive over `CredentialKind`; the day is formatted by `credential-registration-date.ts`
  and pluralisation is three template branches, never `DatePipe` or `I18nPluralPipe`, because nothing
  provides `LOCALE_ID`; the count has **six states and `null` is never `0`**; and
  `+core/security/recovery-codes.ts` has **one caller, registration**, its spec being the only place
  that can check the 128-bit entropy rule. Key rotation and email change render nothing today — do not
  "complete" the screen. See [export.md](docs/business-logic/export.md),
  [erasure.md](docs/business-logic/erasure.md),
  [recovery-codes.md](docs/business-logic/recovery-codes.md) and the credential-list and
  recovery-codes chapters in [components.md](docs/design/components.md).
- **The browser runs both halves of the front door.** `+core/security/webauthn-encoding.ts`
  is pure translation between the API's base64url JSON and the browser's `BufferSource` shapes, over
  the **strict** decoder in `base64url.ts` — a second, lenient decoder must never appear beside it.
  `+core/security/webauthn-ceremony.service.ts` is the injectable seam, for the reason
  `FileDownloadService` is one: the platform it calls does not exist under the test runner. Six rules
  it carries, each silent when broken. **The PRF output never crosses the module boundary** — both
  legs derive through `keyEncryptionKeyFromPasskey`, return a non-extractable `CryptoKey` and
  zero-fill the bytes. **The registration payload projects** `getClientExtensionResults()` into a
  fresh `{prf:{enabled}}` — never forwards, filters or spreads it, because that object carries the PRF
  output itself. **The claim's *value* is what the client established, not what `create()` reported**;
  the server gates on that word, so reporting `create()`'s alone refuses every authenticator that
  derives on the first assertion. **`create()` returning no PRF output is not a refusal** — one
  *local* `get()` follows, carrying `allowCredentials` (WebAuthn throws `NotSupportedError` without
  it) and **discarded, never sent** — but `enabled: false` **is** a refusal, immediately; absent is
  not `false`. **`isArrayBuffer` is a brand check, never `instanceof`** — a narrowing that silently
  goes false derives the key from zero bytes. And **`assertPasskey` asks for PRF too, and the sign-in
  screen lets the key go**: holding the account's master key with no use for it is the defect. See
  [passkeys.md](docs/business-logic/passkeys.md) and
  [account-keys.md](docs/business-logic/account-keys.md).
- **The recovery-code hand-off is the one screen that shows a secret, and it still mints and posts
  nothing.** `register/steps/codes-step.component` takes ten codes through an `input()`, shows them
  once and raises an output; `RegisterService` is what mints them and what posts. It is reachable as
  the third step of `/register` and by **no URL of its own**. Four rules, each silent when broken.
  **The codes never enter a live region** — a `role="status"` holding a list narrates ten secrets as
  events. **What is saved or copied is the grouped codes and nothing else** — not the printed 1-based
  index beside them, and no header naming the product inside a file of secrets. **The acknowledgement
  gate is in the click handler, not only in the attribute** — Material's click-halt is anchors only,
  so on a `<button>` `disabledInteractive` leaves DOM `disabled` false and the click arrives; an
  attribute-only gate creates an account for somebody who acknowledged nothing. And **the consequence
  is its own block, never the checkbox's label**, which would announce a paragraph as the control's
  name on every focus. Copy is **Ghost** beside an Outline Save, deliberately quieter, because the
  clipboard is the worst storage on the device. See the recovery-code hand-off chapter in
  [components.md](docs/design/components.md), "A secret shown once" in
  [voice.md](docs/design/voice.md), and
  [recovery-codes.md](docs/business-logic/recovery-codes.md).
- **Registration is one screen, one route and one *creating* request** — the options leg, asked from
  either of two presses, is not the request the headline counts. `/register` carries `guestGuard`,
  declares **no `children`** (the step is a signal in `register.component.ts`), and provides
  `RegisterService` **on the component** — custody, not lifetime: the account keys, the eleven
  key-encryption keys and the ten codes die with the screen. Rules the chapters argue and a reader
  will undo:
  **the introduction's `Continue` is the options request** and the step moves only on an answer; the
  challenge is **taken once** and dropped by `restart`, so a 409 stays reachable from all three steps
  and each carries a conflict sentence; a browser that cannot run a ceremony asks for **no** challenge
  and advances to the step holding `unsupported`; **a provider token is judged by validity, never by
  presence**, so a dead one publishes `provider-token-refused` while a 403 stays `start-failed`;
  **the device agrees before anything is minted**; **one code's four submitted members are built in one
  scope from one code**, never zipped from parallel arrays; **there is no retry of the POST, only a
  restart**; **`refused`/`conflict` and `unknown` are never collapsed**, and **a 409 has two readings
  told apart by what the previous request *ended as*** — never by whether a button was pressed —
  decided by `mayHaveCreatedAccount`; **no `canDeactivate`, no `beforeunload`**; and both requests
  carry `EXPECTS_UNAUTHENTICATED`, or a 401 mid-flow destroys ten codes already written down.
  **Nothing schedules a silent refresh, and that omission is the rule** — a hidden iframe on the
  provider for the life of every tab, to keep alive a credential used once. The provider `redirectUri`
  points at `/register`, and **the matching entry in the Google Cloud console is part of the change no
  test can catch**. See
  [registration.md](docs/business-logic/registration.md) and the Registration chapter in
  [components.md](docs/design/components.md).
- **Welcome carries two actions and exactly one of them is Primary.** `Create account` routes to
  `/register`; `Sign in with a passkey` is an Outline running the assertion through a
  component-provided `SignInService`. **No provider button here** — the provider is contacted once, on
  the registration screen's introduction step. There is **no `/sign-in` route**: one control, no
  fields, because the authenticator is the form. Two rules a reader will break. **The screen says one
  thing however a sign-in was refused** — the server answers unknown credential, bad signature,
  untrusted origin, spent challenge, counter regression and user-handle mismatch with one
  byte-identical 401 on purpose, so a client that varied its sentence would rebuild the
  credential-enumeration oracle the server refuses to be. And **`refused` is not `unknown`**: a
  refusal means this passkey does not
  work here and the person should try another way in, while an unreachable server means try again in a
  minute. Both assertion legs are **anonymous** and both carry `EXPECTS_UNAUTHENTICATED`, or a 401 —
  the route's own verdict — is read as a session that lapsed. See
  [passkeys.md](docs/business-logic/passkeys.md) and the welcome-screen chapter in
  [components.md](docs/design/components.md).

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
