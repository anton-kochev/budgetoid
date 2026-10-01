# ADR 0003 — Wrap a handler's writes in one transaction through an Application-owned port

- **Status:** Accepted
- **Date:** 2026-07-29
- **Area:** Architecture / Persistence (Application ports, EF Core transactions, Npgsql execution
  strategy)

## Context

Every repository in this codebase calls `SaveChangesAsync` for itself. That is convenient until one
handler needs two of them: each save is its own transaction, nothing spans them, and a failure
between the two leaves the first committed and the second lost.

`EraseAccountHandler` is the case that makes the cost concrete, and it is the shape every future one
will have. It empties the ambient budget's `transactions` through `ITransactionRepository` and then
deletes the `users` row through `IUserRepository` — two repositories, in that order, because the four
`RESTRICT` edges into `transactions` would otherwise refuse the cascade that removes everything else.
Committed separately, an erasure that emptied the transactions and then failed would have destroyed
every recorded money movement while leaving standing the account that justified deleting it. The
consequence is **permanent, not untidy**: recorded movement is the one thing in this product its
owner cannot reconstruct from memory, and there is no repair once the rows are gone. See
[erasure.md](../business-logic/erasure.md).

The window is not theoretical on any such handler. The `cancellationToken` is the endpoint's, which
is `HttpContext.RequestAborted`, and it is passed to every save — so a client closing the tab between
two of them is enough, with no database or network fault at all.

## Decision

**Enclose the writing half of a handler in one database transaction, through a port the Application
layer owns.** `ITransactionalExecutor`
(`BudgetoidApp/Application/Abstractions/ITransactionalExecutor.cs`) takes the unit of work as a
delegate and returns its result; the transaction commits when the delegate returns and rolls back if
it throws. Infrastructure implements it in `DbContextTransactionalExecutor`
(`BudgetoidApp/Infrastructure/Persistence/DbContextTransactionalExecutor.cs`) over the same
request-scoped `BudgetoidDbContext` that every repository resolves. That shared instance is what
lets a wrap this thin cover saves the executor never sees: it does not coordinate the repositories,
it only owns the transaction the context is already going to use.

**The boundary encloses the writes and starts after everything that only reads.** Resolving,
validating and authorizing commit nothing, so opening the transaction above them buys no atomicity at
all and holds a connection, a snapshot and any locks taken along the way for the extra round trips. A
transaction boundary is not free insurance; its width is its cost. `EraseAccountHandler` is the worked
example and its gate is the sharper half of the rule: the re-authentication check runs to completion
**outside** the delegate, for two reasons written at the call site — the spent nonce must commit
independently, or a rolled-back erasure would restore it and make the assertion replayable; and the
delegate is **replayed** by the execution strategy, so a gate inside it would consume a second time
and refuse a valid erasure with the same 401 an attacker gets.

**The dependency is required, not optional.** The handler takes `ITransactionalExecutor` as a plain
constructor parameter and `Infrastructure.DependencyInjection` registers it scoped
(`BudgetoidApp/Infrastructure/DependencyInjection.cs:39`). A nullable dependency with a
run-without-it fallback was rejected outright: a composition that silently resolved `null` would lose
atomicity and report nothing, and the defect this record exists to close is precisely one that
produces no error. A missing registration is a startup failure instead, which is the loudest failure
available.

**The begin/commit pair goes through `Database.CreateExecutionStrategy()`, and this is the part the
next person will trip on.** `EnrichNpgsqlDbContext` in `BudgetoidApp/Api/Program.cs:28` re-applies
Aspire's Npgsql defaults on top of the non-pooled `AddDbContext` registration above it, and those
defaults include `NpgsqlRetryingExecutionStrategy`. A retrying strategy **refuses a user-initiated
transaction outright** — it cannot replay a unit of work whose boundary it does not own, so it
declines to let one be opened rather than silently retrying half of it. Handing it the begin/commit
pair is what gives it something replayable. The same shape is correct when the strategy does not
retry: it then invokes the delegate exactly once, so nothing has to change if retries are ever turned
off. This was measured rather than assumed: without `EnrichNpgsqlDbContext` the context's strategy is
`NpgsqlExecutionStrategy` with `RetriesOnFailure` false; with it, `NpgsqlRetryingExecutionStrategy`
with `RetriesOnFailure` true.

**An operation started while a transaction is already open joins it rather than nesting**
(`BudgetoidApp/Infrastructure/Persistence/DbContextTransactionalExecutor.cs:24`). PostgreSQL and EF
have no true nested transactions, so the
alternatives are both worse than the case they handle: an inner begin/commit pair would commit half
the outer unit while the outer scope still believed it could roll everything back, and throwing would
reject a call that is legitimate. Joining keeps the **outermost** scope owning the commit. Nothing
nests today; the rule exists for the day something does.

**One limitation, stated plainly rather than buried: a replayed attempt runs against a context that
still holds the abandoned attempt's entities.** The execution strategy may replay the whole unit
after a transient failure, and a rollback does not undo the change tracker — EF marks entities
`Unchanged` the moment `SaveChanges` returns, and rolling the transaction back afterwards does not
walk that back. A payee saved and then discarded therefore stays in the tracker looking persisted.
Nothing observes this today, for two reasons that are both circumstantial: the context is
request-scoped and dies with the request, and a replay only happens on a transient database failure
in the first place. That is a property of the hosting model, not of the executor, so it is the sharp
edge waiting for whoever reuses a context across units of work, moves the port onto a long-lived or
pooled context, or adds a second handler to it whose operation is not safe to run twice. The port's
own XML documentation says the same thing at the call site: the operation must be safe to repeat and
must not depend on state left behind by an earlier attempt.

## Alternatives considered

- **Defer the first repository's write into the second one's `SaveChanges`.** Rejected. It turns the
  first repository's method into a staging API — one whose name promises a persisted row while
  returning an untracked intention — and it moves each repository's constraint handling out of the one
  place that understands it into a retry around a combined save, which must then also fix up whatever
  the second write needed from the first before saving again. Strictly hairier code guarding the same
  rule, buying atomicity by making the failure path harder to read.
- **A full Unit of Work: repositories stop calling `SaveChanges` and handlers call it once.**
  Rejected. A single terminal save means a single place where every constraint violation surfaces, so
  every constraint-to-message translation has to live there and discriminate by constraint name on
  behalf of entities it knows nothing about. That is the opposite of
  [ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md)'s restatement rule, which puts the
  sentence a person reads next to the rule it explains — `AccountRepository`'s duplicate-name message,
  `BudgetRepository`'s translation of `23505` into `false`, and `PayeeRepository` answering the *same*
  index with two different statuses because a create's remedy and a rename's differ. It would also
  make the change far larger than the defect: every repository and every handler in the solution, to
  fix the handlers that write twice.
- **Leave the window open and document it.** Rejected. The usual argument for documenting a narrow
  race — that the consequence is cheap and self-correcting — does not hold for the writes this port
  covers. Erasure's is permanent in the strongest sense available: the rows it destroys are the ones
  nobody can reconstruct, and there is no later request that repairs a half-finished one.

## Scope: when one save beats this port

The port exists for handlers that write through more than one **repository**. Two writes that share
one repository and one `DbContext` do not need it, and registration is the case that proves the
distinction: `IRegistrationRepository.RegisterAsync` adds the account, its three credentials, the
passkey's material, the ten recovery-code hashes, the eleven wrapped-key rows, the budget, the
session and its token, and saves **once**. Reaching for `ITransactionalExecutor` there would be the
obvious move and the weaker one — a single save is already atomic, so the execution-strategy retry
loop guards nothing,
and it keeps the `23505` attribution in one `catch` rather than splitting it across two writes that
can each fail for a different reason. The rule is therefore "one transaction per handler", not "one
`ITransactionalExecutor` per handler": when a single save already spans everything that must land
together, that *is* the transaction.

## Consequences

- **Any future handler that writes through more than one repository has this port available and
  should use it.** The problem it solves is structural, not specific to payees: two repositories mean
  two `SaveChanges` calls, and the second one can fail.
- **Both transaction handlers have since *lost* their boundary, and that is this port working rather
  than being abandoned.** `CreateTransactionHandler` and `UpdateTransactionHandler` each used to write
  two rows — a payee found-or-created from a name, and the transaction that named it — and each was
  wrapped for exactly the reason above. Sealing `payees.name` took the server's ability to resolve a
  name to a row, so creating a payee became a request of its own and each handler now performs exactly
  **one** `SaveChanges`. Neither takes `ITransactionalExecutor` any more, and neither should: a
  transaction around a single save commits exactly what the save commits and reads to the next author
  as though something there needed atomicity. **The rule is "one transaction per handler that writes
  twice", so a handler that stops writing twice gives the boundary back.** What that trade accepts —
  a payee created by one request and orphaned by the next one failing — is argued in
  [payees.md](../business-logic/payees.md#edge-cases--known-gotchas); no server-side transaction can
  span two requests, so this port was never the answer to it.
- **The port does not make handlers atomic by default.** A handler that does not call it keeps the
  old shape, one commit per repository. There is no interceptor, no decorator and no ambient
  transaction per request, deliberately: a request-wide transaction would hold a connection for the
  whole request and would silently widen every future handler's boundary. Atomicity is opted into,
  which means the absence of the call is something a reviewer has to notice.
- **Registration is deliberately not wrapped, and there it is fatal rather than merely redundant.**
  `RegisterAccountHandler` writes the account, its budget and everything that guards it in a
  **single** save, so this port has nothing to make atomic. It is also the one path that must not
  reach for it: the identity is published immediately before the insert, and `BeginTransactionAsync`
  opens the connection — which is when `SessionContextInterceptor` writes `app.current_user_id` — so
  a delegate wrapping the publication configures the connection while the setting is still empty and
  every policed statement in the save meets `''::uuid`. The reasoning is in
  [registration.md](../business-logic/registration.md); the existence of this port is not a reason to
  revisit it.
- **The executor is one more thing a unit test of a handler has to supply.** It is an interface with
  a single method, so a pass-through fake is a line long, but a handler that gains the dependency
  gains it in every test that constructs it.
