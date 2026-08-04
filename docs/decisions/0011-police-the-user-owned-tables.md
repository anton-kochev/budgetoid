# ADR 0011 — Police the user-owned tables, and settle coverage by ownership

- **Status:** Accepted
- **Date:** 2026-08-04
- **Area:** Persistence / Security (PostgreSQL row-level security, provisioning, identity
  resolution)

## Context

[ADR 0005](0005-isolate-budget-owned-rows-with-row-level-security.md) put a `budget_isolation`
policy on the five budget-owned tables and deliberately left `users`, `budgets` and `credentials`
outside it. The reason it gave was concrete and, at the time, correct: provisioning reads those
tables *before* an ambient budget exists, so a policy on either would refuse the very query that
decides which budget is ambient and break sign-in for everyone.

The result was that the two tables that name a person were the only two granted tables the database
did not guard. Cross-user isolation rested on application code alone — on `FindFirstForUserAsync`
remembering to write `where user_id = @userId`, and on nobody ever adding a query that forgot. That
is exactly the shape ADR 0005 argued against everywhere else.

There is a second problem, and it is the one that would have cost more. `RlsCoverageTests` — the
test that makes a new table fail closed until someone decides whether it owes a policy — once
discovered its subjects by looking for a `budget_id` column. Every table without one left the
check's scope with nobody deciding it should, which is how `credentials` fell outside row-level
security by accident rather than by decision. Discovery was widened to every table in `public` with
a written exemption list, but the *rule* remained single-axis: one policy name, one ownership kind.
Server-side sessions, passkey public keys and wrapped encryption keys are all user-owned tables
carrying `user_id`, and under a single-axis rule each of them would have had to be argued about
individually, at the moment it was added, by whoever happened to add it.

## What this reverses, and why the original reason no longer holds

ADR 0005's exemption rested on a premise about the **code**, not about the problem.

Only `credentials` is genuinely read to discover who is asking. `users` was reached at all because
the discovery lookup joined `credentials` to `users` in a single statement — and then discarded the
user, keeping nothing but `.Id`. The join proved nothing that `credentials.user_id`, a `NOT NULL`
foreign key to `users.id`, does not already prove. Reading the credential alone yields the same id
and touches no policed table.

With discovery no longer reading `users`, the identity is known before any statement that needs it,
including on registration. `User.Create` mints the id with `Guid.CreateVersion7()` on the
application side, so the id exists before the row does: the handler publishes it, and the `users`
`INSERT` is then checked against an `app.current_user_id` that already names the row being
inserted.

`credentials` keeps its exemption, and it is now the only reason on the list that could not be
argued away. It is the table read to answer "who is asking", so a policy keyed on the identity it
resolves would refuse the query that resolves it. That is a permanent property of what the table is
for, not a property of how the lookup happens to be written.

## Decision

**`users` and `budgets` carry a `user_isolation` policy**, in the same shape as the budget ones,
declared in `app-role-grants.sql` beside them and never in a migration:

```sql
CREATE POLICY user_isolation ON users FOR ALL TO budgetoid_app
    USING      (id = COALESCE(current_setting('app.current_user_id', true), '')::uuid)
    WITH CHECK (id = COALESCE(current_setting('app.current_user_id', true), '')::uuid);
-- budgets: identical, keyed on user_id
```

The `COALESCE(current_setting(…, true), '')::uuid` shape is inherited whole from
[ADR 0008](0008-read-the-ambient-budget-inside-the-policy.md) rather than re-decided. An unset
`app.current_user_id` must fail with the same `22P02` whether the backend has never seen the
parameter or Npgsql's pool reset left it defined-and-empty, and `NULLIF` remains rejected for the
same reason: it would turn "the session named nobody" into "this user owns nothing", which is the
one answer that reads as success.

**The session carries two settings, written on connection open.** `BudgetSessionInterceptor` is
renamed `SessionContextInterceptor` and writes both `app.current_user_id` and
`app.current_budget_id` in a single `set_config` round-trip, in both the sync and async overloads,
unconditionally — `''` when unresolved, never skipped, so a pooled connection cannot carry a
previous request's identity. Connection-opened remains the only correct placement, for the reason
ADR 0005 measured: session state issued as an ordinary statement does not survive pool returns,
retries or rollbacks. ADR 0005 and ADR 0008 refer to this object under its former name.

**Reading the identity and assigning it are separate capabilities.** `IUserContext` exposes
`ResolvedUserId` and a derived strict `UserId`; `IUserContextWriter` exposes `ResolveUser(Guid)`.
The interceptor takes the reader. Had the reader also carried `ResolveUser`, every collaborator
holding it could reassign the request's identity — the exact capability these policies exist to
constrain. Split, that capability is constructor-visible on precisely one class.

**Coverage is settled by ownership rather than by one column.** Every ordinary table in `public`
lands in exactly one bucket:

1. **Exempt** — a written exemption names it, so a deliberate decision is never overridden by a
   column.
2. **Budget-owned** — carries `budget_id`; owes exactly one `budget_isolation`.
3. **User-owned** — is `users`, or carries `user_id`; owes exactly one `user_isolation`.
4. **Unclassifiable** — neither column, so nobody can say which of the two it owes.

Rule 2 outranks rule 3 deliberately: a budget belongs to exactly one user, so `budget_id =
current_budget` is strictly narrower than `user_id = current_user`, and a table carrying both must
be protected by the narrower rule rather than by whichever check ran first. `users` is named
literally in rule 3 because the row that *is* the user has no `user_id` column — one hardcoded name
in the only direction that is safe, since it can add a subject but never skip one.

The fourth bucket is a **failure**, not a shrug. ADR 0005's coverage test argued there was no third
bucket and that this was the point; that argument was right while one policy name existed. With
two, a table carrying neither ownership column cannot be told what it owes, and inventing an answer
would be guessing. The fail-closed property is preserved — unclassifiable is red — and the failure
message has to say *why*, and name both ways forward (give the table an ownership column, or write
down an exemption). Without that, the next contributor closes a red deploy by exempting a table
that needed a policy.

An exemption now declares the ownership it is exempt **despite**, which is what lets the rot guard
survive `credentials` legitimately carrying `user_id`: `currencies` growing `user_id` goes red,
`credentials` growing `budget_id` goes red, and `credentials` staying user-owned stays green. The
original property — an exemption cannot outlive its reason — is preserved and made precise.

**One classifier, read by both the test and the deploy gate.** Discovery, classification and the
exemption list live in `Infrastructure/Persistence/Provisioning/RowLevelSecurityCoverage.cs`.
`RlsCoverageTests` and `DeploymentDatabaseProvisioning.VerifyRowLevelSecurityCoverageAsync` both
read it. This is the one place the repository does not prefer deliberate restatement, and the
distinction is between prose and execution: the comment block in `app-role-grants.sql` is a human
restatement for whoever edits that file, but a second *executed* list has no adjudicator when the
two disagree, and the one that loses stops noticing a table. That is not hypothetical — the
verifier kept its own copy of the column filter, so it could not see `users` at all and would have
reported full coverage on a schema where every person's row was reachable by a session naming
somebody else.

`Classify` still takes the exemption set as a parameter rather than reaching for the list, which is
what keeps it testable instead of merely trustworthy: a test classifies the same live schema
against an empty set to prove discovery is still unfiltered.

**The deploy verifier is strengthened while it is being widened.** It now also refuses a policy
whose *name* is not the one that table's ownership requires, and a policy binding neither
`budgetoid_app` nor `public`. Previously a renamed policy satisfied the count check and shipped.
The role check is "binds the application role" rather than "names it and nothing else", because a
policy written `TO PUBLIC` binds every non-owner role and is therefore broader, not weaker.

## Alternatives considered

**Key the policy on `budgets` to the ambient budget** (`id = current_budget_id`). Rejected twice
over: it would refuse `FindFirstForUserAsync`, which runs before a budget is ambient, and as a
second permissive policy it could only ever widen what another allows.

**Have the middleware mint the candidate user id and pass it into the command.** Rejected. It moves
a domain decision — what a user's id is — into the composition root, and still needs a second
publication on the returning-user path, so it buys nothing for the complexity.

**Publish the identity with a mid-request `set_config` as well, for safety.** Rejected. It would
put the session's identity on a statement issued outside connection-open, which ADR 0005 measured
as unsurvivable, and it is unnecessary: every way the connection-open assumption can break — a
future transaction spanning the whole of provisioning, for instance — breaks **loudly**, with
`22P02` on a read or `42501` on the insert. There is no configuration in which a stale or empty
identity returns the wrong rows instead of an error, which is precisely what the `''::uuid` shape
buys.

**`FORCE ROW LEVEL SECURITY`.** Rejected, unchanged from ADR 0005. Owner and superuser bypass is
load-bearing: migrations run on it, test seeding writes both tenants through it, and every
read-back asserting "the other user's row is untouched" is a question no policed connection could
answer.

**A restrictive second policy on `budgets` combining both axes.** Rejected as premature. Nothing
reads `budgets` after provisioning, so it would narrow nothing today while adding a second rule to
keep in agreement with the first.

**Derive ownership from foreign keys to `users(id)` / `budgets(id)` instead of column names.** More
faithful, and it would survive a differently-named owner column — but it buys that over a naming
convention the schema already keeps everywhere, at the cost of catalog machinery. A table that
breaks the convention lands in the fourth bucket and goes red, which is the behaviour that matters.

## Consequences

- `IUserRepository` no longer returns a `User` from discovery. `FindUserIdByFederatedCredentialAsync`
  returns `Guid?`, which also makes "a later sign-in never refreshes the stored profile" structural
  rather than a comment.
- Any query that touches `users` or `budgets` before the identity is published fails with `22P02`.
  That is the intended failure mode, and `BudgetProvisioningTests` is what notices it.
- `IX_users_email` and `IX_budgets_user_id_name` are unique indexes, and indexes are not
  policy-aware. `TryAddAsync` still receives `23505` against a row the session cannot see, so the
  `ConflictException` path is unchanged. A reader may expect row-level security to hide the
  conflict; it does not.
- `No Reset On Close=true` and `Multiplexing=true` remain forbidden, now for two settings, which
  raises the cost of ever flipping them.
- The new statements need table ownership and nothing more — the same privilege the five existing
  policy blocks need — so the restricted production identity still runs the whole script.
  `NonSuperuserDeploymentProvisioningTests` is the guard.
- Erasure inherits a stronger position than it expected: the `DELETE` grants it still needs will be
  policy-scoped when they arrive.
- A future user-owned table whose owner column is not named `user_id` lands in the fourth bucket
  and goes red. Safe, but it is the case where a contributor is most likely to reach for an
  exemption instead of a rename, which is why the message text is part of the deliverable.
