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
A user-owned table carrying `user_id` — server-side sessions and wrapped encryption keys are both
specified to be exactly that — would under a single-axis rule have had to be argued about
individually, at the moment it was added, by whoever happened to add it. See the Consequences for
the sharp edge on that claim: it covers a new **table**, and says nothing about a new **column** on
a table already exempt.

## What this reverses, and why the original reason no longer holds

ADR 0005's exemption rested on a premise about the **code**, not about the problem.

Only `credentials` is genuinely read to discover who is asking. `users` was reached at all because
the discovery lookup joined `credentials` to `users` in a single statement — and then discarded the
user, keeping nothing but `.Id`. The join proved nothing that `credentials.user_id`, a `NOT NULL`
foreign key to `users.id`, does not already prove. Reading the credential alone yields the same id
and touches no policed table.

With discovery no longer reading `users`, the identity is known before any statement that needs it,
including on registration. `RegistrationAccountId.For` derives the id from the ceremony's own spent
challenge on the application side, so the id exists before the row does: the handler publishes it,
and the `users` `INSERT` is then checked against an `app.current_user_id` that already names the row
being inserted.

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

**Coverage is settled by ownership rather than by one column.** Every relation in `public` that can
hold or expose rows — `relkind` in `r`, `p`, `v`, `m`, `f` — lands in exactly one bucket:

1. **Exempt** — a written exemption names it, so a deliberate decision is never overridden by a
   column, and this is the escape hatch for every bucket below.
2. **Budget-owned** — carries `budget_id`; owes exactly one `budget_isolation`.
3. **User-owned** — is `users`, or carries `user_id`; owes exactly one `user_isolation`.
4. **Unclassifiable** — neither column, so nobody can say which of the two it owes.
5. **Unpoliceable** — a view, materialized view or foreign table, which cannot carry an enforced
   policy at all.

Rule 2 outranks rule 3 deliberately: a budget belongs to exactly one user, so `budget_id =
current_budget` is strictly narrower than `user_id = current_user`, and a table carrying both must
be protected by the narrower rule rather than by whichever check ran first. `users` is named
literally in rule 3 because the row that *is* the user has no `user_id` column — one hardcoded name
in the only direction that is safe, since it can add a subject but never skip one.

The fourth and fifth buckets are **failures**, not shrugs. ADR 0005's coverage test argued there
was no third bucket and that this was the point; that argument was right while one policy name
existed. With two, a table carrying neither ownership column cannot be told what it owes, and
inventing an answer would be guessing. The fail-closed property is preserved — both are red — and
each failure message has to say *why* and name the ways forward. Without that, the next contributor
closes a red deploy by exempting a relation that needed a policy.

**The subject is every relation that can hold or expose rows, not every ordinary table.** Narrowing
discovery to `relkind = 'r'` was this mechanism's own fail-open shape, reintroduced one layer up. A
**view** is not a table but it exposes rows, and unless `security_invoker` is set it runs with its
**owner's** privileges — the owner being the schema owner, who bypasses row-level security — so a
view granted to the application role reads every tenant while coverage reports green. A
**materialized view** cannot be policed at all. A **partitioned table** is the worst shape: its
partitions are `'r'` and visible, its parent is `'p'` and was not, and PostgreSQL applies the
*parent's* policies to queries routed through the parent — so the invisible relation is precisely
the one whose policies fire. Index, sequence, composite type, TOAST table and partitioned index
stay out because they expose no rows of their own, which is the test any future narrowing must
pass.

**Views are refused outright, with no `security_invoker` exception in the verifier.**
`security_invoker = true` does make a view safe, but it is a reloption one `ALTER VIEW` away from
being flipped back, and asserting it would have the gate guarding a property that is not a policy.
The escape is the written exemption list, the same escape everything else here uses: a future view
goes into `Exemptions` with a reason, and a human reads that line in review. **Rejected — refusing
only the views the application role is granted on.** It needs `aclexplode` over `relacl` with
grantee 0 standing for `PUBLIC` and role membership resolved, and *that* query silently matching
nothing is fail-open — this defect's own shape, reintroduced as a second mechanism. Making the
grant irrelevant answers the objection without the query.

**A column that decides tenancy must be `NOT NULL`, and the gate enforces it.** Under `user_id =
current_user` a row whose owner is NULL is invisible to every session: fail-closed, so not a leak,
but undiagnosable — the row exists, no one can reach it, and nothing says why.

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

**The deploy verifier reads a policy's content, not only its identity.** Name and bound role were
the first strengthening: previously a renamed policy satisfied the count check and shipped. The
role check is "binds the application role" rather than "names it and nothing else", because a
policy written `TO PUBLIC` binds every non-owner role and is therefore broader, not weaker.

Identity alone was still not enough. A policy declared `FOR SELECT` instead of `FOR ALL` carries
the right name, binds the right role, counts as exactly one — and leaves `INSERT`, `UPDATE` and
`DELETE` entirely unconstrained on tables the role holds those grants on. So does `USING (true)`.
So does a policy declared `AS RESTRICTIVE`, which grants no access on its own and only narrows what
a permissive policy already allowed, meaning a table whose sole policy is restrictive has no rule
granting anything. The gate therefore also requires the policy to be permissive and `FOR ALL`, and
requires its `USING` expression to name the two things it cannot work without: the session setting
the policy is keyed on, and the ownership column the table's tenancy turns on.

Content is matched **structurally, never against an expected string**. `pg_get_expr` emits
normalized SQL, so an equivalently rewritten body must not refuse a legitimate deploy. The
ownership column is matched on a **word boundary** rather than as a substring, and that is not
fussiness: for `users` the required column is `id`, which is a substring of `budget_id` and of
`app.current_user_id`, so a substring test passes a policy naming no ownership column at all —
vacuous on precisely the table this decision added. `_` is a word character, so `\bid\b` matches
the standalone reference and nothing inside `budget_id` or `current_user_id`.

`WITH CHECK` being absent is **safe** — PostgreSQL reuses `USING` for the check — so requiring it
would be wrong. The rule is that a `WITH CHECK` which is *present* must be textually identical to
`USING`; both strings come from the same normalizer on the same server, so an equivalent rewrite
normalizes identically on both sides. It is deliberately conservative in one direction: a
legitimately narrower `WITH CHECK` would be refused. None exists or is planned, and relaxing it
would be a visible decision.

**The limit of these checks, stated rather than left to be discovered.** They catch drift,
accident, and a hand-edit that weakened one clause. They are not proof against a deliberately
crafted wider-but-plausible predicate — `… OR true` passes every rule above. That is outside the
threat model, because anyone who can rewrite `pg_policy` can `DISABLE ROW LEVEL SECURITY` instead.

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

That claim has a load-bearing precondition, and it is a property of the middleware order rather
than of the schema: it holds only while no policed statement runs inside a transaction opened
*before* the identity is published. True today because `ITransactionalExecutor` wraps command
handlers and never provisioning, and because EF opens and closes the connection per operation. Wrap
provisioning in a transaction and the connection opens once with both settings empty — still loud,
but loud at every request rather than none, so it is a break to notice rather than to survive.

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
  That is the intended failure mode, and `AccountRegistrationTests` is what notices it.
- `IX_users_email` and `IX_budgets_user_id_name` are unique indexes, and indexes are not
  policy-aware. `RegisterAsync` still receives `23505` against a row the session cannot see, so the
  `RegistrationOutcome` refusal path is unchanged. A reader may expect row-level security to hide the
  conflict; it does not. `IX_users_email` is therefore an account-enumeration channel, and it is
  unexploitable **only** while the email arrives inside a provider-verified token, so a caller can
  probe no address but their own. Whichever story lands the email-change flow owns re-arguing that,
  because it is the story that ends the precondition.
- **The classifier fails closed on a new *table*. It says nothing about a new *column* on a table
  that is already exempt** — so the exemption itself now records the column set its reason was
  argued over, and `credentials` growing a column goes red until someone answers for it. The
  structural cause outlives this decision: **the exemption was granted to one query but applies to
  a whole table**, and PostgreSQL offers no finer grain.

  Be precise about what is at stake, because overstating it is how a rule gets ignored. A passkey's
  **public key is public** and its signature counter is not a secret; both leaking across sessions
  is an enumeration and correlation surface, not a credential compromise, and neither breaks
  NFR-015. What does matter is what arrives next to them: each recovery factor stores its own
  wrapped copy of the content and index keys, and a registered passkey *is* a recovery factor. Those
  are AEAD ciphertext under a key-encryption key the deployment never holds, so cross-session
  readability still does not yield plaintext — but handing every application session the ciphertext
  of every account, alongside the hash of every recovery code, is the opposite of what a product
  whose thesis is "the operator cannot read your records" should do by default.

  **The recovery-code hash in that sentence has stopped being hypothetical, and it landed on a table
  of its own.** `recovery_code_hashes` is exempt for a reason of the same shape — the row is found by
  the hash on an anonymous redemption, before anybody has said who they are — and carries its own
  pinned column set
  ([ADR 0016](0016-give-recovery-code-hashes-their-own-exempt-table.md)). Keep the example above
  where it is: the hypothetical is what made this decision visible *before* there was anything to
  decide about, so it is the evidence the mechanism worked rather than a line to retire now that it
  has been used once. The wrapped key beside it is still ahead of us, and it gets argued the same way.

- **Where the cut belongs, so the next story does not have to re-derive it.** The exemption is not
  removable: a WebAuthn assertion verifies a signature with the stored public key *before* it knows
  whose account it is, so those columns genuinely have to be reachable with no identity on the
  session. The fix is to make the exemption's **scope match its reason** — the exempt table holds
  what answers *who is asking* and *is this really them*, and everything read **after** that answer
  lives on a table carrying `user_id`, which the classifier then catches by itself. That is the
  identity / key-custody split the requirements already draw, expressed as a table boundary:
  discovery columns stay, wrapped keys leave. A red on the pinned column set is therefore an
  instruction to **move the column**, never to append its name to the list.
- **A trap for whoever re-argues it.** The obvious resolution — a policy admitting a row when the
  session names nobody *or* names its owner — satisfies discovery and breaks registration under a
  race. `RegisterAccountHandler` publishes the new account's id *before* `RegisterAsync`, so when
  that insert loses the race the re-read of `credentials` runs under the identity of a row that was
  never
  written: such a policy sees a session naming somebody, returns nothing, and the handler reports
  an email conflict where the truth is a lost race on the subject. Resolving the exemption
  therefore requires reordering publication, not merely writing a policy.
- **The phantom identity on the conflict path is deliberate.** When `RegisterAsync` loses and no
  winning credential is found, the id published before the insert stays in request scope naming a
  row that was never written. Nothing reads it — the request ends in a 409 — and anything that did
  would fail closed, since every policed statement it could reach returns zero rows or `42501`.
  Clearing it would mean a second identity-mutating capability on the one interface this decision
  narrowed to a single constructor, which costs more than the state it removes.
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
- A future view, materialized view or foreign table in `public` is red until someone exempts it
  with a written reason — including one nobody granted the application role on. That is the
  intended cost of not consulting grants.
- An ownership column added as nullable is refused at the gate rather than at review.
