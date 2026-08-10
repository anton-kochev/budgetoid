# Business Logic Decision Log

Chronological record of non-obvious business decisions. Newest entries go at the top; existing
entries are never edited. If a decision is reversed, add a new entry referencing the original.

Infrastructure/architecture decisions (auth, hosting, DB) live in `docs/decisions/` (ADRs), not
here — this log is for **business/domain** decisions only.

---

## 2026-08-10 — `GET /api/me` returns the email address alone

**Context:** the account settings surface has to show the address the account is registered under,
and nothing in the product could tell it. The address is stored on `users`, but no endpoint returned
it: the only two places it surfaced were the export document — which drags a person's entire budget
along with it and refuses outright when the owned-budget set is not exactly the ambient one — and the
passkey registration options, which is a ceremony that mints and burns a challenge. The client cannot
read it out of the ID token either: `auth-service.spec.ts` pins that the client reads no claim, and
the stored address is deliberately never refreshed from the provider, so the token and the account
legitimately disagree.

**Decision:** a new authenticated read, `GET /api/me`, answering `{"email":"…"}` — one member, and
the response record carries no `Id` and no `CreatedAtUtc`. It reuses the existing
`IUserAccountReadService.FindEmailAsync` rather than adding a port method, and it carries no
`ProvisionsUser`, so it can refuse but never mint.

**Why the id is the expensive member, and the whole reason the shape is this narrow:** the client has
never seen an internal user id. Every tenancy value in this API is resolved server-side from the
authenticated subject and none is ever addressed by the caller — `BudgetRouteConstructionTests` holds
the route table to that, and `ExportDataQuery` and `EraseAccountCommand` each refuse a user-id member
for the same reason. Publishing one for a field nothing renders is the first half of a
client-supplied tenancy parameter: once a caller holds an id, the next request that accepts one has a
value to carry. Widening the response later is additive and costs a story; narrowing it is breaking.

**Alternatives considered:**

- *Return the whole `users` row* — the natural shape, and it already exists as `ExportedUser`, whose
  completeness is argued against the table's column inventory. A second shape of the same row on a
  display path is one that drifts from it, and it publishes the id for nothing.
- *Return `{id, email}`* — the id as a client-side cache key. Same cost as above for a benefit
  nothing has asked for; a cache key can be minted client-side.
- *Return a bare JSON string* — no room to add a second member without breaking every reader, on an
  endpoint whose whole design bet is that widening stays cheap.
- *Read the `email` claim from the ID token* — no backend work at all, and wrong: it shows what the
  provider asserts today rather than the address the account can be reached at, and it would need two
  shipped client pins relaxed to do it.

**The pin that guards it was watched fail.** `Me_ResponseCarriesTheEmailAndNothingElse` enumerates
the arriving members and joins them, so a widened record reports `"createdAtUtc, email"` rather than
that a count moved. It is green the day it was written — nothing else in either suite goes red when a
member is added — so it was proved against a deliberately widened record before being trusted.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md).

---

## 2026-08-08 — The export's row order promises `CreatedAtUtc` and deliberately stops short of the tiebreaker

**Context:** the export orders every array by `CreatedAtUtc` then `Id`, and that pair was written down
as the read port's contract — the stated reason being that an id-only order would put a
database-backed implementation and an in-memory one into silent disagreement. Two independent reviews
pointed out the same thing: the tiebreaker reintroduces the very disagreement the contract claims to
remove. `uuid` collation is provider-defined — PostgreSQL compares sixteen bytes big-endian,
`Guid.CompareTo` compares fields — so two rows sharing an instant order differently through the read
service and through the in-memory fake, and neither is wrong.

**Decision:** narrow the contract to what is true. `CreatedAtUtc` ascending is promised. `Id` breaks
ties deterministically within one implementation and is explicitly outside the contract; two rows
sharing an instant may order either way across implementations. No behaviour changed — `.ThenBy(Id)`
stays on all six queries and in the fake.

**Why not make the tiebreaker provider-independent instead** (`.ThenBy(x => x.Id.ToString())`, or
comparing big-endian bytes): it would buy a guarantee for a state the product cannot currently
produce — nothing writes two rows of one collection from a single clock reading — at the price of a
sort that no longer uses the index and a rule whose reason nobody could reconstruct later. The honest
narrowing costs nothing and stops the doc from promising what no test holds.

**The gap it exposes is real and stays open:** the ordering test seeds instants minutes apart, so the
tiebreaker is never exercised. `.ThenBy(Id)` could be deleted from all six queries today without a
single test going red. That is acceptable precisely because it is outside the contract now — but
anyone tempted to promote it back into one owes the suite a test that seeds two rows sharing an
instant.

**Alternatives considered:**

- *Leave the contract as written* — it would keep claiming a cross-implementation agreement that no
  code delivers, and the first person to write a second implementation would find it out the hard way.
- *Drop the tiebreaker entirely* — determinism within one implementation is worth keeping; without it
  two exports of unchanged data could diff on a tie.

**Affected areas:** [export.md](export.md).

---

## 2026-08-08 — The export refuses rather than truncates when it cannot reach every budget the user owns

**Context:** the export has to hand back every budget a user owns. Both isolation layers beneath it —
the `BudgetIsolation` query filter and the `budget_isolation` policy — are scoped to the *ambient*
budget, and neither takes an argument. Today the question never arises: only provisioning creates
budgets, so a user owns exactly one and it is always the ambient one. The two sets coincide by
accident of what the product does not yet do, and nothing anywhere said what should happen when they
stop coinciding.

**Decision:** read the owned budgets scoped by `user_id`, and **throw** unless that set is exactly
the ambient budget — set equality, both directions. The export answers everything or it answers
nothing. The failure is a plain `500` from the catch-all handler, with no exception handler of its
own, so the only way to soften the answer is to change the throw.

**The set-equality direction that looks redundant is not.** A count-only guard passes the case where
the ambient budget is one the user does not own, and that case files one budget's rows under
another's id — a wrong document rather than a short one.

**Alternatives considered:**

- *Silently export the ambient budget* — this is the truncation the requirement forbids, and it would
  ship as a green feature. Every test would pass, the file would look complete, and the data loss
  would begin on the day a second budget becomes creatable, in a code path nobody would revisit
  because it had been working for a year.
- *Loop the ambient budget over each owned budget* — there is no mechanism. `IBudgetContext` is
  resolved once per request by the provisioning middleware, and `SessionContextInterceptor` writes
  `app.current_budget_id` at **connection open**, so re-pointing the ambient budget part-way through a
  request would need a fresh connection per budget. That is precisely the thing
  [ADR 0008](../decisions/0008-read-the-ambient-budget-inside-the-policy.md) and the
  connection-opened interceptor rule exist to prevent, and it would also make a single consistent
  read impossible.
- *`IgnoreQueryFilters`* — a compile error by `BannedSymbols.txt`, and the RLS policy underneath would
  return nothing anyway. Worth naming only because it is the first thing that comes to mind.
- *A dedicated `IExceptionHandler` mapping the refusal to a friendlier status* — rejected. `404` says
  the export does not exist, `400` blames a request with no field to correct, and `409` implies a
  resolution the client cannot perform. A named mapping is also the seam through which someone later
  turns the refusal into "return what we have, with a warning".

**Affected areas:** [export.md](export.md), [budgets.md](budgets.md).

---

## 2026-08-08 — The export document carries its own records rather than the ones the list endpoints return

**Context:** every entity the export names already has a DTO and a read service behind the list
endpoints. Reusing them is the obvious move, and the reviewer who finds two nearly-identical record
sets will reach for it.

**Decision:** the export gets its own records and its own read path. The display shapes are lossy for
an archive and are free to change with the screens that consume them.

**The losses are concrete, not theoretical.** `TransactionDto` coerces a null description to the
empty string, so "the person wrote nothing" becomes "the person wrote an empty string" and cannot be
told apart afterwards. `PayeeDto` carries neither `CreatedAtUtc` nor `BudgetId`. `AccountDto` adds
currency name and symbol, which no column holds. The read services also order for display — payees by
name — and a rename would then reshuffle the whole file, so two exports of unchanged data would diff.

**Alternatives considered:**

- *Reuse the DTOs and widen them* — every added field is one the list screens serialize on every
  request for no reader, and the coercion in `TransactionDto` cannot be removed without changing what
  those screens receive.
- *Project straight from the entities with no records at all* — the wire shape would then be whatever
  the domain classes happen to expose, and the schema version would stop meaning anything: a private
  setter added to an entity would silently change a documented file format.

**Affected areas:** [export.md](export.md), [transactions.md](transactions.md),
[payees.md](payees.md), [accounts.md](accounts.md).

---

## 2026-08-08 — The refusal to leave a remnant is its own vocabulary, and irreversibility is pinned on the route table

**Context:** "erasure leaves no remnant and offers no way back" was, until now, a rule the docs cited
as settled without ever stating it — two separate files referred to "the rule against tombstones" as
though it were written down somewhere, and it was not. Nothing stopped a `deleted_at` column or a
`POST /api/me/erasure/restore` route from being added, and both would have been read as ordinary
engineering by anyone who had not been in the room. There was already a schema-wide classifier
refusing column names — the one that keeps analytics and device identifiers out — and the obvious
move was to add a fifth category to it.

**Decision:** a **separate** vocabulary, sharing only the matcher, plus two route pins of different
shapes. The existing classifier's categories are documented as naming *what the schema is keeping
about a person*, and a `deleted_at` says nothing about a person — it says the row is still there. The
remedy differs too, and so does the file that owns the rule. What the two genuinely share is the
matching mechanism, which is now written once: two lists are two rules, but two tokenizers would be
one rule spelled twice, and the copy that quietly missed a fix would start disagreeing with the
original about names nobody was watching.

**The route pins are a corollary, not a second promise.** A restore path needs something to restore
from, so the schema rule is what makes irreversibility true; the pins only stop somebody building the
front half of a path whose back half cannot exist.

**Alternatives considered:**

- *A fifth category on the existing classifier* — fewer files, and it makes that classifier's own
  summary false. A tombstone red would also be reported under a heading named for data minimization,
  which tells the reader the wrong thing at the moment they most need the right one.
- *Copying the tokenizer into the new vocabulary* — rejected for the reason above: the drift is
  silent and shows up as one scanner catching a name the other misses.
- *Refusing the word `cancel` on the route table* — rejected. Cancelling something before it takes
  effect brings nothing back, so a pin refusing the word claims more than the rule does; and a pin
  that has to be fought in order to build a capability the requirements describe is a pin that gets
  deleted instead of extended.
- *Refusing `archived` alongside `archive`* — rejected. Hiding a closed account is a plausible
  live-row product state, and a rule that cannot tell it from a copy-aside would refuse the feature.
  What that costs is accepted rather than waved away: the pinned column sets reach three of the
  thirteen mapped tables, so a `transactions.is_archived` is refused by neither the pins nor the
  vocabulary. The account row is closed; the other ten are open on this axis by choice.
- *Documentation alone, with no test* — the requirement's own stated verification method is
  inspection, and inspection is a person remembering. Prose describing an absence rots quietly,
  because nothing goes red when it stops being true.
- *A second behavioural test proving no remnant row is written during an erasure* — already covered:
  the atomicity suite enumerates every relation that stores rows and asserts each is empty
  afterwards. A list-based duplicate would be strictly weaker than the discovery-based one that
  exists.
- *Leaving the vocabulary in the production assembly* — rejected. It is a test-only deny-list with
  two readers, both of them test projects, and `TestSupport` is where shared test-only code with more
  than one reader already lives. On the production side it was public API on the assembly the API
  container ships, sitting beside four files that genuinely run at deploy time. The token matcher
  stays behind because its own reader is production-side.
- *Refusing `discarded` was itself rejected at first, and that was wrong.* The argument was that
  *discard* is this product's word for an intentional hard delete, so a rule on it would red the
  correct behaviour and the wrong one alike. It does not hold: the classifier reads catalog and model
  *names*, and a hard delete leaves no column behind, so the behaviour the word describes correctly
  can never reach the classifier at all. Every `discarded_at` that does reach it is a soft delete
  wearing the product's own hard-delete word — the spelling a reviewer waves through.
- *Hand-written plural twins for each pattern* — rejected in favour of expanding the plural when the
  rules are compiled. Fourteen patterns become twenty-eight by hand and the fifteenth gets forgotten,
  which is exactly how `tombstones`, `archives` and `users_archives` passed a list that refused their
  singulars.
- *Refusing `recover` on the route table* — rejected on the same ground as `cancel`, from the
  opposite direction. In a passkey product *account recovery* means regaining access to a live
  account; a pin that cannot separate that from resurrecting an erased one reds a path this product
  needs, on every pass, and teaches the reader to ignore it.
- *Freezing the whole `/api/me` surface* — rejected once it was clear `/api/me` is the
  current-principal namespace rather than erasure's. The pin is scoped to the `/api/me/erasure`
  resource instead: a rule that argues with `GET /api/me` or an export route is a rule that gets
  widened by whoever meets it next, and a widened pin catches nothing.
- *Promising "no trace that it happened"* — withdrawn as written. What is held is a name scan and a
  row count, which between them reach the database and nothing else. Logs, traces and metrics are
  named as their own rule with their own narrow test, and the two nouns no name can catch — an
  anonymized remnant and a message payload — are credited to the row count rather than to the
  vocabulary. A promise wider than its gates is the failure mode this whole entry exists to avoid.

**Affected areas:** `erasure.md`.

---

## 2026-08-07 — "Every row unchanged" means the erasure's rows, not the request's

**Context:** a failed erasure must leave every row unchanged. Read as covering the whole request that
is false here, and not by accident: the re-authentication gate runs to completion *before* the
transaction opens, and it writes twice. `ConsumeAsync` deletes the spent nonce from
`webauthn_challenges`; `SaveCounterAsync` advances `passkey_signature_counters.signature_counter`.
Neither comes back with the rollback. Either the rule is scoped, or the gate moves inside the
transaction — there is no third position, and the two cannot both stand.

**Decision:** the rule is scoped to the erasure, and the scope is not chosen here — it is already in
the wording of the requirements themselves. Atomicity is stated as deleting every row **an erasure
covers** or none, and the guarantee is triggered by a failure in *part of an erasure*, where the gate
is the authorization deciding whether an erasure begins rather than a part of one. Accepting that a
failed erasure costs the person their assertion: they must repeat the ceremony before trying again.

**That the anchor is the requirement's own phrasing is load-bearing and not decoration.** Read as a
scope this codebase picked afterwards, the whole entry is the implementation excusing itself, and the
next reader would be right to distrust it.

**Alternatives considered:**

- *Move the gate inside the transaction* — the literal reading, and rejected twice over. A rolled-back
  erasure would restore the spent nonce, making the same assertion replayable; and the delegate is
  replayed by the retrying execution strategy, so a valid erasure would be refused with an attacker's
  error because the database blinked.
- *Gate inside, but consume the nonce on a second connection* — buys nothing, since the delete commits
  either way, and drags the counter advance inside, where a rollback destroys the clone-detection
  evidence. Strictly worse than doing nothing.
- *Only the counter advance inside* — the same defect. An accepted assertion has to mean the same
  thing on every path, and rewinding lets a cloned authenticator re-assert at a value already used.
- *Mark the nonce spent instead of deleting it* — a changed row is still a changed row, so it does not
  make the literal reading true, and it turns a self-cleaning table into a store of spent nonces.
- *Reissue a fresh nonce when the erasure fails* — creates a row rather than restoring one, and
  auto-minting proof of presence for a failed destructive request is a worse rule than asking the
  person to repeat a ceremony they can repeat.

**Affected areas:** `erasure.md`.

---

## 2026-08-07 — An account and its first budget stopped being two saves

**Context:** the user row and its first credential were written in one `SaveChanges` — an orphan user
holding a unique email no sign-in resolves to is unhealable — while the default budget went in a
second. That second save made "a user row with no budget" a reachable state, which is why a
find-or-create heal ran on **every** authenticated request. The heal cost a `SELECT` per request, and
it was a rule the next reader could delete as apparent duplication once it lived in two handlers: doing
so would leave a person whose budget insert was lost unable even to erase their account, with every
test still green.

**Decision:** all three rows are written in one save, through
`IUserRepository.TryAddAsync(User, Credential, Budget, …)`, and the heal is deleted. A resolved account
with no budget now throws. Chosen to remove the state rather than tolerate it — the atomicity argument
already made for the credential applies to the budget verbatim — accepting that the state is
unreachable *from the only path that creates a user* rather than unreachable outright.

**That distinction is the accepted cost and must not be read as an oversight.** Nothing in the schema
forbids the state, so a direct `DELETE FROM budgets` still produces it, and the account is then dead
rather than healed. The stronger guarantee is a participation constraint — a circular
`users.default_budget_id → budgets(id)`, `NOT NULL DEFERRABLE INITIALLY DEFERRED`, checked at commit —
which PostgreSQL can express declaratively and which would make the state unstorable. It was
**deliberately deferred as a separate decision**, because it puts a new column on a table whose column
set is deliberately pinned, and the pin's own rule is *move the column, never widen the pin*. The
present trade is bounded by production holding no data.

**Alternatives considered:**

- *`ITransactionalExecutor` over the three writes* — the named mechanism for "several writes in one
  handler must be atomic". Rejected, and at three rows the reason became correctness rather than cost:
  `BeginTransactionAsync` opens the connection, and that is when `SessionContextInterceptor` writes
  `app.current_user_id`. Inside a transaction the interceptor runs once, at the begin, so a wrap whose
  delegate contains the identity publication configures the connection while the setting is empty and
  the `users` INSERT fails `22P02` against its own `WITH CHECK`. Fixable by publishing outside the
  wrap, but the fix is a new ordering rule someone must not re-break.
- *A dedicated `IAccountProvisioning` port* — a third port for one call site, and it would carry the
  `23505` attribution away from the repository where every other constraint catch in this codebase
  lives. What it buys is a better name, and a name is cheaper to get from a method.
- *Modelling `Credential` inside the `User` aggregate* — already rejected before this change, for
  reasons unchanged: the root would then have to grow to hold sessions and passkeys too.
- *Keeping the heal as belt-and-braces* — rejected. With one save it repairs a state nothing produces,
  and an unconditional repair whose reason has evaporated is exactly the code a future reader deletes
  without understanding what it was for.

**A property the split save did not have, gained here.** When the credential insert loses a race, the
loser now wrote nothing and must adopt the winner's budget — and that budget is *guaranteed* to exist,
because a reported unique violation means the winner's transaction committed and that transaction
contained its budget row. Under the split save the winner could commit its user and lose its budget.
The loser must publish the winner's identity **before** reading its budget: `budgets` is policed by
`user_isolation`, so a read under the loser's phantom id matches nothing.

**Affected areas:** [budgets.md](budgets.md), [users-and-ownership.md](users-and-ownership.md),
[erasure.md](erasure.md).

---

## 2026-08-07 — Provisioning mints an account only where a route declares it may

**Context:** the entry below accepted that a second erasure request is refused, and weighed only the
status code the caller sees. It never asked what the *row* costs. `UserProvisioningMiddleware` minted
a full account — `users` with the email, `credentials` with the Google subject, a default `budgets` —
on any authenticated request whose credential did not resolve, and a Google ID token stays valid for
up to an hour after the account it names is erased. One in-flight poll or second tab therefore
resurrected the account moments after the person asked to be forgotten. Worse, the re-authentication
gate made that resurrection **unerasable**: the fresh account holds no passkey, so erasure refuses it
forever. The product's flagship claim is that leaving means leaving.

**Decision:** provisioning becomes create-on-demand, opt-in at the route group. A `ProvisionsUser`
marker on six data route groups is the only thing that permits minting; every other authenticated
route resolves an existing account or answers 401 and writes nothing. Chosen so the erasure path and
the identity-bearing routes beside it cannot write a row on the way past, accepting that the six
marked routes still resurrect an account while the token lives.

**Opt-in rather than opt-out, and the polarity is the fix.** Marking the routes that must *not* mint
leaves the mint set as "everything else", so a stale token resurrects the account through
`GET /api/transactions` — the original scenario, unfixed — and a new endpoint whose author forgets the
marker mints silently. Under opt-in a forgotten marker gives brand-new users a 401 on that group:
loud, caught by any integration test, and it writes nothing.

**Alternatives considered:**

- *Opt-out on the three routes that must not mint.* Rejected for the reason above. It is also thrown
  away rather than simplified the day registration becomes explicit.
- *A single registration route as the only minting endpoint, now.* The correct end state, and it is
  what a consented registration step will be. Rejected for today: it needs a frontend that has no
  passkey or erasure flow at all, and doing it half — a route the client calls on boot — buys nothing
  over the marker while pretending to.
- *A tombstone keyed on the Google subject, so a returning erased identity is recognised.* Rejected
  outright: retained personal data about a person who left is the regression restated.
- *Letting the erasure route run identity-less and answer 204 for "no account".* Tempting, because it
  restores idempotency and matches the endpoint's own post-condition philosophy. Rejected: it creates
  a path through the erasure handler that reports success having verified nothing.
- *Matching paths in the middleware instead of reading endpoint metadata.* Rejected: a route string in
  middleware is the second definition of the routing surface that
  [passkeys.md](passkeys.md) already argues against for the anonymous legs.

**What this does not fix, recorded so nobody reads it as settled:** a client calling a marked route on
app boot still resurrects an erased account within the token's remaining life. That is older than this
decision and closes when account creation becomes a consented act and the six markers collapse to one.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md), [erasure.md](erasure.md).

---

## 2026-08-07 — Erasure is gated by an assertion carried in the request, not by a stored re-authentication instant

**Context:** erasure had to stop being reachable on a bearer token alone. The requirement is stated in
two halves — a fresh WebAuthn assertion for a credential registered to the account, and a rejection
once more than five minutes have elapsed since that re-authentication — and the second half reads like
a two-step flow in which the server records when someone re-authenticated and checks that record
later. There is nowhere to record it: no session token is issued or presented, so the API has no
per-session state to hang it on, and `webauthn_challenges` cannot gain an owner column because its
pinned column set is exactly what holds its row-level-security exemption to its reason.

**Decision:** the assertion travels in the erasure request itself, and the five-minute window is
realised as the lifetime of the server-issued challenge it was built on — already five minutes,
already server-held, already enforced inside `ConsumeAsync`. Chosen to keep the elapsed check
server-side with no client-supplied instant to distrust and no new mutable per-user table, accepting
that the window is measured from challenge issue rather than from the authenticator touch. That
accepted tradeoff runs in the safe direction: the enforced gap is **shorter** than five minutes, so
the rule is stricter than the requirement rather than looser.

**A future reader will read the missing table as an oversight and try to add one. It is not.**

**Alternatives considered:**

- *A `reauthentications` table holding the instant.* Rejected. It is mutable per-user state on an
  account whose whole purpose is to be destroyable wholesale, it is the shape `app-role-grants.sql`
  argues against accumulating, and it would exist before sessions authenticate requests at all — so
  its owner column would have nothing to key on but the same bearer identity the gate exists to
  distrust.
- *A column on `webauthn_challenges` binding the nonce to a user.* Rejected, and it fails closed by
  design: `RowLevelSecurityCoverage.Exemptions` pins that table's five columns and the coverage test
  goes red on any addition. That pin is what pushed the account binding into the handler, which is
  where it belongs anyway.
- *A short-lived server-signed token minted by a separate re-authentication step.* Rejected: the
  product already refuses to hand the client a handle to a session for exactly this reason, and a
  second bearer artifact would be the thing somebody later decides is close enough to a session token.
- *Reusing the ordinary sign-in nonce pool.* Rejected — that pool is minted from an **anonymous**
  endpoint, so a phished ordinary sign-in assertion would destroy an account. A third ceremony value
  was added instead, and the registration pool is refused too because it is minted for an
  already-signed-in person, which is the stolen-session adversary itself.

**The account comes from the request here, and from the credential at sign-in.** Sign-in has no
identity yet, so the verified credential establishes one; erasure already has one, and publishing the
credential's account over it would be destructive rather than redundant, because
`SessionContextInterceptor` fixes the user and the budget together at connection open and a
re-published user id does not move the budget. Alice's bearer token with Bob's passkey would empty
Alice's budget while deleting Bob's user row. The binding is therefore an owner-scoped lookup rather
than a comparison, so another account's handle is indistinguishable from one nothing answers to.

**Erasure stopped being idempotent to the caller, and that was accepted rather than worked around.** A
second request authenticates as the brand-new account user provisioning just minted, which holds no
passkey, so it is refused. Answering `204` without a valid assertion would defeat the gate, and
storing a marker that an erasure happened would contradict the rule against tombstones. The cost — a
client retrying a lost response sees a failure over data that is already gone — is a client-side
concern.

**Affected areas:** [erasure.md](erasure.md), [passkeys.md](passkeys.md),
[data isolation](../engineering/data-isolation.md).

---

## 2026-08-06 — Erasure empties one table itself and leaves the rest of the graph to the cascade

**Context:** the entry below establishes that one `DELETE` on `users` empties the account's
structural graph, because PostgreSQL performs a referential action with the referencing table
owner's privileges. That is true of every edge that is a cascade. Five are not:
`transactions → budgets`, `→ accounts`, `→ categories`, `→ payees`, and
`categories → category_groups`.

**Decision:** erasure deletes explicitly **only what a `RESTRICT` edge would otherwise block**, in
dependency order, and leaves everything joined by `CASCADE` alone to the cascade — to get a rule a
future table can be measured against rather than a list of statements, accepting one extra
round-trip on an action that runs once per account.

Today exactly one table satisfies that rule: `transactions`, the child of four of the five edges.
Emptying it also disarms the fifth, because a `RESTRICT` edge cannot bite once its child rows are
gone. So the sequence is `transactions`, then the user row — two saves in one transaction, ordered
by the handler rather than by EF.

**`categories → category_groups` is deliberately left to the cascade**, and it was worth settling
rather than assuming. Both tables cascade from `budgets`, so one `budgets` delete reaches two tables
joined to each other by a `RESTRICT` edge, which looks like it should depend on which referential
trigger fires first. It does not: PostgreSQL queues the check for that edge as an after-row trigger
when the `category_groups` row is deleted, strictly after the cascade into `categories` was already
queued, and the after-trigger queue is FIFO. Verified on PostgreSQL 17 against schemas built with
the two constraints created in either order, so their OIDs — and with them the RI trigger names that
decide firing order — were reversed. Both leave the tables empty.

**Alternatives considered:**

- *Delete `categories` explicitly as well.* This is what shipped first, on the belief that the
  cascade rested on constraint-creation order. Removed once that was disproved: it bought nothing,
  and its unit test asserted a refusal PostgreSQL does not make, so it could not fail.
- *Trust the cascade for the `transactions` edges too.* Rejected outright: a budgeting product's
  accounts hold transactions, so `23503` would be the ordinary case rather than the corner.
- *Migrate `budgets → transactions` to `ON DELETE CASCADE`.* Rejected twice over. It dissolves the
  guard that edge exists for — an ordinary budget delete would then silently take recorded money
  movement with it — and it does not even buy the one statement it appears to: `transactions →
  accounts` and `→ payees` stay `RESTRICT`, so a `budgets` delete would still be cascading into
  tables joined to each other by a `RESTRICT` edge, in the opposite direction to the one settled
  above.
- *One `SaveChanges` for the whole batch.* Rejected: EF orders a batch topologically by the foreign
  keys between the entity types **in** it, and `Budget` — the type carrying the edge — is never in
  the tracker. No edge means no guarantee, and a draw that puts the user delete first is refused.
- *Relax `BannedSymbols.txt` to allow `ExecuteDelete` here.* Rejected. The ban's value is that it has
  no exceptions, and erasure is the highest-consequence write in the product — a set-based delete
  with a wrong predicate is the last statement that should be unreadable by the type system.

**Read the absent `budgets` DELETE grant as a decision too.** Removing the tracked `Budget` from the
context is what stops EF composing its own `DELETE FROM budgets`; without it the request dies with
`42501`, an error that names a permission while the cause is the change tracker. Answering that with
a grant would widen the role's reach and fail the grant-matrix pin.

**Affected areas:** erasure (new), users-and-ownership, transactions, categories, budgets.

---

## 2026-08-06 — Erasure needs one delete grant, not one per table

**Context:** erasing an account has to run as the least-privilege application role rather than on an
elevated connection, so the role needs to be able to delete every user- and budget-owned row. The
obvious reading is a `DELETE` grant per owned table — seven of them, on `users`, `budgets`, `payees`,
`credentials`, `sessions`, `passkey_public_keys` and `passkey_signature_counters`.

**Decision:** grant `DELETE` on **`users` alone**. Every owned table hangs off that row by
`ON DELETE CASCADE`, and PostgreSQL performs a referential action through internal triggers running
with the privileges of the **referencing table's owner** rather than of the role that issued the
statement — so one grant empties the structural graph and a grant on any child buys nothing erasure
can use. This is not a belief the code rests on:
`AppRoleGrantsTests.Database_AllowsDeletingAUserAndCascadesTheAccountAway` deletes as the application
role and asserts every child table is empty afterwards.

**It is not sufficient on its own.** `budgets → transactions` is `Restrict`, so a budget holding one
recorded movement refuses the delete — the ordinary case for a budgeting product, not the corner.
Erasure deletes transactions first, per budget, and only then the user row; those are two shapes of
session, because `transactions` is policed on the budget and this delete on the user.

**Alternatives considered:** *all seven* — rejected, and not merely as surplus. `credentials` and
`passkey_public_keys` are exempt from row-level security because they are read before a request has
an identity a policy could key on, so a `DELETE` there would be **unpoliced**: one statement carrying
the wrong id removes somebody else's only way in, with nothing to catch it. On
`passkey_signature_counters` a `DELETE` reopens counter rewind — deleting the row and re-inserting it
at zero is exactly what the single-column `GRANT UPDATE (signature_counter)` exists to forbid, and a
clone giving itself away against that counter is the reason the column is there. *The four policed
tables only* (`users`, `budgets`, `payees`, `sessions`) — rejected: the three extra grants still do
nothing the cascade does not already do, and each would invert a written argument for no gain.

**Read the six absences as a decision.** They look like an oversight, and the obvious "fix" for a
future reader is to close them; the grant paragraph in `app-role-grants.sql` and the rule in
[users-and-ownership.md](users-and-ownership.md) both say why they must not be.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md), [passkeys.md](passkeys.md),
[sessions.md](sessions.md).

---

## 2026-08-06 — Registration is refused on an unverifiable claim, and the docs say so

**Context:** an authenticator that cannot derive a PRF secret cannot hold the account's keys, and the
person holding it finds out by losing their records permanently. The only signal the product has is
`prf.enabled` in the client extension results — asserted by the client, covered by no signature, and
impossible for the server to reproduce or check. Two earlier documents deferred the question rather
than answer it: what may a claim like that be allowed to gate?

**Decision:** gate registration on it, and record in the same breath that this is a **product gate,
not a security control**, so no later reader mistakes it for a guarantee. The justification is
narrower than "defence in depth": there is no adversary here. The claim is the account holder's own
browser describing the account holder's own authenticator, and whoever forges it registers a passkey
whose keys they will not be able to derive — harming only themselves. That places it exactly where
ADR 0002 puts an upper-layer restatement: it exists for error quality, never for enforcement.
Enforcement of key custody arrives with the keys themselves, where a wrapped key under a nonexistent
PRF output simply cannot be produced.

Two consequences worth keeping: the check runs **last**, after signature verification, so a
malformed or replayed response is never told the lie that its authenticator is at fault; and the 201
response stops echoing the flag, because a value the *server* returns reads as a value the server
established.

**Alternatives considered:** *refuse nothing until key custody exists* — correct on the letter, and
it leaves every passkey registered in the meantime to fail silently at the moment it is needed most.
*Put the check with the ceremony verification code* — rejected: everything there judges signed
material, and a verification failure meaning "your client said no" would blur that boundary for
every later reader. *Keep reporting the flag in the response body* — rejected: once `true` is the
only reachable value it carries no information, and echoing it invites the exact misreading this
entry exists to prevent.

**Affected areas:** [passkeys.md](passkeys.md), [_overview.md](_overview.md) (glossary),
[ADR 0013](../decisions/0013-verify-webauthn-ceremonies-without-a-fido-library.md).

---

## 2026-08-05 — A passkey's material is split by whether it is read before or after identity

**Context:** signing in with a passkey means finding a public key and checking a signature *before*
the server knows whose account this is. Until that moment every statement runs with
`app.current_user_id` empty, so a policed table would refuse the very query that establishes the
identity. The question was which of a passkey's facts have to live on a table exempt from row-level
security, and which must not.

**Decision:** the line is drawn at **before proof / after proof**, and it is drawn as a *table
boundary* rather than a convention. `passkey_public_keys` — credential id, public key, algorithm — is
exempt, because all of it is read by the discovery lookup. `passkey_signature_counters` carries
`user_id` and is policed, because the specification compares a counter only after the signature
verifies. `credentials` grows no column at all, which also leaves its frozen constraint snapshot and
its pinned exemption untouched.

What holds the exempt table to its reason is its **pinned column set**, not the absent `UPDATE` and
`DELETE` grants. Those stop mutable per-user state accumulating, which is real but is not the
threat: a wrapped key or a recovery-code hash is written once and never updated, so it satisfies any
append-only rule perfectly while being exactly what must not sit on a table every session reads in
full. A red on the pin means move the column, never widen the pin.

Four candidate columns were **refused outright** rather than relocated — AAGUID, transports, a
last-used instant, and backup-eligibility flags. Nothing in this design reads any of them, and an
authenticator model identifier is a device fingerprint by another name. Each arrives with the feature
that reads it.

**Consequence to carry:** the assertion path must publish the identity only after the signature
verifies, and must not open a transaction before that publication — a transaction opened earlier
configures the connection while the identity is still empty, and every policed statement inside it
fails with `22P02`. The whole ceremony runs over the real least-privilege connection in an
integration test for exactly this reason.

Recorded as [ADR 0012](../decisions/0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md);
the verification decisions that go with it are [ADR 0013](../decisions/0013-verify-webauthn-ceremonies-without-a-fido-library.md).

---

## 2026-08-05 — A session is revoked by writing an instant, not by deleting the row

**Context:** the product now records a sign-in server-side so it can end one without asking an
identity provider. That needs a decision on what "ended" is: a `revoked_at_utc` an application role
may write, or a row it may delete.

**Decision:** revocation is an **`UPDATE` of `revoked_at_utc`**, and the application role holds
**no `DELETE` grant on `sessions` at all**. The row is what says access ended and when; deleting it
needs the one privilege that could erase every session on the system, and it cannot tell "already
revoked" from "never existed" — a distinction anything reporting a revocation needs. `Session.Revoke`
is idempotent, so a retried sweep keeps the instant access actually ended rather than restamping it,
and the `ExecuteUpdate` ban is what keeps the transition running in the domain per row where that
idempotence lives.

Alongside it: the foreign key from a session to its credential is **`CASCADE`, not `RESTRICT`**, so
a session can never hold up the deletion of a credential and through it an account erasure — a row
of access bookkeeping must not outrank a person's request to be forgotten. The cost is named rather
than hidden: because the cascade exists, deleting a credential row satisfies "revoking a credential
ends its sessions" invisibly, so any credential-removal path must revoke explicitly and then delete
or the fact is unobservable.

**Alternatives considered:** *Revocation by `DELETE`* — rejected above. The usual argument for it,
that updated rows accumulate, does not separate the two options: an unrevoked but expired row
accumulates identically, so retention is a problem either mechanism has and neither solves. No sweep
exists yet, and the grant one would need is the grant this decision withholds. *A `bool` on the
session saying whether it may read budget content* — rejected in favour of a derived `kind`: a
boolean named after a permission reads as a permission a caller sets, and the kind is derived from
the establishing credential's type with no way to supply one.

**Affected areas:** [sessions.md](sessions.md), [users-and-ownership.md](users-and-ownership.md).

---

## 2026-08-05 — The deny-list refuses a bare word only where no honest column can carry it

**Context:** the vocabulary landed with `event` as a bare token, and the names most likely to arrive
next are spelled with the same words a tracking column is. A transactional outbox carries
`event_type`; revocable sessions carry `session_id` and `last_used_at`; a passkey record carries
`credential_id` and `device_name`. Each of those is a security or correctness record, and a rule
that cannot tell one from a measurement column refuses the feature along with the surveillance.

**Decision:** the vocabulary refuses a bare token only where no legitimate column in this domain can
carry it — `analytics`, `advertising`, `fingerprint`, `telemetry`, `tracking`, `utm`, `impression`,
a vendor's name — and a phrase everywhere else. Four omissions are therefore deliberate and each
looks like an oversight: **`event`** protects the outbox, **`session`** protects a revocable
session, **`login`** protects a session's own start time, and **`cookie`** protects a first-party
opaque value. The general rule: when a word cannot separate the surveillance case from the security
case, the pattern narrows, because a red bar on a legitimate column is spent credibility and a
missed column is still caught by review. The compensating move goes the other way — the scan now
classifies **relation names** as well as column names, so scope rises a level even as individual
patterns get narrower. A behavioural feature is modelled as a table far more often than as a column.

**Alternatives considered:** *Refuse the bare tokens and carve the exceptions out in each rule's
`Reason`* — rejected: prose is read after a red bar and can only help argue one away, never stop it,
and the vocabulary's own remarks say the check is worth having only while its reds are believed.
*Keep a per-table exemption list the way the row-level security coverage check does* — rejected: an
exemption list is a thing people append to, and appending is the drift this rule exists to stop; a
narrower pattern has no append surface. *Defer until the sessions story lands* — rejected: the
collision would then be discovered as a red bar on somebody else's work, which is the worst moment
to be arguing about a deny-list. *Keep `analytics_event` as a `BehaviouralEvent` rule* — rejected:
it can never fire, because `analytics` matches first whatever the order, and a rule that cannot
fire makes the list's claim to disjoint patterns false.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md).

---

## 2026-08-05 — The list of columns the product refuses to carry is production code, not a test constant

**Context:** "the account row holds nothing but an id, an address and a timestamp" is worth little if
the same data arrives one table over. Refusing analytics identifiers, advertising identifiers, device
fingerprints and behavioural events needs a list of what those look like, and the cheapest place to
put a list read by exactly one assertion is inside that assertion. It would not stay read by one
assertion: the data inventory that classifies every column will have to read the same list from a
build gate, and a build gate cannot reference a test assembly.

**Decision:** `ProhibitedColumnVocabulary` lives in the production assembly beside
`RowLevelSecurityCoverage`, which is the same shape of object for the same reason — a classifier
placed where more than one consumer can reach it so the rule has one spelling. It exposes the rules
with a stated reason each, and a `Classify` that names the category a column name falls into.
Matching is over `_`-separated tokens rather than substrings, because `event_name` must be caught
while `name` must not. It is checked over **every** relation in the `public` schema rather than over
`users`, so a new table is covered without anyone remembering. Two controls make the pair mean
something: a probe table proves the scan can fail, and a false-positive test over the mapped columns
proves the patterns are not wide enough to swallow a real one.

**Alternatives considered:** *A constant in the schema test* — rejected: it is a source the inventory
story is guaranteed to duplicate or move, and a follow-up commit that relocates a rule's definition is
the kind of commit that quietly changes it. *Wire it into the deploy-time verifier beside the
row-level security check* — rejected: that check runs at deploy time because a policy fails **open**
and can drift from outside the repository. A column name cannot; it arrives through a migration CI
already reads, so the check buys nothing there and adds a failure mode to the deploy path.
*Enforce it in the database* — rejected: PostgreSQL cannot refuse a column for what its name
connotes, and reaching it would need an event trigger, which ADR 0002 rules out. The build is the
lowest capable layer, and a rule sitting above its apparent floor has to say why.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md).

---

## 2026-08-05 — The provider must vouch for the address, and a takeover path closes with it

**Context:** the API read `sub` and `email` off the principal and trusted both. An `email` claim the
provider has not vouched for is an address anybody could have typed into a profile field, and the
account row is keyed on exactly that address — it is unique, it is what a support request, an export
and every future notification go on. "One email, one user" below is explicit that this was a known
hole: it rejected rebinding a stale row's subject on collision partly *because* `email_verified` was
not read, so an unverified address in a token would have been enough to reach an existing account.
That premise no longer holds. Two other statements in that entry are also no longer true of the
schema: the `google_subject` column it bounds moved to `credentials.subject`, and the `display_name`
column it bounds was dropped outright. Neither change touches the decision that entry records — the
uniqueness and case-insensitivity of `users.email` — which stands.

**Decision:** require `email_verified` on every authenticated principal, checked in
`UserProvisioningMiddleware` immediately after the missing-claim check and **before** provisioning, so
a refused principal writes no row. Accept only a value `bool.TryParse` reads as `true`, which is
case-insensitive and therefore takes `"True"`; reject absent, blank, `"false"` and anything
unparseable, `"1"` included. No provider this codebase talks to emits a truthy-string dialect, and
accepting one is how a gate quietly stops being a gate. Rejection is a 401 ProblemDetails of the same
shape as the missing-claim path but with its own title, because the caller holds the token and can
read the claim themselves — naming the reason leaks nothing and saves a debugging session. The claim
is read and **not stored**: it decides whether the address may be registered, and answers nothing
about the person worth keeping afterwards. No scope changes; Google returns `email_verified` under
the `email` scope the client already asks for.

**Alternatives considered:** *Enforce it inside JWT validation* — rejected: the integration host
replaces the whole authentication stack, so the rule would ship with no test exercising it, and it
conflates "is this token authentic" with "may this address be registered", which is a provisioning
precondition of the same kind as "the principal carries a `sub`". *Carry the flag on
`EnsureUserCommand`* — rejected: an authentication concern in the Application layer, and a command
field nothing stores. *Enforce it in the database* — rejected: the database cannot inspect a token,
and reaching it would need procedural logic, which ADR 0002 rules out; the API boundary is the lowest
layer capable of the rule. *Revisit rebinding a stale row's subject now that the claim is checked* —
rejected: that rejection rests on a second, independent ground — `sub` must not be mutable — and this
change does not reopen it.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md).

---

## 2026-08-03 — The client stops reading the identity token, and the greeting goes with it

**Context:** the entry below removed the stored name but left the client's home-screen greeting
alone, on the argument that a name rendered from the ID token and never sent to the API is not
something the product *stores*. That reasoning holds, and it is not why this changes. The owner
decided the greeting is not worth what it costs: it was the **only** thing on the client reading
any ID-token claim, and so the only reason to ask Google for the `profile` scope at all.

**Decision:** remove the greeting, and with it everything that existed to serve it — the NgRx
`profile` slice, the `userProfileInformation` effect, `AuthService.userProfile$`, `+common/guid.ts`
and the `immer` dependency. The authorization request narrows to `openid email`. `AuthService` now
reads no claim from the token whatsoever; the token is a bearer credential and nothing else.

**A route moved as a consequence, not as an intention.** The greeting was the entire content of
`/app/home`, so removing it would have left the post-sign-in landing screen blank. `/app` now lands
on `/app/transactions` — the screen that actually shows something — and the `home` route is gone
rather than kept as an empty shell waiting for a dashboard nobody has designed.

**On the invariant that was guarding this:** `auth-service.spec.ts` pinned FR-086 ("no image
supplied by the identity provider is displayed") at the claims-mapping boundary. Deleting the
mapping would have deleted the guard, so it was replaced by a stronger one — that no observable
`AuthService` exposes reads *any* ID-token claim. "Drops the picture" is a special case of that.
`no-profile-scope.spec.ts` reads the **built bundle** and asserts the shipped config requests
`openid` and `email` and nothing else; it is an allow-list rather than a `profile` deny-list, so a
scope nobody vetted fails it too. Pinning the artifact rather than a test fixture is the point —
the fixture proves nothing about what deploys.

---

## 2026-08-03 — The account keeps the address and nothing else the provider says

**Context:** `users.display_name` was written from the Google `name` claim on every sign-in and read
by **nothing** — no endpoint returns it, no serialiser touches it, and the client's home-screen
greeting renders the claim straight from the ID token without ever asking the API. So the column
was a copy of somebody's real name, kept indefinitely, protected at every layer, included in any
future export and any future breach, serving no purpose that could be named out loud. It arrived
because the claim was in the token, which is the worst reason to store anything.

**Decision:** the user row holds an internal id, an email address, and a creation timestamp. Of
what the identity provider asserts, only the address is kept — it is the one channel by which the
product can reach its user and the reason one provider account maps to one account here. The
`name` claim is no longer read at all. `User.Create` loses its multi-field validation aggregation
along with the second field, because aggregating one field's errors is machinery with nothing to do.

**Why the column and not just the write:** leaving it nullable and unwritten would keep every row
that already has a value, so the data would still be there to leak and still have to be erased. A
column nobody writes is not minimization, it is a slower version of the same exposure.

**What did not change, and is worth knowing:** the greeting on the home screen still shows the
person's name. It comes from the ID token into client-side state and never crosses the API, so it
is not something the product stores. When sign-in stops carrying an ID token on every request that
feature loses its source and gets decided then, on its own merits, rather than being quietly
removed here as a side effect of a storage decision.

---

## 2026-08-03 — The provider is not consulted about an account after it exists

**Context:** provisioning re-read the Google ID token's `email` and `name` claims on **every**
authenticated request and wrote back anything that differed. That made the provider a standing
authority over the account: whatever it reported today became what the account held today. It also
meant the one channel by which the product can reach its user could move without the user doing
anything, and that the provider was consulted continuously rather than once. "One email, one user"
(2026-07-28) and the entry below both assumed that refresh existed — the email was described there
as a *cached copy of an attribute the identity provider owns*, and this entry is what retires that
description.

**Decision:** the provider vouches for a person **once**, when the account is created, and is not
asked again. `EnsureUserHandler`'s existing-user branch resolves the account and returns; it
performs no write at all. Changing the stored address becomes its own operation, requiring its own
fresh authorization exchange, rather than a side effect of signing in. `User.UpdateProfile` and
`UserRepository.UpdateProfileAsync` are deleted rather than left unused — an unused write path is
one somebody restores.

**Consequence, accepted rather than worked around:** the stored address **freezes** at whatever
registration captured, and there is no way to change it until the gated exchange is built. A user
whose provider address changes keeps the old one on file. That is worse than a stale-free refresh
in exactly one respect and better in three: nothing moves the account's reachable address without
the user asking, the provider stops observing every sign-in, and a compromised provider account no
longer silently rewrites what the product knows about its owner.

**What this deletes, so nobody looks for it:** the asymmetry where an email collision was a 409 on
the insert path and *ignored* on the refresh path. There is now one path on which a collision can
occur, and it fails closed. The reasoning for the asymmetry was sound while two paths existed; it
is not a rule that was wrong, it is a rule whose second half no longer has a subject.

**Alternatives considered:** *Refresh only when the address is unclaimed, and warn otherwise* —
rejected: it keeps the provider as standing authority and only changes what happens on the rare
conflict. *Keep the refresh until the gated change ships, so no address ever goes stale* — rejected:
the refresh is not a stopgap for the missing feature, it is the behaviour the feature exists to
replace, and shipping the replacement is not made easier by leaving the thing it replaces running.

---

## 2026-08-03 — A credential is a typed row, and the account holds no identity key of its own

**Context:** "One email, one user" (2026-07-28) and the entries around it treat `users.google_subject`
as the identity anchor: one column, one provider, one way in. That shape can hold exactly one sign-in
method per account, so a second authenticator or a provider-free sign-in has nowhere to live, and it
puts an identifier an external party issues in the position of *defining* the account rather than
merely reaching it.

**Decision:** Move every way of signing in into a **`credentials` table**, each row carrying exactly
one type — `federated` (a provider vouches, naming its `provider` and the `subject` it issued) or
`passkey` (the authenticator holds it, so neither column applies). The user row keeps only its
internal id, its cached email and display name, and its creation timestamp; `google_subject` ceases
to exist. `EnsureUserHandler` resolves a principal by `(provider, subject)` and adopts a race
winner's *user* rather than its row. An account may hold more than one credential — nothing today
registers a second, but the schema no longer refuses one.

Three choices inside that, each the reason a later reader might otherwise "simplify" it back:

- **One table with CHECK-enforced shape**, not table-per-type and not a discriminated hierarchy.
  `CK_credentials_type` bounds the vocabulary; `CK_credentials_type_shape` says federated rows carry
  provider and subject while passkey rows carry neither, which is what makes "exactly one type" a
  database rule instead of a convention. Passkey columns arrive later as nullable additions and one
  widened arm.
- **The unique index over `(provider, subject)` is partial**, filtered to federated rows. A plain
  unique index would also work — `NULL`s are distinct — but by accident rather than by statement.
- **The user row and its first credential are written in one save.** A refused insert must leave
  neither behind: an orphaned user row would hold its unique email while no credential resolved to
  it, and every later sign-in with that address would 409 with no path back. Unlike a missing default
  budget, nothing heals it on the next request.

**Alternatives considered:** *Keep `google_subject` and add a second nullable column per future
method* — rejected: it re-decides the same thing at every new method and cannot express "more than
one of the same kind". *Give credentials their own repository now* — rejected as premature: the only
consumer is provisioning, and the atomic two-row insert wants one `DbContext` and one save; the
question reopens when listing and revoking arrive. *Deciding the insert outcome from the reported
constraint name* — still rejected, for the reason the 2026-07-28 entry gives, and now with an extra
edge: the user row is written before its credential, so a self-race reports the email index.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md),
[ADR 0004](../decisions/0004-connect-as-a-least-privilege-role.md) (the grant matrix gains a table
with no `UPDATE` and no `DELETE` grant of any shape).

---

## 2026-07-29 — A payee's name is correctable, and the payee list gains no other write

**Context:** "Payees are free-text find-or-create, not a managed list" (2026-07-13) below chose the
lighter of two designs — a payee comes into existence as a side effect of naming a counterparty on a
transaction, with no create/edit/delete payee UI or endpoints — and accepted in as many words that
payees "can't be renamed or pruned directly". That acceptance carried two consequences, and the entry
priced only one of them. It named the near-duplicate problem, which case-insensitive matching
partially answers. It did not name what happens to a name typed wrong: a misspelling entered once was
permanent, and self-propagating besides, because correcting it on the transaction does not undo it —
the corrected spelling runs the same find-or-create and mints a *second* payee, so the fix leaves the
budget holding both spellings with the wrong one still offered by autocomplete. The name is also the
entirety of a payee: there is no other field a person could tell two apart by, and therefore no other
field they can be wrong about. A list a user can only ever add to, in which the single field that
identifies a row is uncorrectable and a mistake reproduces itself when fixed, is not the lightness
the original decision was buying.

**Decision:** Ship `PATCH /api/payees/{id:guid}` — 204 on success; 404 for an unknown id and for one
belonging to another budget alike, on the standing tenancy rule; 400 for a name another payee in the
budget already holds — so that **the payee list gains exactly one write operation, rename, and no
others.** The name is **required** rather than a three-state optional field like the six on
`PATCH /api/transactions/{id:guid}` ("A recorded transaction is corrected in place, and silence is not
an empty value", 2026-07-29 above): a payee has exactly one mutable field, so a body that omits it
asks for nothing, and there is no second field whose silence would need a meaning. `BudgetId` stays
immutable — moving a payee between pools is the tenancy rule, not an edit. Three operations are
deliberately still absent, and each for its own reason. **No create**: a counterparty nobody has
transacted with is not a fact about the budget worth storing, and making the user register one first
puts a second task in front of the entry the original decision exists to keep fast — find-or-create
stays the only way a payee comes into being. **No delete**: the composite `transactions → payees`
foreign key is `Restrict` and the schema refuses while any transaction references the row, which is
the same protection of recorded history the account and category guards give; a delete that could only
ever succeed for rows nobody has used is not an operation worth an endpoint. **And no merge** —
repointing every transaction from one payee onto another and removing the emptied row — even though
merge, not rename, is what would actually clean up a duplicated list. Rename fixes a misspelling; it
fixes nothing about two spellings that both already carry transactions, since the second cannot take
the first's name (the unique index refuses it) and nothing moves the transactions. Merge is a real
feature rather than an increment on this one: it rewrites references across a table, it needs an
answer for which name survives, and it is the only payee operation that could not be undone. The
accepted consequence of the rename is that **it rewrites the counterparty shown on every past
transaction** — `TransactionDto` resolves the payee name at read time, so the correction is
retroactive by construction and costs no fan-out write. That is intended, and it is what separates a
meaningful rename from a cosmetic one: the row stands for one real-world party over its whole life,
so its history is not a set of independently spelled events. It does not contradict the payee delete
refusal stated above, or "Money movement is discarded only by explicit intent, never as a side
effect" (2026-07-29 below), both of which take the opposite stance on letting history change: the
schema will not let a payee be erased from transactions that already happened, while a rename changes
what every one of them displays. The two positions reconcile on one distinction: **a name is a
mutable label on a stable identity, while a transaction is the record of an event.** Relabelling the
party alters nothing about what happened.

**Alternatives considered:** *Leave payees unwritable and let correction happen on the transaction* —
rejected: that is the status quo the context argues against, and it is worse than doing nothing at
all, because the act that looks like a correction adds a row instead of repairing one. *A full payee
CRUD with its own managed list* — rejected for exactly the reasons 2026-07-13 rejected it, which this
decision leaves standing: creating a payee ahead of the transaction slows the entry, and a managed
list is heavier than a personal budgeting app needs. Rename is the one operation whose absence was a
defect rather than a simplification. *Refuse a duplicate name with 409 instead of 400* — rejected: a
taken name is a statement about a field of the request, which is what a validation problem document
carries and what a bare conflict status has nowhere to put, and `AccountRepository` already answers
400 for a duplicate account name, so a second status for the same class of mistake would only make
clients branch on which entity they were editing. *Precheck the new name with a lookup and report the
duplicate from the application* — rejected twice over: it is check-then-act, so the unique index has
to catch the loser of a race regardless and the catch it was meant to replace cannot be removed; and
"does a payee with this name exist?" answers yes for the row being renamed, which would refuse
`"starbucks"` → `"Starbucks"` — the commonest real use of the feature, since a payee is minted with
whatever casing was typed mid-entry. *Make the rename non-retroactive by snapshotting the name onto
each transaction as it is recorded* — rejected: it turns a rename into a fan-out write across
history, and until that write finishes, or if it is never run at all, the same counterparty reads two
different ways on two different screens.

**Affected areas:** [payees.md](payees.md), [_overview.md](_overview.md). This amends "Payees are
free-text find-or-create, not a managed list" (2026-07-13) below rather than reversing it: the
find-or-create creation path, the absence of a create endpoint, the absence of a delete and the
case-insensitive near-duplicate answer all stand — only the "can't be renamed" half is reversed, and
"can't be pruned" is untouched. [transactions.md](transactions.md) needs nothing: the payee is still
supplied by name on both transaction write paths, and no rule about a transaction changes.

---

## 2026-07-29 — A recorded transaction is corrected in place, and silence is not an empty value

**Context:** Transactions were create-read-delete, so the only remedy for a mis-entered row was to
delete it and record it again. That is a poor remedy for the mistake it has to serve. Correcting one
digit costs the user every other field on the row — account, date, payee, category, memo — retyped
from memory, and the row they are copying from disappears the moment they delete it, so whatever they
misremember is silently lost in the act of fixing something else. It also changes the row's identity:
a new `Id` and a new `CreatedAtUtc` for what the person experienced as fixing a typo, which points
anything holding a transaction id — a client cache, an in-flight list, and any future attachment,
reconciliation mark or import key — at a row that no longer exists. The gap was already named as a
defect in the alternatives of "Money movement is discarded only by explicit intent, never as a side
effect" (2026-07-29) below: *"No edit path exists either, so the only remedy the product offers for a
mis-entered row would remain none at all."* The second question fell out of the first. A partial edit
has to say what an unmentioned field means, and the wire format has only two states to say it with,
so the answer could not be left to fall out of a serializer's defaults.

**Decision:** Ship `PATCH /api/transactions/{id:guid}` — 204 on success; 404 for an unknown id and
for one belonging to another budget alike, on the standing tenancy rule — so that **a recorded
transaction is corrected in place, keeping its identity.** `amount`, `date`, `description`,
`accountId`, `payeeName` and `categoryId` are mutable; the budget, the `id` and `CreatedAtUtc` are
not accepted at all, because none of them is the caller's to rewrite and the first would move money
between pools. **And make the edit partial in the three-state sense**: each mutable field is either
absent (leave the stored value alone), present with a value (replace it), or present and null (clear
it), carried by `Optional<T>` and an `OptionalJsonConverterFactory` registered on the HTTP JSON
options. That is not a preference between two workable designs — both ways of collapsing three states
into two are defects. If an absent property reads as null, an edit that fixes an amount silently
erases the memo, the payee and the category the caller never mentioned, and nothing rejects it,
because all three may legitimately be empty. If an absent property reads as "leave it", clearing an
optional field becomes impossible and a category assigned once can never be removed. Three of the six
fields are declared over non-nullable types — amount, date and account — so an explicit null for them
is a `JsonException` and a 400 rather than a clear: a transaction without them is not a transaction,
and there is no empty `decimal` to clear to, so a permissive design would fall back on `0`, which is
a legal amount, and zero an entry instead of refusing a request that meant nothing. That falls out of
the type rather than from a branch anyone has to maintain. The contract is **application- and
transport-owned by necessity**: the database has no opinion about which properties a request
mentioned, and by the time a row is written the distinction has already been resolved into a value
([ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md)). Two consequences are
accepted. **An edit can mint a payee**, because the counterparty is supplied by name with the same
find-or-create as on creation, which makes `UpdateTransactionHandler` a second writer of the `payees`
table and is why its write runs inside `ITransactionalExecutor` for exactly the reason creation's
does — a payee committed without the edit that named it is permanent litter, since nothing deletes
payees. And **editing a transaction to name a different counterparty strands the old payee** just as
deleting the transaction does, so the payee list grows with corrections as well as with new entries,
and fixing a misspelt name leaves both spellings in it (see [payees.md](payees.md)). This does not
disturb "Money movement is discarded only by explicit intent, never as a side effect" (2026-07-29)
below, which stands in full: **correcting an entry is not discarding it.** No row leaves the ledger
on this path — an edit changes what a recorded movement says about itself, and the record survives
the change. The two decisions are complementary rather than competing: the delete is for a row that
should never have existed, the edit is for a row that should exist differently, and it is the delete
that stays the only act which removes recorded movement.

**Alternatives considered:** *Leave transactions create-read-delete and let correction be
delete-and-re-record* — rejected: this is the status quo restated, and it is the case the context
above argues against — identity churn, retyping from memory, and the source row gone before the
replacement is typed. *A full-body `PUT` replace* — rejected: it makes the client the authority on
every field of every edit, so it must hold the whole row and echo it back, and an edit to one field
overwrites a concurrent change to another with no signal that anything was lost. It also does not
remove the question, it hides it: a client that omits a property from a replace body is still
asking for something, and the answer is now "clear it" whether they meant it or not. *A two-state
PATCH, either reading absent as null or reading null as absent* — rejected, and it is one rejection
rather than two: both directions are the collapse argued against above, one destroying unmentioned
fields and the other making an optional field unclearable once filled. *JSON Patch (RFC 6902)
operation documents* — rejected: it buys array indexing and nested paths that a flat record of six
scalar fields has no use for, and it moves validation out of a typed command into a document
interpreter, where a malformed path is a runtime error instead of a binding failure. *Supply the
payee by id on edit even though creation takes a name* — rejected: the same field would resolve by a
different rule according to which verb carried it, so a corrected transaction could end up on a
different payee row from an identical one typed right the first time, which is the duplication a
shared payee row exists to prevent.

**Affected areas:** [transactions.md](transactions.md), [payees.md](payees.md) — which gains a
second writer and a second cause of stranded rows — and [_overview.md](_overview.md). The delete
guards in [accounts.md](accounts.md) and [categories.md](categories.md) are untouched: an edit that
moves the last transaction off an account or a category clears their refusals by the same mechanism
a delete does, without any of their rules changing.

---

## 2026-07-29 — Money movement is discarded only by explicit intent, never as a side effect

**Context:** Transactions were append-only at every layer — no update, no delete, no repository
method that could remove one — and that was never a decision, only what had been built. Against that
sits a sentence recorded in "A budget holding transactions cannot be deleted; its empty structure
still cascades" (2026-07-26) and repeated in `TransactionConfiguration` beside the `Restrict`
foreign key: **recorded money movement is the one thing a budget must not lose.** Read literally,
that forbids a person removing an entry they fat-fingered, which is not what it decided. What it
decided is that money movement is never thrown away as *collateral* — as a side effect of a delete
aimed at something else, where the user asked to remove a budget and their history went out with it
unasked. Deleting one's own mistaken entry is a different act entirely: the movement is the thing
the user is aiming at. The record is entered by hand, so it carries hand-made mistakes — a purchase
typed twice because the first attempt looked like it failed, an amount recorded against the wrong
account — and a ledger that cannot drop such a row is not more truthful than one that can. It holds
a movement that never happened, and every sum a person builds on it is wrong by exactly that amount.
The wording forbade the correction by accident, and one consequence was already visible:
`DeleteAccountHandler` refuses with "Account cannot be deleted because it has transactions.", a
sentence naming an obstacle the system gave the user no way to clear.

**Decision:** Ship `DELETE /api/transactions/{id:guid}` — 204 on success, and 404 for an unknown id
and for one belonging to another budget alike, because the `BudgetIsolation` filter makes that row
invisible and this API deliberately has no 403 path — and **sharpen the rule to: money movement is
never discarded as a side effect, only ever by explicit intent.** That clarifies "A budget holding
transactions cannot be deleted; its empty structure still cascades" (2026-07-26) rather than
reversing it. **The schema does not move.** `transactions.budget_id → budgets.id` stays `Restrict`
and its four siblings stay `Cascade`, so a budget holding any transaction is still undeletable; the
two rules are about different acts, not different strengths of the same act — one refuses a delete
aimed at a budget, the other permits a delete aimed at exactly one transaction. The removal is a
**hard delete**: the row goes, nothing is flagged. The real user-facing win is a knock-on.
`DeleteAccountHandler`'s refusal above, and `CategoryRepository.DeleteAsync`'s "Category cannot be
deleted because it has transactions.", were dead ends while nothing could remove a transaction —
the user was told what to do and given no way to do it. Deleting the last transaction that
referenced an account or a category now makes that row deletable again, which is what turns a
refusal into an instruction. Two consequences are accepted. A delete can **strand a payee** no
transaction names, and payees have no delete path at all, so the list only grows and the orphan is
permanent (see [payees.md](payees.md)). And the `23503` catches in `AccountRepository.DeleteAsync`
and `CategoryRepository.DeleteAsync` **stop being belt-and-braces and become the real guard**: a
precheck reading "no transactions" can now be invalidated by a concurrent insert, and one reading
"has transactions" by a concurrent delete, so both directions are live where only one was before.
Per [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) the precheck exists
for the *message* and the constraint is what is *correct*: those catch blocks must not be simplified
away as redundant later. `DeleteTransactionHandler` needs no catch of its own — nothing in the schema
references `transactions`, so a delete has no foreign key to violate.

**Alternatives considered:** *A soft delete behind a deleted flag* — rejected: there is no sharing,
no compliance retention, nothing that restores a deleted row, and no soft-delete infrastructure
anywhere in this schema, so the flag would put a predicate on every transaction query — and a second
filter interacting with `BudgetIsolation` — to serve a need this product has not expressed, while
leaving a row the user asked to be gone still sitting there. It reopens the moment any of four
things is wanted: an undo that restores a deletion, a trash the user can pull a row back out of,
more than one person per budget, or an export whose integrity depends on deletions staying
recoverable. *Keep transactions append-only and answer the account and category refusals some other
way* — rejected: this is the status quo restated, and there is no other way. No edit path exists
either, so the only remedy the product offers for a mis-entered row would remain none at all, while
two error messages go on instructing the user to do something the system does not let them do.
*Answer 403 for a transaction in another budget* — rejected on the standing rule in
[budgets.md](budgets.md#must-not): a 403 confirms the row exists, and a by-id target in this API
surfaces as 404 whether it is missing or someone else's.

**Affected areas:** [transactions.md](transactions.md). The delete guards in
[accounts.md](accounts.md) and [categories.md](categories.md) gain a remedy without any of their
rules changing, and the permanent-orphan gap in [payees.md](payees.md) widens to a second cause.
This clarifies "A budget holding transactions cannot be deleted; its empty structure still cascades"
(2026-07-26) below, which stands in full.

---

## 2026-07-29 — Ordering contiguity stays in the domain, and the near-miss constraint is named

**Context:** Category Group and Category positions are persisted, zero-based, and **contiguous**
within their scope — groups over the whole budget, categories inside their group. Contiguity is what
makes a position mean anything: it is the only thing that turns "position 3" into "the fourth item",
which is what a client sends when someone drops a row into the fourth slot. With gaps or duplicates
in the stored list, that number stops naming the slot the user aimed at. The rule lives in
`CategoryOrdering` and `CategoryGroupOrdering`, which reindex the whole affected list on every
insertion, move and removal. That puts it **above** its nominally lowest capable layer, and
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) requires such a rule to
say why — without which the next reader reads the gap as an oversight and starts writing triggers.
The database holds only the non-negative half, in `CK_categories_position` and
`CK_category_groups_position`; the ordering indexes are deliberately **not** unique.

**Decision:** Keep contiguity domain-owned, because checking it on a write means comparing a row
against every one of its siblings and the only PostgreSQL construct that can do that is a deferred
constraint trigger — procedural logic in the database, which is precisely the boundary ADR 0002
draws around "lowest capable layer". Record the reasoning in
[categories.md](categories.md#business-rules--invariants) beside the rule, and name the rejected
near-miss there explicitly rather than leaving it to be rediscovered.

**Alternatives considered:** A **`DEFERRABLE INITIALLY DEFERRED` unique constraint on
`(category_group_id, position)`** is the near-miss worth naming, because it catches duplicates
declaratively and *is* a constraint rather than a trigger — so the declarative boundary is satisfied
and is not what rules it out. Three other things do. First, the consequence it would prevent is
cosmetic: both read services tie-break on `Id`, so a duplicate produces a deterministic-but-wrong
order that the next reindex of that list repairs on its own — nothing like the tenancy breach or the
bulk loss of recorded money the rules that do live at the bottom prevent — and the bottom layer's
price is paid on every later change. Second, EF has no `DEFERRABLE` support, so it would be
hand-written SQL inside the single baseline migration this repository regenerates by convention;
grants can live outside migrations because provisioning owns them, but schema has nowhere else to
go. Third, it buys half the invariant at best: `0, 1, 5` holds no duplicate, is equally broken to
the person reading the list, and would stay legal.

**Affected areas:** [categories.md](categories.md).

---

## 2026-07-28 — A zero amount is a legal transaction: the record is the point, not the number

**Context:** "Amount must be non-zero (zero has no direction and records no movement)" was recorded
below as a closing clause of the direction decision (2026-07-13) and never argued in its own right.
It is wrong on the ledger's own terms: a ledger records what happened, not only where money moved. A
fully discounted purchase, a refund that exactly cancels the purchase it reverses, a zero-value
invoice worth keeping for its payee, date and category — each is a real event, and what makes such a
row worth having is the record itself, not the number on it. Refusing zero does not remove the event;
it forces the user to invent an amount or drop the entry, and both are worse than storing the truth.
The rule also produced an asymmetry against `Account.OpeningBalance`, which has always accepted zero,
and the reason given for that asymmetry — a new account may legitimately start empty, unlike a
transaction, which must move something — was an assertion rather than an argument. The likeliest real
reason the rule existed is neither of these: an empty amount field binds to `0`, so a blank form
submit would otherwise record a meaningless entry.

**Decision:** **Allow `Amount == 0`.** `Transaction.Create` validates precision and magnitude only,
and the sign keeps exactly the meaning it had, with zero reading as neither direction rather than as
an invalid one. The blank-form guard the rule was probably standing in for **becomes the client's,
explicitly and by name**: nothing below the client can tell a deliberate zero from an untouched
field, so the amount input must require a value rather than default to one. Under
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) that is where a rule of
this shape belongs — it is about the interaction, not about what a ledger may hold — but a guard that
changes layers without anyone building it is a guard that was deleted, which is why it is named here
and in [transactions.md](transactions.md) instead of being left to follow from the principle.
Accepting that lists and totals can now contain rows that add nothing to any sum; that is the
intended outcome, because they add a fact.

**Alternatives considered:** *Keep the rule as the blank-form guard* — rejected: it is product policy
wearing an invariant's clothes, and it pays for catching one careless submit by refusing every honest
zero, permanently and for every caller — including importers and any future consumer that has no form
at all. *Refuse zero only when description, payee and category are all absent* — rejected: it makes
an amount's legality depend on unrelated fields, so the same number is valid or invalid according to
what else was typed, and it still cannot see the difference it is trying to detect. *Leave it and let
users record `0.01` or nothing* — rejected: this is the status quo restated. It either puts a cent
that never moved into the account's total or drops a record the user wanted, and the second is what
people actually do.

**Affected areas:** [transactions.md](transactions.md), [accounts.md](accounts.md),
[_overview.md](_overview.md). This reverses the final sentence of "Transaction direction is the sign
of a single Amount" (2026-07-13) below; the rest of that entry — direction as the sign of one signed
amount, and the rejection of a separate type flag — stands.

---

## 2026-07-28 — Recorded precision is a property of the currency, not a constant

**Context:** `Account` and `Transaction` both rounded against a hard-coded two decimal places, while
every seeded `Currency` carried a `MinorUnit` of 0 to 4 that **nothing read**. The model therefore
advertised a precision it could not honour, and failed in both directions: a JPY account accepted
`1000.5`, half a yen being a denomination that does not exist, while a BHD or KWD account — three
minor units — could not record its smallest unit at all, since `0.125` was refused as over-precise
and, had it got past the domain, `numeric(14,2)` would have silently stored it as `0.13`. Reference
data nothing reads is decoration; worse, this piece of it was already documented as coupled to the
scale of the money columns while that scale was 2 and its own ceiling was 4.

**Decision:** **Make the currency's minor unit the precision rule.** `Transaction.Create`,
`Account.Create` and `Account.Update` take an `int minorUnit` and reject any amount `decimal.Round`
would change, with the message computed from it — "Amount must be a whole number." for JPY, "…no more
than 3 decimal places." for BHD. An out-of-range `minorUnit` is an `ArgumentOutOfRangeException`
rather than a validation error, because it can only arrive from a `currencies` row that
`CK_currencies_minor_unit` would have refused: a broken caller, not user input.
`accounts.opening_balance` and `transactions.amount` widen from `numeric(14,2)` to `numeric(14,4)`,
so the most precise currency the constraint permits is representable at all.
`UpdateAccountHandler` gains `ICurrencyReadService`, which it had never needed — `Account.Update`
re-validates against the account's own unchangeable `CurrencyCode` and now needs the currency behind
it, resolved after the load, where a miss is corruption rather than user error. The rule is
**domain-owned by necessity, not by preference**: checking a row's precision against its currency
means joining `currencies` per row, which PostgreSQL can only do in a trigger, and
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) rules procedural logic out
of the database even where it would be the lower layer. What the schema owns is the envelope — the
column scale bounds what is representable, the check bounds what a currency may claim — and the
domain picks the right value inside it. The scale still coerces rather than rejects, so widening it
changes what fits, not who enforces.

**Alternatives considered:** *Round everything to four places, the widest a currency may declare* —
rejected: it swaps one lie for another. A USD account would accept `10.0001`, which is not money in
the currency that account is denominated in, and the yen case would be no better than before.
*Express the rule in the schema* — rejected on ADR 0002's declarative boundary: a per-row check would
have to read `currencies`, so it exists only as a trigger — business logic invisible to the type
system, untested by the unit suite, and versioned only by migrations. *Widen the precision as well,
to `numeric(15,4)`* — rejected: `CK_accounts_opening_balance` and `CK_transactions_amount` already
refuse anything above 1e9, and `numeric(14,4)` still leaves ten integer digits, tenfold headroom
above a magnitude no accepted write can reach. The extra digit would buy room for values the schema
rejects, at the cost of changing more of the column than the decision requires.

**Affected areas:** [currencies.md](currencies.md) — now the canonical home of the precision rule and
of why it sits above its nominally lowest layer — [accounts.md](accounts.md),
[transactions.md](transactions.md). The `numeric(14,2)` illustration in
[users-and-ownership.md](users-and-ownership.md) and in
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) names these same two
columns and now reads `numeric(14,4)`; the point it makes — a scale coerces and therefore enforces
nothing — is untouched by the widening.

---

## 2026-07-28 — The provisioned budget has no name, and race safety keys on that absence

**Context:** The budget created at provisioning was named from a constant, and the unique index over
`(user_id, name)` was what made two concurrent first requests collide: both racers wrote the same
literal, so one of them got a `23505` and re-read the other's row. The guarantee therefore rested on
a string two callers had to agree on. Such a constant can drift — a second creation path, a caller
that supplies a name of its own, a rename that looks harmless — and the day it drifts nothing fails
loudly: both inserts succeed and the user silently owns two budgets, the second one invisible behind
a lookup that returns the first. The literal was also product copy living in storage, and a
non-nullable `Name` went on asserting that every budget has a name when the only budget that exists
was never named by anyone.

**Decision:** Make `budgets.name` **nullable** and give the provisioned budget **no name at all** —
`Budget.CreateDefault` produces `Name == null`, the default-name constant is deleted, and
`Budget.Create` still requires a trimmed name of at most 200 characters, so the two factories differ
in exactly that one respect and share a single owner check. Declare the unique index over
`(user_id, name)` **`NULLS NOT DISTINCT`** (`AreNullsDistinct(false)`, PostgreSQL 15+), which is what
makes two nameless rows collide: PostgreSQL's default treats every NULL as distinct, so without the
opt-out both racers' inserts land. The index now states a real invariant instead of a coincidence of
a shared literal — **at most one unnamed budget per user, plus any number of named ones** — which is
exactly the shape multi-budget needs. What a client shows in place of a missing name is
**presentation and stays in the client**, localized there; neither the domain nor the schema holds a
display string, so there is nothing to keep in sync and nothing a migration would have to translate.
Accepting that `Name` is nullable everywhere it is read, so every future budget list, header and
export has to answer "no name" explicitly rather than inherit an answer from storage — which is the
point, but it makes the render contract something each client must be given rather than something it
can assume.

**Alternatives considered:** *A store-level `DEFAULT` on `name` carrying the display string* —
rejected twice over: EF Core sends every mapped, non-store-generated property in the INSERT, so the
default would never fire unless the property were made store-generated, and it would put UI-visible
product copy in the schema, unlocalizable and changeable only by migration. That is the "invariants
down, policy up" boundary of
[ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md): the uniqueness invariant
belongs at the bottom, the label a person reads does not. *An empty string instead of NULL* —
rejected: `""` is a sentinel meaning both "default by design" and "blank by accident", and a
non-nullable `Name` would keep claiming a name always exists, so the compiler could not tell the two
states apart and every reader would have to remember the convention. *A unique index on `user_id`
alone* — already rejected in "Budget replaces the user as the unit of tenancy" (2026-07-25) as a
constraint a later release must remember to drop; nothing here changes that reasoning.

**Affected areas:** [budgets.md](budgets.md). This supersedes the half of "Budget replaces the user
as the unit of tenancy" below that made provisioning race-safe "because the default budget's name is
a constant"; the rest of that entry, including the deliberate absence of a one-budget-per-user
constraint, stands.

---

## 2026-07-28 — One email, one user: a legible 409 beats silently provisioning a second person

**Context:** `users.email` was unbounded and unconstrained, so two rows could hold the same address.
That sits uncomfortably against the identity model this log already records: identity is keyed on the
Google `sub` **precisely because email is mutable**, and a duplicate address is exactly what a user
whose `sub` changed looks like. Making email unique therefore introduces a failure mode that did not
exist before — a legitimate Google account refused because a stale row holds its address. What tips
the balance is the alternative, which is worse and silent: without the constraint, that same person is
provisioned as a brand-new user with an empty default budget and no signal whatsoever that their
accounts, categories and transactions still exist under the old row. They see a working, empty app and
conclude their data is gone. The address is also the only human-readable identifier a user has, so two
rows holding one address make every support question, export and future notification ambiguous.

**Decision:** Make `users.email` **unique, case-insensitively** — `varchar(254)` on the
`case_insensitive` collation under `IX_users_email` — and bound the other two text columns at
`varchar(255)` for `google_subject` (Google's documented `sub` cap) and `varchar(200)` for
`display_name` (matching every other name column). The two collision paths behave **asymmetrically on
purpose**: a brand-new `sub` presenting a taken address gets a **409** with a sentence naming the
cause, because there is no row to adopt and failing closed is the honest answer; an already-identified
user whose refreshed email is taken has the change **discarded** and the sign-in proceeds on the
stored one, because email is a cached copy of an identity-provider attribute and a cached attribute
failing to refresh must never lock a person out of their own budget. The 409-versus-continue split is
application policy sitting deliberately above the database, which rejects both writes identically and
cannot know that one of them is a sign-in. Accepting three consequences: an over-long email now fails
an existing user's sign-in with a 400 (truncating or swallowing is ruled out by ADR 0002); the
collation folds case but not accents, so `josé@` and `jose@` remain two users; and, being
nondeterministic, it makes `LIKE` on the column fail with `0A000`, so the first search-by-email needs
an explicit `COLLATE`.

**Alternatives considered:** *Leave email unconstrained* — rejected: it converts a changed `sub` into
silent, unsignalled data loss, and the database is the only layer that still holds when the
provisioning handler is wrong. *Rebind the stale row's `google_subject` to the new subject on
collision* — rejected: it makes `sub` mutable in direct contradiction of the identity rule this log
already records, and it would only be safe if the `email_verified` claim were checked, which the API
does not read — an unverified address in a token would then be enough to take over an existing
account. *Throw on both paths, or swallow on both* — rejected: throwing on refresh locks out existing
users over an attribute they do not control, and swallowing on insert drops the caller back into the
race re-read, which finds nothing under the new `sub` and produces the unexplained 500 this change
exists to remove.

**Affected areas:** [users-and-ownership.md](users-and-ownership.md).

---

## 2026-07-26 — A budget holding transactions cannot be deleted; its empty structure still cascades

**Context:** Every entity a budget owns cascaded from `budgets.id`, so a single budget delete would
take accounts, category groups, categories, payees **and every transaction** with it. That treats
recorded money movement and the scaffolding around it as equally disposable. They are not: structure
can be retyped from memory, financial history cannot, and a mistakenly created budget is a real
scenario a user must be able to undo without an archive feature existing first.

**Decision:** Make `transactions.budget_id → budgets.id` **`Restrict`** while its four siblings —
`accounts`, `category_groups`, `categories`, `payees` — stay **`Cascade`**, so **a budget that holds
any transaction cannot be deleted at all**, and a budget with no recorded movement is deletable and
takes its structure out with it. Recorded money movement is what deserves the guard; empty
scaffolding does not, and protecting it too would only trade a lost history for a budget the user can
never get rid of. Add `IBudgetRepository.HasTransactionsAsync` — implemented in `BudgetRepository` as
a `BudgetIsolation`-filtered `Transactions.AnyAsync` — as the application-side seam for asking the
question. It takes **no budget id**: tenancy comes from the query filter via `IBudgetContext`, the
system's only authorization mechanism, so a caller-supplied budget id would be a tenancy parameter
with no ownership check to pair with it. Accepting that the delete policy is no longer uniform across
the five owned tables — the asymmetry has to be explained rather than inferred, and a future reader
may take it for an oversight — and that, because PostgreSQL fires referential actions in foreign-key
creation order, the refusal is guaranteed while *which* constraint reports it is not, so any future
delete feature needs an application precheck to explain itself rather than an error translation.

**Alternatives considered:** *Make all five references `Restrict`* — rejected: an empty, mistakenly
created budget would then be undeletable, so undoing a typo would require building an archive feature
first. *Keep all five `Cascade` and guard the delete in application code only* — rejected: a single
unguarded write path silently destroys financial history, and the database is the only control that
still holds when application code is wrong. *Give `HasTransactionsAsync` a `budgetId` parameter and
`IgnoreQueryFilters()`* — rejected: that token is forbidden on this DbContext and a CI guard for it
is planned, and the parameter would reintroduce tenancy as an argument no ownership check validates.

**Affected areas:** [budgets.md](budgets.md), [transactions.md](transactions.md). This partially
reverses "Budget replaces the user as the unit of tenancy" below, which shipped uniform `Cascade`
from a budget to all five entities it owns without recording that as a decision; the rest of that
entry stands.

---

## 2026-07-25 — Transaction references are same-budget in the schema, not just in application code

**Context:** `transactions` reached `accounts`, `categories` and `payees` through plain single-column
foreign keys, so the database would accept a transaction pointing at another budget's row. Nothing
produced one, because `CreateTransactionHandler` resolves every reference through a
`BudgetIsolation`-filtered repository — but the guarantee lived entirely in application code, and a
query filter enforces nothing on a write.

**Decision:** Give `Account`, `Category` and `Payee` an alternate key `(Id, BudgetId)` and reference
them through composite foreign keys `(account_id, budget_id)`, `(category_id, budget_id)` and
`(payee_id, budget_id)` → `(id, budget_id)`, so **PostgreSQL refuses a cross-budget reference**
whatever code path wrote the row. Optionality survives free: a multi-column check is skipped entirely
when any of its columns is NULL (MATCH SIMPLE). The payee reference takes `Restrict`, making **a
referenced payee undeletable** — the guard accounts and categories already have, forcing an explicit
decision about historical rows instead of silently erasing the counterparty from past transactions.
Accepting that the three alternate keys create `UNIQUE (id, budget_id)` indexes redundant with each
primary key: PostgreSQL requires a unique constraint on a foreign key's referenced columns, and
promoting the primary key to `(id, budget_id)` would break every single-column foreign key and every
by-id lookup, so the redundancy is unavoidable — already accepted once for `category_groups`.

**Alternatives considered:** *Leave the boundary to application code* — rejected: every future write
path that bypasses the filtered repositories loses it silently, with no failure signal. *Keep SET
NULL on the payee reference via PostgreSQL 15+ column-list `ON DELETE SET NULL (payee_id)`* —
rejected: unreachable from EF Core 10, whose `ReferentialAction` has no column-list variant, and raw
SQL would need re-applying on every baseline regeneration while the model snapshot still recorded
`SetNull`, leaving the tooling diffing against a lie. *Exclude payees to preserve SET NULL* —
rejected: it leaves one of the three references unprotected for a delete path no application code
exercises.

**Affected areas:** [transactions.md](transactions.md), [budgets.md](budgets.md),
[categories.md](categories.md).

---

## 2026-07-25 — Budget replaces the user as the unit of tenancy

**Context:** A person can preside over more than one pool of money — funds for an event, a club, or a
relative are under their control without being part of their own life — and forcing those pools into
one total falsifies the single picture rather than simplifying it. Ownership was per user, which made
that impossible to express. The envelope-budgeting layer about to be built (allocations, carryover,
month view, base currency) hangs off whatever the unit of tenancy is, so it had to be settled before
that layer exists; retrofitting tenancy underneath a finished envelope layer would be a far larger
change.

**Decision:** Put a **Budget between the user and everything else** — a user owns budgets, a budget
owns accounts, category groups, categories, payees and transactions — because the single picture
belongs to a coherent pool of money, not to a person. `UserId` is **dropped** from those five
entities in favour of `BudgetId`, leaving `Budget.UserId` as the only owner link; name uniqueness and
ordering re-scope to the budget; payees do not cross budgets. Isolation moves from `UserIsolation` to
`BudgetIsolation` query filters reading an ambient `IBudgetContext` resolved once per request. The
schema is **multi-budget-ready from day one** — no one-budget-per-user constraint at all; the unique
index is `(user_id, name)`, which still makes provisioning race-safe and idempotent because the
default budget's name is a constant, so two racers collide and the loser adopts the winner's row. The
default budget is created **inside `EnsureUser`**, unconditionally on every authenticated request, so
"an account exists ⇒ it has its budget" stays one idea and a partially provisioned user heals on the
next sign-in. Shipped as a **single fresh initial migration** with no data migration, accepting the
loss of existing development data.

**Alternatives considered:** *Keep both `UserId` and `BudgetId`* — rejected: it creates an
`entity.UserId == entity.Budget.UserId` invariant enforceable only by composite foreign keys on all
five tables, protecting a column no query reads. *A unique index on `user_id` alone as a temporary
one-budget-per-user guard* — rejected: a constraint that a later release must remember to drop is a
trap, and the one-per-user property is better pinned by tests over the only code path that inserts a
budget. *A separate provisioning handler for the budget* — rejected: it opens a window where a user
exists with no budget, after which every filtered query throws. *A lazy budget lookup inside the query
filter* — rejected: a synchronous property getter issuing a query on the very context being queried.
*A budget identifier in routes* — rejected: it turns tenancy into a client-supplied, tamperable
parameter and makes the concept visible to a user who has only one budget.

**Affected areas:** [budgets.md](budgets.md) (now the canonical home of the tenancy invariant),
[users-and-ownership.md](users-and-ownership.md), [accounts.md](accounts.md),
[categories.md](categories.md), [transactions.md](transactions.md), [currencies.md](currencies.md).

---

## 2026-07-14 — Replace flat Groups with required Category Group → Category hierarchy

**Context:** The original `Group` entity was actually a flat transaction category. It could not
represent a user-facing organizational heading such as “Essential Obligations” containing
“Groceries” and “Utility Bills,” and its name made the domain ambiguous.

**Decision:** Replace Group with two user-owned resources: every **Category** belongs to exactly one
**Category Group**, while a Transaction may still be uncategorized. Membership is required and
same-owner at the database level. Names are case-insensitively unique per user (Category names across
all groups), both levels use persisted custom order, and typed PATCH operations move/reorder items.
Block deleting a non-empty Category Group and a Category referenced by Transactions. Read historical
Transactions through current Category/Category Group names rather than snapshots. Make a clean API
and schema break with no `/api/groups` aliases or data migration.

**Alternatives considered:** Keep Group as the Category name and add a loosely associated heading —
rejected because it preserves ambiguous terminology and permits invalid membership. Optional
Category Group membership — rejected because orphan Categories violate the intended hierarchy.
Nested or many-to-many groups — rejected as unnecessary complexity. Alphabetical order — rejected
because users need deliberate personal organization. Snapshotting names on Transactions — rejected
because renames and moves should update historical display.

**Affected areas:** [categories.md](categories.md), [transactions.md](transactions.md),
[users-and-ownership.md](users-and-ownership.md). This reverses the Category portion of the
2026-07-13 account/group deletion entry below; the Account decision remains unchanged.

---

## 2026-07-13 — Payees are free-text find-or-create, not a managed list

**Context:** A transaction can name a counterparty (payee). We had to decide whether payees are a
first-class thing the user creates and manages, or something lighter.

**Decision:** Treat a payee as **free text with autocomplete**, created automatically (find-or-create
by name, case-insensitive) as a side effect of recording a transaction — no create/edit/delete payee
UI or endpoints — to keep transaction entry fast and frictionless, accepting that payees can't be
renamed or pruned directly and that near-duplicates are only prevented by case-insensitive matching.

**Alternatives considered:** A managed payee list with its own CRUD and an FK picker on the
transaction form — rejected as heavier than a personal budgeting app needs and slower to use. A plain
free-text string with no entity at all — rejected because a shared payee entity is what powers
autocomplete and consistent naming across transactions.

**Affected areas:** [transactions.md](transactions.md) (Payees).

---

## 2026-07-13 — Deleting an account or group with transactions is blocked, not cascaded

**Context:** Users can delete accounts and groups. Those entities may have transactions pointing at
them. We had to decide what happens to the transactions.

**Decision:** **Block the delete** while any transactions reference the account or group (a validation
error the user must resolve), rather than cascade-deleting or silently nulling the references — to
protect financial history from accidental bulk loss, accepting that the user must recategorize or
clear transactions before removing the account/group.

**Alternatives considered:** Cascade delete (remove the transactions too) — rejected: a single
mis-click could wipe months of records. Null the reference and keep the transactions — rejected for
accounts (a transaction with no account has no currency/context); considered less harmful for groups
but kept symmetric with accounts for consistency and predictability.

**Affected areas:** [accounts.md](accounts.md), groups (now [categories.md](categories.md)).

---

## 2026-07-13 — Transaction direction is the sign of a single Amount

**Context:** A transaction is either money in (income) or money out (expense). We had to decide how
to represent direction.

**Decision:** Encode direction as the **sign of one signed `Amount`** — negative = expense, positive =
income — rather than a separate type/flag field, so the net effect on an account is simply the sum of
its amounts and a "positive expense" contradiction is structurally impossible. Amount must be
non-zero (zero has no direction and records no movement).

**Alternatives considered:** A separate `TransactionType` enum (Income/Expense) plus an unsigned
amount — rejected as redundant with the sign, and it introduces an invalid-combination surface
(e.g. Type=Expense with a positive amount) that then needs its own validation. Two separate amount
columns — rejected as over-modeled for the need.

**Affected areas:** [transactions.md](transactions.md).
