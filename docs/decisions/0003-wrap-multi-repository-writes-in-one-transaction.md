# ADR 0003 — Wrap a handler's writes in one transaction through an Application-owned port

- **Status:** Accepted
- **Date:** 2026-07-29
- **Area:** Architecture / Persistence (Application ports, EF Core transactions, Npgsql execution
  strategy)

## Context

`CreateTransactionHandler` writes two rows when the command carries a payee name: the payee, and the
transaction that named it. Each went to the database on its own. `PayeeRepository.GetOrCreateAsync`
commits its own `SaveChangesAsync`
(`BudgetoidApp/Infrastructure/Repositories/PayeeRepository.cs:33`), and `TransactionRepository.AddAsync`
commits another (`BudgetoidApp/Infrastructure/Repositories/TransactionRepository.cs:12`), with
nothing spanning them. Every repository in this codebase saves for itself, which is convenient until
one handler needs two of them.

A failure between the two saves therefore committed the payee and lost the transaction it existed
for. The consequence is **permanent, not untidy**: nothing deletes a payee. `IPayeeRepository`
exposes exactly one method and `PayeeEndpoints` maps exactly one route, a `GET`, so there is no
handler, no command and no endpoint that could remove the stray row — only a raw `DELETE`, which the
composite `transactions → payees` foreign key refuses as soon as anything references it. The litter
is therefore load-bearing on nothing and removable by nobody, and it weakens a property the domain
would otherwise have: that a payee's existence says a transaction once named it (see
[payees.md](../business-logic/payees.md#edge-cases--known-gotchas)).

The window was not theoretical. The handler's `cancellationToken` is the endpoint's, which is
`HttpContext.RequestAborted`, and it is passed to both saves — so a client closing the tab between
them is enough to produce the orphan, without any database or network fault at all.

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

**The boundary starts after the lookups, not at the top of the handler.** Everything above
`BudgetoidApp/Application/Transactions/CreateTransaction/CreateTransactionHandler.cs:74` — resolving
the account, its currency, the category and the category group, and constructing the `Transaction` —
reads and validates, and commits nothing. Only the two writes below it need to agree, and they are
one logical operation. Opening the transaction earlier would buy no atomicity at all and would hold a
connection, a snapshot and any locks taken along the way for up to four extra round trips. A
transaction boundary is not free insurance; its width is its cost.

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

**EF sets a savepoint before each `SaveChanges` inside an explicit transaction, and that is what
keeps the payee race working unchanged.** `PayeeRepository.GetOrCreateAsync` recovers from a losing
find-or-create by catching the `23505` on `IX_payees_budget_id_name`, detaching the rejected entity
and re-reading the winner
(`BudgetoidApp/Infrastructure/Repositories/PayeeRepository.cs:39-48`). Inside the wrap that flow
becomes: savepoint
→ `23505` → roll back to the savepoint → re-read → carry on, with not a line of it altered. This was
confirmed against a forced collision, not inferred. It is stated here because someone who does not
know the savepoint exists would reasonably conclude the race handling had to be redesigned: without
it, the failed statement would leave the whole unit aborted and every later statement in it —
including the re-read — would fail with `25P02`, `in_failed_sql_transaction`.

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

- **Defer the payee insert into the same `SaveChanges` as the transaction row.** Rejected. It turns
  `GetOrCreateAsync` into a staging API — a method whose name promises a persisted payee while
  returning an untracked intention — and it moves the `23505` race handling out of the one place that
  understands it into a retry around a combined save, which must then also fix up
  `transaction.PayeeId` with the winner's id before saving again. That is strictly hairier code
  guarding the same rule, and it buys atomicity by making the failure path harder to read.
- **A full Unit of Work: repositories stop calling `SaveChanges` and handlers call it once.**
  Rejected. A single terminal save means a single place where every constraint violation surfaces, so
  every constraint-to-message translation has to live there and discriminate by constraint name on
  behalf of entities it knows nothing about. That is the opposite of
  [ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md)'s restatement rule, which puts the
  sentence a person reads next to the rule it explains — `AccountRepository`'s duplicate-name message,
  `PayeeRepository`'s swallow-and-re-read, `BudgetRepository`'s translation of `23505` into `false`.
  It would also make the change far larger than the defect: every repository and every handler in the
  solution, to fix one handler that writes twice.
- **Leave the window open and document it.** Rejected. The usual argument for documenting a narrow
  race — that the consequence is cheap and self-correcting — does not hold here. The consequence is
  permanent, because payees cannot be deleted by any application path, and it accumulates: every
  disconnect at the wrong moment adds a row that nothing will ever remove.

## Consequences

- **Any future handler that writes through more than one repository has this port available and
  should use it.** The problem it solves is structural, not specific to payees: two repositories mean
  two `SaveChanges` calls, and the second one can fail.
- **The transaction edit path needs the same boundary if an edit can mint a payee.** If editing a
  transaction resolves the payee by name, as creation does, then every edit is a two-row write with
  exactly this failure mode — so the port pays for itself twice, and the edit should be written
  against it from the start rather than retrofitted.
- **The port does not make handlers atomic by default.** A handler that does not call it keeps the
  old shape, one commit per repository. There is no interceptor, no decorator and no ambient
  transaction per request, deliberately: a request-wide transaction would hold a connection for the
  whole request and would silently widen every future handler's boundary. Atomicity is opted into,
  which means the absence of the call is something a reviewer has to notice.
- **Provisioning is deliberately not wrapped.** `EnsureUserHandler` writes the user and the budget in
  two saves and relies on a unique index plus a re-read to stay safe under concurrency, and that
  design is load-bearing — the second save is also the heal path for a user left without a budget.
  The reasoning is in [budgets.md](../business-logic/budgets.md#edge-cases--known-gotchas); the
  existence of this port is not a reason to revisit it.
- **The executor is one more thing a unit test of a handler has to supply.** It is an interface with
  a single method, so a pass-through fake is a line long, but a handler that gains the dependency
  gains it in every test that constructs it.
