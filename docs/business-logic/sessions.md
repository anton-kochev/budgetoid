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
without asking anyone. Identity lives in [users-and-ownership.md](users-and-ownership.md); this file
covers what happens *after* a credential has answered who is asking. The distinction is the whole
point: a token issued by an identity provider cannot be taken back by this product, while a session
row can be ended here, in one write, by the same role that serves every request.

**Five things establish a session and there is no sixth.** Four open a `Full` session and one
opens a `Locked` session, and all five last 14 days. The fourth is the earliest in a person's life
with the product: **completing `POST /api/registration`**, which signs somebody in on the passkey
the same request created — see [registration.md](registration.md). The fifth is the **locked
sign-in**, `POST /api/locked-session`: a Google ID token and nothing else, turned into a `Locked`
session over the account's federated credential — see the rule on it below.

**The loop is closed on the server.** All five paths mint a handle and set the cookie; a request
presenting it is authenticated from it, publishing the account and the ambient budget; and
`POST /api/me/session/revocation` ends it. **Three of the five have a screen**: `/register` runs
its creation ceremony, `/welcome` runs the assertion, and `/release` runs the locked sign-in — its
**Continue with Google** starts the trip, and on the return the screen takes the answer once and
sends it without a second press. The other two are reached today only by the integration suite —
nothing in the browser redeems a code or regenerates a set. A person reaches `/release` from
Welcome's standing link, or because the guards send a locked session there. Every request this app
makes is authenticated from the cookie except the three that carry a provider token in its place. The browser contacts the identity provider
from three screens: `/register`, to create an account, `/app/settings`, to change its address, and
`/release`, to sign in to an account nobody can open — and the email change's request carries a
provider token **beside** the cookie, never in its place; see [email-change.md](email-change.md).

## Key Entities

- **Session** — one established sign-in. It carries a `Guid Id` of its own, the `UserId` whose
  account it reaches, the `CredentialId` of the credential that established it, that credential's
  `CredentialType`, a `SessionKind`, the instant it began, the instant it expires, and a nullable
  instant at which it was revoked. It holds no navigation properties: it names its user and its
  credential by id, exactly as **Credential** names its user by id.
- **SessionKind** — `Locked` or `Full`, **derived from the establishing credential's type**, never
  supplied. A `Passkey` and a `RecoveryCodes` credential each open a `Full` session; a `Federated`
  one opens a `Locked` session and is the **only** type that does. `Session.ReadsBudgetContent` is
  the computed reading of that, true only for `Full`. `Locked` is declared first so that
  `default(SessionKind)` is the value reaching nothing — the fail-closed direction, not alphabetical
  accident.
- **`Session.CredentialType`** — a copy of the establishing credential's type, carried on the row so
  the database can check the derivation. A `CHECK` sees only the row in front of it, so the fact
  `kind` is derived from has to be on that row for the derivation to be checkable at all.
- **SessionToken** — the handle one session will be presented by, stored as `SHA-256(token)`. It
  carries the digest, the `SessionId` it opens and the `UserId` that owns it, and **nothing else** —
  no timestamps, because the session row already carries when it began, when it expires and whether
  it was revoked, and a second copy is a second set of the same facts to keep in step.

  **Why it is a table of its own.** A presented token has to be looked up *before* the request has
  an identity, and `sessions` is policed by `user_isolation` keyed on `app.current_user_id` —
  exactly the value the lookup exists to produce. So the discovery key goes on its own **exempt**
  table and everything read *after* the answer stays on the policed one. That is the split
  [ADR 0012](../decisions/0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md)
  argues for a passkey's material, applied here by
  [ADR 0019](../decisions/0019-authenticate-a-request-from-a-first-party-session-cookie.md). The
  ordering rule below states what breaks if the two are folded back together.

  **What the hash buys is not what a recovery code's hash buys.** A recovery code never reaches this
  server; a session token does — this server mints it and reads it on every request — so the digest
  is not a claim that the value is unknown here. It is a claim about what a *copy* of the table is
  worth: a backup, a replica or one unbounded read yields digests, and a digest of a 256-bit uniform
  value cannot be turned back into the token a request would have to present.

A session record deliberately carries **no** last-used instant, device name, IP address or user
agent. Each would be a column nothing reads, and a column nothing reads is data held for no one.

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

**`SESSION_TOKEN` is drawn one-to-many because that is what the schema holds.** The primary key is
the digest, so nothing stops a session from having several token rows or none. One-to-one would need
a unique constraint on `session_id` for one half and a trigger for the other, and
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) forbids pushing
procedural logic down to satisfy "lowest layer".

**The port's shape holds it instead.** `ISessionRepository.AddAsync` takes the session **and** its
token with no overload taking a session alone, so a session cannot be written without its handle by
construction; `ISessionTokenRepository` stays read-only from the other side, since a second way to
write a token is a way to produce one naming a session that was never committed.
`IRegistrationRepository.RegisterAsync` is a **second writer** and the invariant survives, because
the pairing was what was pinned rather than the port: that path has no transaction, so a second
`AddAsync` — which saves on its own — would be a second transaction and the account would stop being
atomic, silently, while `Domain.Users.Registration` carries the session **and** its token as
required members. A third writer is a decision rather than a refactor.

## Constraints

### MUST

- **A session is isolated by user, like `users` and `budgets`.** `sessions` carries `user_id` and no
  `budget_id`.
  - **Why**: a session belongs to a person; the budgets that person owns are reached through their
    own policies, one layer down.
  - **Enforced in**: the `user_isolation` policy on `sessions` in `app-role-grants.sql`, comparing
    `user_id` against `app.current_user_id` in both `USING` and `WITH CHECK`;
    `SessionContextInterceptor` puts that setting on every connection the context opens.
    `RowLevelSecurityCoverage` reaches the verdict from the table's own columns rather than from a
    list, so `RlsCoverageTests` and the deploy-time verifier both required this policy the moment
    the table existed. `RlsIsolationTests` proves the three halves — another person's rows are
    invisible, an insert naming another person is refused, and a connection naming nobody fails with
    `22P02`. See [ADR 0011](../decisions/0011-police-the-user-owned-tables.md).
    - **`user_id` is `NOT NULL`, and that is load-bearing**: a `NULL` owner fails *closed*, because
      `NULL = anything` is `NULL` and never true, so the row would be invisible to every session
      including the one that wrote it — a write that succeeds and a read that cannot be explained.

- **A session's identity columns — `user_id`, `credential_id`, `credential_type`, `kind`,
  `created_at_utc`, `expires_at_utc` — are immutable. `revoked_at_utc` is the only column an edit
  may reach.**
  - **Why**: changing `credential_id` would relabel which key opened the door, and which key opened
    it is the fact revocation is decided by. Changing `kind` would hand budget content to a session
    a federated credential opened, the one thing the kind exists to refuse.
  - **Enforced in**: the grant matrix. `GRANT SELECT, INSERT, DELETE ON sessions` plus
    `GRANT UPDATE (revoked_at_utc) ON sessions` — the other six are immutable by **omission from the
    column list**, never by a `REVOKE`, which additive column privileges could not express. A
    one-column list is still a list and must not be collapsed into a table-wide grant. Above it,
    `Session` exposes no public setter. `AppRoleGrantsTests` pins a `42501` for each of them in the
    same test as a permitted `revoked_at_utc` update reporting one affected row — the affected-row
    count is what stops the pair passing when row-level security matched nothing. See
    [ADR 0004](../decisions/0004-connect-as-a-least-privilege-role.md).
    - **Omission refuses an edit in place, and no longer refuses the same row written again.**
      [Guessing] Reasoned, not run: holding `DELETE` and `INSERT` on `sessions`, and `SELECT` and
      `INSERT` on `session_tokens`, the role can delete a session and insert one under the same id,
      with its handle, and a later `expires_at_utc` — `CK_sessions_lifetime` asks only that the
      expiry follow the creation. So the grant matrix holds these columns against an `UPDATE` and
      not against a delete and a re-insert. What holds them there is `Session` exposing no setter,
      and review.

- **A session's expiry MUST be after its creation.**
  - **Why**: a session whose expiry is at or before its creation was never live, and a row that was
    never live can only mislead whatever reads it.
  - **Enforced in**: `CK_sessions_lifetime` (`expires_at_utc > created_at_utc`), restated in
    `Session.Establish` so a bad call fails with a named field rather than a raw `23514`. No request
    can reach it: each of the five establishing paths computes the expiry by adding the shared
    lifetime to the instant it just read. The restatement guards against a future caller that
    computes an expiry from something a request supplied.

### MUST NOT

- **A `DELETE` on `sessions` MUST NOT take more than one of its two callers may take.** The
  application role holds `DELETE` there for two acts, and each is entitled to a different set. The
  **ended-session sweep** takes the published account's sessions that have already ended — revoked
  or expired — when a session is established on it, and leaves every live one. Four of the five
  establishing paths run it; registration has nothing to sweep — see
  [Integration Points](#integration-points). **Displacement** takes the one session the browser's
  incoming cookie names, live or ended, on whichever account owns it, once a session has been
  established over that cookie — see its rule under [Business Rules](#business-rules--invariants).
  - **Why**: revocation writes `revoked_at_utc`, because the row is what says access ended and when
    — see the revocation rule below. A delete of a live row a browser still presents would sign that
    browser out and leave nothing saying so. The sweep takes no live row. Displacement takes one only
    from the browser being handed a new cookie on the same response, which stops presenting the old
    handle either way — except when that response is lost, the cost the displacement rule states,
    and except when the handle is held in two places. A copied cookie's other holder is signed out
    by the displacement with no revoked row to say when; a revoked row would have stood at most
    until that account's next sign-in swept it anyway.
    The sweep's half of the grant serves the other end of a row's life. Without it, a session row
    leaves only by descending from a deleted credential or account, or by displacement, which
    reaches only a row some browser still presents — so **ended rows accumulate**:
    an account's `sessions` rows become a timestamped record of its sign-ins for the life of the
    credential that opened them — the record the behavioural-record rule in
    [users-and-ownership.md](users-and-ownership.md#must) weighs. The sweep bounds that record to
    the sessions that were live when the account's most recent session was established, whether or
    not they have ended since. The cascade from `credentials`, and through it from `users`, takes
    rows too.
    - **Where the sweep runs is the rule, and three other placements were refused.** It runs inside
      `SessionRepository.AddAsync`, in the same `SaveChanges` as the new session and its handle, so
      a sign-in that fails to store deletes nothing and a sweep that fails stores no sign-in. A
      **scheduled sweep** needs a role that reaches every account — the elevated reach
      [ADR 0004](../decisions/0004-connect-as-a-least-privilege-role.md) keeps off the application
      role — and the API scales to zero. **Deleting at sign-out** would make a second sign-out
      present a cookie naming no row, the `401` the sign-out rule's idempotence exists to avoid, and
      would remove the row saying when the person signed out. **Sweeping at authentication** is a
      write on every request, on a path no transaction may wrap.
    - **"Only ended rows" sits in the application, which ADR 0002 requires stating.** The database
      cannot judge liveness against the application's clock declaratively. A restrictive
      `FOR DELETE` policy comparing against `now()` was refused: it is a second clock beside the one
      the handlers read, the suite pins the sweep at fixed instants that a policy reading `now()`
      would judge differently, and it would be a third input to isolation on a table whose policy
      reads `user_id` alone — the axis the MUST NOT on `kind` below refuses. The sweep filters
      through `Session.IsActiveAt` at the new session's `CreatedAtUtc`, the reading that decides
      whether a request is authenticated, so "ended" keeps one spelling. `IsActiveAt` reads any
      revocation as ended, so a row revoked *after* that instant goes too.
    - **Whose rows it reaches is the database's.** The read names no owner; `user_isolation` covers
      the delete as it covers the read, so the sweep reaches the account the caller published. The
      handles leave by the `ON DELETE CASCADE` from `sessions`, which runs as the table's owner, so
      `session_tokens` still holds no `DELETE`.
    - **What remains.** An account that never signs in again keeps its last batch of ended rows.
      Live rows still record recent sign-ins, each normally held by a browser: a session whose
      cookie a later sign-in overwrote is displaced, not left behind — see the overwrite gotcha.
      The exceptions are rows no browser holds, live until they expire or a revocation ends them,
      and these are the ways known to leave one, not a closed list: the new session of an
      establishment whose displacement failed, the new session of one whose response never reached
      the browser — both in the displacement rule — and a session whose browser discarded its
      cookie without signing out, which tells the server nothing — [Guessing] reasoned, not run.
      [Guessing] Two more, reasoned from the code and not run: two establishing requests from one
      browser at once, such as two tabs, both carry the old cookie — one displaces its session, the
      other finds nothing, and the browser keeps one of the two new cookies, so the other new
      session is held by nobody; and a client that disconnects after the commit and before
      displacement cancels the request's token, which cancels displacement, so the old session
      survives and the new one is orphaned.
      And PostgreSQL's own statistics counter for deletes on `sessions` (`n_tup_del` in
      `pg_stat_user_tables`) counts every delete on the table — measured on a PostgreSQL 17
      container, where a delete later rolled back counted too — so swept rows, displaced rows and
      rows cascading from a deleted credential or account all land in it, and `session_tokens`'
      counter moves with it through the cascade. Each is a per-table total that names no account.
  - **Enforced in**: the grant matrix, which pins both ends of the delete's reach —
    `AppRoleGrantsTests.Database_LetsTheAppRoleDeleteItsOwnSession_AndTheCascadeTakesItsHandle` and
    `Database_RefusesADeleteOnASessionToken_WhileTheCascadeFromItsSessionStillTakesIt` — and
    `RlsIsolationTests.Database_RefusesToDeleteAnotherUsersSession_WhileStillAllowingItsOwn`, which
    pins the owner scope. The sweep is pinned by `SessionRepositoryTests`' `AddAsync_*` cases on the
    app role: ended rows on two credentials go and live ones stay, a row revoked after the
    establishing instant among the ended; the expiry boundary is `IsActiveAt`'s; another account's
    ended rows stay; a failed insert deletes nothing and a failed delete stores nothing; and a row
    another request deleted or revoked first is re-read rather than raised. Displacement's half is
    `SessionRepository.RemoveAsync`, pinned by `RemoveAsync_DeletesOnlyTheNamedSessionAndItsHandle`
    (the named row unrevoked and revoked, another row on the same credential kept),
    `RemoveAsync_ForAnotherAccountsSession_RemovesNothing` and
    `RemoveAsync_WhenRevokedConcurrently_StillRemovesIt`.
    **Nothing confines the grant to its two callers**: with it, EF's own delete of a tracked session
    succeeds rather than raising `42501` — see the change-tracker gotcha.

- **No policy on `sessions` may read `kind`.**
  - **Why**: the two policies the schema carries answer *whose* a row is. The kind answers something
    else — how far into their own account a person's own credential reaches — and a predicate
    consulting it would invent a third isolation axis beside those two. It is also the wrong table:
    the rule has to refuse reads of `accounts`, `transactions` and the rest, and a policy on
    `sessions` governs `sessions`.
    - **A correction worth reading, because the obvious repair rests on it.** This entry once argued
      that a locked session satisfies `budget_isolation` nowhere *because it resolves no ambient
      budget*. That was never true. `AuthenticateSessionHandler` publishes the tenant for every
      **live** session whatever its kind — only an *ended* one is left with no budget — so a locked
      session reaching a budget-scoped route was reaching it with its own budget resolved and being
      answered normally. Nothing was refusing this until the requirement below.
  - **Enforced in**: the `user_isolation` policy compares `user_id` alone, and no other policy
    exists on the table. What the kind *is* enforced by is the application's fallback authorization
    policy — see the rule below, which carries the ADR 0002 statement for why it sits there.

- **The API MUST NOT set any cookie but `__Host-budgetoid-session`.**
  - **Why**: the session handle is the one thing a cookie is needed for, and it serves the request.
    Any other cookie would be set for some other purpose — a preference, tracking, analytics, load
    balancer affinity — which is data the product does not keep, and it would be the first thing to
    need a consent surface. The product presents none because it has nothing to ask consent for. No
    framework default writes one today: no cookie authentication scheme, antiforgery, session or
    TempData middleware, or OpenID Connect handler is registered — only the session scheme and
    `JwtBearer`, which is stateless.
  - **Enforced in**: `CookieCensusTests.Traffic_SetsNoCookieButTheSession`. It drives the log
    census's traffic, so every declared route answers a 2xx at least once — the same floor
    `LogRedactionTests` asserts — then requires the session cookie to have been seen and no other
    cookie name on any response, matched or not, at any status. `RouteTally` captures the names from
    `Response.OnStarting`, registered first so it runs last, and
    `Census_ReportsACookieAddedOutsideAnEndpoint` is the control: a cookie appended outside any
    endpoint, after the pipeline has returned, is still seen.
    - **What it does not reach**: a branch the traffic never takes; the provider-token path on the
      real bearer handler; a cookie appended from an `OnStarting` callback registered before the
      tally's; a host outside Development, since the census host runs in Development and a cookie
      set only on another environment's branch is not seen; a cookie written outside ASP.NET Core's
      response headers, which nothing in this API can do; cookies set outside the API — the web
      client's scripts and the static host; and the session cookie's own attributes, which the
      cookie rule below pins.

## Business Rules & Invariants

- **Rule**: A session's kind is **derived** from the establishing credential's type. There is no way
  to ask for one.
- **Why**: an authorization exchange with an identity provider returns claims, not a secret the
  client can turn into a key. So any account reachable by a provider sign-in would be an account the
  provider's holder could read — which is why a federated credential opens a session reaching no
  budget content, and **`federated` is the only credential type that cannot**. The rule runs that
  way round: a passkey and a set of recovery codes are each a secret in the holder's own possession,
  so both open a `Full` session. **The key custody those secrets carry is exercised on one of the
  two**: a passkey sign-in derives a key-encryption key from the assertion's PRF branch, reads the
  account's wrapped rows, opens the one pair that key was sealed against and holds the account's two
  keys for the visit — so a federated credential's inability to do any of that is a difference the
  client can now demonstrate rather than only argue. Nothing redeems a code in a browser yet, so the recovery-code half of the
  same claim is still carried by possession alone. See [recovery-codes.md](recovery-codes.md) and
  [account-keys.md](account-keys.md).
- **Enforced in**: `CK_sessions_kind_matches_credential`,
  `(kind = 'full') = (credential_type in ('passkey', 'recovery_codes'))`, the lowest layer that can
  state the rule declaratively. Without it the rule lived only in the factory while
  the `INSERT` grant on `sessions` stayed table-wide —
  `(credential_id = <a federated credential>, kind = 'full')` was a fully storable row.
  `CK_sessions_kind` bounds only the vocabulary and the composite foreign key proves only whose the
  two rows are; neither refuses that pair. Above it, `Session.Establish` takes the `Credential` and
  no kind, deriving it through a switch with every arm written out and a throwing discard arm.
  - **The full side stays enumerated, and the spelling is a decision rather than a style.** The
    mirror form — `(kind = 'locked') = (credential_type = 'federated')` — says the same thing about
    every row this schema can hold today, reads better, and is what a later reader will propose. It
    fails **open**: a fourth credential type is not `federated`, so it satisfies the right-hand side
    and is granted a full session by default, with nobody having decided that. The shipped form
    fails closed. **No test in the suite can tell the two spellings apart until that fourth type
    exists**, so this paragraph and the comment beside the constraint are the only things carrying
    the difference. It is also why adding `recovery_codes` to the `in` list was the correct edit
    rather than the occasion to simplify.
  - **The rule is unrepresentable, not merely untested.**
    `SessionTests.Session_ExposesNoWayToChooseItsKind` reflects over the public surface and fails on
    any parameter or settable property of type `SessionKind`. Without it, the obvious accommodation
    for a caller wanting a different kind is an overload taking one, and the rule dissolves with no
    test going red.
  - **The copy cannot drift**: `credential_type` duplicates `credentials.type`, `credentials` holds
    no `UPDATE` grant of any shape, and the composite foreign key below ties the two columns
    together on every insert.
- **Example**: a `RecoveryCodes` credential handed to `Session.Establish` yields a `Full` session; a
  `Federated` one yields `Locked`; a fourth credential type yields a throw rather than a default.
- **Counterexample**: a `bool canReadBudgetContent` argument on the factory. It reads as a
  permission the caller sets, and the first caller that sets it wrongly is the whole rule gone.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: Revocation is an **`UPDATE` of `revoked_at_utc`**, never a `DELETE`, and it is
  **idempotent** — an already-revoked session keeps the instant access actually ended.
- **Why**: the row is what says access ended and when. Deleting it at revocation leaves nothing
  saying so, and it cannot tell "already revoked" from "never existed" — a distinction anything
  reporting a revocation needs. The role does hold `DELETE` on `sessions`, for the ended-session
  sweep and for displacement, and neither is a revocation — see the MUST NOT on `DELETE` above. The
  usual argument for deleting at revocation, that revoked rows accumulate, does not separate the two
  options: an unrevoked but expired row accumulates identically, so whatever answers it has to take
  both kinds of ended row. The sweep does, at the account's next sign-in, so a revoked row stays
  readable until then, unless a sign-in in a browser still presenting its cookie displaces it
  sooner. **Note what this
  is not**: a tombstone. A session row exists
  only while its account does, so a revoked session leaves nothing behind an erasure.
- **Enforced in**: `Session.Revoke` returns without writing when `RevokedAtUtc` is already set;
  `SessionRepository.RevokeForCredentialAsync` loads the credential's unrevoked sessions and calls
  it per row. `ExecuteUpdateAsync` is a compile error under `BannedSymbols.txt`, and the ban buys
  correctness here rather than uniformity: a set-based `UPDATE` would rewrite every matched row's
  instant on every call and report a retry as if it had ended access a second time.
  - **Concurrently, too**: `Session.Revoke`'s idempotence is a property of one object in memory, so
    on its own it does not survive two sweeps running at once — both would read the rows as
    unrevoked and the later commit would overwrite the first instant. `revoked_at_utc` is therefore
    a **concurrency token**: the `UPDATE` carries `and revoked_at_utc is null`, the losing sweep
    matches zero rows and raises `DbUpdateConcurrencyException`, and `RevokeForCredentialAsync`
    answers it by re-reading and retrying. A token in the `WHERE` clause needs only `SELECT`, so the
    `GRANT UPDATE (revoked_at_utc)` column list is unaffected.
- **Example** — **what the returned count means**: the number of sessions **this call** ended,
  excluding any a concurrent sweep ended first. It counts the **unrevoked**, not the live: the
  filter is `revoked_at_utc is null` and says nothing about expiry, so a session that expired with
  nobody revoking it is in the number — **while it is still there**. The ended-session sweep takes
  such a row at the account's next sign-in, so whether an expired session counts depends on whether
  a session was established on the account between its expiry and this call — or a sign-in in a
  browser still presenting its cookie displaced it. A session still live
  when the call runs has never been swept, because it was live at every sign-in since it began, so
  it always counts — on the generation path that includes the caller's own session whenever the
  caller signed in with the set being replaced. It reaches the wire on both paths as
  `sessionsEnded`, which makes it a published contract; on the generation path it is also the
  condition that path's re-established session is written on, so **what this number counts cannot
  be changed on one caller alone**. See [recovery-codes.md](recovery-codes.md).
- **Counterexample**: tightening the filter to "live at the caller's instant". It would look like a
  fix to the recovery-code rule and would silently change what a passkey revocation reports.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: Revoking a credential ends **only** the sessions that credential established. Every
  other credential on the same account stays signed in.
- **Why**: revoking one device is the reason the operation exists.
- **Enforced in**: `SessionRepository.RevokeForCredentialAsync` filters on `CredentialId` and never
  on `UserId`, and `RevokeSessionsForCredentialHandler` names a credential in its command.
- **Example**: an account holding a passkey on a phone and another on a laptop. Losing the phone
  revokes the phone passkey's sessions; the laptop stays signed in.
- **Counterexample** — the one to watch: a predicate keyed on `UserId`. Every session in a
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
- **Enforced in**: one composite foreign key,
  `sessions (credential_id, user_id, credential_type) → credentials (id, user_id, type)`, against
  the `AK_credentials_id_user_id_type` alternate key — the same idiom the budget-owned tables use
  one level down. `credential_type` rides along so a session cannot disagree with its credential
  about what opened it, which is what makes `CK_sessions_kind_matches_credential` a claim about the
  real credential rather than about a value the row asserted for itself.
  `SessionSchemaTests.Database_RefusesASessionWhoseCredentialBelongsToAnotherUser` pins the `23503`.
  - **Consequence**: `credentials` gained an alternate key and **no new column**, which matters —
    the exemption in `RowLevelSecurityCoverage.Exemptions` pins that table's exact column set.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: Deleting a credential deletes its sessions (`ON DELETE CASCADE`). There is deliberately
  **no** second foreign key from `sessions` to `users`.
- **Why**: `Restrict` would let a session hold up the deletion of a credential, and through it an
  account erasure — a row of access bookkeeping outranking a person's request to be forgotten. The
  credential's own cascade to `users` reaches sessions transitively, so a direct one would add
  nothing but another constraint name for the pinned snapshots to carry.
- **Enforced in**: `SessionConfiguration`;
  `SessionSchemaTests.Database_RemovesASessionWithTheCredentialThatEstablishedIt` and
  `Database_RemovesASessionWithTheUserThatOwnsIt` pin both hops.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: A request authenticates from an opaque token in a first-party cookie, and the two reads
  that turn it into an identity happen in **one order that cannot be rearranged**: the exempt
  `session_tokens` lookup first, the identity published second, the policed `sessions` row third.
- **Why**: this is the circularity
  [ADR 0019](../decisions/0019-authenticate-a-request-from-a-first-party-session-cookie.md) exists
  for. `sessions` is policed by `user_isolation` keyed on `app.current_user_id`, exactly the value
  the lookup exists to produce, so a session read issued before the publication meets `''::uuid` and
  raises `22P02` — on **every** authenticated request, not on an edge. The digest is what makes
  publishing on the strength of the lookup alone defensible: it is SHA-256 of a 256-bit value this
  server minted, so a caller presenting one it was never given is guessing it.
- **Enforced in**: `AuthenticateSessionHandler`, in Application, where the order is the security
  property; `SessionCookieAuthenticationHandler` in the API decodes the cookie and decides, and
  looks nothing up — `CompositionBoundaryTests` holds the API to composing Infrastructure rather
  than consuming it, and this is the path where that shortcut would cost most.
  `AuthenticateSessionHandlerTests` pins the order in both directions over
  `RecordingUserContextWriter`; `SessionCookieAuthenticationTests` drives the whole path over the
  real least-privilege connection, which is the test that dies with `22P02` if anyone ever wraps it.
  - **No transaction anywhere on this path**, the same trap from the other side: one opened before
    the publication configures its connection while the setting is still empty, and every policed
    statement inside it fails. `RegisterAccountHandler`, `CompleteAssertionHandler`,
    `RedeemRecoveryCodeHandler` and `EstablishLockedSessionHandler` each carry the same warning. Nothing here writes, so an atomic unit
    would be protecting nothing.
  - **Displacement is a second caller of `AuthenticateSessionHandler`, not a second owner of the
    order.** `DisplaceSessionHandler` hands it the incoming cookie's digest rather than reading
    `session_tokens` itself, so the three steps still run in one place. It runs in a dependency
    scope of its own, so the identity published for it is that scope's and never the request's, and
    it opens no transaction either — its one write, the delete, comes after the publication. See the
    displacement rule below.
  - **Two round trips per authenticated request**, stated as the cost rather than hidden. Folding
    them into one is the "denormalise the expiry onto the exempt table" alternative ADR 0019
    refuses.
- **Counterexample**: reading `sessions` first and publishing afterwards, which reads as the same
  three steps in a tidier order and refuses every request in the product.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: The handle travels in `__Host-budgetoid-session` — `HttpOnly`, `Secure`, `SameSite=Lax`,
  `Path=/`, no `Domain` — and its expiry is **absolute, never sliding**.
- **Why**: the `__Host-` prefix is a rule a browser enforces rather than a naming style: it refuses
  the cookie unless it is `Secure`, `Path=/` and carries no `Domain`, which stops a neighbouring
  host from setting one. `HttpOnly` is why no response body ever carries a session identifier — the
  cookie is the handle precisely so that script is not. `Lax` suffices because the frontend and the
  API share one registrable domain; a cross-site topology would have forced `None`, the deployment
  argument [ADR 0010](../decisions/0010-serve-the-app-from-a-custom-domain.md) carries. A
  **sliding** expiry would need `GRANT UPDATE (expires_at_utc)`, a column the list leaves off on
  purpose, or a delete and a re-insert under the same id, which the grants no longer refuse
  ([Guessing] reasoned, not run — see the identity-columns MUST) and nothing in `Session` offers.
  Either way it would write a row on every request to buy it.
- **Enforced in**: `SessionCookie`, which owns the name and builds the attributes once so the issue
  and the clear cannot drift. `SessionCookieTests` pins each attribute. It is also the only cookie
  the API sets — see the MUST NOT above. `SessionCookie.TryReadTokenHash` is the one reader of the
  presented value, shared by the session scheme and by displacement, and `SessionCookieWriter`'s
  file is the one that calls `SessionCookie.Issue` — see the displacement rule below.
  - **The clear must match the issue attribute for attribute**, and this is the pin most worth
    having: a browser silently keeps a cookie whose clear does not match, and the symptom is a
    sign-out that appears to work and a session that comes back.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: Establishing a session **displaces** the one the browser's incoming
  `__Host-budgetoid-session` names: once the establishing handler has returned, that session is
  deleted — live or ended, on whichever account owns it — and only then is the new cookie written.
  It happens only on the arm that established a session, and a cookie naming no row displaces
  nothing.
- **Why**: overwriting a cookie ends nothing. The browser stops presenting the old handle, and its
  row stays live until it expires, held by no browser — a standing record that this browser was
  signed in to that account. Neither the sweep nor a sign-out reaches that row. The ended-session
  sweep takes only ended rows of the account signing in, while the cookie may name a live session,
  or a session of another account, whose rows `user_isolation` hides from this one; and no browser
  is left to sign it out. Only its credential or its account leaving takes it sooner — a passkey
  revocation, an email change retiring the federated credential, a regeneration deleting the old
  set, or an erasure.
  - **Displacement takes no more than sign-out could end.** It needs the cookie's handle, the same
    proof `POST /api/me/session/revocation` needs, and takes the one session that handle names. An
    ended row it takes is one its own account's next sweep would take. Its handle leaves by the
    cascade from `sessions`, and `session_tokens` is the table holding a foreign key into
    `sessions`, so the delete reaches nothing past the handle.
  - **After the handler, never before.** Every refusal on an establishing path leaves before
    `SessionCookieWriter` runs. Measured: displacing before the handler reddens both tests that a
    refused sign-in leaves the presented session live — a passkey assertion that does not verify, in
    `PasskeyCeremonyTests`, and a registration whose address is taken, in `AccountRegistrationTests`.
    Run earlier, a refusal would sign the browser out of the session it had, and a stranger holding
    nothing could cause that. A regeneration that re-establishes no session hands nothing over, so
    it displaces nothing.
  - **The delete comes before the cookie.** Written first, the cookie would ride a response that a
    failed delete is about to replace with an error.
  - **In a dependency scope of its own.** `SessionCookieWriter` creates a child scope and resolves
    `DisplaceSessionHandler` there. That handler authenticates the digest through
    `AuthenticateSessionHandler`, which publishes the cookie's account into the child scope — before
    it judges liveness, which is why an ended session is displaced too — and then calls
    `ISessionRepository.RemoveAsync`, which names no owner and leaves the scoping to
    `user_isolation`. Measured: deleting in the request's scope by the cookie scheme's session id
    reddens the cross-account cases — a locked sign-in over another account's locked cookie, a
    registration from a browser holding another account's session, a passkey sign-in over another
    account's ended cookie — while the same-account tests
    `PasskeyCeremonyTests.PasskeySignIn_OverItsOwnLiveSession_DeletesTheSessionTheCookieNamed`,
    `RecoveryCodeRedemptionTests.Redemption_OverItsOwnLiveSession_DeletesTheSessionTheCookieNamed`
    and `LockedSignInEndpointTests.LockedSignIn_OverALiveLockedSession_DeletesTheReplacedSession`
    stayed green.
    `Generation_ThatReestablishes_DeletesTheCallersPasskeySession`, also same-account, was not
    reported under that run. The request is published as the new account, and the policy hides the
    old account's row from it.
  - **One reader of the cookie, the scheme's own.** The digest comes from
    `SessionCookie.TryReadTokenHash`, so a value that reader refuses to decode is not one
    displacement deletes by. Measured: a writer that decoded leniently, truncating to 32 bytes,
    deleted a live full session presented as its handle with one byte appended, on the locked
    sign-in. With the shared strict reader the scheme refuses that cookie, the `409` `full_session`
    does not fire, and the full session survives.
  - **No check that the cookie names the new session.** The new handle was minted on this request
    and the incoming cookie was issued before it.
  - **A cookie naming no row is a no-op**, whether it was never issued or its row is already gone —
    including a cookie whose ended session the establishing path's own sweep just took. Measured:
    without that guard, a sign-in from a browser holding a cookie that named no row answered `500`
    after the new session committed.
  - **A concurrent sign-out does not fail it.** `RemoveAsync` re-reads when a revocation lands
    between its read and its delete — `revoked_at_utc` is a concurrency token — up to three
    attempts. Measured: without the retry, or without detaching the failed delete,
    `RemoveAsync_WhenRevokedConcurrently_StillRemovesIt` fails with `DbUpdateConcurrencyException`.
    Whether a row was removed does not change the response.
  - **The failure window**, stated as the cost. If the delete throws, the request answers `500` after
    the new session committed, and no cookie is written, because the exception handler clears the
    response. The new session stands with no browser holding it, and the old one survives in the
    browser still presenting it. What reaches this is a failure underneath — the database, or the
    bounded retries running out — or a cancelled request, whose client has already gone; that case
    is among the residues under the MUST NOT on `DELETE`. A failure stays loud: measured,
    swallowing it turned that `500` into a `200` with the old session left live.
  - **A lost establishing response.** When the response carrying the new cookie never reaches the
    browser, the browser keeps its old cookie, whose row displacement has already deleted, and the
    new session stands with no browser holding it. The old cookie's next request is answered `401` —
    another way a cookie comes to name no row, beside an erasure, the sweep and its credential's
    deletion; see the ended-session rule. Without displacement that browser would have kept its old session.
  - **Another tab's request in flight.** [Guessing] Reasoned, not run in a browser: tabs share one
    cookie jar, so a request another tab sent under the old cookie, reaching the server after the
    delete, is answered `401` while the jar holds, or is about to hold, the new cookie. That `401`
    speaks for the cookie the request carried, not for the browser. Before displacement the old
    session stayed live and that request succeeded. The client therefore judges a `401` before it
    ends a tab's session — see the interceptor rule.
  - **Six shapes were refused**, and
    [ADR 0030](../decisions/0030-displace-the-session-an-overwritten-cookie-names.md) records them.
    - **Displacing before the handler** — the measured refusal above.
    - **`Response.OnCompleted`.** It runs after the response is sent, so a failure goes to the log
      rather than to the person, and the delete races the browser's next request.
    - **A displaced id handed to `AddAsync`**, deleted in the establishing save. That save runs under
      the new account's publication, so it reaches the same account only, and across accounts it
      silently deletes nothing.
    - **A permissive `FOR DELETE` policy keyed on an `app.displaced_session_id` setting.** It is a
      third input to isolation on a table whose policy reads `user_id` alone, and one mis-published
      value deletes any session. That refusal is also this rule's
      [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) statement: telling
      the database which row the cookie names takes a setting like that one, so the choice of row
      sits in the application.
    - **Republishing the cookie's account in the request's scope.** Anything the request did after
      it would run as that account. Measured: resolving `DisplaceSessionHandler` from the request's
      scope passes every endpoint test, because every establishing endpoint writes the cookie last,
      and leaves the request published as the cookie's account; only
      `SessionCookieWriterTests.WriteEstablishedAsync_OverAnotherAccountsCookie_LeavesTheRequestPublishedAsItWas`
      catches it.
    - **Revoking instead of deleting.** A revoked row stays until its own account next signs in, and
      when the cookie named another account, that sign-in may never come.
- **Enforced in**: `SessionCookieWriter.WriteEstablishedAsync` in the API, which each establishing
  endpoint calls on its established arm, after its handler returned.
  `SessionCookieIssueCensusTests` holds that one file calls `SessionCookie.Issue`, the writer's —
  it counts files, not call sites — and measured, a direct `Issue` in an endpoint reddens it. `DisplaceSessionHandler` in
  `Application/Sessions/DisplaceSession`, and `SessionRepository.RemoveAsync` beneath it, pinned by
  the `RemoveAsync_*` cases named under the MUST NOT on `DELETE`. The endpoint cases, the
  cross-account and refused-sign-in ones measured above among them, sit in the establishing paths'
  own integration tests.
- **Example**: somebody signs in on a shared laptop and walks away without signing out. A second
  person signs in on the same browser with their own passkey. The first person's live session is
  deleted with its handle, so the row saying this browser was signed in to that account goes too.
- **Counterexample**: an endpoint calling `SessionCookie.Issue` itself. It compiles, answers `200`
  and sets a working cookie, and leaves the overwritten session live, held by nobody.
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
  ones that *set* a cookie, and login-CSRF is signing somebody into an account they do not own so
  that what they record next is filed under it.
- **Enforced in**: `FirstPartyRequestMiddleware`, registered **after** `UseCors` — so a preflight is
  answered by the CORS middleware and never meets a check no `OPTIONS` request can satisfy — and
  **before** `UseAuthentication`. `/health` is named from `ServiceDefaults.Extensions.HealthPath`
  rather than typed again. `FirstPartyRequestTests` sweeps the route table and pins the exempt set
  in both directions.
  - **The suite cannot see this control**, because `ApiFactory` gives every client it hands out the
    header. `FirstPartyRequestTests` therefore removes it again on its own clients, and the two are
    a pair: delete the factory's line and the whole suite answers 403; delete the removal and the
    three tests that exist to withhold the header start sending it and stay green while proving the
    opposite.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: The web client decides **once** whether a request is going to this product's API, and
  that one answer carries **three** effects: `withCredentials: true` and the `X-Budgetoid-Client`
  header on every such request, and the `Authorization: Bearer` header on **four routes and no
  others**, from two sources: `POST /api/registration/options` and `POST /api/registration` take the
  id token the library stored, and `POST /api/me/email-change` and `POST /api/locked-session` take
  only the token their own request carries on the `PROVIDER_CREDENTIAL` context token. The decision
  compares **origins**, never a string prefix, and the origin is settled **before** the route is
  looked at.
- **Why**: the three effects share a predicate because two predicates drift, and the drift is silent
  in both directions. Drop the cookie and every request arrives unauthenticated; drop the header and
  every request answers 403; widen the predicate and the browser hands this app's credentials to
  somebody else's host. That last is not hypothetical: `url.startsWith(apiBaseUrl)` — the shape the
  bearer-only interceptor shipped with — admits `https://api.budgetoid.app.attacker.example`, a name
  anybody can register. The cookie itself is safe there, because a browser scopes `__Host-` cookies
  to the registrable domain that set them; the **bearer** is not, and it is a token this app
  volunteers. Two halves a reader will fold together: the bearer is conditional on holding an id
  token and the other two are **not**, because a browser with a session cookie and no id token is
  every browser after registration; and an empty `apiBaseUrl` classifies **nothing** as this API,
  because `''` is a prefix of every string on earth and failing open there hands credentials to
  every request the app makes.
  - **The bearer is permanent on the two registration routes.** They authenticate on the provider's
    scheme and nothing else, because an account may not exist without a completed provider exchange
    and there is no first-party credential to present on the one call that creates the first-party
    account.
  - **On the email change the bearer comes from the request, never from storage.** The library's
    storage is either empty — the provider-token rule below cleared it when the session began — or
    holds a token an abandoned registration left before that clear ran. It never holds the token
    this change was handed, which the return took into memory and discarded from storage. So a
    stored token is at best somebody's old answer to another question, sent on a request that was
    handed none. `MeApiService.changeEmail` puts the token the email
    change's return handed over on the context and writes no header itself; the interceptor reads
    that context on the exact path `EMAIL_CHANGE_PATH` and ignores it on every other route. An empty
    string is no credential. See [email-change.md](email-change.md).
  - **The locked sign-in takes its bearer the same way, though it holds no session.** Its route is
    authenticated by the provider scheme alone, like the registration routes, so storage would
    have been the obvious source. It is the wrong one: the token this request needs is the one its
    own return handed over, which `AuthService.initialize()` took into memory and discarded from
    storage. A stored token there is whatever an abandoned registration left, sent on a request
    that was handed none. `MeApiService.openLockedSession` puts the token on the context, marks the
    request `EXPECTS_UNAUTHENTICATED` and writes no header; the interceptor reads that context on
    the exact path `LOCKED_SESSION_PATH`, after the origin check. `ReleaseFlowService` takes that
    return once, when `/release` is built, and hands the token straight to this request.
  - **Narrowing was worth doing, and no route the client calls reads a provider token anywhere else.**
    `SessionService` discards the stored token once the tab holds a session, but a browser that
    abandoned registration still holds it when that person does what they usually do next, a
    passkey sign-in, and the discard comes only after that sign-in answers. So both anonymous
    assertion legs were being handed a provider credential they could not act on. Every hop a
    credential makes is another log, proxy and error report it can be recorded in.
  - **The order of the two questions is the security property.** Origin first, route second.
    Reversed — or folded into one path test —
    `https://api.budgetoid.app.attacker.example/api/registration` is a registration request. The
    path predicate therefore takes a **pathname** rather than a URL, so a caller has to have settled
    the origin in order to have an argument for it at all.
- **Enforced in**: `apiCredentialsInterceptor` in `+core/interceptors/`. The predicate is **exported
  from there and imported** by `sessionExpiryInterceptor`, which needs the same answer on the way
  back: one definition with two callers. The two registration paths are declared there too and
  imported by `RegistrationApiService`, which builds the requests — that direction and not the
  other, because the service already imports two paths from here, so the opposite edge would close a
  cycle directly. A second spelling of either path fails silently in both directions: corrected only
  in the service, the bearer is lost and the flow's first call meets a `401`; corrected only in the
  interceptor, the provider's token goes to a route that has moved. `EMAIL_CHANGE_PATH` and
  `LOCKED_SESSION_PATH` are declared there and imported by `MeApiService` for the same reason, and
  `PROVIDER_CREDENTIAL` lives in a module of its own, `provider-credential.token.ts`, for
  `EXPECTS_UNAUTHENTICATED`'s reason below.
  `api-credentials.interceptor.spec.ts` holds the email change's half in a block of its own: the
  context's token as the bearer, no stored token when the request carries none, a carried token
  ignored on every other route, nothing to the path on another origin, exact-path matching, and no
  bearer for an empty string. A second block holds the same for the locked sign-in, plus a carried
  token winning over a stored one, the route's spelling under `/api/me` sent nothing, and the other
  provider routes left to their own rules.
  - **`EXPECTS_UNAUTHENTICATED` is the same rule reached from the other side and answered
    differently**: it lives in `expects-unauthenticated.token.ts`, a module holding the token and
    nothing else, rather than in the interceptor that reads it. Three unrelated services set it and
    one interceptor reads it, and that interceptor depends on `SessionService`, which depends on
    `MeApiService` — so declaring the token inside the reader drags the reader's whole import graph
    into every writer and closes a three-module cycle the moment one writer is on that graph, which
    is exactly what happened when the session probe became a writer. The origin predicate can stay
    where it is because both ends of its edge are interceptors.
  - The interceptor's spec calls the function directly and therefore cannot see whether anybody
    registered it, so `app.config.spec.ts` stands up the real provider list with only the HTTP
    backend swapped and goes red on an emptied `withInterceptors([…])`. Without it the registration
    can be deleted with the whole suite green and the product answering 403 to everything. It also
    sends the email change and the locked sign-in through the registered chain, each carrying a
    token on its context, and requires that token as the bearer — the interceptor's spec cannot see
    a path that never reaches the chain. **Three of the interceptor spec's own assertions separate
    mistakes nothing else would catch**: that the two assertion
    legs and `GET /api/me` carry no bearer; that the narrowing touched the bearer alone, since
    narrowing the whole interceptor to the registration routes would cost every other request its
    cookie and its header — a 403 on every route, from a change that reads as a tightening; and that
    a registration **path** on another origin is sent nothing at all, the one assertion a path-first
    implementation fails.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: On a cold load the client asks the server who the visitor is, **once**, before the first
  route activates. The answer has **five** values: `authenticated`, `locked-session`, `anonymous`,
  `unreachable`, and `unknown` before the question has been answered. **Only a status the server
  answered moves anybody** — `anonymous` to `/welcome`, `locked-session` to `/release`,
  `authenticated` off the guest screens — and every guard admits `unreachable` and `unknown`.
- **Why**: the cookie is `HttpOnly`, so there is no local evidence to read and asking is the only
  way to know. `unreachable` and `unknown` exist because **an answer that never arrived is not
  evidence about the visitor**. Collapsed into `anonymous`, one blinked request during the cold load signs a person
  holding a perfectly good session out of their own account and drops them on a page served by the
  same server they could not reach, where nothing they do fixes it — the same defect as collapsing
  `null` into `0` on the recovery-code count. `403` joins `401` as `anonymous`, while a 500, a
  timeout, a status-`0` network failure, a body that does not decode and a kind this bundle does not
  know all read `unreachable` — the last never as a session of either kind, because reading it as
  `locked-session` would tell somebody holding a full session that their data is unrecoverable. `unknown` is the same argument
  before the first ask rather than after a failed one; admitting it means a deleted initializer
  costs a redundant state rather than every visitor bounced on every cold load.
- **Enforced in**: `SessionService` in `+core/session/`, probed from the `APP_INITIALIZER` in
  `core.providers.ts` **after** `config.load()` and **awaited**. Two rules ride on that one call and
  each is silent when broken.
  - **The ordering.** The config holds `''` until `load()` resolves, so a probe made before it
    addresses `GET /api/me/session` to this app's own origin — which answers neither 404 nor 401 but **200
    with `index.html`**, the SPA fallback of the dev server and of Azure's `navigationFallback`
    alike. That body fails to parse under `responseType: 'json'`, which reads as `unreachable`, and
    every guard admits it — so the visitor reaches `/app`, the screen paints, and its own requests
    are refused: a flash of somebody else's screen on every cold load. `BaseApiService` resolves the
    base **per request** so no service can hold a stale copy, which rests the ordering on when the
    request is made rather than on when a class is built.
  - **The probe asks `MeApiService.getSession()` first** — `GET /api/me/session`, the one read both
    kinds of session reach — and only for a full session goes on to `getSessionOwner()` for the
    budget, sequentially, so an anonymous or locked visitor sends no budget read at all. A full
    session whose owner read then fails stays `authenticated` with no budget, the same reading
    `established()` gives its own follow-up. Both members carry `EXPECTS_UNAUTHENTICATED`, and the
    probe never goes through `getMe()`. One route, two questions: the probe
    asks whether there is a session and a `401` is its answer, while the Settings screen reads the
    same route signed in and a `401` there is a session that ended. Unmarked, the probe navigates
    **every anonymous cold load** to `/welcome` from inside the initializer, before any route
    activates — so `/register`, the address the identity provider redirects back to, is unreachable
    by URL.
    - **A second caller asks the same question and needs the same suppression for a different
      reason.** An unlock reads `GET /api/me` beside the account's keys, to learn the budget its
      rotation-epoch record is filed under, and it reaches it through this member. It is **not** a
      browser holding no session — it is signed in, demonstrably — so the sentence above does not
      cover it and must not be stretched to. What covers it is `AccountKeyCustodyService`'s own rule:
      a key that will not open is not a session that ended, and neither is an identity read that was
      refused mid-unlock. Both of the unlock's reads therefore carry the token, and custody publishes
      one word about the pair rather than letting the interceptor navigate. See
      [account-keys.md](account-keys.md).
  - **Three session bodies are decoded strictly, so changing one takes two releases.**
    `MeApiService` refuses any member it does not declare, and any declared member missing, on
    `GET /api/me/session`, `POST /api/locked-session` and `POST /api/me/erasure/schedule`. A member
    the server adds, renames or drops therefore ships client first — a client that accepts it
    present or absent, then the server — for [export.md](export.md)'s reason: the two deploy jobs
    run in parallel and an open tab keeps its old bundle. Skip the first release and every bundle
    older than the change refuses the new body: its probe reads `unreachable`, and its release
    sign-in and its schedule read `undetermined`. One commit carrying both halves is still that
    outage.

  The **await** is what keeps every guard synchronous — bootstrapping cannot finish while the answer
  is outstanding — and `core.providers.spec.ts` pins both halves separately, because a
  `void probe()` satisfies one and fails the other. `probe()` resolves however the read ends and
  **never rejects**; a rejection is not a failed probe but an application that never finishes
  starting.
  - **One read rides *after* the probe and is conditional on its answer.**
    `KeyRotationService.readStagedRotation()` runs in the same initializer, so that "a key rotation
    is in flight" survives a reload — see [key-rotation.md](key-rotation.md) for what the three
    content screens do with it. It is sequential and **not** parallel, and it is made only when the
    probe answered `authenticated`: that route is authenticated, an anonymous visitor asking it is
    answered `401`, and `sessionExpiryInterceptor` acts on `401` alone and ends a tab holding no
    full session without a re-read — so an unconditional read announces a session ending to somebody
    who never had one, on every anonymous cold load. Nothing in it is awaited for the guards' sake; what the
    await buys is a screen that does not draw a list the answer would have replaced.
  - **The reading also moves twice mid-visit, and both moves are a *set* rather than a re-probe.**
    `ended()` is called by `SessionService.judgeRefusal` on a `401` the interceptor hands it and the
    judgement confirms, by the Settings screen's sign out, by the release screen's Sign out, and by
    `ErasureFlowService` on the erasing request's `204`. Each runs **before** the navigation to
    `/welcome` — the interceptor navigates only once the verdict is in — because `guestGuard` reads
    the status the moment the router asks, and a navigation made first is judged against a stale
    session and sent back. `established()` is called by the registration flow on the `201` and by
    the sign-in flow on the assertion's answer, and `establishedLocked()` by the release flow on the
    locked sign-in's `200`. Each time the server has just said what it thinks, in the same breath as
    the cookie it set or the refusal it answered, so asking again would replace an answer with a
    guess over a network that may itself be the problem. On the establishing side a re-probe also
    costs a round trip at the happiest moment of the flow and can come back `unreachable` — a
    **third** reading of a fact already stated.
    - **A `401` is the one move that asks first, and it asks once rather than re-probing.** Since a
      sign-in displaces the session the old cookie named, a `401` speaks for the cookie its request
      carried and not for the jar, so the refusal alone is not the server saying this tab's session
      is over. The judgement asks `GET /api/me` — not the probe's session read, which names nobody —
      and sets the status from that answer. The interceptor rule below argues the rest.
  - **The scheduled erasure is the one fact read again, and the read publishes nothing else.** A
    sign-in's answer does not carry the schedule, so `established()` sends one marked
    `GET /api/me/session` behind it, unawaited, exactly as it reads the budget: the status is
    `authenticated` while it is out, a failure publishes nothing — the schedule stays as the sign-in
    found it, which is `'unread'` because both establishing flows start anonymous — and nothing it
    answers moves the status. Without it the owner the schedule exists to warn — Google stolen,
    signing in with the passkey that survived — would see no notice until a reload. A tab that stays
    open learns of a schedule filed *after* it started through `refreshSchedule()`, which the shell
    calls each time the document becomes visible again, and only for a status the server answered
    as a session; the shell owns the listener and removes it with itself. **No timer**: the API
    scales to zero, and every open tab polling would keep it awake for the one case only a poll
    reaches — a tab left visible for days without a reload.
    - **Two guards keep a late answer from overwriting a fresher one**, both pinned in
      `session.service.spec.ts`. A **generation**, raised by every status write and by the
      schedule's own writers (`erasureScheduled()`, `erasureCancelled()`) — not by a read's
      publication — drops an answer to a read sent before such a write, so a read out while the
      person withdraws cannot bring the notice back. And the answer's `kind` must match the status,
      which catches a cookie replaced underneath the tab by a session of the *other* kind; another
      account of the same kind passes it, and only an identity in the answer could catch that. That
      is why the judgement of a `401` re-reads `GET /api/me` and compares its `budgetId` rather than
      re-reading this route: the session read carries no identity, so it cannot tell this tab's
      account from another of the same kind.
      Separately, at most one refresh is out at a time — a load rule, not a freshness one, and
      `established()`'s own read does not wait on it.
    - **A withdrawal's `204` publishes only into the session that sent it.** `erasureCancelled()`
      takes the `sessionToken()` the flow read just before posting and does nothing when it is no
      longer current, so an answer that lands after this tab signed out and somebody else signed in
      cannot hide the new account's notice. The token moves only when one session ends or another
      begins — `ended()`, `established()`, `establishedLocked()` and a probe that answered a
      session — and is deliberately not the generation, which also moves inside one session and
      would drop the right answer. Otherwise it publishes `null` — nothing scheduled, which the
      server just said — never `'unread'`.
    - **The token has a second reader: the judgement of a `401`.** `sessionExpiryInterceptor` reads
      `sessionToken()` as a request leaves and hands it to `judgeRefusal`, which answers `'stale'`
      and writes nothing when the visit has moved — a `401` to a request sent before a sign-out and
      a sign-in says nothing about the session after them. The re-read in flight is keyed on the
      same token, and so is its answer, which publishes nothing once the visit has moved. Keyed on
      the generation, a schedule request's `200` inside the visit would wave a real ending through
      as stale; `still ends the session when a schedule was written while the re-read was out`
      pins that half, and measured, a mutation of the visit keying reddens its own test.
    - A locked tab learns of a withdrawal only on reload; whoever reads it either cannot withdraw or
      is the person who filed it. See [erasure.md](erasure.md).
  - **`ended()` is the single owner of "the account's keys go too", and `established()` owns no key
    material.** What `established()` does own is discarding the provider's tokens, which is a
    different fact and has its own rule below. A session ending is where `AccountKeyCustodyService.lock()` is called, in that
    one method rather than at each of its callers: a path added later by somebody thinking about
    something other than key material clears the keys for free — the erasure's `204` is one that
    arrived that way, and it has no line of its own for the keys — where copies at the call sites
    would leave such a path holding an ended session whose content key is still readable from the
    root injector for the life of the tab, with nothing red either way. It is **not** an
    `effect()` over `status`: that fires on construction, so whether it wipes a set already adopted
    would be decided by injection order, and the only honest predicate available to it is "lock on
    `anonymous`" — locking on `unreachable` would destroy both keys over one blinked request and
    demand a full WebAuthn ceremony to get them back, which is the failure the fourth state exists
    to prevent, reappearing one layer down. The mirror is that a session *beginning* says nothing
    about which factor opened it, so `established()` unlocks nothing and the two establishing flows
    that do know hand the keys over themselves. **Those two are no longer the only handers**, which
    is the sharper reason `established()` cannot own this: the settings screen's Unlock hands custody
    a key on a session that opened hours ago, so unlocking and establishing are not even the same
    kind of event — one of them is invisible to every rule in this file. See
    [account-keys.md](account-keys.md).
  - **The order at the end of an establishing flow is the requirement, not the tidiness.** The
    session is published **before** the navigation to `/app`; published after, the guard judges that
    address against a stale `anonymous` and bounces the person straight out of the account they have
    just opened — a defect that reproduces every time and reads as a routing problem.
    `register.component.spec.ts` records the reading at the instant the navigation is asked for, the
    only way to see the ordering at all. **Both flows that end this way keep the same three
    statements in the same order**: publish the session, hand the account's keys to custody,
    navigate. The custody call sits between them rather than after the navigation because
    `/welcome` and `/register` are discarded by that navigation, and it is **not awaited** — `unlock`
    returns `void` precisely so a round trip cannot land between a verified assertion and the app.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: A session beginning is the single owner of discarding the identity provider's tokens.
  `SessionService` calls `AuthService.forgetProviderToken()` on **every** arm that publishes a
  session — `established()`, `establishedLocked()`, and a start-up probe the server answers with a
  full or a locked session — and **never** on `anonymous` or `unreachable`.
- **Why**: the library keeps the provider's tokens in `sessionStorage`: the access token, the id
  token, the decoded claims with the email among them, and the nonce. That storage is per tab and
  survives a reload, so without a discard a registration that was refused or abandoned leaves them
  sitting in the tab when the same person signs in there a minute later, and a signed-in visit
  carries a provider credential and an email address it has no use for. Nothing reads the stored
  tokens once the session cookie has taken over: they are read on `/register` and nowhere after.
  The email change reads a token on a signed-in tab, and it never reads one out of this storage —
  see the bullet on its return below.
  - **The probe arm is not a duplicate of `established()`.** A cold load that finds a session runs
    no establishing flow and skips `auth.initialize()`, so `established()` never runs there. That
    is the tab that reloaded after a registration whose `201` was lost on the way back, or after the
    person signed in from another tab — it holds a session and the tokens both, and only the probe
    sees it.
  - **Never on `anonymous` or `unreachable`**, and the reason is order. The provider-return leg —
    the page load Google redirects back to `/register` — runs the probe **before**
    `auth.initialize()` reads the answer off the URL. A discard there takes the library's nonce with
    the tokens, the answer no longer validates, and registration becomes impossible with nothing on
    the screen saying why. `unreachable` is refused for the same reason: a blinked probe on that leg
    would cost the same.
  - **The same discard ends the exchange marker.** `forgetProviderToken()` also removes
    `budgetoid-provider-exchange`, the key a press on **Continue with Google** or **Change email
    address** leaves so the return leg knows this tab started that trip. A tab that pressed,
    abandoned at the provider and then signed in by passkey — or came back on a probe that found a
    session, which skips
    `auth.initialize()` — would otherwise keep it, and an answer-shaped address opened there later
    would contact the provider. It rides the same arms for the same ordering reason: removed on
    `anonymous` or `unreachable`, it would be gone before the return leg read it.
  - **One place, not one per flow**, for the reason `ended()` owns `custody.lock()`: the next
    establishing path will be written by somebody thinking about sign-in rather than about an id
    token in `sessionStorage`, and here it discards for free.
  - **Housekeeping never outranks the session.** A discard that throws — storage refused in a
    locked-down browser, a quota error — is logged through `logFailure` and changes nothing
    published. On the probe the call sits outside the `try`, because inside it a throw would reach
    the `catch` and rewrite a session the server just confirmed as `unreachable`.
  - **The edge runs one way**: `SessionService` injects `AuthService`, and `AuthService` must never
    inject `SessionService`. Both are asked from the `APP_INITIALIZER`, and an edge back would be an
    import cycle between two things bootstrap awaits.
  - **A return that can reach a tab holding a session is read before the probe, and the discard
    then finds nothing.** The email change comes back to a tab holding a session, so the probe
    answers `authenticated` and its discard would take the nonce the answer is checked against. A
    locked sign-in's return usually reaches a tab holding none, but one that holds a session would
    lose its nonce the same way. So the `APP_INITIALIZER` reads either return **before** the probe:
    `AuthService.initialize()` validates the answer, keeps the token in memory — with the address,
    on an email change — runs `logOut(true)` itself and removes the marker. By the time a session
    arm runs, the library's storage is already empty, and the discard leaves both in-memory
    hand-offs alone. This rule therefore still owns "a session beginning discards the provider's
    tokens" without exception; what the two returns add is an ordering in front of it, held by
    `core.providers.spec.ts` (`is read before the server is asked who the visitor is`, in each
    return's block) and by `core.providers.cold-boot.spec.ts` (`hands a signed-in email change the
    id token the provider sent back`, `hands the locked sign-in the id token the provider sent
    back`). The next flow that needs the provider has to take the same position or re-argue this
    rule. See [email-change.md](email-change.md).
    - **Reading first does not mean keeping.** After the probe the bootstrap drops a locked
      sign-in's hand-off when the probe found a session already open, full or locked. Over a full
      session the server refuses that post itself, `409` with `conflictKind: "full_session"`, and
      writes nothing — see the locked sign-in's rule below. So the drop over a full session is a
      restatement for the person, as [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md)
      allows: it spares a request whose answer is known, and the release screen has no sentence
      for that `409` — `ReleaseFlowService` reads it as `undetermined`. The server's refusal is
      what holds when the drop does not run, because the probe answered `unreachable`. Over a
      locked session the drop is the client's own choice: the server would replace that session.
      `core.providers.spec.ts` holds the drop (`is dropped once the probe finds a %s session
      already open`) and `core.providers.cold-boot.spec.ts` holds it against the real library
      (`hands nothing over when the probe finds a full session`).
  - The rule sits in the client because the client is the only layer that holds the tokens; the
    server cannot clear a browser's storage.
- **Enforced in**: `session.service.spec.ts` — the discard happens on `established()`, on
  `establishedLocked()`, and on a full or locked probe; it does not happen on a `401` or `403` probe, nor on a network failure, a 500
  or a timeout; the provider service is asked for nothing but the discard; and a discard that throws
  does not unpublish the session. `auth-service.spec.ts` holds the discard itself: a local
  `logOut(true)` that removes the library's keys and no others and makes no request.
  `register.component.spec.ts` holds that one registration discards exactly once.
  `auth-service.spec.ts` holds that the discard leaves an email-change hand-off in place
  (`forgetProviderToken leaves a captured email-change answer in place`).
- **Example**: somebody opens `/register`, comes back from Google, and is told an account already
  exists for that address. They go to `/welcome` and sign in with their passkey in the same tab;
  `established()` publishes the session and drops the tokens before the navigation to `/app`.
- **Counterexample**: a discard call in each establishing flow — registration, sign-in, and the
  probe as a third. It reads as explicit and it is correct on the day it is written; the next flow
  forgets it and nothing goes red. The other is an `effect()` over `status`, rejected for the reasons
  the `ended()` bullet above gives: it fires on construction and then when Angular schedules it
  rather than at the transition, so its order against bootstrap and the navigation is decided by
  injection and scheduling order, which nothing here controls.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: A `401` answered to a request this app made to its own API is **judged** before it ends
  anything. `SessionService.judgeRefusal` sends one marked `GET /api/me`, and the session ends
  client-side — and the browser goes to `/welcome` — only when that read names another budget,
  names none, or fails. An answer naming the tab's own budget keeps the session. A tab holding no
  full session, or no budget to compare against, ends without asking. A `403`, another origin's
  `401`, and any request carrying the `EXPECTS_UNAUTHENTICATED` context token are all left alone.
  The error is **always re-thrown** — a judged one once the verdict is in — and never retried.
- **Why**: a session ending is an application-wide fact — every screen's reads start failing at once
  — so it is noticed in one place rather than in each caller, which is why no screen carries a
  lapsed-session sentence of its own.
  - **Judged, because a `401` speaks for the cookie its request carried, not for the jar.** Since a
    sign-in displaces the session the browser's old cookie named, a request another tab had in
    flight under that cookie comes back `401` while the jar already holds the new one — the cost the
    displacement rule names, [Guessing] reasoned and not run in a browser. Read as the end of the
    session, that signs a tab out of an account it is still inside and locks its keys.
  - **The re-read is `GET /api/me` and never the session read.** `GET /api/me/session` names nobody
    by design, so a session of another account of the same kind passes it, and a tab kept on that
    would fold its budget and keys into writes made under that account's cookie. Measured: the
    candidate that re-read the session route and kept the session on any `200` reddened 18 specs.
    `GET /api/me` names the budget, and the budget is what the tab's writes are keyed by.
  - **Fail closed, and to `anonymous`.** Anything but a `200` naming this tab's budget ends the
    session — a `401`, a `403`, a `5xx`, no answer, a body naming no budget. Not `unreachable`: the
    server has already refused the request being judged, and a tab left signed in stays on screens
    whose every read is refused. A locked session cannot be judged — `GET /api/me` refuses one — so
    it ends without asking, as a tab with no budget does — and so a locked tab still ends over the
    race above, [Guessing] reasoned and not run.
  - **What the judgement does not reach**, stated as behaviour. [Guessing] Reasoned from the code,
    not run. It runs only on a `401`: a tab with nothing in flight when another tab's sign-in
    replaces the cookie hears none, and carries on under whatever session the jar now holds —
    another account's included — with nothing in the client noticing. And a re-read that leaves
    before the new cookie has landed carries the old one, is refused, and ends the tab anyway.
  - **Still an observer.** The interceptor calls `judgeRefusal` and navigates on an ending verdict;
    it never calls `ended()` itself — that would be a second owner of the transition — and never
    retries. The navigation hangs off the verdict rather than off the observable it returns, so a
    caller that stops listening while the verdict is out still leaves an ended session for
    `/welcome`. The error waits for the verdict, so the navigation is asked before the caller's own
    `catchError` runs — where the erasure, its withdrawal and the email change each read their
    probe's `401`.

  Three exclusions, each silent when wrong. **`403`** is the
  first-party refusal and the locked-session refusal, both answered to a browser whose session is
  intact, so acting on one ends a live session over a bug in the request builder. **Another origin's
  `401`** is a statement about a token this product does not issue — the app reaches the identity
  provider through the same `HttpClient`, so a sign-out would be caused by a third party. And a
  **request whose `401` is its own answer** carries the token: the anonymous ceremony routes, the
  session probe, and **both reads an unlock makes** — `GET /api/me/account-keys` and the
  `GET /api/me` beside it. For the first two there is no session yet, so
  there is none to end. **The unlock's pair is what does not fit that sentence**, and both carry the
  token anyway: `AccountKeyCustodyService` is the caller and never calls anything on
  `SessionService`, because a key that will not open is not a session that ended — unmarked, the
  request made that call through this interceptor instead, over an edge no import graph shows, and
  raced a just-signed-in person off `/app` and onto a `/welcome` that had nothing to say. If the
  session genuinely has ended, the next unmarked read says so from a screen that can render it. See
  [account-keys.md](account-keys.md). The erasing request is signed in as well and carries the
  token on the rule itself — its `401` is usually its gate's verdict, and the flow asks an unmarked
  probe whether it was — as the census below sets out. The **re-throw** keeps this an observer rather than a handler; swallowed, the error reaches no
  caller's `catchError` and the screen that made the request sits on its loading line forever.
- **Enforced in**: `sessionExpiryInterceptor`, registered after `apiCredentialsInterceptor` so the
  unwinding puts it nearest the backend. The exclusion is carried on the **request**, as an
  `HttpContextToken`, and deliberately **not** as a list of anonymous URLs held in the client: a
  list is a second definition of the anonymous surface, and the first route to move leaves it ending
  the session of somebody who mistyped a recovery code. `app.config.spec.ts` carries a registration
  pin for this interceptor too, independent of the credentials one.
  - **The judgement is `SessionService.judgeRefusal`'s, and the interceptor hands it one value.** It
    reads `sessionToken()` as the request leaves and passes it on a qualifying `401`. The verdict is
    `'ended'`, with `ended()` already run, `'kept'` or `'stale'`, and the promise never rejects:
    awaited inside the interceptor's `catchError`, a rejection would replace the caller's `401`. The
    re-read is one flight per visit — a screen's reads fail together, so their `401`s join one
    `GET /api/me` — and is dropped when it settles, so a later refusal asks again. The
    `SessionService judging a refusal` block in `session.service.spec.ts` pins the decision table
    over a stubbed `MeApiService`. `session-expiry.interceptor.spec.ts` pins the interceptor's half
    over a stubbed `SessionService` — the token read at send time, the navigation asked before the
    error is handed on, a caller that stopped listening still sent to `/welcome`, nothing retried —
    and, in its `sessionExpiryInterceptor judging a 401 against the session` block, the whole chain
    over the real `SessionService`, `MeApiService` and `HttpClient`: one marked re-read, the session
    kept on the same budget, and ended on another budget or on a refused re-read with nothing sent
    after it. Measured: hanging the navigation inside the returned observable, and leaving the
    judgement's `ended()` unguarded, each reddened its own test.
  - **Which members set it is a rule and deliberately not a tally.** A number written in prose is a
    second copy of the list standing beside it, kept in step by nobody and reddening nothing when
    the two disagree — and it is the number that rots, because a member added to a service is added
    without the sentence two files away being opened. So the rule carries it: a member sets the
    token when the `401` it may collect is **that route's verdict on that request** rather than a
    session ending. `RegistrationApiService` sets it on both legs, `SignInApiService` on both
    assertion legs, and `MeApiService` on `getSession()`, `getSessionOwner()`, `getAccountKeys()`, `eraseAccount()`,
    `changeEmail()` and `openLockedSession()` — `changeEmail()` on `eraseAccount()`'s terms, resolved
    the same way, by one unmarked `GET /api/me` the email-change flow makes before it names a
    refusal. `getMe()` is the
    counterexample — the same route as `getSessionOwner()`, asked as somebody already signed in —
    and carries none; nor do `endSession()` and `scheduleErasure()`, whose `401`
    means the session they presented had already ended. **The rule is about the member and not about
    who calls it**, which is what keeps it true now that `getSessionOwner()` has a second caller.
    Most of the members are the plain case: the browser holds no session to lose. The members a
    signed-in browser makes are not that case, and each carries its own reason.
    `getAccountKeys()` carries the token on custody's own argument: a key that will not open is not
    a session that ended, so a refusal mid-unlock is custody's to publish and not the interceptor's
    to navigate on, and the `GET /api/me` the same unlock makes rides the same reasoning through
    `getSessionOwner()`. `eraseAccount()` carries it on the rule's own terms: a `401` there is
    usually the gate declining the assertion — that route's verdict on that request — and otherwise
    a session that had already ended before the gate ran, and either way the request erased
    nothing. Unmarked, the interceptor would take the tab to `/welcome` over a sentence the dialog
    never got to show. **A session that really had ended is not lost by the mark**, because
    `ErasureFlowService` resolves every `401` there with one **unmarked** `GET /api/me` —
    `getMe()`, the counterexample above, used for exactly the reason it is one. A `401` on that
    probe is the interceptor's to judge: on an ending verdict the tab leaves for `/welcome` while
    the dialog says nothing. On a kept one the dialog says nothing either and its commit is live
    again — [Guessing] the case where the probe, too, carried a cookie another tab's sign-in had
    just displaced, so the erasing request was refused before the gate and erased nothing. A
    `200`, or a probe that cannot answer, lets the dialog say `refused`.
    The re-authentication challenge before the erasing request, from `ReauthenticationApiService`,
    is unmarked as well — so the mark is per request rather than per act, and the legs of one
    erasure answer the question opposite ways. See [erasure.md](erasure.md).
    - **The rotation begin is the difference worth reading beside it.**
      `KeyRotationApiService.beginRotation` also carries a re-authentication assertion to a gate
      that answers `401` when it declines, and it is unmarked, like the other three members of that
      service. So a `401` from the rotation gate reaches the interceptor, where the same verdict
      from the erasure gate stays with the dialog. There the judgement re-reads `GET /api/me` over a
      session that is still live and keeps it, so a declined rotation passkey does not take the tab
      to `/welcome`, and what stays on screen is the rotation flow's own reading of that `401` —
      [Guessing] read from the code, not run. That service's header argues the absence from the
      judgement: it settles an ended session on all four routes, and on the begin the body's own
      `refusal` member is what tells a declined passkey from any other `401`.

    `RegistrationApiService` builds a **fresh** `HttpContext` per call, because that object is
    mutable and a shared one would be read and written by every registration request in the visit. What the token buys there is concrete: without it, a `401` on the second leg
    navigates the person to `/welcome` mid-flow, away from a screen showing ten recovery codes they
    may already have written down — reachable rather than theoretical, because a provider id token
    lives an hour and somebody can sit on the codes step for longer.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: `POST /api/me/session/revocation` ends **only the caller's own session**, clears the
  cookie, answers `204`, and answers `204` again on a second call.
- **Why**: leaving people with no way out once sessions are real is worse than the route costs. The
  caller names no session — the id comes from the claim its own authentication produced — so there
  is no session id on the wire for anyone to substitute. Idempotence stops a dead cookie living on
  the client for the rest of its lifetime: a `401` on the second call would leave the browser
  holding a handle nothing clears before its `Expires`. Some ends do leave one, and these are the
  ones known — an erased account, an ended session the account's next sign-in swept, and a session
  whose credential was deleted from another device, all on purpose, and an establishing response
  lost on its way back after displacement took the session the browser still presents — and the
  rule below says why the dead handle is harmless at each.
- **Enforced in**: `SessionEndpoints` and `RevokeSessionHandler`, over
  `ISessionRepository.RevokeAsync`, whose idempotence is `Session.Revoke`'s.
  `SignOutTests.SigningOut_LeavesAnotherDeviceSignedIn` is the negative control — without it, a
  sign-out that revoked every session on the account passes every other test in the file.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: An **ended** session — revoked or expired — authenticates on exactly one route, the one
  that ends sessions, and reaches **no ambient budget** even there. It does so while its row stands,
  which is until the account's next sign-in at the latest — see the note below.
- **Why**: the idempotence above needs it. Signing out twice presents the same dead cookie twice, so
  the second call has to authenticate far enough to answer `204`. What makes this narrow rather than
  a hole is what it does *not* relax: the token still has to match, so it is a verdict about
  liveness and never about the handle. And the tenant is published only on the live path, so a
  marked route is structurally unable to reach budget-owned rows.
- **Enforced in**: `AcceptsEndedSessionAttribute`, an **opt-in marker on the route**, read by
  `SessionCookieAuthenticationHandler`. A marker rather than an authorization requirement because
  `AuthorizationMiddleware` answers `403` where this needs `401`, and rather than `AllowAnonymous`
  because that would widen the anonymous surface `AnonymousSurfaceTests` holds.
  `AcceptsEndedSessionTests` pins the marked set at exactly one route, so a second marker goes red
  until somebody argues for it.
- **Counterexample**: relaxing the *lookup* instead — admitting a request whose token matched
  nothing so that sign-out "always works". That is an unauthenticated route with extra steps.
- **Note** — **this route answers `401` at each end below, and at each the cookie stays on the
  client** until its own `Expires`, which is the deleted session row's expiry. They are the ends
  known, not a closed list. **An erased account** leaves no *ended* session behind, only an absent
  one: the cascade takes the `session_tokens` row, so
  `AuthenticateSessionHandler` finds no token and returns nothing, and
  `SessionCookieAuthenticationHandler` answers `NoResult` before it ever reads
  `AcceptsEndedSessionAttribute`. **A swept session** arrives at the same place by another road. An
  ended session stays, and this route answers its cookie `204`, until a session is established on
  its account; the ended-session sweep then deletes the row and the cascade its handle. A sign-in
  in the same browser overwrites that cookie, so the dead one is held by a browser the sign-in did
  not happen in. **A deleted credential** takes its sessions and their handles by the cascade from
  `credentials`: a passkey revocation, an email change retiring the federated credential, or a
  regeneration deleting the old set, made from another device, leaves the cookie of a browser
  holding one of those sessions naming no row. **A lost establishing response** is the one where
  the dead cookie is held by the browser the sign-in did happen in: displacement deleted the
  session it names, and the response that would have overwritten it never arrived. That is the
  dead cookie the sign-out rule above exists to avoid, and at each of these ends it is harmless:
  the handle names no row, so it opens nothing and every route answers it `401`.
  [Guessing] The client needs nothing new for it — read from the code, not run: a `401` reaches
  `sessionExpiryInterceptor` like any other, and `endSession()` carries no
  `EXPECTS_UNAUTHENTICATED`, so even a sign-out press ends the tab's session and lands on
  `/welcome`. The outcome goes through the judgement's re-read, which carries the same dead cookie
  and is refused in turn, so it does not change. **Do not "fix" it by relaxing the lookup** — that is this rule's own counterexample.
  See [erasure.md](erasure.md).
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: `POST /api/locked-session` turns a Google ID token, and nothing else, into a `Locked`
  session over the account's **federated** credential — or into a `404` that writes nothing. It
  takes no body and creates no account. **It never replaces a live full session**: a browser
  holding one is refused `409` with `conflictKind: "full_session"`, and nothing is written.
- **Why**: somebody whose passkeys and recovery codes are all gone still holds their Google sign-in.
  A locked session is what lets that sign-in reach the one act built for them — the erasure schedule
  — and nothing with budget content in it. See [erasure.md](erasure.md).
  - **The route names the provider's scheme in a policy of its own**, the registration group's
    shape: `RequireAuthenticatedUser` over `ProviderAuthentication.SchemeName`. Naming the scheme is
    what makes `AuthorizationMiddleware` authenticate the bearer rather than the cookie, so a browser
    already holding a session — full or locked — cannot stand in for the provider here. Declaring a
    policy takes the route off the fallback, which is right rather than worked around: the two
    requirements about session kinds judge the session a request is served under, and this route
    serves none — it opens one. Whether the browser already holds one is the next bullet's
    question. Not `AllowAnonymous`: the provider's signature is the whole proof the session is
    opened on.
  - **A weaker proof never overwrites a stronger sign-in.** The delegate's first statement, after
    the policy and the claim gate and before the handler, authenticates the session cookie scheme
    by name and judges that result — never `HttpContext.User`, which holds the provider's
    principal. A live session whose kind is anything but `Locked`, or whose kind claim does not
    read back, is refused: a `ConflictException` whose detail names no subject, address or account,
    so the handler never runs, nothing is written, no cookie is set and nothing is displaced. A live
    **locked** session is replaced, on the same account or another, and displaced once the new one
    is established. An ended session authenticates as nothing here, so its cookie falls through.
    Why this sits on the server: the bootstrap's drop of the hand-off
    does not run when the startup probe answers `unreachable`, and before this check that return
    could replace a full cookie with a locked one and show a passkey holder the screen that says
    their data is unrecoverable. `full_session` is a conflict kind because every `409` in the
    product carries one — see [payees.md](payees.md). The alternatives are in
    [ADR 0028](../decisions/0028-open-a-locked-session-from-the-federated-credential.md).
  - **The claims are judged by `RegistrationClaimGate`, the filter registration uses, not a copy of
    it.** No usable `sub` or `email` is a `401` titled `MissingClaimsTitle`; an address the provider
    does not vouch for is a `401` titled `UnverifiedEmailTitle`. The checks are `ProviderClaims`', so
    a second filter would be the second copy that class exists to prevent.
  - **One discovery read, on `credentials` alone.** `FindFederatedCredentialBySubjectAsync` matches
    type `federated`, provider and subject, untracked, and never joins `users`. It returns the
    credential itself, not the account id. Two reads — the id, then "the account's federated
    credential" — would let an email change landing between them hand the session a credential this
    token never named. And it never reads the account's credentials in general, because a passkey
    there would open a `Full` session, the one thing a provider sign-in must not reach.
  - **The order is the one every establishing path keeps.** The exempt read runs before the handler
    publishes anyone; then `ResolveUser(credential.UserId)`; then everything policed — the handle
    minted, `Session.Establish` over that credential with `SessionPolicy.Lifetime`, one `AddAsync`
    carrying the session and its handle, and the account's erasure schedule read.
    `Session.Establish` derives `Locked` from the credential's type, so nothing on this path can
    ask for anything else. **No
    transaction and no `ITransactionalExecutor`**: there is one save, and a transaction opened
    before the publication would configure its connection with the identity still empty. On a
    request carrying a live locked cookie, `ResolveUser` replaces that cookie's account with the
    credential's owner.
  - **That one save also runs the ended-session sweep, and the cookie's own session can be in it.**
    An ended cookie falls through to the handler, and when it names a session of the token's own
    account, `AddAsync` deletes that row with the account's other ended ones and the cascade takes
    its handle. Nothing on this path clears the change tracker, and the cookie scheme read that
    handle earlier, on the same request's context, which is why
    `SessionTokenRepository.FindByTokenHashAsync` reads untracked. Tracked, EF cascades into the
    handle and sends its own delete on `session_tokens`, and the sign-in answers `500` with `42501`
    — measured: `LockedSignIn_OverAnEndedSession_Succeeds` goes red. Displacement then finds no
    token row for that cookie and does nothing.
  - **An unknown subject is a `404` with `refusal: "no_account"`**, and the handler publishes,
    writes and sets nothing — no session, no handle, no cookie. The body repeats neither the subject
    nor the address. A cookie the browser sent has already published its own account by then — see
    the residue gotcha under [Edge Cases](#edge-cases--known-gotchas) — and nothing policed runs
    after the `404`. **It is not an enumeration oracle**: only a caller holding a provider-verified
    token for that exact subject learns it, the argument registration's own subject refusal rests
    on — see [registration.md](registration.md).
  - **The `200` carries three members** — `kind` (always `"locked"`), `expiresAtUtc`, and `erasure`,
    which is `null` or `{ "takesEffectAtUtc": … }` — the shape `GET /api/me/session` answers. The
    cookie is written through `SessionCookieWriter` on the established arm only, after the handler
    returned, so the `404` displaces nothing either.
- **Enforced in**: `EstablishLockedSessionHandler` and the route in `SessionEndpoints`.
  `EstablishLockedSessionHandlerTests` pins the order over one log of calls, on an account holding a
  passkey beside its federated credential and a stranger's account filed before it — a handler that
  took "the account's first credential", or the first federated row, lands on the wrong one.
  `LockedSignInEndpointTests` runs the real `JwtBearer` handler with a test signing key, on the app
  role, so a handler that published late answers `500` with `22P02`. It pins the stored row
  (`locked`, over the federated credential and not the passkey), the three-member body, the `403`
  that session meets on `GET /api/accounts`, and a `404` that writes nothing — counted on `users`,
  `credentials`, `sessions`, `session_tokens` and `erasure_schedules`. It pins a `401` with no
  token even beside a full or a locked cookie, on a forged signature, on an unverified address, on a
  missing `email` and on a missing `sub`, and the first-party `403` without `X-Budgetoid-Client`.
  Five cases hold the refusal over a cookie.
  `LockedSignIn_OverALiveFullSession_IsRefused409FullSession_AndWritesNothing` runs with the token
  naming the session's own account and another account; it asserts the token, a body repeating
  neither the subject nor the address, no `Set-Cookie`, unchanged row counts, and the full cookie
  still answering `"kind": "full"` on `GET /api/me/session`.
  `LockedSignIn_OverALiveLockedSession_ReplacesIt` and `LockedSignIn_OverAnEndedSession_Succeeds`
  hold the two cookies that are not refused, and
  `LockedSignIn_OverItsOwnAccountsEndedSession_DeletesThatSessionAndItsHandle` holds the sweep
  taking the ended one; `SessionRepositoryTests.FindByTokenHashAsync_ReturnsAnUntrackedEntity`
  holds the cause on the tracker.
  `LockedSignIn_WithAnotherAccountsLockedCookie_OpensEverythingOnTheTokensAccount` holds that a
  replaced locked cookie lends the new session nothing: the row and the schedule are the token's
  account's. `LockedSignIn_OverAFullSession_WithAnUnverifiedEmail_Is401NotTheConflict` holds the
  order — the claim gate answers before the conflict does. `ConflictKindSpellingTests` pins the
  token and `ConflictKindDispositionCensusTests` pins `SessionEndpoints.cs` as the one file raising
  it. `RegistrationRouteTests` reads the route among the provider-scheme routes.
- **Counterexample**: keying the lookup on the address. The `404` test's second case sends an
  unregistered subject carrying an address an account holds; a lookup on the address opens a locked
  session on an account the provider never named.
- **Note** — **a race recorded rather than handled.** An erasure committing between the discovery
  read and the save removes the credential the session names, so the insert fails its foreign key
  with `23503` and the request answers `500`; a retry finds no credential and answers `404`. Nothing
  in the suite drives that interleaving.
- **Source**: `[SOURCE: discussion]`

---

- **Rule**: A session whose kind reads no budget content reaches **three** routes — the one that
  ends sessions, `GET /api/me/session`, which tells the caller what it holds, and
  `POST /api/me/erasure/schedule`, which files the account's erasure for seven days out. Every other
  route answers `403`, and every one of those refusals is the same answer. The schedule route is
  also the one route a **full** session is refused: it is for a locked session only.
  - **`GET /api/me/session` answers what the caller already holds, and nothing else**: its kind
    (`"full"` or `"locked"`, spelled as every establishing leg spells it), its own expiry from the
    row, and its account's scheduled erasure instant or `null`. No session, account, budget or
    credential identifier, no email. Both kinds reach it, because a client has to learn which one it
    holds: a locked tab asking `GET /api/me` gets the `403` every budget route gives it, which reads
    as no session at all. `SessionReadEndpointTests` pins the member set whole.
- **Why**: the kind has been on the row since sessions existed and on the request's claims since a
  cookie authenticated one, and until this requirement **nothing read either**: a locked session was
  answered normally by every route, ambient budget and all. What the rule protects is the thing a
  federated credential cannot do — an authorization exchange returns claims, not a secret the
  account's keys can be wrapped under — so a provider sign-in reaching budget content would be the
  provider's holder reading rows they hold no key for.
  - **Why the application and not the database**, which ADR 0002 requires stating.
    `budget_isolation` cannot express it: the ambient budget is resolved from the *user*, and a live
    locked session resolves one like any other. A policy on `sessions` cannot express it either —
    see the MUST NOT above. And `GET /api/me/export` reads user-owned `budgets`, so even a
    budget-keyed rule would let the largest single disclosure in the product through. No declarative
    database rule reaches it, which makes the application the lowest capable layer; within it, an
    authorization policy is the declarative mechanism the framework provides and the only one a
    route-table test can read whole.
  - **Opt-out, and the polarity is the argument.** `AcceptsEndedSession` is opt-**in** because a
    forgotten marker there refuses something: loud, and filed within a day. Here both directions are
    loud, but only one is loud in the *right* direction: a forgotten opt-out is a `403` on a route
    that should have worked, while an opt-**in** gate whose marker was forgotten hands budget
    content to a locked session with nothing going red. **The derivation is what matters, not the
    tally**: which polarity a marker takes follows from which of its two failures is audible.
- **Enforced in**: `FullSessionRequirement` and its handler, carried on the fallback authorization
  policy beside `RequireAuthenticatedUser` and beside the **session cookie scheme, named
  explicitly** — so it reaches every route declaring no policy of its own, which is everything
  outside the anonymous surface, the **registration** group and the **locked sign-in**. Both declare
  a policy naming the identity provider's scheme, which takes them out of the fallback; the outcome
  is right rather than worked around, because neither is served under a session — each opens one —
  so a requirement about session kinds has nothing to judge. The locked sign-in asks the cookie
  scheme itself whether the browser already holds one, for its `409`, through the same
  `SessionCookieAuthenticationHandler.TryReadSessionKind` the two requirement handlers use.
  `POST /api/me/email-change` reads a provider token too and **stays** on
  the fallback: it authenticates that token in a filter beside the session rather than in a policy,
  so a locked session is refused there like everywhere else — see
  [email-change.md](email-change.md). Naming the scheme on the fallback restates the default and
  is worth the
  line twice over: it makes the fallback readable off the route table, and a later change of default
  cannot silently move every route that declares nothing onto some other handler. Routes opt out
  with `AllowsLockedSessionAttribute`; the opted-out set is exactly
  `POST /api/me/session/revocation`, `GET /api/me/session` and `POST /api/me/erasure/schedule`, read
  whole off the route
  table by `LockedSessionTests`, which carries a paragraph arguing each. The
  kind claim is judged by a **round trip** — parse, then compare the presented text ordinally
  against what the parsed member renders as — because `Enum.TryParse` admits `"full"` under its
  case-insensitive overload and `"1"` under *every* overload, and the claim is written by
  `SessionKind.ToString()`. `SessionKindReach.ReadsBudgetContent` is the single definition of the
  rule; `Session.ReadsBudgetContent` calls it rather than restating the comparison, so a kind added
  later cannot be admitted by one caller and refused by the other.
  - **The locked-only route is held by a second requirement on the same fallback policy, and an
    opt-in marker.** `LockedSessionOnlyRequirement` rides the fallback beside
    `FullSessionRequirement`. Its handler succeeds on every route not carrying
    `RequiresLockedSessionAttribute`, and on a marked route only for a kind claim that round-trips to
    exactly `Locked` — an equality, never "reads no budget content", so a kind added later reaches no
    locked-only route until somebody edits that line. The marker **narrows and never widens**: a
    locked session gets past `FullSessionRequirement` only by `AllowsLockedSessionAttribute`, so a
    locked-only route carries **both**, and one carrying the narrowing marker alone refuses every
    session there is. **Opt-in, because the other polarity cannot ship**: a gate on by default would
    refuse every full session in the product until each route argued its way out. The cost is that
    a forgotten marker is quiet — a full session reaches the route and nothing goes red at runtime —
    so the route is pinned twice: by `ErasureScheduleEndpointTests`, which refuses a full session
    beside a locked one succeeding on the same account, and by `LockedSessionTests`, whose census
    reads the locked-only set whole off the route table and whose
    `EveryLockedOnlyRoute_AlsoAllowsALockedSession` refuses a route carrying one marker without the
    other. **Both markers mean something only on the fallback**: a route declaring authorization of
    its own — a policy, a bare `RequireAuthorization()`, `AllowAnonymous()` — leaves the fallback,
    and both requirements with it, so a marked route there admits whatever its own declaration does.
    `EveryRouteCarryingALockedSessionMarker_RidesTheFallbackPolicy` refuses any marked route
    carrying such metadata. The handler is registered in `Program.cs`, and `LockedSessionOnlyRequirementTests`
    resolves the registered set — unregistered, the fallback carries a requirement nothing can
    satisfy, a `403` on every authenticated request. Three other shapes were refused:
    - **The route declaring a policy of its own.** The routes that declare one are the
      registration group and the locked sign-in, which are served under no session — each opens
      one; this route is served under one. Leaving the fallback means the route carries only the
      rules it restates — the cookie scheme, an authenticated user, every requirement beside them.
    - **A kind check in the route delegate or the handler.** It refuses the same requests and is
      invisible to the route table, so no census can read which routes carry it. The locked
      sign-in's delegate does check a kind, and that is not this shape: it judges a cookie beside a
      request its policy authenticated on the provider scheme, which no requirement on that policy
      can see.
    - **Teaching `FullSessionRequirement` to refuse a full session on marked routes.** A requirement
      named for admitting full sessions would then also refuse them, and its name would lie.
  - **A real sign-in reaches the gate.** `POST /api/locked-session` opens a locked session over the
    federated credential, and `LockedSignInEndpointTests` follows that session to `GET /api/accounts`
    (`403`), `GET /api/me/session` (`200`, `"kind": "locked"`) and the sign-out (`204`). The census
    and refusal tests seed their session and its handle directly through the database, and each
    refusal is paired with a `Full` session on the same account against the same route — without
    that arm, a policy refusing everybody passes. The schedule route's tests seed theirs too, and
    the pairing runs the other way round: the full session is refused and the locked one succeeds.
  - **A principal arriving here with no kind claim is refused, and nothing escapes on its scheme.**
    The fallback names the cookie scheme, so `AuthorizationMiddleware` re-authenticates against that
    handler alone — which makes a claimless principal a cookie principal without one, a session this
    product did not write. **Do not add an escape for another scheme**: one existed while a bridge
    forwarded bearer-bearing requests to `JwtBearer`, and it was a hole with a comment on it rather
    than a rule.
- **Example**: `POST /api/me/session/revocation` answers `204` to a locked session,
  `GET /api/me/session` answers it `200` with `"kind": "locked"`, and
  `POST /api/me/erasure/schedule` answers it `200` with the instant the erasure takes effect while
  answering a full session on the same account `403`, with the same body as the refusals below;
  `GET /api/accounts`, `GET /api/me`, `GET /api/me/export`, `GET /api/me/credentials`,
  `GET /api/me/account-keys` and
  `POST /api/me/erasure` each answer `403` with a body identical to the others and naming no
  session, credential or kind. Two 403s live on this path and they must stay distinguishable to a
  reader: the first-party control's carries its own title, this one carries none.
  - **The email change is refused the same way, and on this route the reason is sharpest**: the
    Google identity is exactly what a locked session holds, so letting one move it would let the
    weakest credential re-point the account. Pinned by
    `EmailChangeEndpointTests.EmailChange_ForALockedSession_IsRefused403_WhileAFullSessionOnTheSameAccountSucceeds`,
    which pairs the refusal with a full session on the same account.
  - **The account-keys refusal is the one this rule reads most literally**, and the widening made it
    more so rather than less. A federated credential can derive no key-encryption key, so a locked
    session reaching that route would be handed **every** wrapped row on the account and hold nothing
    to open any of them. It is pinned by
    `AccountKeysEndpointTests.AccountKeys_ForALockedSession_AreRefusedWithForbidden`, which pairs
    the refusal with a `Full` session on the same account, so a gate refusing everybody cannot
    pass it.
- **Counterexample**: refusing a locked session by publishing no ambient budget for it. It looks
  equivalent and is not — the export reads `budgets` by `user_id`, so it would sail through, and
  every other route would fail with a raw exception rather than a refusal.
- **Note**: the schedule route is the release valve for somebody holding nothing but a provider
  sign-in. The sign-in that brings them to it exists on the server — `POST /api/locked-session`,
  which answers the account's scheduled instant beside the session. In the browser, `/release`
  starts its trip, sends its request on the return, and then files the schedule with
  `MeApiService.scheduleErasure`. The **immediate**
  erasure, `POST /api/me/erasure`, is refused to a locked session like everything else. Why a
  schedule needs no passkey, and why a full session must not file one, is argued in
  [erasure.md](erasure.md).
- **Source**: `[SOURCE: discussion]`

## Workflows & State Transitions

```mermaid
stateDiagram-v2
    [*] --> Established : a credential authenticates its owner
    Established --> Revoked : someone ends this session, or the credential that opened it
    Established --> Expired : expires_at_utc passes with nobody revoking anything
    Established --> [*] : the account is erased, or displaced when the browser presenting it establishes a session (the row is deleted)
    Revoked --> [*] : swept when the account next establishes a session, displaced when the browser presenting it does, or deleted with its credential or the account
    Expired --> [*] : swept when the account next establishes a session, displaced when the browser presenting it does, or deleted with its credential or the account
```

| Transition | Triggered by | Validations |
|---|---|---|
| → Established | `Session.Establish(credential, createdAtUtc, expiresAtUtc)`, reached from `RegisterAccountHandler` once a registration ceremony verifies — over the **passkey** credential it just created, never the recovery-codes one — from `CompleteAssertionHandler` once a passkey assertion verifies, from `RedeemRecoveryCodeHandler` once a presented verifier matches a stored hash, from `GenerateRecoveryCodesHandler` when replacing a set ended at least one of that set's sessions, and from `EstablishLockedSessionHandler` once a provider token's subject matches a federated credential — the one path whose session is `Locked` | the credential is required; the expiry must be after the creation instant; the kind is derived from the credential's type and cannot be supplied |
| Established → Revoked | `Session.Revoke(revokedAtUtc)`, reached two ways: through `RevokeSessionsForCredentialHandler`, which `RevokePasskeyHandler`, `GenerateRecoveryCodesHandler` and `ChangeEmailHandler` each call before deleting a credential, and through `RevokeSessionHandler`, which `POST /api/me/session/revocation` calls to end the caller's own | none. Already revoked is a no-op keeping the first instant, which is what makes a retry honest about having ended nothing new |
| Established → Expired | the clock | none. `IsActiveAt` reads the expiry as well as the revocation, with an exclusive boundary: a session is live up to its expiry and not at it |
| Revoked / Expired → deleted (swept) | `SessionRepository.AddAsync`, in the save that writes a new session and its handle, reached from `CompleteAssertionHandler`, `RedeemRecoveryCodeHandler`, `GenerateRecoveryCodesHandler` when it re-establishes, and `EstablishLockedSessionHandler`. **Not** from `RegisterAccountHandler`, which writes through `IRegistrationRepository` onto an account that holds no session yet | the row is not `IsActiveAt` the new session's `CreatedAtUtc` — any revocation, or an expiry at or before that instant; `user_isolation` keeps it to the published account; the handle leaves by the cascade from `sessions`. A failed save deletes nothing and stores nothing, and a row another request deleted or revoked first is re-read, up to three attempts. The next request presenting the cookie finds no token row and answers `401` — **the sign-out route included**; see the ended-session rule |
| Established / Revoked / Expired → deleted (displaced) | `SessionRepository.RemoveAsync`, called by `DisplaceSessionHandler`, which `SessionCookieWriter` runs in a dependency scope of its own once an establishing handler has returned — on each of the five establishing paths' established arm, before the new cookie is written | the row is the one the incoming cookie's handle names, through `AuthenticateSessionHandler`, live or ended; that handler publishes the row's owner into the child scope, so `user_isolation` admits it whichever account owns it; the handle leaves by the cascade from `sessions`. A handle naming no row deletes nothing, and a row revoked under the delete is re-read, up to three attempts. A failed delete answers `500` after the new session committed and writes no cookie. Whether a row was removed does not change the response — see the displacement rule |
| Established → deleted | `EraseAccountHandler` deleting the user row; the session and its `session_tokens` rows leave by the cascade `users → credentials → sessions → session_tokens`, in the erasure's own transaction. A revoked or expired row leaves the same way | none, and nothing is stamped: a `revoked_at_utc` would be a remnant. The cascade runs as the referencing table's owner, so the erasure needs no `DELETE` grant on either table — see [erasure.md](erasure.md). The next request presenting the cookie finds no token row and answers `401` — **the sign-out route included**, which makes this one of the ends where the cookie stays on the client until its `Expires`; a swept session, a deleted credential and a lost establishing response are others the ended-session rule lists. Harmless, because it names nothing; do not answer it by relaxing the lookup — see the ended-session rule |

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

## Integration Points

- **[Users & Ownership](users-and-ownership.md)** — the credential that establishes a session, and
  the account it belongs to. A session adds nothing to identity; it records what a credential
  already proved. On the pipeline order: `FirstPartyRequestMiddleware` runs after CORS and before
  authentication, and **nothing runs between authentication and authorization**.
- **[Registration](registration.md)** — the **fourth** establishing path, and the only one that
  writes `sessions` and `session_tokens` through a port other than `ISessionRepository`. It has no
  transaction, so the session row and its handle ride on the same save as the account; the session
  is opened over the **passkey** credential and never over the recovery-codes one, a mistake that
  satisfies every constraint in the schema and changes only which credential a later revocation
  sweeps. **It runs no ended-session sweep**, and needs none: the account id is derived from this
  registration's own challenge and the `users` row is inserted in the same save, so no session can
  already exist under that account. A sweep there would be a read and a delete over a set that is
  empty by construction. **Displacement does run there**, as a step after the ladder rather than
  inside it: the endpoint hands the committed session's handoff to `SessionCookieWriter`, so the
  account is still one save and `RegisterAccountHandler` knows nothing of it. The browser
  registering can hold a cookie of another account, one of the cases the writer's own dependency
  scope exists for.
- **[Recovery Codes](recovery-codes.md)** — a set of codes opens a `Full` session exactly as a
  passkey does. The two routes divide differently than the names suggest: a **redemption** opens a
  session and revokes nothing, while a **regeneration** revokes the replaced set's sessions and —
  when it ended any — opens one over the new set in their place, so it is the one path that does
  both. The condition is the sweep's own count, which is why that file argues the `sessionsEnded`
  contract from the other side.
- **[Account Keys](account-keys.md)** — `GET /api/me/account-keys` reads **no session at all**, and
  the sign-out route this file argues is once again the only one that reads the `session_id` claim its
  own request's authentication produced. That route briefly narrowed its answer to the credential the
  session was opened over, on the theory that a browser can only ever hold a key-encryption key
  derived from the factor its own session began with. It cannot: a ceremony can present **any** of the
  account's factors — re-authentication looks a passkey up by account and the assertion options carry
  no `allowCredentials`, so the authenticator chooses — so a session tells that route nothing it may
  act on. A session here answers "who is asking" and nothing more, which is what it answers
  everywhere else in the product.
  - **Two words share a spelling and must not share a meaning.** A **locked session** is what this
    file means throughout: a row a federated credential opened, a fact about what the *server* will
    answer. A **locked account** is that file's word for a browser that does not hold the content
    key, which is the state every tab starts in and which a page reload returns to, on a session
    that is perfectly live. **The two are left by different acts, and that is the sharpest way to
    keep them apart.** Nothing in the product turns a *locked session* into a full one — `kind` is
    off the grant's `UPDATE` list and `Session` has no setter — so a person holding one reaches budget content only by a
    new sign-in on a passkey or a recovery code, which writes a new row. A *locked account* is left on
    `/app/settings`, by the Account keys section's **Unlock**: a passkey ceremony the browser mints
    and discards, which calls no route, spends no challenge and changes no row in `sessions`. So an
    unlock is invisible to everything this file describes, and a sign-in is not the only way to
    reach an opened account. What a *factor* can open is the account keys' subject; **the session's
    own lifetime is not custody's** — the keys end at a sign-out, at an erasure's `204`, at a `401`
    the session judgement confirms and at a page load, and every one of those but the page load
    goes through `SessionService.ended()`, which is the part this file records.
- **`user_isolation`** — the policy on `sessions` is the policy every user-owned table carries, keyed
  on the same session setting: `users`, `budgets`, `sessions` itself,
  `passkey_signature_counters`, `wrapped_account_keys`, `key_rotations`, `key_rotation_seals`,
  `factor_manifests` and `erasure_schedules`.
  Written as the whole set rather than as the neighbours, because a reader checking whether a table
  is policed reads the list they are standing in front of, and one that silently omits its own
  subject teaches them to read it as a sample. What actually holds the rule is
  `RowLevelSecurityCoverage`, which reaches the verdict from a table's columns and fails closed on a
  table nobody decided about.
- **CORS** — the default policy gains `AllowCredentials()`, because a browser drops a cross-origin
  response carrying a cookie unless the header says so, and drops it **silently**: the request
  succeeded, the server wrote the `Set-Cookie`, and the jar is simply empty afterwards. The
  configured allow-list is unchanged and stays the control. `AllowAnyOrigin` must never appear
  beside it — the CORS middleware rejects the pair at runtime, and the reason it refuses them is the
  reason not to want them.
- **`SessionContextInterceptor`** — **not** about a session in this file's sense. It writes
  `app.current_user_id` and `app.current_budget_id` onto each PostgreSQL connection the context
  opens; the PostgreSQL backend session and a `Domain.Sessions.Session` share a word and nothing
  else.

## Edge Cases & Known Gotchas

- **The handle never appears in a response body.** The cookie is `HttpOnly` precisely so that
  nothing else is a handle; no response record carries a token or a session id, and
  `SessionTokenSecrecyTests` is a census over every type a route serialises so a record added later
  is covered without anybody remembering. A caller learns *that* a session exists and when it
  expires, and cannot spend that knowledge.
- **The cookie is written by the endpoint, through `SessionCookieWriter`, on the handler's success,
  and never before it.** An
  endpoint that wrote one unconditionally would leave a cookie behind on a refused ceremony. Note
  what does *not* hold that rule: on the ceremony routes a refusal leaves as an exception and
  `UseExceptionHandler` clears the response, so the framework would wipe such a cookie anyway. The
  ordering is held by the positive tests, not by the refusal ones. Displacement rides the same
  placement, and there the refusal tests *do* hold it, because no framework undoes a delete — see
  the displacement rule. **The locked sign-in is the
  exception**: its `404` is a returned outcome, nothing clears that response, so the cookie is
  written on the established arm only and
  `LockedSignIn_ForAnUnregisteredSubject_Answers404NoAccount_AndWritesNothing` asserts no
  `Set-Cookie`. Its `409` is not part of the exception: it leaves as a `ConflictException`, thrown
  before the handler runs, so there is no cookie to clear —
  `LockedSignIn_OverALiveFullSession_IsRefused409FullSession_AndWritesNothing` asserts none.
- **A new cookie overwrites the old one, and overwriting it ends nothing — so the old session is
  displaced first.** Every establishing path writes `__Host-budgetoid-session` over whatever the
  browser held. Left to the overwrite, the session the old cookie named would stay live, held by no
  browser, until it expired or a sweep of its credential ended it — the recovery-code
  regeneration's own sweep is one — and the ended-session sweep could not reach it. So
  `SessionCookieWriter` deletes that session, live or ended, on whichever account owns it, before
  it writes the new cookie; see the displacement rule. The locked sign-in refuses to overwrite a
  live full session, and so displaces nothing there; see its rule above.
- **A first issue of recovery codes sets no cookie.** Only the branch that revoked at least one
  session of the replaced set re-establishes one, and that condition is the rule rather than a
  detail — a handler minting unconditionally passes every other test on that path. Registration is
  not an exception: that route always sets a cookie, because it always establishes a session, and
  there is nothing of the account's for it to have revoked.
  - **The condition is any session of the replaced set, not the caller's own.** A regeneration that
    ended a session the set opened on another device still re-establishes one here, so a browser
    signed in with a passkey is handed a session over the new set, and its passkey session is
    displaced — deleted, not left live beside the new one.
    `RecoveryCodeGenerationTests.Generation_ThatReestablishes_DeletesTheCallersPasskeySession`
    holds it.
- **The token is drawn outside the transactional delegate**, on the three paths that have one. Both
  positions are correct and no test distinguishes them, which is exactly why the choice is written
  down: outside means one secret per request rather than one per retry attempt, and correctness does
  not rest on the subtle property that the value a retried delegate returns belongs to the attempt
  that survived. **Registration and the locked sign-in have no delegate at all**, so there the
  question does not arise.
- **The session cookie is the API's default authentication scheme, and nothing forwards to another
  one.** `JwtBearer` stays registered and is reached two ways: by exactly **two** policies — the
  registration group's and `POST /api/locked-session`'s — and by `ProviderAuthorizationGate`, a
  filter on `POST /api/me/email-change`
  that authenticates the bearer **beside** a full session and never as the request's identity. A
  bearer presented anywhere else authenticates nothing; a bearer presented to the email change with
  no cookie gets the fallback's own `401`
  (`EmailChange_WithAProviderTokenAndNoSession_IsRefused401_AndChangesNothing`). A policy scheme that
  chose a handler per request would put a second way to authenticate an ordinary route back on the
  table.
- **A session's `sub` and a provider's `sub` meet on one request, and never in one principal.**
  On the registration routes and the locked sign-in the provider's principal is the only one — each
  policy names that scheme alone — and the account id is never a claim: registration derives it and
  publishes it after the signature verifies, and the locked sign-in publishes the owner of the
  credential its subject found. The locked sign-in also reads the cookie scheme's result, once, by
  its own `AuthenticateAsync` call, for the session's kind alone — never merged in and never read
  for a `sub`. On the email change both exist at once: `HttpContext.User` is the session's,
  and its `sub` is this installation's account id; the provider's principal is the result of the
  gate's own `AuthenticateAsync` call, read once for its subject and address and never merged in. So
  no principal on any request carries both, and a `sub` read off one means one thing. A policy naming
  both schemes would break that — `AuthorizationMiddleware` merges every named scheme's principal
  into one, carrying two `sub` claims — which is why the email change takes a filter instead; see
  [ADR 0027](../decisions/0027-authenticate-the-email-change-on-the-session-and-a-fresh-provider-token-side-by-side.md).
  `EmailChange_WithANewSubjectAndAddress_StoresBothFromTheProviderToken` asserts the filed subject is
  not the account id.
- **A refused request can leave an identity published behind it.** When a token's digest matches but
  the session is dead, `app.current_user_id` names the account whose handle really did match, on a
  request that then goes on to be refused. It reaches no budget, and every route that runs without
  authenticating publishes its own identity after its own proof, so today this residue changes no
  answer. **The locked sign-in carries it too, and more of it**: its policy names the provider
  scheme, but the cookie is the default scheme and runs first, so a handle that matches publishes
  its account there, and a live session its budget as well. It still changes no answer — the
  discovery read is on an exempt table, a `404` or `409` runs nothing policed after it, and an
  established sign-in publishes the credential's owner over it. Displacement authenticates the
  cookie a second time and publishes its account again, but into a dependency scope of its own, so
  it adds nothing to this residue — see the displacement rule. It is written down because **the
  next anonymous route added is where it would start to**,
  and because the only way to remove it is a "clear" member on `IUserContextWriter`, a decision
  about that port rather than about this path.
- **The cascade can satisfy "revoking a credential ends its sessions" by accident.** Because
  deleting a credential row deletes its sessions, a path that removes a credential without revoking
  first passes a test asserting the sessions are gone — while leaving nothing to say when access
  ended. Any credential-removal path must revoke explicitly **and then** delete, or the fact is
  unobservable.
  - **How the three paths that exist resolve it.** `RevokePasskeyHandler`,
    `GenerateRecoveryCodesHandler` and `ChangeEmailHandler` — which retires the federated credential
    when the Google identity moves — each revoke and then delete, and because the delete removes the
    very rows the revocation just stamped, the schema afterwards is identical either way. So the
    evidence leaves in the response instead, as `sessionsEnded`. See
    [email-change.md](email-change.md).
  - **Erasure is the one exception, and it is named rather than implied.** `EraseAccountHandler`
    removes every credential on the account and revokes nothing: each session and its token rows
    leave by the cascade from `users`, in the erasure's own transaction. The rule above exists so a
    surviving account can be told when access ended; after an erasure nobody is left to tell, and a
    stamp would be a remnant deleted by the transaction that wrote it. See
    [erasure.md](erasure.md). This does not loosen the rule for any path that leaves the account
    standing.
  - **The test nobody should write** is "after revocation the credential has no active session". It
    is green with the revocation call deleted, and therefore proves nothing.
  - **On the recovery-code path a second mutation produces the same wrong number**: swapping the
    revocation with the delete also reports `0`, because the cascade has already taken the session
    rows and the sweep matches nothing. So the count discriminates ordering as well as presence.
- **Between the revocation and the delete, the tracked sessions are discarded, and on `sessions`
  nothing notices when they are not.** Revoking loads every unrevoked `Session` into the change
  tracker. Remove the credential with those dependents still tracked and EF cascades into the copies
  it can see and emits its own `DELETE FROM sessions`. The role holds `DELETE` there for the
  ended-session sweep and for displacement, so that statement succeeds and removes the rows the
  database's cascade would
  have taken: the outcome is the same. Measured: removing the second `DiscardTrackedEntities()` in
  `RevokePasskeyHandler` or `GenerateRecoveryCodesHandler` reddens no integration test, and only the
  unit replay and placement tests notice. The discard stays because the statements past it are
  written to an empty tracker, and because the cascade a tracked `Session` drags behind it does not
  stop at `sessions`. Same mechanism `EraseAccountHandler` documents for `budgets`, which still holds
  no `DELETE`.
  - **The `DELETE` the sweep and displacement need costs this trap its alarm on `sessions`.** Like
    `recovery_code_hashes`, which is granted `DELETE` too, the mistake there succeeds silently rather
    than raising `42501` — see [recovery-codes.md](recovery-codes.md). The `42501` a table holding no
    `DELETE` raises is a diagnostic the grant matrix buys, not an inconvenience it imposes, and on
    this chain `session_tokens` is the table still buying it.
  - **`session_tokens` is where the alarm still sounds.** A session carries a handle, so the cascade
    a tracked `Session` drags behind it reaches one more relation, and the role holds no `DELETE`
    there. A `SessionToken` tracked under a session EF removes makes EF send its own delete on
    `session_tokens`, and the request dies with `42501`. **The failure names a permission and the
    cause is the change tracker; do not answer it with a grant on `session_tokens`.** The
    ended-session sweep removes sessions on requests that may already have read a handle; the locked
    sign-in is where that was measured, because it reads the cookie and never clears the tracker,
    and it is why `FindByTokenHashAsync` reads untracked — see the locked sign-in's rule.
    Displacement reads a handle and deletes its session in one context, its own scope's, and that
    read is the same untracked `FindByTokenHashAsync`, reached through `AuthenticateSessionHandler`.
    `GenerateRecoveryCodesHandler`'s never-materialise rule names three tables, this one among them.
    - **The trap needs *both* links in the tracker, which is what makes it easy to lose.** A read
      that projects — `Select(session => session.Id)` — materialises no entity, so EF has no cascade
      to walk and nothing fails. Somebody "optimising" a read into a projection will find the rule
      stops biting and conclude it no longer applies. It does; the read simply stopped being the
      shape that triggers it.
- **Revoked and expired rows stay until the account's next sign-in.** The ended-session sweep takes
  them in the save that writes the next session, and only their credential or the account leaving,
  or displacement by a sign-in in a browser still presenting one's cookie, takes them sooner — the
  MUST NOT on `DELETE` says where else a sweep could run and why it does not. So a signed-out session stays readable, and its cookie still gets the sign-out route's `204`,
  until then; afterwards that cookie gets `401` on every route. An account that never signs in
  again keeps its last batch. A registration sweeps nothing, because no session can predate it.
- **`RevokeSessionsForCredentialHandler`'s three callers do not all mean the same thing by the
  number.** To `RevokePasskeyHandler` and `ChangeEmailHandler` it is evidence and nothing more; to
  `GenerateRecoveryCodesHandler` it is also the condition a re-established session is written on.
  So a change to what `RevokeForCredentialAsync` counts — the obvious candidate being to narrow
  `revoked_at_utc is null` to a live-at-now reading — changes behaviour on one path while looking
  like a reporting fix on all three. All three reach it through the command handler rather than
  straight to `ISessionRepository`, because the handler is where the clock is read, so one decision
  to end access is stamped as one instant however many rows it touches. **`ChangeEmailHandler` is the
  one caller that sweeps the federated credential**, and it does so only when the Google identity
  moves. The locked sign-in opens sessions over exactly that credential, so the sweep is what ends
  them when the identity they were opened on moves — see [email-change.md](email-change.md).
- **The expiry is decided by the caller, and the five callers read one value.** `Session.Establish`
  validates only that the expiry is after the creation instant; the number — **14 days** — is
  `SessionPolicy.Lifetime` in `Application/Sessions`, which all five handlers add to the instant
  they read. It lives in Application rather than Domain because how long a session lasts is product
  policy, which ADR 0002 keeps above the invariants, and it is not on `IPasskeyCeremonyPolicy`
  because a session lifetime varying per environment is a difference nobody meant.
  - **The equality is the rule, and one value is what makes it one.** A recovery sign-in that
    expired sooner would tell somebody who has just lost their device that the way back in they were
    issued is worth less than the one they lost; the regeneration path is held to the same number
    because its caller cleared a passkey gate, stronger than whatever opened the session its sweep
    took. **Any two of them differing is a defect rather than a decision**, and sharing the value is
    the only shape in which a single edit cannot separate them — which is exactly what happened when
    the fourth path arrived: `RegisterAccountHandler` inherited the interval by construction rather
    than by anybody remembering.
  - **The locked sign-in shares it by decision, not by force.** Nothing in the domain makes a
    locked session's interval match a full one's; "locked sessions last as long as full ones" is a
    product rule like the recovery paths' equality. `EstablishedSessionLifetimeTests` drives it
    beside the passkey assertion, the redemption and the regeneration, so a locked-only constant
    reddens there.
  - **What is shared is the interval and nothing else.** *When* each handler establishes its session
    is a security property that path owns, argued at its own call site and stated as its own rule in
    [passkeys.md](passkeys.md), [recovery-codes.md](recovery-codes.md) and
    [registration.md](registration.md). One lifetime is not licence to lift those sequences into
    anything shared.
