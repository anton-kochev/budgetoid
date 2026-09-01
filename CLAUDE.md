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
  `UnitTests` reaching `Api`, a whole new project, **and an assembly handing another its internals**.
  `ProjectReferenceGraphTests` renders every csproj declaration as a row and pins the set, dropping
  package *versions* so a routine bump never reddens it. It renders `InternalsVisibleTo` too —
  `Domain: internals Infrastructure`, one row per grant, the solution's only one, argued at the
  element itself. That row exists because the grant went in first and the guard could not see it:
  **the renderer knows the kinds it was taught, so a new kind of outward edge is invisible until
  somebody notices.** Two guards sit beside it for rules the graph provably cannot express:
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
  asserts the buffer was non-zero at the moment of the call. **`GET /api/me/account-keys` is the one
  route that hands these rows back, and it is keyed on the *account*, never on a credential** — every
  factor, so eleven for an account holding a passkey and a set of codes. It was narrowed to the
  session's credential once and that was **wrong**: `PasskeyReauthentication` looks a passkey up **by
  account** and the assertion options carry **no `allowCredentials`**, so the *authenticator* chooses
  which credential answers a ceremony and the client cannot know in advance which one. Redeem a code,
  then ask for a new set — gated on a fresh **passkey** assertion — and the narrow read returns the ten
  code envelopes to a browser holding the passkey's key: correct rows, a 200, and an account that will
  not open, with nothing on the server seeing it. The cost of widening is that a caller receives
  entries it cannot open; accepted, because the operator already holds every one of these rows and a
  factor's envelopes open **only** under a key-encryption key derived from that factor. The handler
  therefore takes **no `ISessionRepository`**, the query declares **no member**, and the route reads
  **no claim**. It states **`Cache-Control: no-store`** for itself — a direct write, never a second
  `Response.OnStarting` callback, which would displace `SecurityHeadersMiddleware`'s four headers.
  **An empty array, never a 404 — but no longer for the oracle reason**, which the widening retired:
  what keeps it is `AccountKeyCustodyService`, which reads `[]` as "present another factor" and a
  failed read as one of **two** other next steps — a 401 or a 403 as "sign in again", everything
  else as "try again in a minute". Four causes reach an empty answer and the fourth is a
  **keyless factor**, which is representable exactly because "every factor has a row" is not a schema
  fact. **There are two import
  doors, not one**: `importAesGcmKey` and `importHmacSha256Key`, because the index key is
  HMAC-SHA-256 and the platform refuses each key object in the other's role. The claim was never a
  count — it is that **every** import sits behind a door holding all five decisions (algorithm,
  width, usage list, non-extractability, the death of the bytes), which two doors keep and a
  hand-written import beside a caller destroys; `key-import-single-source.spec.ts` reads the source
  tree and pins the two owner files (`account-keys.ts`, `hkdf.ts`; three call sites). On the HMAC
  door the shared `requireAccountKeyWidth` is the **only** guard there is — measured, HMAC
  `importKey` accepts 1, 15, 31, 33 and 64 bytes and signs a full tag under every one, refusing only
  zero, while AES refuses everything but 16, 24 and 32 — so that one check is all that stands between
  a truncated key and a blind index that keys perfectly, never collides, and is wrong for the life of
  the account. See
  [account-keys.md](docs/business-logic/account-keys.md) and
  [ADR 0018](docs/decisions/0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md).
- **One AEAD envelope serves both consumers, and its associated data is never carried inside it.**
  `version(1) ‖ nonce(12) ‖ ciphertext ‖ tag(16)`, `0x01` for AES-256-GCM, unpadded base64url on the
  wire; twenty-nine bytes is a **floor**, not a width. `Domain/Security/CiphertextEnvelope` owns the
  framing and `Application/Security/CiphertextEnvelopeText` re-applies the caller's ceiling to the
  **decoded** length — the encoded bound is computed padded and overshoots by up to two characters,
  which only the wrapped-key path's exact width was catching. Because associated data is rebuilt
  from wherever a ciphertext was found, the wrapped-key grammar **folds** a UUID's spelling while
  the narrative grammar **refuses** one: folding defends against values arriving from elsewhere,
  emitting one spelling is a property of values this client mints, and neither is a mistake to
  correct into the other. The eight narrative table-and-column pairs are a **runtime list with the
  type derived from it**, so a ninth entry reddens two cases — a member unioned onto the derived
  type reddens nothing and is held by review. **The row id being version 7 is now two claims, not
  one**: what `mintNarrativeRowId` emits is pinned by its spec — the version nibble, the big-endian
  millisecond rendering under stubbed clocks, and `crypto.randomUUID` refused by name — while
  *which* minter a write path calls is still held by review, which is the half ADR 0022 meant.
  **They are pairs,
  not a cross product**: `transactions` is a table and `name` is a column and `transactions.name` is
  neither, so `refuseInvalidBinding` looks a binding up **as a pair** and never as two membership
  tests — a mapper taking its table from one place and its column from another is where such a
  binding comes from. **Every refusal the codec makes about its caller is
  `NarrativeFieldMisuseError`**, thrown before any cipher; a ciphertext that failed to authenticate
  never is, and `openField`'s `catch` re-throwing on that type is the only thing keeping a caller's
  defect out of `unreadable`. **Two columns are typed for an envelope and one carries a blind index,
  and no screen has caught up** — `budgets.name` and `accounts.name` are `bytea`, `accounts.name_key`
  holds the only index, the account routes accept a sealed name and refuse a plaintext one, and
  `/app/accounts` still sends the old shape, so that screen cannot create or rename until the client
  is wired. Nothing in the browser seals anything yet. Neither codec is callerless: `AccountKeyCustodyService` reaches both halves
  of the narrative one and the whole of the blind index, which is the only way either key can be
  applied without leaving the class that holds it. **The blind index is built**, over its own
  grammar: `budgetoid/blind-index/v1 ⌷ table ⌷ column ⌷ normalized name`, looked up **as a pair**
  like its neighbour, and deliberately carrying **no row id** — an index must be equal for equal
  names across rows, which is the exact inverse of what the narrative binding requires, and why the
  two are separate modules rather than one with a parameter. Normalization is trim → NFKC → **full
  case fold** → UTF-8, and the fold is a **table this repository ships** at Unicode 17.0, statuses C
  and F: `toLowerCase` is not a fold and differs on 239 code points, and the platform this builds on
  reports Unicode 16.0 and leaves 52 of the table's code points unfolded, so two clients calling
  their platform would key one name to two values. The step **order** is the contract, not a
  preference — 50 code points where trim-then-NFKC and NFKC-then-trim disagree — and `İstanbul` is
  **not** `istanbul`, which is what a conforming fold does and must not be "fixed" into disagreeing
  with the database's unique index. Do not relax any of it to
  make a later screen easier. **`accounts.name` is the first column where uniqueness *survived*, and
  it is what the blind index was for.** `IX_accounts_budget_id_name_key` is the same rule — one name
  per budget — by the same mechanism, a unique B-tree index, over bytes the database cannot
  interpret: it never needed to *read* a name to enforce that, only to compare names for equality.
  Case folding **relocated** rather than vanishing — the collation left by force and the client folds
  before hashing, so the database still guarantees two identical index values cannot coexist and no
  longer guarantees two spellings of one name are recognised as identical. **A blind-indexed name is
  a pair and the pair travels together** — in the domain (`IndexedName`), in the schema (two `NOT
  NULL` columns) and in `GRANT UPDATE`. Half a grant either forbids the operation outright (`42501`,
  which is what shipped and what no test saw, because the case proving renames issued a one-column
  `UPDATE`) or admits half a row whose uniqueness value disagrees with its content, and nothing can
  see the second: recomputing the digest needs an index key the server does not have.
  **`budgets.name` is the first column to hold ciphertext, and two things
  it gave up are decisions rather than gaps.** Per-user name uniqueness is **surrendered, not
  deferred** — every seal draws a fresh nonce so two identical names produce different bytes, and
  FR-077 gives this column no blind index, so it never comes back; what survives is `NULLS NOT
  DISTINCT` on that index, which is the half that always mattered, stopping two racing provisioners
  each writing a nameless budget. And **the server can no longer refuse a blank or over-long budget
  name** — it holds an envelope it cannot count characters in, so restoring that check cannot be
  honest. The `case_insensitive` collation left the column **by force**: `bytea` is not collatable.
  Its version check is written `substring(name from 1 for 1)` and **not** `get_byte`, which raises
  `2202E` on a zero-length value rather than answering false — and measured on PostgreSQL 17.10,
  which of a column's checks fires first is decided by the **constraint name, alphabetically**, not
  by declaration order. `wrapped_account_keys` still carries `get_byte` and is correct only because
  "length" sorts before "version". **The server carries the narrative edge and no traffic**:
  `NarrativeFieldLimits` holds two caps over field *classes* — 1024 for the five name columns, 2560
  for the three description ones — bounding the **envelope** and never characters, because that is
  the only length this side can measure; `NarrativeField` is the one type a narrative column accepts
  and has **no** constructor, factory or conversion taking a `string`, so writing plaintext into a
  column does not compile — **that absent member, and no test, is what holds "no narrative value is
  ever server-readable"**; `IndexedName` says a **call** cannot be half where the schema's `NOT NULL`
  pair will say a **row** cannot, two guards at two moments and neither replaceable by the other; and
  `BlindIndexText` rides the **shared** base64url decoder, never `CiphertextEnvelopeText`, which would
  demand a `0x01` a digest has nothing to answer with. `NarrativeField.FromStore` **does not
  re-validate** — a validating read makes a lowered cap retroactive and turns a one-integer diff into
  data loss — and it is `internal` with no `InternalsVisibleTo` anywhere, so that rule is held by
  review until the persistence step grants access, which the dependency-direction guard **cannot
  see**. See
  [ciphertext-envelope.md](docs/business-logic/ciphertext-envelope.md) and
  [ADR 0022](docs/decisions/0022-mint-narrative-row-identifiers-on-the-client.md).
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
  export, **Sign out**, the credential list from `GET /api/me/credentials`, the recovery-code
  count, and the **Account keys** section, whose Outline **Unlock** is the one way out of a locked
  account: it runs a passkey ceremony the browser mints and **discards**, calling no route and
  spending no challenge, and hands what it derived to `AccountKeyCustodyService`.
  `AccountUnlockService` is provided **on the component** and injects neither `SessionService` nor
  `Router` — which is what makes "a refused unlock changes nothing about the session" structural
  rather than remembered. **A refused unlock must leave custody exactly as it found it**: a
  `custody.lock()` on that branch passed an entire spec, and it costs somebody whose account is
  already open both keys when they cancel a prompt. Present and **disabled**: erasure, passkey
  registration, code generation. Rules the chapters argue and a reader will undo:
  **Home and Add are specified and not built**; the export writes the response bytes **unread**
  (`responseType: 'blob'`); the credential list shows **type and day only**, never an id or provider
  subject; **three different reasons hold the disabled controls off across four sites and the screen
  says all three** — the account's keys *as bytes* under Register a passkey, those bytes **and** a
  server-checked assertion under Generate recovery codes, a server-checked assertion under Revoke and
  Erase — so do not
  paste one sentence over all of them, and do not "correct" the erasure copy into saying the browser
  is incapable or that this screen asks for no passkey, because it now asks for one that no server
  checks; **Revoke renders only on a row something can revoke**, carried per kind in a map
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
  `FileDownloadService` is one: the platform it calls does not exist under the test runner. It runs
  **three** ceremonies and sends two of them: the third, `deriveKeyFromLocalAssertion`, mints its own
  challenge, is verified by nobody and is discarded where it stands — the account's own envelopes are
  what judge the factor, so it spends **neither** the anonymous sign-in nonce pool nor the
  re-authentication pool, the latter authorizing the **three** acts a live session alone is not
  trusted with — erasing the account, revoking a credential and replacing a recovery-code set — so a
  nonce spent there and discarded would be left good for any of them. It takes no
  parameters and returns a bare `CryptoKey`, both so that no caller can thread a server nonce in or
  find anywhere to put a payload. Rules it carries, each silent when broken.
  **The PRF output never crosses the module boundary** — every
  leg derives through `keyEncryptionKeyFromPasskey`, returns a non-extractable `CryptoKey` and
  zero-fills the bytes, and the bytes reaching that derivation are the **platform's own buffer**
  rather than a copy of it: an implementation that copies, derives from the copy and wipes the
  original leaves the account's key material on the heap for the life of the tab with every wipe
  test still green. **The registration payload projects** `getClientExtensionResults()` into a
  fresh `{prf:{enabled}}` — never forwards, filters or spreads it, because that object carries the PRF
  output itself. **The claim's *value* is what the client established, not what `create()` reported**;
  the server gates on that word, so reporting `create()`'s alone refuses every authenticator that
  derives on the first assertion. **`create()` returning no PRF output is not a refusal** — one
  *local* `get()` follows, carrying `allowCredentials` (WebAuthn throws `NotSupportedError` without
  it) and **discarded, never sent** — but `enabled: false` **is** a refusal, immediately; absent is
  not `false`. **`isArrayBuffer` is a brand check, never `instanceof`** — a narrowing that silently
  goes false derives the key from zero bytes. And **`assertPasskey` asks for PRF too, and the sign-in
  screen *spends* the key rather than letting it go**: `SignInService` reads it and hands it on in
  **one statement** to `AccountKeyCustodyService.unlock`, never naming it on a field, a signal or a
  local of its own. See [passkeys.md](docs/business-logic/passkeys.md) and
  [account-keys.md](docs/business-logic/account-keys.md).
- **The account's keys are held per tab by one root-provided service, and every clause of that was a
  decision.** `AccountKeyCustodyService` reads `GET /api/me/account-keys`, tries **every** entry
  under its own `factorId` (an account holding one passkey gets one entry, an ordinary account eleven
  — `entries[0]` works forever on the first kind and tells the second that their valid factor opened
  nothing), and
  keeps what opened as two `CryptoKey`s on `#` fields with **no accessor, and there never may be
  one** — which is what forced `sealField`, `openField` and `blindIndex` onto this class rather than
  into a module beside it: each codec takes the key as its first argument, so any other holder would
  have to be handed one. **Both fields now have readers and no lint suppression is left on either.**
  The two narrative operations take a `NarrativeFieldBinding` and custody never imports
  `NARRATIVE_FIELDS`, so the eight pairs stay the codec's; two source-text pins hold both halves. All
  three judge their argument **before** custody, through a named refusal the owning codec exports —
  never by building a value in order to throw it away — or an unrecoverable caller defect is
  swallowed by a locked tab. Only the refusals a person can act on become results; a refused
  argument and an extractable key keep **throwing**, because a caught throw rendered as a sentence is
  a bug wearing a UI. **The three do not behave alike when custody moves mid-cipher, and a reader
  will try to make them**: a seal compares **key identity** — a plain `lock()` keeps its answer,
  because the ciphertext is still that account's and dropping it discards typed text — while a read
  and an index compare the **generation counter** and drop in both cases, because an index computed
  under a key the account no longer holds matches no row and its lookup comes back *empty rather
  than failing*. Copy the seal's guard onto the index and one case greens while its neighbour
  reddens; that is the check, not a memory.
  `providedIn: 'root'` **breaks the component-provided habit
  deliberately**: `RegisterService` and `SignInService` hold an *attempt*, which should die with its
  screen, while these are state of the **session**, which outlives every screen. Route-providing on
  `app` is the near miss and must not be taken — `guestGuard` bouncing an authenticated visitor off
  `/welcome` destroys that injector, so a stray navigation discards the keys and locks the account
  with no ceremony left on screen and nothing red. **Nothing is persisted and nothing is shared
  across tabs**: a non-extractable `CryptoKey` is structured-cloneable, so IndexedDB and
  `BroadcastChannel` both *work* and both are refused — the first makes the account's decryption
  capability outlive the browser closing, so the device plus a live cookie reads the narrative with
  no factor presented, and the second unlocks a tab in which nobody presented one. Say so in the
  docs, or the next reader who learns `CryptoKey` is cloneable reads the absence as an oversight.
  **A page reload therefore locks the *account*** — a browser holding no content key, which is
  FR-065's word and **not** a locked session (that is a federated credential's row, what
  `sessions.md` means by "locked" throughout); vacuously satisfied today because nothing is
  encrypted, and the locked screen and the unlock control are a later story. **`unlock` returns
  `void` as enforcement** — awaitable, it lands a round trip between a verified assertion and the app
  and one refactor later grows a `catch`, at which point a key that did not open has become an
  authentication that failed. Custody calls nothing on `SessionService`, and **`'unopened'`,
  `'unreachable'` and `'unauthenticated'` are three different next steps for a person** — present
  another factor, the same factor again in a minute, sign in again — so collapsing any two sends
  somebody down a road that cannot help them: a 401 or a 403 is a usable answer from a server that
  was reached, so `unreachable`'s advice can never come true of it, and no factor was judged, so
  `unopened`'s cannot either. A 404 stays `unreachable`. That 401/403 reading is **written out in
  custody rather than imported from `SessionService.readingOf`**, which makes the same judgement
  four lines away: the import closes a cycle, and it puts the rule the whole class is built on one
  call from being undone by somebody reusing what was already there. The words differ from the
  session's on purpose too — `anonymous` is about *who is asking*, `unauthenticated` about *this
  read* — which is what keeps the two copies from being folded together later. Any of it is readable
  only because `getAccountKeys()` carries `EXPECTS_UNAUTHENTICATED` **on the method**: one route,
  one caller, one meaning for a 401, unlike `getMe()`/`getSessionOwner()`, where two callers ask two
  questions of one route and only the request can tell them apart. Clearing has **one owner**,
  `SessionService.ended()` — never an
  `effect()` (it fires on construction, and its only honest predicate would have to lock on
  `unreachable` and `unauthenticated` too — destroying the keys over one blinked request, and
  demanding a full ceremony to get them back) and never the callers. Registration hands the pair over as **objects** on the
  201 (`adopt`) rather than re-reading through the route: re-reading needs the passkey's
  key-encryption key to survive the codes step — the same power one step removed — puts a round trip
  and a new failure mode on the happiest path in the product, and verifies nothing — the read returns
  all eleven pairs, but the browser holds only the **passkey's** key-encryption key, so the ten code
  pairs where the pairing hazard lives are still never opened. Two clearing lines (`restart()` and the failed POST) **cannot be shown to
  fail** — the minting writes the keys before the payload `create()` requires, so no stale pair is
  readable — and are kept as depth, not deleted as dead. See
  [account-keys.md](docs/business-logic/account-keys.md) and
  [sessions.md](docs/business-logic/sessions.md).
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
  `RegisterService` **on the component** — custody, not lifetime: the eleven key-encryption keys, the
  ten codes and an *abandoned* attempt's account keys all die with the screen. The one thing that
  outlives it is a **created** account's key pair, handed to `AccountKeyCustodyService` on the 201 by
  `adopt` and never on any other outcome. Rules the chapters argue and a reader will undo:
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
