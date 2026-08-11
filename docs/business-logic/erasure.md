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
    CREDENTIAL ||--o{ RECOVERY_CODE_HASH : "cascade"
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
- Erasure **MUST NOT** leave a row or a column behind — no soft-delete flag, no tombstone, no
  deletion record, no anonymized remnant, no archived copy. There is nothing to mark, because the
  row is gone; a column recording the deletion is the row surviving under a different name. This is
  the rule the rest of this file has long cited as settled, stated here at last rather than referred
  to. What holds it is a name scan and a row count, which between them reach what the database
  stores and nothing else — the rule below says which of the five each half actually catches.
- An identifier of an erased account **MUST NOT** be written to a log, a trace or a metric. A line
  naming the user id that was just erased is a deletion record kept outside the database, and it is
  the cheapest remnant in this product to create: one constructor parameter and one statement. Every
  gate above reads names in a catalog or a route table, and a log line has no name for either to
  read.
- The product **MUST NOT** offer any path that reverses an erasure that has taken effect — no
  cancellation, no grace period, no restore. Erasure takes effect at commit, and the constraint above
  is what makes this one true rather than merely asserted: once no remnant exists, a reversal has
  nothing to restore from.

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
  that an erasure happened, which the constraint against remnants forbids outright, nor by answering `204`
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

---

- **Rule**: There is no remnant, and two gates of different shapes say so — the **schema** refuses
  the names a remnant arrives under, and a **row count** refuses the rows. No table carries a
  soft-delete flag, a tombstone, a deletion record, an anonymized remnant, or a column naming an
  archived copy.
- **What the name half holds is names it recognises, and that bound is stated rather than implied.**
  A vocabulary refuses `deleted_at`, `tombstones` and `users_archive`; it has nothing to say about
  `users_shadow`, `legacy_users`, `closed_accounts` or `retained_profiles`, and it never will,
  because a list of refused words cannot enumerate the words nobody has thought of. What catches
  those is the row count below, which reads no names at all.
- **Why**: every one of those is the same defect wearing a different name — a row that outlived the
  erasure that was supposed to destroy it, kept where a query can still reach it. "Deleted means
  deleted" has to be a property of the schema rather than a habit of whoever wrote the last handler,
  because a handler is one review away from being changed and a column is not.
- **Enforced in**: `ErasureRemnantVocabulary`, whose sixteen patterns each carry the argument for
  refusing them, matched by whole-token runs through `IdentifierTokens`. Each pattern is matched both
  as written and in the plural — the matcher does not stem, and the plural is the form a remnant
  *table* arrives in.
  `ErasureRemnantVocabularyTests` holds the vocabulary honest from above — the strongest of its
  controls reads every mapped column off the EF model and fails naming any real column a widened
  pattern swallowed. `ErasureRemnantSchemaTests` scans the live catalog for both columns and relation
  names, with one probe per axis so neither control can pass for the other's reason.
- **The row-shaped half of this rule is carried elsewhere, and deliberately not duplicated here.**
  `ErasureAtomicityTests.Erasure_WhenNothingFails_RemovesEveryOwnedRow` enumerates every relation in
  the database that stores rows of its own — ordinary tables, materialized views and foreign tables —
  and asserts each is empty after a successful erasure, so a remnant *row*, written into an existing
  table or a new one, goes red there. Views and partitioned parents are excluded because they would
  report rows already counted underneath them, not because their rows are safe. A materialized view
  is counted for the opposite of the obvious reason: nothing in a request writes to it, so an erasure
  does not reach it either, and a reporting matview over `transactions` would keep an erased budget's
  money movement until somebody refreshed it.
- **Naming a table in that test's `TablesOutsideTheTransactionBoundary` removes it from this
  assertion too.** The list feeds the shared counting helper, so it excuses a table from the
  completeness check as well as from the drift comparison it was written for — and the comment beside
  it invites exactly that as the remedy when a new table reds the guard. Splitting the two effects is
  a change to that test's design and has not been made; until it is, a table added there is a table
  neither gate covers.
- **Two of the five nouns are held by the row count alone, and no name refuses them.** An anonymized
  remnant is a row that stays with its identifying columns *overwritten under their existing names* —
  an `email` holding `deleted-user-4f2a@example.invalid` is the shape it actually arrives in, and no
  `anonymized_*` column ever appears for a scan to find. A deletion record can arrive the same way: an
  outbox row carrying `event_type = 'AccountErased'` with a user id inside `event_data` is a record
  kept about a deletion, under column names the vocabulary blesses by name as proof it is narrow. In
  both cases the count is the only thing standing there.
- **The omissions are deliberate, and each is an argument rather than a gap.** `archived_at` and
  `is_archived` are permitted, because hiding an account somebody no longer uses is a plausible
  live-row product state, and a rule that cannot tell "this account is closed" from "this user's data
  was copied aside" would refuse the feature. **What that costs is stated rather than waved away.**
  The account row is guarded on this axis by a narrower test —
  `DataMinimizationSchemaTests.Schema_PinsTheColumnsOfTheUserRow` pins `users` to exactly
  `created_at_utc`, `email` and `id` — and that pin, with the two beside it, reaches three of the
  thirteen tables the schema maps. On the other ten a `transactions.is_archived` is refused by neither
  the pins nor the vocabulary, deliberately: the only rule that would reach them refuses the word
  `archived` outright and buys them by refusing the live-row state. `backup` and `history` are
  permitted against collisions that exist today — `__EFMigrationsHistory` is a relation EF owns and
  cannot be renamed, and `backup_eligible` / `backup_state` are the WebAuthn authenticator-data flags.
- **`discarded` is refused, and *discard* being this product's word for an intentional hard delete is
  not an argument against that.** The vocabulary classifies catalog and model *names*, and a hard
  delete leaves no column behind — so the behaviour the word describes correctly can never appear as
  an identifier, and every `discarded_at` reaching the classifier is a soft delete wearing the
  product's own hard-delete word.
- **Counterexample**: a `deleted_at` on `users` so support can undo a mistake. It converts every
  erasure into a hide, and the person who asked to be forgotten stays in the table indefinitely with
  no way to tell.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: No identifier of an erased account is written to a log, a trace or a metric.
- **Why**: a log line naming the user id that was just erased is a deletion record that outlives the
  row, kept where no gate on this page can see it. Both other gates read *names* — a column in a
  catalog, a pattern in a route table — and a log line has no name to read. It is also the cheapest
  remnant in the product to create: adding a logger to a destructive handler and recording who was
  erased is the ordinary next step after such an endpoint ships, in a style used elsewhere in this
  codebase.
- **Enforced in**: `ErasureLoggingTests`, which asserts by reflection that the two types carrying an
  erasure out — `EraseAccountHandler` and `PasskeyReauthentication`, the two that hold the account id
  — take no `ILogger` or `ILoggerFactory` constructor dependency.
- **The test is narrow on purpose and its scope is stated rather than implied.** It reds on exactly
  the move it names and covers nothing else: not the endpoint mapping, which is static and takes a
  logger as a delegate parameter if at all, not the repositories those handlers call, and not the
  ASP.NET Core, EF Core and hosting stacks, all of which log on their own and none of which this
  constrains. A rule this shape cannot be made to cover an application; it can be made to cover the
  one move that would otherwise happen by habit.
- **Counterexample**: `logger.LogInformation("Erased account {UserId}", userId)` at the end of the
  handler, added so an operator can answer "did the erasure run?". It answers that question by
  keeping the identifier the erasure existed to remove.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: No path reverses an erasure that has taken effect, and the **route table** is what says
  so. Erasure takes effect at commit; nothing after that point restores, undeletes, reactivates or
  reinstates the account.
- **Why**: this rule is a corollary of the one above rather than an independent promise. A restore
  path needs something to restore *from*; once no remnant exists, a reversal has no source. What the
  route pins add is that nobody can build the front half of such a path and discover the back half
  missing later.
- **Enforced in**: `ErasureIrreversibilityTests`, with two pins of different shapes. One scans every
  route's pattern, display name and endpoint name for a vocabulary of reversal words, each spelling
  proved by a case of its own so the list cannot grow an entry nothing exercises. The other pins the
  **`/api/me/erasure` resource** exhaustively to `POST /api/me/erasure`, which is what closes the
  naming loophole a word list cannot see — a route called `/api/me/erasure/second-chance` trips the
  second pin and not the first. Each has its own control built from a hand-made endpoint list.
- **The second pin is scoped to the erasure resource, not to `/api/me`.** `/api/me` is the
  current-principal namespace: freezing it would refuse `GET /api/me`, `/api/me/sessions` and
  `/api/me/export` on erasure's behalf, and a rule that argues with unrelated features gets widened
  by whoever meets it. Comparison is by path segment and case-insensitive, matching how ASP.NET
  routing itself matches — a raw ordinal prefix would have pulled in `/api/members` and let
  `/API/Me/erasure` escape.
- **`cancel` is deliberately not a reversal word.** Cancelling something before it takes effect
  brings nothing back, because nothing left; every word on the list names retrieving something
  already gone. Were a delayed, cancellable erasure ever built, the cancellable window would belong
  to the *schedule* and not to the erasure, and the erasure-resource pin — not the word list — is the
  line that would have to move, one literal sitting directly under the comment that says which single
  route may join the set and why.
- **`recover` is deliberately not a reversal word either, for the opposite reason.** In a passkey
  product *account recovery* means regaining access to a live account, and a word that cannot
  separate that from resurrecting an erased one narrows to nothing: it would red the recovery path
  this product needs on every pass and teach the reader to ignore the check. The derived forms of
  every other word are on the list — `restoration`, `reinstatement`, `reactivation`, `reversal`,
  `undeleted` — because the matcher compares whole tokens and does not stem, so a word added in one
  form catches only that form.
- **The limits are stated rather than engineered around.** The route table is the capability boundary
  only because this codebase has no background jobs and no second entry point: a reversal driven from
  a hosted service, a queue consumer or a deploy-time tool would slip both pins. Both pins also boot
  the host in `Production`, and `Api/Program.cs` maps at least one route inside an `IsDevelopment()`
  branch — so a reversal registered there is not a surface nobody has built, it is a surface that
  already exists and neither pin reads.
- **Counterexample**: a "restore within 30 days" endpoint added because it seems kind. It cannot work
  without keeping the rows, so it silently reintroduces the remnant the rule above forbids.
- **Source**: `[SOURCE: user-story]`

---

- **Rule**: The backup window is erasure's one physical limit. Erased rows persist in point-in-time
  database backups for up to seven days and in no other location.
- **Why**: erasure is irreversible *as an offered capability* and time-bounded *as a physical fact*,
  and both sentences are true at once. A point-in-time restore rebuilds the whole database as an
  operator action against the whole service — it cannot be aimed at one account, and it is reachable
  from no route, handler, role or grant. So it is not a path the rule above forbids, and reading it
  as one leads to the wrong conclusion that the rule is a lie.
- **Enforced in**: nothing. `BackupRetentionDays = 7` is set on the Postgres resource in
  `AppHost/Program.cs` and is what `DEPLOYMENT.md` provisions, but no test reads it — editing that
  literal to `35` reds nothing in this repository, and neither does an operator changing retention on
  the server directly. This is the one rule on this page held by a value in a file rather than by a
  gate, and it is written down here so the gap is a known one rather than an assumption.
- **The product now tells a person about this window, and the number in the copy is held by
  nothing.** The account settings screen states the seven-day limit in words —
  `settings.component.spec.ts` pins the sentence, so the copy cannot drift on its own — which makes
  `BackupRetentionDays = 7` no longer merely a provisioning value: it is the number a user was told.
  Nothing ties the two together, and they live in different projects and different languages, so
  editing the literal to `35` reds nothing and leaves the screen quietly lying about a privacy
  guarantee. **Whoever changes retention changes the copy in the same commit**; until a gate holds
  that pairing, this sentence is the only thing that says so. A gate is possible — a test reading
  `AppHost/Program.cs` as text — and was deliberately not written here, because no test in this
  repository reads a source file, and inventing that pattern for one literal is a larger
  decision than this rule warrants. It is on the hardening backlog rather than in this commit.
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
  - **Two of the role's other `DELETE` grants look like they belong to erasure and do not.**
    `credentials` holds one for passkey revocation and `recovery_code_hashes` holds one for redeeming a
    code. Erasure uses neither: it empties both tables through the cascade from `users`, and would
    still work if both grants were revoked tomorrow. Reading either as erasure's is how a future change
    ends up "fixing" an erasure problem by widening a privilege that was never in this path.
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
- **`archived_at` is permitted by the schema scan and forbidden on `users` by a different test.**
  Two rules meet here and neither one alone is the whole answer, so somebody reading only the
  vocabulary sees a gap and widens the pattern — which takes a plausible product feature down with
  it. The remnant rule above states the division; read it before touching either side.
- **A client surface describes an erasure; none can start one.** The account settings screen carries
  an erasure section — what will be destroyed, that there is no undo, and the backup window above —
  but its control is **disabled**, because confirming an erasure needs a fresh WebAuthn assertion
  and the client cannot register a passkey yet. There is no dialog, no typed confirmation word and
  no client-side ceremony, so `POST /api/me/erasure` remains reachable only by a caller that builds
  the assertion itself. The wording of that copy is owned by [voice.md](../design/voice.md) rather
  than by this file; what this file owns is the gate, and the gate is why the control is off.
- **A failed erasure still spends the assertion, and still advances the signature counter.** Both are
  the gate's writes, both were committed before the transaction opened, and neither returns with the
  rollback — so the person has to run the ceremony again to try once more. That is correct rather
  than a defect; see the atomicity rule above for why the boundary is drawn where it is. It must
  **not** be answered by moving the gate inside the transaction.
- **`webauthn_challenges` is not in the verification query, and that is not an oversight.** A
  challenge belongs to a ceremony rather than to a person and carries neither `user_id` nor
  `budget_id`, so "no row references the erased user" holds vacuously.
- **A recovery code leaves nothing behind an erasure, and it left nothing behind its own redemption
  either.** `recovery_code_hashes` carries `user_id` and cascades from `credentials`, so erasure
  reaches it structurally and it is inside the verification query like everything else. What is worth
  reading here is that the table could never have held a remnant in the first place: consuming a code
  is **deleting its row**, so there is no `redeemed_at_utc` for `ErasureRemnantVocabulary` to refuse
  and no spent-code row for the count to find. The same argument this file makes against a deletion
  record is the argument that decided the shape of that table — see
  [ADR 0017](../decisions/0017-consume-a-recovery-code-by-deleting-its-row.md) and
  [recovery-codes.md](recovery-codes.md). The cost is symmetrical too: *"was this code used, or never
  issued?"* is as unanswerable as *"was this account erased?"*, and deliberately so.
