# Sessions

## Table of Contents

- [Purpose](#purpose)
- [Key Entities](#key-entities)
- [Constraints](#constraints)
- [Business Rules & Invariants](#business-rules--invariants)
- [Workflows & State Transitions](#workflows--state-transitions)
- [Decision Trees](#decision-trees)
- [Integration Points](#integration-points)
- [Edge Cases & Known Gotchas](#edge-cases--known-gotchas)

## Purpose

This area covers **an established sign-in the product owns**: a row it wrote, can read, and can end
without asking anyone. Identity — who a person is, and which credentials prove it — lives in
[users-and-ownership.md](users-and-ownership.md); this file covers what happens *after* a credential
has answered that question. The distinction is the whole point: a token issued by an identity
provider cannot be taken back by this product — revoking access would mean asking the provider to
revoke it — while a session row can be ended here, in one write, by the same role that serves every
request. **Three things establish a session and there is no fourth**, and all three open a `Full`
session lasting 14 days.

**A session is now issued, presented and ended, and this file describes a working thing.** All three
establishing paths mint a handle and set the cookie; a request presenting it is authenticated from
it, publishing the account and the ambient budget; and `POST /api/me/session/revocation` ends it. The
loop is closed on the server.

What is still missing is on the **client**: nothing in the browser runs a passkey ceremony from a
screen, so no person has been through this path — the requests that mint a cookie are made today only
by the integration suite, and every request the app itself makes still carries the identity
provider's ID token. The first gotcha below carries what that means for reading the rest of this
file.

## Key Entities

- **Session** — one established sign-in. It carries a `Guid Id` of its own, the `UserId` whose
  account it reaches, the `CredentialId` of the credential that established it, that credential's
  `CredentialType`, a `SessionKind`, the instant it began, the instant it expires, and a nullable
  instant at which it was revoked. It holds no navigation properties: it names its user and its
  credential by id, exactly as **Credential** names its user by id.
- **SessionKind** — `Locked` or `Full`, and it is **derived from the establishing credential's
  type**, never supplied. A `Passkey` credential and a `RecoveryCodes` credential each open a `Full`
  session; a `Federated` one opens a `Locked` session, and it is the **only** type that does.
  `Session.ReadsBudgetContent` is the computed reading of that, and it is true only for `Full`.
  `Locked` is declared first so that `default(SessionKind)` is the value reaching nothing — the order
  is the fail-closed direction, not alphabetical accident.
- **`Session.CredentialType`** — a copy of the establishing credential's type, carried on the row so
  the database can check the derivation. A `CHECK` sees only the row in front of it, so the fact
  `kind` is derived from has to be on that row for the derivation to be checkable at all.
- **SessionToken** — the handle one session will be presented by, stored as `SHA-256(token)` and
  never as anything the token can be recovered from. It carries the digest, the `SessionId` it opens
  and the `UserId` that owns it, and **nothing else** — no timestamps, because the session row already
  carries when it began, when it expires and whether it was revoked, and a second copy is a second set
  of the same facts to keep in step.

  **Why it is a table of its own, which is the whole of the decision.** A presented token has to be
  looked up *before* the request has an identity, and `sessions` is policed by `user_isolation` keyed
  on `app.current_user_id` — which is exactly the value the lookup exists to produce. A hash column on
  `sessions` would therefore be read by a statement the policy refuses, and refuse it loudly: an unset
  setting reaches the policy as `''::uuid` and raises `22P02`, on every request rather than on some
  edge. So the discovery key goes on its own **exempt** table and everything read *after* the answer —
  the expiry and the revocation instant above all — stays on the policed one. That is the split
  [ADR 0012](../decisions/0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md)
  already argues for a passkey's material, and
  [ADR 0019](../decisions/0019-authenticate-a-request-from-a-first-party-session-cookie.md) applies
  it here.

  **What the hash buys is not what a recovery code's hash buys.** A recovery code never reaches this
  server at all; a session token does — this server mints it and reads it on every request presenting
  it — so the digest is not a claim that the value is unknown here. It is a claim about what a *copy*
  of the table is worth: a backup, a replica or one unbounded read yields digests, and a digest of a
  256-bit uniform value cannot be turned back into the token a request would have to present.

A session record deliberately carries **no** last-used instant, device name, IP address, or user
agent. Each would be a column nothing reads today, and a column nothing reads is data held for no one
— the same rule the minimal account row is built on.

It carries no token and no token hash either, and that absence is a different decision from the four
above rather than another instance of them: the hash exists, on `session_tokens`, and it is on its
own table **because** it cannot be on this one. Read the entity below for why, and do not "tidy" the
two back together.

```mermaid
erDiagram
    USER ||--o{ CREDENTIAL : "signs in with"
    CREDENTIAL ||--o{ SESSION : establishes
    SESSION ||--o{ SESSION_TOKEN : "is presented by"
    SESSION {
        guid Id
        guid UserId
        guid CredentialId
        string CredentialType
        string Kind
        datetime CreatedAtUtc
        datetime ExpiresAtUtc
        datetime RevokedAtUtc
    }
    SESSION_TOKEN {
        bytea TokenHash
        guid SessionId
        guid UserId
    }
```

**`SESSION_TOKEN` is drawn one-to-many because that is what the schema holds**, and the gap between
that and what the design intends is worth stating rather than drawing over. The primary key is the
digest, so nothing stops a session from having several token rows, and nothing stops it from having
none. One-to-one would need either a unique constraint on `session_id` — which would be a real rule
and is simply not there — or a trigger for the "at least one" half, which
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) forbids pushing down.

**What holds one-to-one is the port's shape, and it is stronger than it had to be.**
`ISessionRepository.AddAsync` takes the session **and** its token, and there is no overload taking a
session alone — so a session cannot be written without its handle by construction rather than by
every caller remembering. `ISessionTokenRepository` stays read-only for the same reason, stated from
the other side: a second way to write a token is a way to produce one naming a session that was never
committed. One write path, one save, exactly as it is the whole of what holds "every factor has
wrapped keys".

The schema still permits what the application refuses — several tokens for one session, or none — and
that gap is deliberate rather than an oversight. Closing it would need a unique constraint on
`session_id` for one half and a trigger for the other, and
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) forbids pushing procedural
logic down to satisfy "lowest layer". So the diagram is drawn one-to-many because that is what the
database holds, and the sentence above is what makes it one-to-one in fact.

## Constraints

### MUST

- **A session is isolated by user, like `users` and `budgets`.** `sessions` carries `user_id` and no
  `budget_id`, so it is user-owned by the same rule and owes the same policy.
  - **Why**: a session belongs to a person; the budgets that person owns are reached through their
    own policies, one layer down.
  - **Enforced in**: the `user_isolation` policy on `sessions` in
    `BudgetoidApp/Infrastructure/Persistence/Provisioning/app-role-grants.sql`, comparing `user_id`
    against `app.current_user_id` in both `USING` and `WITH CHECK`;
    `SessionContextInterceptor` puts that setting on every connection the context opens.
    `RowLevelSecurityCoverage` reaches the verdict from the table's own columns rather than from a
    list, so `RlsCoverageTests` and the deploy-time verifier both required this policy the moment
    the table existed. `RlsIsolationTests` proves the three halves — another person's rows are
    invisible, an insert naming another person is refused, and a connection naming nobody fails with
    `22P02` rather than reading anything. See
    [ADR 0011](../decisions/0011-police-the-user-owned-tables.md).
    - **`user_id` is `NOT NULL`, and that is load-bearing rather than tidy**: a `NULL` owner fails
      *closed*, because `NULL = anything` is `NULL` and never true, so the row would be invisible to
      every session including the one that wrote it — a write that succeeds and a read that cannot
      be explained.

- **A session's identity columns — `user_id`, `credential_id`, `credential_type`, `kind`,
  `created_at_utc`, `expires_at_utc` — are immutable. `revoked_at_utc` is the only column an edit
  may reach.**
  - **Why**: changing `credential_id` would relabel which key opened the door, and which key opened
    it is the fact revocation is decided by. Changing `kind` would hand budget content to a session a
    federated credential opened, which is the one thing the kind exists to refuse.
  - **Enforced in**: the grant matrix. `GRANT SELECT, INSERT ON sessions` plus
    `GRANT UPDATE (revoked_at_utc) ON sessions` — the other six are immutable by **omission from
    the column list**, never by a `REVOKE`, which additive column privileges could not express. A
    one-column list is still a list and must not be collapsed into a table-wide grant. Above it,
    `Session` exposes no public setter. `AppRoleGrantsTests` pins a `42501` for each of them in
    the same test as a permitted `revoked_at_utc` update that reports one affected row — the
    affected-row count is what stops the pair passing when row-level security matched nothing. See
    [ADR 0004](../decisions/0004-connect-as-a-least-privilege-role.md).

- **A session's expiry MUST be after its creation.**
  - **Why**: a session whose expiry is at or before its creation was never live, and a row that was
    never live can only mislead whatever reads it.
  - **Enforced in**: `CK_sessions_lifetime` (`expires_at_utc > created_at_utc`), restated in
    `Session.Establish` so a bad call fails with a named field rather than a raw `23514`. No request
    can reach it: each of the three establishing paths computes the expiry by adding the shared
    lifetime to the instant it just read, so the pair is well-formed by construction and nothing
    renders the field error into a response. The restatement is a guard against a future caller
    that computes an expiry from something a request supplied, not a validation a client can trip
    today.

### MUST NOT

- **The application role MUST NOT hold `DELETE` on `sessions`.**
  - **Why**: revocation writes `revoked_at_utc`; it does not remove the row. The absent grant is
    what keeps the role from removing a session while the account it belongs to still exists.
    Session rows do leave — the cascade from `credentials`, and through it from `users`, takes every
    one of them when the account is erased — but that reaches them by descending from a row rather
    than by a privilege over this table, so it cannot single one out. No retention sweep exists;
    when one is built it needs this grant, and the paragraph in `app-role-grants.sql` is what has
    to be re-argued rather than quietly deleted.
  - **Enforced in**: the grant matrix in `app-role-grants.sql` — no `DELETE` appears for
    `sessions` — pinned by `Database_RefusesToDeleteASession`.

- **No policy on `sessions` may read `kind`.**
  - **Why**: whether a session reaches budget content is answered by `budget_isolation` on the
    budget-owned tables, which a locked session never satisfies because it resolves no ambient
    budget. A predicate here consulting `kind` would invent a third isolation axis beside the two
    the schema already carries, and which rows a person could see would then depend on which of the
    three fired last.
  - **Enforced in**: the `user_isolation` policy on `sessions` in `app-role-grants.sql` compares
    `user_id` alone, and no other policy exists on the table.

## Business Rules & Invariants

- **Rule**: A session's kind is **derived** from the establishing credential's type. There is no way
  to ask for one.
- **Why**: an authorization exchange with an identity provider returns claims, not a secret the
  client can turn into a key. So any account reachable by a provider sign-in would be an account the
  provider's holder could read — which is why a federated credential opens a session that reaches no
  budget content at all, and **`federated` is the only credential type that cannot**. The rule runs
  that way round rather than the other: a passkey and a set of recovery codes are each a secret in
  the holder's own possession — one held by an authenticator, one written down — so both open a
  `Full` session. The key custody those secrets are meant to carry is designed and **not built**, so
  it is not what the rule rests on today. Redeeming a code establishes exactly that session, from the
  set's own credential — see [recovery-codes.md](recovery-codes.md).
- **Enforced in**: `CK_sessions_kind_matches_credential`,
  `(kind = 'full') = (credential_type in ('passkey', 'recovery_codes'))`, which is the lowest layer
  that can state the rule declaratively. Without it the rule lived only in the factory while
  `GRANT SELECT, INSERT ON sessions` stayed table-wide on `INSERT` —
  `(credential_id = <a federated credential>, kind = 'full')` was a fully storable row.
  `CK_sessions_kind` bounds only the vocabulary and the composite foreign key proves only whose the
  two rows are; neither refuses that pair. Above it, `Session.Establish` takes the `Credential` and
  no kind, and derives it through a switch with every arm written out and a throwing discard arm.
  - **The full side stays enumerated, and the spelling is a decision rather than a style.** The
    mirror form — `(kind = 'locked') = (credential_type = 'federated')` — says the same thing about
    every row this schema can hold today, reads better, and is what a later reader will propose. It
    fails **open**: a fourth credential type added to the vocabulary is not `federated`, so it
    satisfies the right-hand side and is granted a full session by default, with nobody having
    decided that. The shipped form fails closed — an unenumerated type gets no full session until
    somebody adds it here, which is the same decision `Session.KindFor` forces by writing out every
    arm. **No test in the suite can tell the two spellings apart until that fourth type exists**, so
    no assertion can separate "the rule changed meaning" from "the wording changed", and this
    paragraph and the comment beside the constraint are the only things carrying the difference.
    That is also why adding `recovery_codes` to the `in` list was the correct edit rather than the
    occasion to simplify: the list growing by one member is exactly what the form is for.
  - **The rule is unrepresentable, not merely untested.**
    `SessionTests.Session_ExposesNoWayToChooseItsKind` reflects over the public surface and fails on
    any parameter or settable property of type `SessionKind`. Without it, the obvious accommodation
    for a caller wanting a different kind is an overload taking one, and the rule dissolves with no
    test going red.
  - **Note on the copy**: `credential_type` duplicates `credentials.type` and cannot drift from it —
    `credentials` holds no `UPDATE` grant of any shape, and the composite foreign key below ties the
    two columns together on every insert.
- **Example**: a `RecoveryCodes` credential handed to `Session.Establish` yields a `Full` session; a
  `Federated` one yields `Locked`; no argument exists to override either, and a fourth credential
  type yields a throw rather than a default.
- **Counterexample**: a `bool canReadBudgetContent` argument on the factory. It reads as a permission
  the caller sets, and the first caller that sets it wrongly is the whole rule gone.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: Revocation is an **`UPDATE` of `revoked_at_utc`**, never a `DELETE`, and it is
  **idempotent** — an already-revoked session keeps the instant access actually ended.
- **Why**: the row is what says access ended and when. Deleting it needs a `DELETE` grant, which is
  the single privilege that can erase every session on the system, and it cannot tell "already
  revoked" from "never existed" — a distinction anything reporting a revocation needs. The usual
  argument for `DELETE`, that updated rows accumulate, does not separate the two options: an
  unrevoked but expired row accumulates identically, so retention is a problem either mechanism has
  and neither solves.
  - **Note what this is not**: a tombstone. A session row exists only while its account does — the
    cascade rule below takes every one of them — so a revoked session leaves nothing behind an
    erasure.
- **Enforced in**: `Session.Revoke` returns without writing when `RevokedAtUtc` is already set;
  `SessionRepository.RevokeForCredentialAsync` loads the credential's unrevoked sessions and calls
  it per row. `ExecuteUpdateAsync` is a compile error under `BudgetoidApp/BannedSymbols.txt`, and the
  ban buys correctness here rather than uniformity: a set-based `UPDATE` would rewrite every matched
  row's instant on every call and would report a retry as if it had ended access a second time.
  - **Concurrently, too**: `Session.Revoke`'s idempotence is a property of one object in memory, so
    on its own it does not survive two sweeps running at once — both would read the rows as
    unrevoked and the later commit would overwrite the first revocation instant. `revoked_at_utc` is
    therefore a **concurrency token**: the `UPDATE` carries `and revoked_at_utc is null`, the losing
    sweep matches zero rows and raises `DbUpdateConcurrencyException`, and `RevokeForCredentialAsync`
    answers it by re-reading and retrying. A token in the `WHERE` clause needs only `SELECT`, so the
    `GRANT UPDATE (revoked_at_utc)` column list is unaffected.
- **Example**:
  - **What the returned count means**: the number of sessions **this call** ended, excluding any a
    concurrent sweep ended first. Two simultaneous revocations of one credential therefore report a
    total of the sessions ended, not that number twice. It counts the **unrevoked**, not the live:
    the filter is `revoked_at_utc is null` and says nothing about expiry, so a session that expired
    with nobody revoking it is in the number.
  - **The count reaches the wire on both paths**, as `sessionsEnded` on the passkey-revocation and
    recovery-code-generation responses, which makes it a published contract rather than an internal
    return value; narrowing it later is breaking. On the generation path it is more than a report —
    it is the condition that path's re-established session is written on — so **what this number
    counts cannot be changed on one caller alone**. See [recovery-codes.md](recovery-codes.md).
- **Counterexample**: tightening the filter to "live at the caller's instant". It would look like a
  fix to the recovery-code rule and would silently change what a passkey revocation reports —
  see [recovery-codes.md](recovery-codes.md).
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: Revoking a credential ends **only** the sessions that credential established. Every other
  credential on the same account stays signed in.
- **Why**: revoking one device is the reason the operation exists.
- **Enforced in**: `SessionRepository.RevokeForCredentialAsync` filters on `CredentialId` and never
  on `UserId`, and `RevokeSessionsForCredentialHandler` names a credential in its command.
- **Example**: an account holding a passkey on a phone and another on a laptop. Losing the phone
  revokes the phone passkey's sessions; the laptop stays signed in. Ending both would hand the
  person a blunter instrument than they asked for.
- **Counterexample** — and the one to watch: a predicate keyed on `UserId`. Every session in a
  single-credential account has the same owner, so every test in the suite passes under it except
  the two written for exactly this —
  `RevokeSessionsForCredentialHandlerTests.HandleAsync_LeavesAnotherCredentialsSessionsActive` and
  `SessionRepositoryTests.RevokeForCredentialAsync_RevokesOnlyThatCredentialsSessions`.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: A session and the credential that established it belong to the **same person**, and the
  database refuses a row where they do not.
- **Why**: `user_isolation` reads `user_id` and never looks at the credential, so a session naming
  someone else's credential is a row the policy would happily show to the wrong person.
- **Enforced in**: one composite foreign key, `sessions (credential_id, user_id, credential_type) →
  credentials (id, user_id, type)`, against the `AK_credentials_id_user_id_type` alternate key. This
  is the same idiom the budget-owned tables use one level down, where every cross-row reference
  carries `budget_id`. `credential_type` rides along so a session cannot disagree with its credential
  about what opened it, which is what makes `CK_sessions_kind_matches_credential` a claim about the
  real credential rather than about a value the row asserted for itself.
  `SessionSchemaTests.Database_RefusesASessionWhoseCredentialBelongsToAnotherUser` pins the `23503`.
  - **Consequence**: `credentials` gained an alternate key and **no new column**, which matters —
    the exemption in `RowLevelSecurityCoverage.Exemptions` pins that table's exact column set, and a
    new column there would correctly go red.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: Deleting a credential deletes its sessions (`ON DELETE CASCADE`). There is deliberately
  **no** second foreign key from `sessions` to `users`.
- **Why**: `Restrict` would let a session hold up the deletion of a credential, and through it an
  account erasure — a row of access bookkeeping outranking a person's request to be forgotten, which
  is what the `credentials → users` foreign key already refuses. The credential's own cascade to
  `users` reaches sessions transitively, so a direct one would add nothing but another constraint
  name for the pinned snapshots to carry.
- **Enforced in**: `SessionConfiguration`;
  `SessionSchemaTests.Database_RemovesASessionWithTheCredentialThatEstablishedIt` and
  `Database_RemovesASessionWithTheUserThatOwnsIt` pin both hops.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: A request authenticates from an opaque token in a first-party cookie, and the two reads
  that turn it into an identity happen in **one order that cannot be rearranged**: the exempt
  `session_tokens` lookup first, the identity published second, the policed `sessions` row third.
- **Why**: this is the circularity [ADR 0019](../decisions/0019-authenticate-a-request-from-a-first-party-session-cookie.md)
  exists for. `sessions` is policed by `user_isolation` keyed on `app.current_user_id`, which is
  exactly the value the lookup exists to produce, so a session read issued before the publication
  meets `''::uuid` and raises `22P02` — on **every** authenticated request, not on an edge. The
  digest is what makes publishing on the strength of the lookup alone defensible: it is SHA-256 of a
  256-bit value this server minted, so a caller presenting one it was never given is guessing it.
- **Enforced in**: `AuthenticateSessionHandler`, in Application, where the order is the security
  property; `SessionCookieAuthenticationHandler` in the API decodes the cookie and decides, and looks
  nothing up — `CompositionBoundaryTests` holds the API to composing Infrastructure rather than
  consuming it, and this is the path where that shortcut would cost the most.
  `AuthenticateSessionHandlerTests` pins the order in both directions, over
  `RecordingUserContextWriter`; `SessionCookieAuthenticationTests` drives the whole path over the
  real least-privilege connection, which is the test that dies with `22P02` if anyone ever wraps it.
  - **No transaction anywhere on this path**, which is the same trap from the other side: one opened
    before the publication configures its connection while the setting is still empty, and every
    policed statement inside it fails. `EnsureUserHandler`, `CompleteAssertionHandler` and
    `RedeemRecoveryCodeHandler` each carry the same warning. Nothing here writes, so an atomic unit
    would be protecting nothing.
  - **Two round trips per authenticated request**, stated as the cost rather than hidden. Folding
    them into one is what the "denormalise the expiry onto the exempt table" alternative in ADR 0019
    proposes, and that decision refuses it.
- **Example**: a well-formed 32-byte token whose digest matches a row publishes that row's account,
  then reads the session it names, then publishes the account's first budget as the ambient tenant.
- **Counterexample**: reading `sessions` first and publishing afterwards, which reads as the same
  three steps in a tidier order and refuses every request in the product.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: The handle travels in `__Host-budgetoid-session` — `HttpOnly`, `Secure`,
  `SameSite=Lax`, `Path=/`, no `Domain` — and its expiry is **absolute, never sliding**.
- **Why**: the `__Host-` prefix is a rule a browser enforces rather than a naming style: it refuses
  the cookie unless it is `Secure`, `Path=/` and carries no `Domain`, which is what stops a
  neighbouring host from setting one. `HttpOnly` is why no response body ever carries a session
  identifier — the cookie is the handle precisely so that script is not. `Lax` suffices because the
  frontend and the API share one registrable domain; a cross-site topology would have forced `None`,
  which is the deployment argument [ADR 0010](../decisions/0010-serve-the-app-from-a-custom-domain.md)
  and `DEPLOYMENT.md` Step 6 carry. A **sliding** expiry would need `GRANT UPDATE (expires_at_utc)`
  on `sessions` — the column list this file argues is immutable by omission — and would write a row
  on every request to buy it.
- **Enforced in**: `SessionCookie`, which owns the name and builds the attributes once so the issue
  and the clear cannot drift. `SessionCookieTests` pins each attribute.
  - **The clear must match the issue attribute for attribute**, and this is the pin most worth
    having: a browser silently keeps a cookie whose clear does not match, and the symptom is a
    sign-out that appears to work and a session that comes back.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: Every request must name itself as first-party with a non-empty `X-Budgetoid-Client`
  header. `GET /health` is the only exemption. A request without it is refused **403**, before
  authentication.
- **Why**: this is the CSRF control, and a cookie is what makes one necessary — a browser attaches a
  `SameSite=Lax` cookie to a top-level cross-site navigation, and the header is the thing no
  cross-site form can add and no cross-origin `fetch` can send without surviving a preflight. Two
  halves a reader will want to weaken: the **value is deliberately unchecked**, because an attacker
  who could set the header could set any value in it and a checked value would be a shared secret
  shipped to every client; and the control **covers the anonymous routes**, because those are the
  ones that *set* a cookie and login-CSRF is signing somebody into an account they do not own so that
  what they record next is filed under it.
- **Enforced in**: `FirstPartyRequestMiddleware`, registered **after** `UseCors` — so a preflight is
  answered by the CORS middleware and never meets a check no `OPTIONS` request can satisfy — and
  **before** `UseAuthentication`. `/health` is named from `ServiceDefaults.Extensions.HealthPath`
  rather than typed again. `FirstPartyRequestTests` sweeps the route table and pins the exempt set
  in both directions.
  - **The suite cannot see this control**, because `ApiFactory` gives every client it hands out the
    header — it stands in for the first-party web client. `FirstPartyRequestTests` therefore removes
    the header again on its own clients, and the two are a pair: delete the factory's line and the
    whole suite answers 403; delete the removal and the three tests that exist to withhold the header
    start sending it and stay green while proving the opposite of what they claim.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: The web client decides **once** whether a request is going to this product's API, and
  that one answer carries **three** effects: `withCredentials: true`, the `X-Budgetoid-Client`
  header, and — until sign-in leaves the identity provider — the `Authorization: Bearer` header. The
  decision compares **origins**, never a string prefix.
- **Why**: the three effects share a predicate because two predicates drift, and the drift is
  silent in both directions. Drop the cookie and every request arrives unauthenticated; drop the
  header and every request answers 403; widen the predicate and the browser hands this app's
  credentials to somebody else's host. That last one is not hypothetical:
  `url.startsWith(apiBaseUrl)` — the shape the bearer-only interceptor shipped with — admits
  `https://api.budgetoid.app.attacker.example`, a name anybody can register. The cookie itself is
  safe there, because a browser scopes `__Host-` cookies to the registrable domain that set them;
  the **bearer** is not, and it is a token this app volunteers. Two further halves a reader will
  fold together: the bearer is conditional on holding an id token and the other two are **not**,
  because the browser that has a session cookie and no id token is every browser after the provider
  drops out of sign-in; and an empty `apiBaseUrl` — which is what the config holds until it
  loads — classifies **nothing** as this API, because `''` is a prefix of every string on earth and
  failing open there hands credentials to every request the app makes.
- **Enforced in**: `apiCredentialsInterceptor` in `+core/interceptors/`. The predicate is
  **exported from there and imported** by `sessionExpiryInterceptor`, which needs the same answer
  on the way back; it is one definition with two callers rather than one interceptor, and that is
  what keeps "one predicate" literally true. The interceptor's spec calls the function directly and
  therefore cannot see whether anybody registered it, so `app.config.spec.ts` stands up the real
  provider list with only the HTTP backend swapped and goes red on an emptied
  `withInterceptors([…])`. Without that second spec the registration can be deleted with the whole
  suite green and the product answering 403 to everything.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: On a cold load the client asks the server who the visitor is, **once**, before the
  first route activates. The answer has **four** values: `authenticated`, `anonymous`,
  `unreachable`, and `unknown` before the question has been answered. **Only `anonymous` may
  bounce anybody** — both guards admit `unreachable` and `unknown`.
- **Why**: the cookie is `HttpOnly`, so there is no local evidence to read and asking is the only
  way to know. The four values exist because **an answer that never arrived is not evidence about
  the visitor**. Collapsed into `anonymous`, one blinked request during the cold load signs a
  person holding a perfectly good session out of their own account and drops them on a page served
  by the same server they could not reach, where nothing they do fixes it. It is the same defect as
  collapsing `null` into `0` on the recovery-code count, one screen over: a claim about the account
  manufactured out of a failure to ask. `403` joins `401` as `anonymous` — neither describes an
  authenticated visitor and the next step is the same — while a 500, a timeout and a status-`0`
  network failure all read `unreachable`. `unknown` is the same argument before the first ask
  rather than after a failed one; it should be unobservable, and admitting it means a deleted
  initializer costs a redundant state rather than every visitor bounced on every cold load.
- **Enforced in**: `SessionService` in `+core/session/`, probed from the `APP_INITIALIZER` in
  `core.providers.ts` **after** `config.load()` and **awaited**. The ordering is not stylistic:
  `BaseApiService` reads `apiBaseUrl` in its constructor and the config holds `''` until `load()`
  resolves, so an earlier probe sends `GET /api/me` to this app's own origin. The **await** is what
  keeps every guard synchronous — bootstrapping cannot finish while the answer is outstanding — and
  `core.providers.spec.ts` pins both halves separately, because a `void probe()` satisfies one and
  fails the other. `probe()` resolves however the read ends and **never rejects**; a rejection is
  not a failed probe but an application that never finishes starting.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: A `401` answered to a request this app made to its own API ends the session client-side
  and sends the browser to `/welcome`. A `403`, another origin's `401`, and any request carrying the
  `EXPECTS_UNAUTHENTICATED` context token are all left alone. The error is **always re-thrown**.
- **Why**: a session ending is an application-wide fact — every screen's reads start failing at
  once — so it is noticed in one place rather than in each caller. That single ownership is why the
  Settings export no longer carries a word of its own for a lapsed session; the sentence it used to
  render described a screen the visitor is no longer on. Three exclusions, each silent when wrong.
  **`403`** is the first-party refusal and the locked-session refusal, both answered to a browser
  whose session is intact, so acting on one ends a live session over a bug in the request builder.
  **Another origin's `401`** is a statement about a token this product does not issue — the app
  reaches the identity provider through the same `HttpClient`, so a sign-out would be caused by a
  third party. And the **anonymous ceremony routes answer `401` as their own verdict**: a passkey
  that did not verify, a recovery code that matched nothing. None of those is a session ending,
  because there is no session yet. The **re-throw** is what keeps this an observer rather than a
  handler; swallowed, the error reaches no caller's `catchError` and the screen that made the
  request sits on its loading line forever, under a navigation a guard may itself cancel.
- **Enforced in**: `sessionExpiryInterceptor`, registered after `apiCredentialsInterceptor` so the
  unwinding puts it nearest the backend. The exclusion is carried on the **request**, as an
  `HttpContextToken`, and deliberately **not** as a list of anonymous URLs held in the client: a
  list is a second definition of the anonymous surface, and the first route to move leaves it
  ending the session of somebody who mistyped a recovery code. The services that set the token
  arrive with the screens that call those routes, so the mechanism ships ahead of its caller.
  `app.config.spec.ts` carries a registration pin for this interceptor too, independent of the
  credentials one.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: `POST /api/me/session/revocation` ends **only the caller's own session**, clears the
  cookie, answers `204`, and answers `204` again on a second call.
- **Why**: leaving people with no way out once sessions are real is worse than the route costs. The
  caller names no session — the id comes from the claim its own authentication produced — so there is
  no session id on the wire for anyone to substitute. Idempotence is what stops a dead cookie living
  on the client forever: a `401` on the second call would leave the browser holding a handle nothing
  will ever clear.
- **Enforced in**: `SessionEndpoints` and `RevokeSessionHandler`, over
  `ISessionRepository.RevokeAsync`, whose idempotence is `Session.Revoke`'s and is not restated.
  `SignOutTests.SigningOut_LeavesAnotherDeviceSignedIn` is the negative control — without it, a
  sign-out that revoked every session on the account passes every other test in the file.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: An **ended** session — revoked or expired — authenticates on exactly one route, the one
  that ends sessions, and reaches **no ambient budget** even there.
- **Why**: the idempotence above needs it. Signing out twice presents the same dead cookie twice, so
  the second call has to authenticate far enough to answer `204`. What makes this narrow rather than
  a hole is what it does *not* relax: the token still has to match, so it is a verdict about liveness
  and never about the handle. And the tenant is published only on the live path, so a marked route is
  structurally unable to reach budget-owned rows — a budget-scoped statement under an ended session
  meets an unresolved budget and throws rather than being scoped to a stranger and matching nothing.
- **Enforced in**: `AcceptsEndedSessionAttribute`, an **opt-in marker on the route**, read by
  `SessionCookieAuthenticationHandler`. A marker rather than an authorization requirement because
  `AuthorizationMiddleware` answers `403` where this needs `401`, and rather than `AllowAnonymous`
  because that would widen the anonymous surface `AnonymousSurfaceTests` holds.
  `AcceptsEndedSessionTests` pins the marked set at exactly one route, the way `AnonymousSurfaceTests`
  pins the anonymous one, so a second marker goes red until somebody argues for it.
- **Counterexample**: relaxing the *lookup* instead — admitting a request whose token matched nothing
  so that sign-out "always works". That is an unauthenticated route with extra steps.
- **Source**: `[SOURCE: discussion]`

## Workflows & State Transitions

```mermaid
stateDiagram-v2
    [*] --> Established : a credential authenticates its owner
    Established --> Revoked : someone ends this session, or the credential that opened it
    Established --> Expired : expires_at_utc passes with nobody revoking anything
    Revoked --> [*]
    Expired --> [*]
```

| Transition | Triggered by | Validations |
|---|---|---|
| → Established | `Session.Establish(credential, createdAtUtc, expiresAtUtc)`, reached from `CompleteAssertionHandler` once a passkey assertion verifies, from `RedeemRecoveryCodeHandler` once a presented verifier matches a stored hash, and from `GenerateRecoveryCodesHandler` when replacing a set ended at least one of that set's sessions | the credential is required; the expiry must be after the creation instant; the kind is derived from the credential's type and cannot be supplied |
| Established → Revoked | `Session.Revoke(revokedAtUtc)`, reached two ways: through `RevokeSessionsForCredentialHandler`, which `RevokePasskeyHandler` and `GenerateRecoveryCodesHandler` each call before deleting a credential, and through `RevokeSessionHandler`, which `POST /api/me/session/revocation` calls to end the caller's own | none. Already revoked is a no-op keeping the first instant, which is what makes a retry honest about having ended nothing new |
| Established → Expired | the clock | none. `IsActiveAt` reads the expiry as well as the revocation, with an exclusive boundary: a session is live up to its expiry and not at it |

There is no transition back. Nothing un-revokes a session and nothing extends one.

**A session is not a state machine a request advances**; presenting one changes no column. What a
request does with a handle is read three rows in one order, and the order is the rule:

```mermaid
sequenceDiagram
    participant Request
    participant Cookie as SessionCookieAuthenticationHandler
    participant Use as AuthenticateSessionHandler
    participant Db as PostgreSQL

    Request->>Cookie: __Host-budgetoid-session
    Cookie->>Cookie: decode base64url, refuse anything but 32 bytes
    Cookie->>Use: SHA-256 of the token, never the token
    Use->>Db: session_tokens by digest — EXEMPT, no owner, nobody published
    Db-->>Use: session id, user id
    Use->>Use: ResolveUser — the identity is published here and nowhere earlier
    Use->>Db: sessions by id — POLICED, works only because of the line above
    Db-->>Use: expiry, revocation, kind
    Use->>Db: the account's first budget
    Use->>Use: ResolveBudget — second, because ResolveUser clears it
    Use-->>Cookie: account, session, kind
    Cookie-->>Request: sub, session_id, session_kind
```

The hash is computed at the boundary that decoded the cookie, so the live token stops in that method
body: no command, no port and no log statement below it has a member the token could travel through.
`SessionToken.For` takes a `ReadOnlySpan<byte>` for the same reason, and there the compiler enforces
it rather than a reviewer.

## Decision Trees

The one multi-branch decision in this area is the kind derivation, written out in `Session.KindFor`
with a throwing discard arm and restated declaratively by `CK_sessions_kind_matches_credential`:

```
IF the establishing credential's type is `passkey`      ← arms are mutually exclusive
  THEN the session's kind is `Full`
ELSE IF it is `recovery_codes`
  THEN the session's kind is `Full`
ELSE IF it is `federated`
  THEN the session's kind is `Locked`
ELSE                                                    ← an unenumerated future type
  THEN `Session.KindFor` throws; the constraint refuses the row at the database either way
```

The default arm throwing rather than picking a kind is the fail-closed choice the constraint's
enumerated spelling makes at the database — see the first rule above.

## Integration Points

- **[Users & Ownership](users-and-ownership.md)** — the credential that establishes a session, and
  the account it belongs to. A session adds nothing to identity; it records what a credential already
  proved.
- **[Recovery Codes](recovery-codes.md)** — a set of codes opens a `Full` session, exactly as a passkey
  does, and `RedeemRecoveryCodeHandler` establishes one the way `CompleteAssertionHandler` establishes a
  passkey's. `GenerateRecoveryCodesHandler` calls the revocation sweep, as `RevokePasskeyHandler` does.
  The two routes divide differently than the names suggest: a **redemption** opens a session and revokes
  nothing, while a **regeneration** revokes the replaced set's sessions and — when it ended any — opens
  one over the new set in their place, so it is the one path that does both. The condition is the
  sweep's own count, which is why that file argues the `sessionsEnded` contract from the other side.
- **`user_isolation`** — the same policy `users`, `budgets` and `passkey_signature_counters` carry,
  keyed on the same session setting. `sessions` is policed on the person rather than on a budget,
  like each of them.
- **CORS** — the default policy gains `AllowCredentials()`, because a browser drops a cross-origin
  response carrying a cookie unless the header says so, and drops it **silently**: the request
  succeeded, the server wrote the `Set-Cookie`, and the jar is simply empty afterwards. The
  configured allow-list is unchanged and stays the control. `AllowAnyOrigin` must never appear beside
  it — the CORS middleware rejects the pair at runtime, and the reason it refuses them is the reason
  not to want them: an origin wildcard plus credentials is every site on the internet reading this
  API as the signed-in person.
- **[Users & Ownership](users-and-ownership.md), on the pipeline order** — `FirstPartyRequestMiddleware`
  runs after CORS and before authentication, then `UserProvisioningMiddleware` runs after
  authentication and returns immediately for a request the cookie already spoke for. The provisioning
  middleware and everything it reads are deleted when sign-in leaves the identity provider.
- **`SessionContextInterceptor`** — **not** about a session in this file's sense. It writes
  `app.current_user_id` and `app.current_budget_id` onto each PostgreSQL connection the context
  opens; the PostgreSQL backend session and a `Domain.Sessions.Session` share a word and nothing
  else. The isolation policy above is the one place the two meet, and only because the policy reads
  the setting the interceptor writes.

## Edge Cases & Known Gotchas

- **The loop is closed on the server and open on the client, and that is now the whole of the gap.**
  All three establishing paths mint a handle and set the cookie, a request presenting it is
  authenticated from it, and sign-out ends it. Every operation in this file is live rather than
  anticipatory: revoking a passkey really does end that device's access, and a regeneration that
  swept a live session really does sign the person back in over the new set. What is missing is a
  **screen**. No client code runs a passkey ceremony from a page, so the routes that mint a cookie
  are reached today only by the integration suite, and the app itself still authenticates every
  request from the Google ID token it is handed — exactly as
  [users-and-ownership.md](users-and-ownership.md) describes.
  - **The handle never appears in a response body.** The cookie is `HttpOnly` precisely so that
    nothing else is a handle; no response record carries a token or a session id, and
    `SessionTokenSecrecyTests` is a census over every type a route serialises so a record added later
    is covered without anybody remembering. A caller learns *that* a session exists and when it
    expires, and cannot spend that knowledge.
  - **The cookie is written by the endpoint, on the handler's success, and never before it.** An
    endpoint that wrote one unconditionally would leave a cookie behind on a refused ceremony. Note
    what does *not* hold that rule: on these routes a refusal leaves as an exception and
    `UseExceptionHandler` clears the response, so the framework would wipe such a cookie anyway. The
    ordering is held by the positive tests, not by the refusal ones.
  - **A first issue of recovery codes sets no cookie.** Only the branch that swept a live session
    re-establishes one, and that condition is the rule rather than a detail — a handler minting
    unconditionally passes every other test on that path.
  - **The token is drawn outside the transactional delegate**, on all three paths. Both positions are
    correct and no test distinguishes them, which is exactly why the choice is written down: outside
    means one secret per request rather than one per retry attempt, and it means correctness does not
    rest on the subtle property that the value a retried delegate returns belongs to the attempt that
    survived.
  - **The API's default authentication scheme is a temporary bridge**, `Budgetoid.Bridge`, a policy
    scheme that forwards to the cookie handler when the cookie is present and to `JwtBearer`
    otherwise. It exists so the whole existing surface keeps working while this area lands one commit
    at a time, and it is deleted when sign-in moves off the identity provider entirely. A request
    carrying both is treated as a session request, which is the safe direction rather than an
    arbitrary one: the cookie is a credential this product issued and can end, the provider token is
    the one it cannot.

- **`sub` means two different things depending on how the request authenticated, and nothing brings
  the two together.** On the cookie path it is this installation's own account id; on the bearer path
  `UserProvisioningMiddleware` reads it as a provider subject. They stay apart because that
  middleware returns immediately for a request whose identity is already published — and it tests the
  **published state**, not which scheme ran, so the session path cannot be re-provisioned by any edit
  short of deleting that arm, and the next scheme that publishes an identity inherits the rule
  without being named. If the two ever did meet, the collision fails closed: a GUID resolves to no
  federated credential and the request is refused rather than answered as somebody else.

- **A refused request can leave an identity published behind it.** When a token's digest matches but
  the session is dead, `app.current_user_id` names the account whose handle really did match, on a
  request that then goes on to be refused. It reaches no budget — the tenant is published only on the
  live path — and every route that runs without authenticating publishes its own identity after its
  own proof, so today this residue changes no answer. It is written down because **the next anonymous
  route added is where it would start to**, and because the only way to remove it is a "clear" member
  on `IUserContextWriter`, which is a decision about that port rather than about this path.

- **The cascade can satisfy "revoking a credential ends its sessions" by accident.** Because deleting
  a credential row deletes its sessions, a path that removes a credential without revoking first
  passes a test asserting the sessions are gone — while leaving nothing to say when access ended. Any
  credential-removal path must revoke explicitly **and then** delete, or the fact is unobservable.
  - **How the two paths that exist resolve it.** `RevokePasskeyHandler` and
    `GenerateRecoveryCodesHandler` each revoke and then delete — and because the delete removes the
    very rows the revocation just stamped, the schema afterwards is identical either way. So the
    evidence leaves in the response instead: each answers with the count `RevokeForCredentialAsync`
    returned, as `sessionsEnded`. That is what the returned count was for; see the revocation
    rule above.
  - **The test nobody should write** is "after revocation the credential has no active session". It
    is green with the revocation call deleted, and therefore proves nothing.
  - **On the recovery-code path a second mutation produces the same wrong number**: swapping the
    revocation with the delete also reports `0`, because the cascade has already taken the session
    rows and the sweep matches nothing. So the count discriminates ordering as well as presence.

- **Between the revocation and the delete, the tracked sessions have to be discarded.** Revoking
  loads every unrevoked `Session` into the change tracker. Remove the credential with those
  dependents still tracked and EF cascades into the copies it can see and emits its own
  `DELETE FROM sessions` — on a table granted `SELECT, INSERT, UPDATE (revoked_at_utc)` and
  deliberately no `DELETE`, so the request dies with `42501`. **The failure names a permission and
  the cause is the change tracker; do not answer it with a grant on `sessions`.** This is the same
  mechanism `EraseAccountHandler` documents for `budgets` and `GenerateRecoveryCodesHandler`
  documents for the set it replaces, on the same stack.
  - **The absent `DELETE` is what makes this loud, and one table beside it does not have that
    protection.** `recovery_code_hashes` **is** granted `DELETE`, so the same change-tracker mistake
    there succeeds silently instead of raising `42501` — see
    [recovery-codes.md](recovery-codes.md). Read the two together: the `42501` on this table is a
    diagnostic the grant matrix buys, not an inconvenience it imposes.
  - **`session_tokens` is the third table this binds, and it is the loudest.** A session now carries a
    handle, so the cascade a tracked `Session` drags behind it reaches one more relation — and the
    role holds no `DELETE` there either, so the same mistake dies with `42501` rather than removing a
    row. `GenerateRecoveryCodesHandler` is where it bites, because that path revokes and then deletes
    a credential; its never-materialise rule now names three tables.
    - **The trap needs *both* links in the tracker, which is what makes it easy to lose.** A read
      that projects — `Select(session => session.Id)` — materialises no entity, so EF has no cascade
      to walk and nothing fails. Somebody "optimising" a read into a projection will therefore find
      the rule stops biting, and will conclude it no longer applies. It does; the read simply stopped
      being the shape that triggers it.
- **Revoked and expired rows accumulate.** Nothing sweeps them, and the application role holds no
  `DELETE` grant to do it with. Not a defect at today's size; it becomes one before the product has
  many users, and the grant that a sweep needs is the one this file argues against adding.
- **`RevokeSessionsForCredentialHandler` has two callers**, `RevokePasskeyHandler` and
  `GenerateRecoveryCodesHandler`, and the `int` it returns is the only observable evidence the
  explicit revocation ran — see the cascade gotcha above. Revoking the **federated** credential
  still has no caller and is not expected to gain one: that credential is replaced rather than
  removed.
  - **The two callers do not mean the same thing by the number**, and that is the trap on this page
    for whoever edits the sweep. To `RevokePasskeyHandler` it is evidence and nothing more; to
    `GenerateRecoveryCodesHandler` it is also the condition a re-established session is written on. So
    a change to what `RevokeForCredentialAsync` counts — the obvious candidate being to narrow
    `revoked_at_utc is null` to a live-at-now reading — changes behaviour on one path while looking
    like a reporting fix on both.
  - **Both callers reach it through the command handler rather than straight to `ISessionRepository`**,
    and that is deliberate: the handler is where the clock is read, so one decision to end access is
    stamped as one instant however many rows it touches.
- **The expiry is decided by the caller, and the three callers read one value.**
  `Session.Establish` validates only that the expiry is after the creation instant; the number itself
  — **14 days** — is `SessionPolicy.Lifetime` in `Application/Sessions`, which
  `CompleteAssertionHandler`, `RedeemRecoveryCodeHandler` and `GenerateRecoveryCodesHandler` each add
  to the instant they read. It lives in Application rather than Domain because how long a session
  lasts is product policy, which
  [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) keeps above the
  invariants, and it is not on `IPasskeyCeremonyPolicy` because a session lifetime that varies per
  environment is a difference nobody meant.
  - **The equality is the rule, and one value is what makes it one.** Both credentials open a `Full`
    session — a set of recovery codes is a secret its holder possesses as an authenticator is, and
    reaches as far — and a recovery sign-in that expired sooner would tell
    somebody who has just lost their device that the way back in they were issued is worth less than
    the one they lost. The regeneration path is held to the same number by an argument of its own: its
    caller cleared a passkey gate, which is stronger than whatever opened the session that path's
    sweep took, so the session it hands back must not be worth less than the one it ended. **Any two
    of them differing is a defect rather than a decision**, and sharing the value is the only shape in
    which a single edit cannot separate them — a fourth establishing path inherits the interval by
    construction rather than by somebody remembering the rule.
  - **What is shared is the interval and nothing else.** *When* each handler establishes its session —
    after the signature verifies, after the code is spent, after the replacement set is saved — is a
    security property that path owns, argued at its own call site and stated as its own rule in
    [passkeys.md](passkeys.md) and [recovery-codes.md](recovery-codes.md). One lifetime is not licence
    to lift those sequences into anything shared; they agree about a number and about nothing else.
