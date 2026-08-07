# Erasure

## Table of Contents

- [Purpose](#purpose)
- [Key Entities](#key-entities)
- [Constraints](#constraints)
- [Business Rules & Invariants](#business-rules--invariants)
- [Workflows & State Transitions](#workflows--state-transitions)
- [Integration Points](#integration-points)
- [Edge Cases & Known Gotchas](#edge-cases--known-gotchas)

## Purpose

Erasure is the one action that destroys an account and everything owned beneath it. It exists so
that leaving the product means actually leaving, rather than being archived: after it completes, no
row in any table references the erased user or any budget it owned.

Because it is irreversible, it is the one action a bearer token does not buy on its own. A request
must carry a WebAuthn assertion made moments earlier on an authenticator registered to the account —
so a stolen session cannot destroy a budget.

It cuts across almost every domain area — `users` and `credentials` from
[users-and-ownership.md](users-and-ownership.md), the identity material in
[passkeys.md](passkeys.md) and [sessions.md](sessions.md), and every budget-owned table — so the
ordering rule and the post-condition live here rather than being split across the files whose rows
it removes.

## Key Entities

Erasure owns no entity of its own. It acts on the account graph that already exists, and the shape
of that graph is what the rules below are about:

```mermaid
erDiagram
    USER ||--o{ CREDENTIAL : "cascade"
    USER ||--o{ BUDGET : "cascade"
    CREDENTIAL ||--o{ SESSION : "cascade"
    CREDENTIAL ||--o| PASSKEY_PUBLIC_KEY : "cascade"
    CREDENTIAL ||--o| PASSKEY_SIGNATURE_COUNTER : "cascade"
    BUDGET ||--o{ ACCOUNT : "cascade"
    BUDGET ||--o{ PAYEE : "cascade"
    BUDGET ||--o{ CATEGORY_GROUP : "cascade"
    BUDGET ||--o{ CATEGORY : "cascade"
    CATEGORY_GROUP ||--o{ CATEGORY : "restrict"
    BUDGET ||--o{ TRANSACTION : "restrict"
    ACCOUNT ||--o{ TRANSACTION : "restrict"
    CATEGORY ||--o{ TRANSACTION : "restrict"
    PAYEE ||--o{ TRANSACTION : "restrict"
```

Every edge is `ON DELETE CASCADE` except the five marked `restrict`, and that distinction is the
whole of the deletion order below. Four of the five have `transactions` as their child, which is why
that is the one table erasure empties itself.

## Constraints

### MUST

- Erasure **MUST** leave no row in any table referencing the erased user or any budget it owned.
  That is the post-condition the feature exists to deliver, and it is what a verification query
  asserts rather than a count of statements issued.
- Erasure **MUST** run as one database transaction, and **MUST** either delete every row it covers
  or leave every one of them exactly as it found them. A half-finished erasure is worse than none:
  the person cannot tell what survived, and nothing in the product would be left to tell them.
- Erasure **MUST** run as the least-privilege application role, on the connection serving the
  request. Doing it on an elevated connection would dissolve
  [ADR 0004](../decisions/0004-connect-as-a-least-privilege-role.md), and an administrator is not
  subject to row-level security at all.
- The handler **MUST** discard the context's tracked entities before deleting the user row. This is
  not retry hygiene; see the rule below, where the reason is the grant matrix.
- Erasure **MUST** delete, in dependency order and before the user row, every table a `RESTRICT`
  edge would otherwise block.
- Erasure **MUST** be authorized by a fresh WebAuthn assertion on a `reauthentication` challenge, for
  a passkey registered to **the account the request is authenticated as**. A bearer token alone is
  not proof; it is the thing the gate exists to distrust.

### MUST NOT

- Erasure **MUST NOT** take the account's identity from the request — not from the route, not from
  the body, not from a query string. It reads `IUserContext` and nothing else.
- Erasure **MUST NOT** answer `404` for an account that is already gone. It states a post-condition
  rather than acting on a row, and a `404` would tell someone their data might still be there.
- The re-authentication gate **MUST NOT** publish an identity — it never calls `IUserContextWriter`.
  The sign-in handler does exactly that and the rule does not transfer, which makes this the most
  inviting wrong turn in the area. `SessionContextInterceptor` writes `app.current_user_id` and
  `app.current_budget_id` together at connection open, so a user id re-published mid-request does
  **not** move the budget: Alice's bearer token with Bob's passkey would empty Alice's budget while
  deleting Bob's user row.
- The gate **MUST NOT** run inside the transactional delegate. See the rule below — the reasons are
  the nonce, not the `22P02` that governs the sign-in path.
- The application role **MUST NOT** be granted `DELETE` on `budgets`. Budget rows leave by the
  database's own cascade from `users`, which runs with the referencing table owner's privileges. A
  `42501` naming `budgets` is a change-tracker fault, never a missing grant — see the rule below.

## Business Rules & Invariants

---

- **Rule**: The freshness window is the **challenge's own server-issued lifetime**, five minutes. No
  re-authentication instant is stored anywhere, and no timestamp is accepted from the client.
- **Why**: the assertion travels in the erasure request itself, so the only thing that can be stale is
  the nonce it was built on — and that nonce is minted by the server, held by the server, and expired
  by the server inside `ConsumeAsync`. There is nothing for a client to supply and therefore nothing
  to trust. **The gate reads no clock at all**; a `TimeProvider` on it would suggest a second instant
  somewhere matters.
- **This is stricter than the requirement, not looser.** The window is measured from **challenge
  issue**, which is strictly before the person touched their authenticator, so the enforced gap
  between proof and destruction is shorter than five minutes rather than longer.
- **The absence of a stored instant is a decision, not an omission.** A `reauthentications` table
  would be mutable per-user state on an account whose whole point is that it can be destroyed
  wholesale, and it would exist before sessions authenticate requests at all. Anyone reaching for one
  should read the decision-log entry first.
- **Enforced in**: `DbWebAuthnChallengeStore.ChallengeLifetime` and the expiry comparison inside
  `ConsumeAsync`. `ErasureReauthenticationTests.Erasure_OnAChallengeOlderThanTheWindow_IsRefusedAnd`
  `ErasesNothing` inserts a pre-expired `reauthentication` row out of band and signs those exact
  bytes; `…Erasure_OnALiveChallengeInsertedTheSameWay_ReturnsNoContent` is its control, without which
  a gate refusing every out-of-band challenge for an unrelated reason would pass the first vacuously.
  `…Erasure_SendsNoTimestampAndReadsNone` pins the shape on both legs.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: The gate runs to completion **outside** the transactional delegate.
- **Why**: two reasons, and **neither is the `22P02` one** that governs `CompleteAssertionHandler`.
  Identity here is published by `UserProvisioningMiddleware` before the handler runs, so the
  connection is configured correctly whenever it opens.
  1. `ConsumeAsync` deletes the nonce on its own save. Inside the erasure transaction, a rolled-back
     erasure would **restore the spent nonce** and make the same assertion replayable — destroying the
     single-use property the design rests on.
  2. The delegate is replayed under `NpgsqlRetryingExecutionStrategy`. A gate inside it would consume
     a second time, find the nonce spent, and refuse a **valid** erasure with the same 401 an attacker
     gets, because the database blinked.
- **Enforced in**: the call ordering in `EraseAccountHandler`, with both reasons on the call site.
  `EraseAccountHandlerTests.HandleAsync_WhenTheUnitOfWorkIsReplayed_StillErasesTheAccount` runs a
  single-use challenge stub through `RetryingTransactionalExecutor(2)` and goes red the moment the
  call moves below `ExecuteAsync`. It is an outcome pin, not a call-order pin.
- **Counterexample**: wrapping gate and erasure in one transaction for tidiness. Both halves of the
  damage are invisible on a green day.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: The unit of atomicity is the **transactional delegate, not the request**. Everything
  erasure covers commits together or not at all — but the gate's writes commit *before* the
  transaction opens and are deliberately **not** rolled back with it.
- **There are exactly two such writes**: the spent nonce, which `ConsumeAsync` deletes from
  `webauthn_challenges` on its own save, and the advanced
  `passkey_signature_counters.signature_counter`, which `SaveCounterAsync` flushes. One moves a row
  count; the other moves only a value, so a verification that counted rows would catch the first and
  be structurally blind to the second. Both are pinned, and by different means.
- **Why the scope is the erasure and not the request**, when the rule is stated as "every row": the
  atomicity rule states its own scope — an erasure either deletes every row **it covers** or none —
  and the condition that triggers the guarantee is a failure in *part of an erasure*. The gate is the
  authorization deciding whether an erasure begins at all, not a part of one. When it refuses, no
  erasure runs and nothing it covers moves, which is what the re-authentication tests already show.
- **What the literal reading would cost**, beyond the two reasons the rule above already carries: a
  rolled-back erasure would also rewind the counter, so a cloned authenticator could re-assert at a
  value it had already used.
- **Enforced in**: `EraseAccountHandler`, whose single `ITransactionalExecutor.ExecuteAsync` covers
  both saves, and `DbContextTransactionalExecutor`, which opens one transaction inside the execution
  strategy and commits once.
  `ErasureAtomicityTests.Erasure_WhenTheUserDeleteFails_LeavesEveryRowCountUnchanged`
  fails the **second** save and compares every ordinary table in the database either side of the
  request; the claim is that whole comparison, and the surviving `transactions` rows are the one
  count that carries the conclusion, because two transactions would have committed the first.
  `…Erasure_WhenNothingFails_RemovesEveryOwnedRow` is the control that keeps the comparison from
  passing vacuously — an enumeration that found nothing would satisfy "every count is unchanged"
  perfectly. `…Erasure_WhenTheUserDeleteFails_SpendsTheAssertionAnyway` pins the boundary from the
  other side, the nonce by count and the counter by value, and goes red the moment the gate moves
  inside.
- **Counterexample**: proving the boundary with a verification that counts rows. It catches the spent
  nonce and is structurally blind to the advanced counter, so green there does not mean the boundary
  holds — which is why the counter is pinned by its value instead.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: Erasure deletes explicitly **only what a `RESTRICT` edge would otherwise block**, in
  dependency order; everything joined to the account by `CASCADE` alone is left to the cascade.
  Today exactly one table satisfies that, and the sequence is **transactions → the user row**.
- **Why**: the rule is stated as a rule rather than as a list of statements because the list is what
  a future table has to be measured against. The owned graph carries five `RESTRICT` edges, and
  `transactions` is the child of four of them — `→ budgets`, `→ accounts`, `→ categories`,
  `→ payees`. Those four are the guard that stops an ordinary delete taking recorded money movement
  with it, so a delete that leant on the cascade answers `23503` for any account that ever recorded a
  transaction, which in a budgeting product is the ordinary case rather than the corner. Emptying
  that one table is also what disarms the fifth edge, because a `RESTRICT` edge cannot bite once its
  child rows are gone.
- **`categories → category_groups` is left to the cascade, and that does not rest on constraint
  ordering.** Both tables cascade from `budgets`, so one `budgets` delete reaches two tables joined
  to each other by a `RESTRICT` edge — but PostgreSQL queues the check for that edge as an after-row
  trigger when the `category_groups` row is deleted, which is strictly after the cascade into
  `categories` was already queued, and the after-trigger queue is FIFO. `RESTRICT` being
  non-deferrable does not make the check fire mid-statement. Verified on PostgreSQL 17 against
  schemas built with the two constraints created in either order, so that their OIDs — and with them
  the RI trigger names that decide firing order — were reversed: both leave the tables empty.
- **Enforced in**: `EraseAccountHandler`, whose two saves are ordered by the method rather than by
  EF. `AccountErasureEndpointTests.Erase_ForAFullyFurnishedAccount_ReturnsNoContent` seeds a
  categorized transaction, which puts four of the five edges in the path;
  `…Erase_ForAnAccountWithCategoriesAndNoTransaction_LeavesNoneOfEither` is the one that covers the
  fifth in isolation, and is what goes red if the FIFO behaviour above ever stops holding.
  `EraseAccountHandlerTests.HandleAsync_DeletesTheTransactionsBeforeTheUser` pins the order itself,
  against a fake that models the real edge. `SchemaConstraintSnapshotTests.Schema_PinsEveryForeignKeyAndItsDeleteRule`
  is the schema tripwire: a new `RESTRICT` edge into the owned graph moves a line there, which forces
  someone to come back and measure it against this rule.
- **Example**: an account with one categorized transaction. Deleting the user row first cascades into
  `budgets`, which is refused by `FK_transactions_budgets_budget_id` with `23503`.
- **Counterexample**: collapsing the two saves into one and letting EF order the batch. EF sorts
  topologically by the foreign keys *between the entity types in the batch* — `Transaction` points at
  `Budget`, `Budget` points at `User`, and `Budget` is not in the tracker — so there is no edge and
  no guarantee. It may draw the right order today and a different one after an EF upgrade.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: The handler discards the context's tracked entities before it deletes anything, and that
  call is load-bearing on the **first** attempt of the **first** request, not only under retry.
- **Why**: `UserProvisioningMiddleware` has already resolved the request's identity through the same
  scoped context, which leaves the `Budget` entity tracked. Removing the `User` with that dependent
  still in the tracker makes EF cascade to the copy it can see and emit its own
  `DELETE FROM budgets` — and the application role holds `SELECT` and `INSERT` on `budgets` and
  deliberately no `DELETE`, so the request dies with `42501` before it deletes anything. **The
  failure names a permission and the cause is the change tracker.** Answering it with a grant would
  widen the role's reach, fail `AppRoleGrantMatrixTests`, and leave the real fault in place.
- **Enforced in**: `IPersistenceState.DiscardTrackedEntities()`, called as the first line inside the
  `ITransactionalExecutor` delegate in `EraseAccountHandler`, with the reason written on the call.
  Every test in `AccountErasureEndpointTests` that expects `204` fails with `42501` without it,
  including the one that seeds nothing at all.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: The whole sequence runs on **one** session shape, not two.
- **Why**: `transactions` is policed by `budget_isolation`, which reads
  `app.current_budget_id`, while `users` is policed by `user_isolation`, which reads
  `app.current_user_id`. Those are two policies, but not two connections:
  `SessionContextInterceptor` writes both settings in the same statement on every connection open,
  so the connection serving an authenticated request already names both.
- **Enforced in**: `SessionContextInterceptor`, unchanged by this feature — see
  [ADR 0008](../decisions/0008-read-the-ambient-budget-inside-the-policy.md) for why it must stay a
  connection-opened interceptor.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: The account erased is whichever one the request is authenticated as. The identity comes
  from `IUserContext` and from nowhere else, and **no field exists for a caller to name an account
  in**. `EraseAccountCommand` carries the assertion and nothing else: its members name a credential
  *handle*, and the owner-scoped lookup makes a handle incapable of selecting an account — one
  registered to somebody else answers nothing rather than redirecting the erasure. The rule is "no
  account may be named", not "no members".
- **Why**: `user_isolation` is `FOR ALL`, so a `DELETE` naming another user's id affects **zero rows
  and reports success**. There is no error to catch and no refusal to log; a handler that took an id
  from the request and got it wrong would answer `204` having erased nothing. Keeping the id out of
  the command makes that state unreachable by the type system rather than by a check.
- **Enforced in**: `EraseAccountCommand` (no account-naming member), `EraseAccountHandler` (reads
  `IUserContext.UserId`), and the route, which carries no id segment.
  `AccountErasureEndpointTests.Erase_LeavesAnotherAccountUntouched` is the counterweight: without
  it, a handler that emptied every table in the database would satisfy every other assertion.
- **Counterexample**: `POST /api/me/{userId}/erasure`. Even with an ownership check it would be a
  second place the identity could come from, and the check would be the only thing between a typo and
  a silent no-op.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: Erasure never answers `404`. A handler that is reached over an account already gone
  completes with `204`.
- **Why**: the caller asked for a post-condition — that the account not exist — and that
  post-condition holds. A `404` would be an answer about a row, and the one thing it would
  communicate to the person asking is uncertainty about whether their data is still there.
- **Enforced in**: `UserRepository.DeleteAsync`, which removes whatever the id matched and saves; an
  absent row leaves an empty set and the save is a no-op rather than a branch. The handler never
  reads the user first.
- **A second request from the same client is refused, and — this is the part that matters — it
  creates nothing.** It never reaches the handler. The erasure route declares no `ProvisionsUser`
  metadata, so `UserProvisioningMiddleware` finds no credential for the still-valid token, answers
  `401`, and writes no row — literally none, since the budget heal that used to run on every
  authenticated request is gone: see [users-and-ownership.md](users-and-ownership.md). That answer makes no
  claim about data — it says the request did not prove who it was, which is true, because the account
  it names no longer exists.
- **Erasure is therefore not idempotent to the caller**, and the cost is real: a client retrying after
  a lost `204` sees a failure over data that is already destroyed. The remedy is client-side — do not
  re-run the ceremony on a presumed-lost response. It must **not** be answered by storing a marker
  that an erasure happened, which the rule against tombstones forbids outright, nor by answering `204`
  without a valid assertion, which would put a path through this handler that reports success having
  verified nothing.
- **Enforced in**: `AccountErasureEndpointTests.Erase_CalledASecondTime_IsRefusedAndCreatesNoAccount`
  pins both halves — the second call is `401` **and** `select count(*) from users` comes back `0`.
  That count is the whole assertion: a middleware that minted on the way past would leave `1`.
- **This holds for a row that leaves between the read and the save, too.** Two erasures of the same
  account in flight at once — a double-click, or a client retrying a slow response — both load the
  rows; the loser blocks on the winner's locks, then finds nothing to delete and gets zero rows
  affected against EF's expected one. Left alone that is a `DbUpdateConcurrencyException` and a
  `500`, which tells the user their erasure failed when it succeeded, and is a worse version of the
  `404` this rule already rejects. Both repositories therefore treat a conflict **naming only rows
  this call marked deleted** as the post-condition already holding, and let any other conflict
  propagate. They also detach those rows from the change tracker, sweeping the tracker rather than
  the entries the exception reported — EF reports only the first mismatching command's entries, so a
  surplus entry would otherwise be re-flushed by the next save and abort the erasure outright.
- **Enforced in**: `TransactionRepository.DeleteAllForAmbientBudgetAsync` and
  `UserRepository.DeleteAsync`. `…_WhenAnotherRequestDeletedTheRowsFirst_LetsTheErasureFinish` is the
  one that matters: it stages the conflict on **two** rows, because one row passes under the broken
  shape as well, and then runs erasure's real next step on the same context.
  `…_WhenTheConflictNamesAnotherEntity_LetsItEscape` pins the narrowing on both.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: Erasure reaches every budget the user owns, which today is exactly one.
- **Why**: the repositories it calls are scoped by the `BudgetIsolation` query filter, which resolves
  the **ambient** budget, and a user has exactly one budget with no way to create a second — see
  [budgets.md](budgets.md). So "every budget it owns" and "the ambient budget" name the same rows.
- **This is the assumption a multi-budget change must revisit.** The schema is multi-budget-ready
  and this handler is not: a second budget's transactions would sit outside the ambient filter, be
  left in place, and refuse the user delete with `23503`. The failure would at least be loud rather
  than silent, but it is the first thing to fix on the day a second budget can exist.
- **Enforced in**: `ITransactionRepository.DeleteAllForAmbientBudgetAsync` and
  `ICategoryRepository.DeleteAllForAmbientBudgetAsync`, neither of which takes a budget id — the
  filter is the tenancy, and an id parameter would be a tenancy argument with no ownership check to
  pair with it.
- **Source**: `[SOURCE: user-story]`

## Workflows & State Transitions

```mermaid
sequenceDiagram
    participant C as Client
    participant O as POST /api/passkeys/reauthentication/options
    participant A as POST /api/me/erasure
    participant H as EraseAccountHandler
    participant G as PasskeyReauthentication
    participant D as PostgreSQL

    C->>O: authenticated
    O->>D: issue a reauthentication challenge (lives 5 minutes)
    O-->>C: challenge
    Note over C: the authenticator signs it
    C->>A: assertion (authenticated)
    A->>H: EraseAccountCommand(assertion)
    H->>G: VerifyAsync — outside the transaction
    G->>D: consume the nonce, require ceremony = reauthentication
    G->>D: find the key BY HANDLE AND OWNER, verify, accept the counter
    G-->>H: proved, nothing returned
    H->>H: DiscardTrackedEntities()
    H->>D: BEGIN
    H->>D: delete transactions (ambient budget)
    H->>D: delete the user row
    D-->>D: cascade: credentials, sessions, passkey rows, budgets,<br/>accounts, category groups, categories, payees
    H->>D: COMMIT
    A-->>C: 204 No Content
```

The transaction opens **after** the gate, and the diagram is drawn to make that visible — see the
rule above for the two reasons.

There is no state to transition through: an account is present or it is not. Nothing is marked,
scheduled or flagged, and no row survives to record that an erasure happened — including the proof
that authorized it, which leaves as the deleted nonce.

## Integration Points

- **The grant matrix** — `app-role-grants.sql` gives the role `DELETE` on `users` and `transactions`,
  which is everything erasure needs. `AppRoleGrantMatrixTests` pins the set in both directions, so a
  grant added to make an erasure problem go away fails a test rather than shipping.
- **Row-level security** — `user_isolation` scopes the `users` delete, `budget_isolation` scopes the
  `transactions` delete. Both are `FOR ALL`, so they constrain a delete exactly as they constrain a
  read. See [data isolation](../engineering/data-isolation.md).
- **The referential cascade** — everything not listed above leaves because PostgreSQL performs the
  referential action through internal triggers running with the **referencing table owner's**
  privileges, not the caller's. That is why no grant on any child table is needed, and why adding
  one would widen the role's reach without extending what erasure can do.
- **`ITransactionalExecutor`** — both saves run in one transaction, and the gate is deliberately not
  inside it. `ErasureAtomicityTests` holds both halves: that a failure moves no row the erasure
  covers, and that the gate's own writes survive that failure rather than being undone by it.
- **[Passkeys](passkeys.md)** — the `reauthentication` ceremony, the third nonce pool, and the rule
  that on this path the account comes from the request rather than from the credential.
- **The grant matrix, again, by what it did *not* need.** The gate reads `passkey_public_keys`, writes
  `passkey_signature_counters.signature_counter`, and deletes a `webauthn_challenges` row — all
  already granted. `AppRoleGrantMatrixTests` and
  `RlsCoverageTests.Exemptions_PinTheColumnsTheirReasonCovers` staying green **untouched** is the
  proof this design added neither a privilege nor a column.

## Edge Cases & Known Gotchas

- **A `42501` naming `budgets` is a change-tracker fault.** It means the tracked `Budget` from user
  provisioning was still attached; the fix is `DiscardTrackedEntities()`, never a grant. This is the
  single most likely wrong turn in this area, because the error message points at exactly the wrong
  layer.
- **Erasing twice creates nothing, and that took a deliberate change to the provisioning rule.** A
  Google ID token stays valid for up to an hour after the account it names is gone, and provisioning
  used to mint an account on any authenticated request whose credential did not resolve — so a second
  erasure attempt, an in-flight poll, or a second tab wrote a fresh `users` row carrying the person's
  email moments after they asked to be forgotten. The erasure route now declares no `ProvisionsUser`
  metadata, so those requests are refused before anything is written. See
  [users-and-ownership.md](users-and-ownership.md).
- **A request to a route that *does* mint still resurrects an erased account while the token lives.**
  That hole is older than the re-authentication gate and is not closed here; it closes when account
  creation becomes a consented act. Do not read the rule above as making erasure durable against a
  live token — it makes the erasure path itself, and every identity-bearing route beside it, write
  nothing.
- **The request's own session row is deleted mid-request.** Nothing in the request path reads a
  `sessions` row today — the API authenticates with a bearer token from the identity provider — so
  the row cascades away and the response completes normally. The moment a session-bearing token
  authenticates a request, this endpoint will be deleting the row that authorizes the request it is
  running inside, and that is worth checking then rather than assuming.
- **A failed erasure still spends the assertion, and still advances the signature counter.** Both are
  the gate's writes, both were committed before the transaction opened, and neither returns with the
  rollback — so the person has to run the ceremony again to try once more. That is correct rather
  than a defect; see the atomicity rule above for why the boundary is drawn where it is. It must
  **not** be answered by moving the gate inside the transaction.
- **`webauthn_challenges` is not in the verification query, and that is not an oversight.** A
  challenge belongs to a ceremony rather than to a person and carries neither `user_id` nor
  `budget_id`, so "no row references the erased user" holds vacuously.
