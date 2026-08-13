# Data Isolation Invariant

> Read this before touching budget-scoped queries.

**A row must never be visible to a budget it doesn't belong to, nor to a person who does not own
it.** Two axes, because two things own rows: the money data belongs to a budget, and the identity
rows belong to a user. Both are enforced in layers; the bottom one is PostgreSQL row-level
security, and the layers above it exist for error quality. Keep all of the following true.

Enforced today:
- **Row-level security, on both axes.** A `budget_isolation` policy on `accounts`,
  `category_groups`, `categories`, `payees` and `transactions` compares `budget_id` against the
  session's ambient budget, and a `user_isolation` policy on `users`, `budgets`, `sessions`,
  `passkey_signature_counters` and `wrapped_account_keys`
  compares `id` and `user_id` against the session's authenticated user — each in both `USING` and
  `WITH CHECK`, so the connection every request is served by reaches no other tenant's rows and can insert into
  no tenant but its own, whatever produced the statement. `SessionContextInterceptor` puts both
  `app.current_user_id` and `app.current_budget_id` on each connection the context opens, in one
  round-trip. The policies live in `Infrastructure/Persistence/Provisioning/app-role-grants.sql`,
  never in a migration ([ADR 0005](../decisions/0005-isolate-budget-owned-rows-with-row-level-security.md),
  [ADR 0011](../decisions/0011-police-the-user-owned-tables.md)).
  **A new tenant-owned table needs a grant *and* a policy**: the grants are fail-closed, so a
  missing one fails loudly with `42501`, but RLS is fail-**open** — a granted table with no policy
  is readable across every tenant, silently. `tests/IntegrationTests/RlsCoverageTests.cs` reads the
  live schema and requires **every relation in `public` that can hold or expose rows** to be
  accounted for: an ordinary or partitioned table policed with the policy its ownership calls for,
  or an explicit exemption carrying its reason. Two refusals rather than one. A table carrying
  neither `budget_id` nor `user_id` is refused because "we forgot" and "it needs nothing" produce
  the identical catalog. A **view, materialized view or foreign table is refused outright** — a
  view runs with its *owner's* privileges unless `security_invoker` is set, and an owner bypasses
  RLS, so a granted view over a policed table reads every tenant; a materialized view cannot be
  policed at all. Index, sequence, composite type and TOAST table stay out because they expose no
  rows of their own, which is the test any future narrowing must pass. That direction is the whole
  point and must not be inverted — a list of *policed* relations fails open, because the one nobody
  added to it keeps the suite green. Exempt today, six of them: `credentials` (read to discover *who is
  asking*, so a policy keyed on the identity it resolves would refuse the query that resolves it),
  `passkey_public_keys` (read to decide whether an assertion's signature is genuine, which a WebAuthn
  ceremony must answer *before* it knows whose account it is), `recovery_code_hashes` (found by the
  `SHA-256` of the verifier on an **anonymous** redemption request — somebody redeeming a code has lost
  the authenticator that would have proved who they are, so the lookup by hash is what establishes the
  identity, and a policy keyed on `app.current_user_id` would refuse the very query that produces the
  value it wants to compare against; it would refuse it *loudly*, because an unset setting reaches the
  policy as `''::uuid` and raises `22P02` —
  [ADR 0016](../decisions/0016-give-recovery-code-hashes-their-own-exempt-table.md)),
  `webauthn_challenges` (a nonce
  belonging to a ceremony rather than to a person — the sign-in leg issues one before anybody has
  said who they are, so there is nobody for a policy to key on), `currencies` (reference data owned
  by no tenant), and `__EFMigrationsHistory`. The same list and the same classification are what the
  deploy-time verifier reads, so the gate and the test cannot drift apart.
  **The `recovery_code_hashes` exemption is exercised by the query it was written for**: `POST
  /api/recovery-codes/redemption` matches a row by the `SHA-256` of the verifier it was handed, on a
  connection naming nobody, and publishes the account that row carries before it opens a transaction.
  The classifier reaches its verdict from the table's own columns either way, so it demanded a decision
  the moment the table existed whether or not anything read it.
  `passkey_signature_counters` is the counterexample that keeps the `passkey_public_keys` exemption
  honest: it is the *same ceremony* one step later, reached only after the signature has verified, so
  it carries
  `user_id` and is policed with no rule added
  ([ADR 0012](../decisions/0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md)).
- **An exemption records the columns its reason was argued over, and `credentials` growing one goes
  red.** The exemption is granted to a *query* — the one that discovers who is asking — but
  PostgreSQL applies it to a whole *table*, so without this a column read only after authentication
  could land beside the discovery columns and be readable by every session. Going red means **move
  the column** to a table carrying `user_id`, which the coverage rule then polices by itself; it
  never means appending the name to the pinned list. `passkey_public_keys`, `recovery_code_hashes` and
  `webauthn_challenges`
  pin their columns for the same reason, and the pin — not the grant matrix — is what holds each
  exemption to its stated reason. The absent `UPDATE` and `DELETE` on `passkey_public_keys` stop
  *mutable* per-user state accumulating, which is real but is not the threat: a wrapped key or a
  recovery-code hash is written once and never updated, so it would satisfy any append-only rule
  while being precisely what must not sit on a table every session reads in full. **Neither of those
  two is hypothetical any more, and both landed on tables of their own** — the recovery-code hash on
  `recovery_code_hashes`, and the wrapped key on `wrapped_account_keys`, which carries `user_id` and
  is policed by `user_isolation` with no rule added. The example stays because both arrived exactly
  where the pin said they would, which is the pin working. `recovery_code_hashes` pins the columns its
  own anonymous lookup needs before an identity exists — the hash it is found by, and the credential,
  user and type the redemption then adopts — and the wrapped key it was offered first is read *after*
  redemption has answered who is asking, so it went to the policed table instead.
  `currencies` and
  `__EFMigrationsHistory` pin nothing on purpose — the first belongs to no tenant whatever columns it
  grows, the second has its shape owned by EF.
- **An exempt table scopes nothing, so the application is the only thing scoping access to it — and
  some of those accesses destroy rows.** On the exempt tables that carry an owner column —
  `credentials`, `passkey_public_keys` and `recovery_code_hashes` — one query per table is allowed to
  omit it, the one that discovers who is asking, and every other **read** must
  carry its own `where user_id = …`, exactly as `FindFirstForUserAsync` does on `budgets`. **The
  destructive statements are an exception to that sentence and not to the rule**: EF issues each of
  them by primary key, with no owner predicate in the statement at all, and what scopes one is the
  owner-bearing read that produced its entity inside the same transaction — spelled out below and in
  [ADR 0014](../decisions/0014-scope-the-credential-delete-in-the-application.md). On
  `recovery_code_hashes` that one query is the discovery lookup a redemption is matched by, and the
  `DELETE` beside it is scoped by a **second** read of the same row, inside the transaction that
  spends it, naming the account the first one resolved. The two reads answer two different questions:
  the discovery lookup decides **whose account this is**, the re-read decides **which row
  disappears**, and the owner predicate is what makes the second an answer to the first. What makes
  the discovery lookup sound rather than merely unscoped is that the row is named by the digest of a
  256-bit secret the caller presented in full.
  `webauthn_challenges` is the exception to the sentence rather than to the rule: it has **no** owner
  column at all, because a nonce belongs to a ceremony rather than to a person, so there is nothing
  for an access to be scoped by and the pinned column set is what keeps it that way. The whole
  inventory, because a list is the only way to see that it is complete:

  | Table | Access | Scoped by |
  |---|---|---|
  | `passkey_public_keys` | the discovery lookup on sign-in | **nothing, deliberately** — it runs before there is an identity to key a filter on |
  | `passkey_public_keys` | the `excludeCredentials` read in the registration ceremony | `where user_id`, watched by `RegistrationOptions_ForOneAccount_ExcludeNoOtherAccountsCredential` |
  | `passkey_public_keys` | `FindByWebAuthnCredentialIdForUserAsync` on the re-authentication gate | `where user_id`, watched by `ErasureReauthenticationTests.Erasure_WithAnotherAccountsPasskey_IsRefusedAndErasesNeitherAccount` |
  | `passkey_public_keys` | `INSERT` at registration | the credential it hangs off, written in the same save |
  | `credentials` | the provisioning lookup resolving a `sub` claim | provider and subject, which name a principal rather than an account |
  | `credentials` | `FindPasskeyCredentialAsync`, on the **sign-in assertion** and on revocation | `where user_id`, watched by `Revocation_OfAnotherAccountsCredential_IsRefusedAndRemovesNeitherAccountsRows`; and `type`, watched by `Revocation_OfTheFederatedCredential_IsRefusedAndRemovesNothing` |
  | `credentials` | `CountPasskeysForUserAsync`, behind the last-passkey rule | `where user_id` and `type`, watched by `Revocation_OfTheOnlyRemainingPasskey_IsRefusedWithConflictAndRemovesNothing` |
  | `credentials` | `ListForUserAsync`, behind `GET /api/me/credentials` | `where user_id`, watched by `Credentials_ForASecondAccount_ListThatAccountsCredentialsAndNotTheFirsts` |
  | `credentials` | `INSERT` at provisioning, and again at passkey registration | the owner is a value the application supplies, not one it filters by |
  | `credentials` | `FindRecoveryCodeCredentialAsync`, on a generation and on a redemption | `where user_id` and `type` — and on the redemption the owner is the one the matched code named, never the request's |
  | `credentials` | **`DELETE`**, revoking a passkey, and again replacing a recovery-code set | the owner-scoped read above it, and nothing else — `FindPasskeyCredentialAsync` for the first, `FindRecoveryCodeCredentialAsync` for the second, each carrying owner **and** type |
  | `recovery_code_hashes` | `FindByVerifierHashAsync`, the discovery lookup on redemption | **nothing, deliberately** — it runs before there is an identity to key a filter on, and the account it answers is the one the redemption then adopts |
  | `recovery_code_hashes` | `FindOwnedByVerifierHashAsync`, the re-read inside a redemption's transaction | `where user_id` **and** `verifier_hash` — the owner being the account the discovery lookup resolved, and this is the read that scopes the `DELETE` below |
  | `recovery_code_hashes` | `CountRemainingForUserAsync`, behind `GET /api/me/recovery-codes` and again at the end of a redemption | `where user_id` — the account's own on the read, the matched code's on the redemption |
  | `recovery_code_hashes` | **`DELETE`**, consuming the code a redemption spent | the owner-scoped read above it, in the same transaction, and nothing else — never the discovery lookup, whose answer is an account rather than a row to spend |
  | `recovery_code_hashes` | `INSERT` at generation | the credential it hangs off, written in the same save |
  | `webauthn_challenges` | issue, consume, sweep | **nothing, and there is nothing to scope by** — the row names no person |

  Where a row names a test, that test is what would notice the access losing its filter — no layer
  below the application can.

  **Every named test on `credentials` has been watched fail**, under the deletion of the exact clause
  it guards, rather than merely asserted to guard it. That is worth recording because three of them
  were green the day they were written and a test that has only ever been green is not yet evidence.
  What each mutation produces, so the next reader can repeat it: dropping the owner clause from
  `FindPasskeyCredentialAsync` lets one account revoke another's passkey (`200` where `404` was
  wanted); dropping its `type` clause lets an account revoke its **own federated** credential, after
  which its `sub` resolves to nothing and every authenticated route answers `401` — the account is
  permanently unreachable and cannot even erase itself; dropping the owner clause from
  `CountPasskeysForUserAsync` measures the last-passkey floor against every account's passkeys at
  once, so it never fires and the first person to revoke their only passkey locks themselves out.

  The middle one is the reason a test seeding a single account proves less than it appears to: with
  one account in the table, "this account's rows" and "every row" are the same set, and a predicate
  that has stopped filtering looks identical to one that never needed to. Two of the three tests
  above therefore seed a **bystander account** whose rows the operation must not touch, and that
  arrangement is what makes them bite rather than an extra they could be tidied out of.

  **The `credentials` `DELETE` is a destructive statement with nothing beneath the application
  scoping it**, and EF issues it by primary key alone. Two things make that sound, and
  both have to stay true: `credentials.user_id` is immutable, so the binding between an id and its
  owner cannot move between the read that scoped it and the write that used it; and the read and the
  write share one transaction. The delete takes the loaded **entity**, never an id — which is worth
  something only because no source of a `Credential` accepts a caller-chosen id: every public factory
  mints its own, and every query that materializes an existing row carries the owner and the type.
  Adding a source that does not is what review has to catch. See
  [ADR 0014](../decisions/0014-scope-the-credential-delete-in-the-application.md).

  **`recovery_code_hashes` holds an unpoliced `DELETE` too, and it has one caller.**
  `RecoveryCodeRepository.ConsumeAsync` spends the row a redemption matched; replacing a set deletes
  the *set's* `credentials` row instead, and these rows leave by the database's own cascade, running
  with the referencing table owner's privileges rather than this role's. Two consequences follow and
  both matter.
  First, a reader looking for a second caller will not find one and should not add one.
  Second — and this is the sharpest hazard on this page — **an EF cascade into tracked
  `RecoveryCodeHash` copies would silently succeed here.** On `sessions` the identical change-tracker
  mistake dies loudly with `42501`, because that table deliberately holds no `DELETE`; on this one the
  rows would simply leave by the application instead of by the database, the request would answer
  `200`, and **no SQLSTATE would say so**. So the generation path must never materialise the previous
  set's rows, and nothing below the application can notice if it starts to. See
  [recovery-codes.md](../business-logic/recovery-codes.md) and
  [ADR 0017](../decisions/0017-consume-a-recovery-code-by-deleting-its-row.md).
- **The column that decides tenancy must be `NOT NULL`.** Under `budget_id = current_budget` a row
  whose owner is NULL is invisible to every session — fail-closed, so not a leak, but a row that
  exists, that nobody can reach, and that nothing explains. The coverage gate refuses it.
- **The deploy gate reads a policy's content, not only its name.** It requires the single policy on
  a policed relation to be permissive and `FOR ALL`, and its `USING` expression to name both the
  session setting it is keyed on and the ownership column it turns on; a `WITH CHECK` may be absent
  (PostgreSQL then reuses `USING`) but if present must be identical to it. Identity alone was not
  enough: `FOR SELECT` instead of `FOR ALL` carries the right name and leaves every write
  unconstrained, and so does `USING (true)`. The column is matched on a **word boundary**, not as a
  substring — `users`' ownership column is `id`, which is a substring of `budget_id` and of
  `app.current_user_id`, so a substring test would be vacuous on exactly that table.
- **Read-side filter.** `BudgetoidDbContext` defines a global query filter named
  `BudgetIsolation` on `Transaction`, `Account`, `Payee`, `CategoryGroup`, and `Category`, scoped
  to the current `IBudgetContext.BudgetId`. Every LINQ query against those sets is auto-scoped —
  you cannot forget it. Each filter lambda **must** read the DbContext's primary-constructor
  parameter (`budgetContext!.BudgetId`) directly: Roslyn lowers that parameter to an instance
  field, so the lambda closes over `this` and EF re-roots the closure to the context instance
  running the query. A captured local, a static, or a service-locator call would bake the first
  request's budget into EF's cached model. The context is registered **non-pooled**
  (`AddDbContext` + `EnrichNpgsqlDbContext` in `Api/Program.cs`) because pooling forbids scoped
  constructor injection. Repositories deliberately do *not* re-filter by budget
  (`ITransactionRepository.GetAllAsync`), so these filters are the only thing above the policies —
  they turn another budget's row into a correct empty result and the 404 or 400 the API answers
  with. That is error quality, not enforcement; do not delete either layer as duplication.
- **Write-side schema guard.** The filter enforces nothing on a write, so every reference between
  two budget-owned rows is a composite foreign key carrying `budget_id` — `categories →
  category_groups` and `transactions → accounts | categories | payees`, each against an
  `(Id, BudgetId)` alternate key. PostgreSQL rejects a cross-budget reference whatever code path
  wrote it. This proves internal consistency only; *which* budget a write lands in is still the
  filter's and `IBudgetContext`'s job alone.
- **None of the user-owned entities carries a query filter** — `Budget`, `User`, `Credential`,
  `Session`, `PasskeyPublicKey`, `PasskeySignatureCounter`, `RecoveryCodeHash`, and the challenge row.
  The provisioning
  lookup runs before a budget id exists, so every query over `Budgets` must scope by owner explicitly
  — `BudgetRepository.FindFirstForUserAsync` and `ExportReadService.ListOwnedBudgetsAsync`, which are
  the two that exist today and which a third must join rather than assume it is covered; a session and
  a passkey name no budget at all, so there is none to filter them by. That is a statement about the
  *read-side filter* only, and it no longer travels with the coverage exemption: `users`, `budgets`, `sessions`, `passkey_signature_counters` and `wrapped_account_keys` are
  policed on the user, while `credentials`, `passkey_public_keys`, `recovery_code_hashes` and
  `webauthn_challenges` are
  exempt. The first three have to be — reading them is how a request discovers who is asking and
  whether it is really them, so they are the tables reached with no identity on the session at all
  ([ADR 0011](../decisions/0011-police-the-user-owned-tables.md),
  [ADR 0012](../decisions/0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md),
  [ADR 0016](../decisions/0016-give-recovery-code-hashes-their-own-exempt-table.md) — the third for
  the lookup a redemption is matched by, which is all a redemption touches before it knows who is
  asking).
  That is also why the credential lookup projects to `credentials.user_id` and never joins `users`:
  the join would touch the table policed on the very id being resolved.
- **Immutable ownership.** `Transaction.BudgetId` has no public setter and is set only via the
  factory. The query filter is read-side only — `SaveChanges` ignores it — so that immutability is
  what stops the *application* from moving a row between budgets. The database holds the same rule
  independently: `budget_id` is absent from every `UPDATE` column list granted to the application
  role, so a raw `UPDATE` fails with `42501` whatever issued it
  ([ADR 0004](../decisions/0004-connect-as-a-least-privilege-role.md)).
- **Server-assigned ownership.** `BudgetId` comes only from `IBudgetContext`, never from a request
  DTO or route. `CreateTransactionCommand` has no `BudgetId` field; keep it that way. The policies'
  `WITH CHECK` half holds the same rule underneath: an insert can only land in the ambient budget,
  so a future importer or bulk endpoint cannot stamp a foreign `budget_id` even consistently.

Escape hatches the filter does **not** cover. These no longer leak — each one now meets
the policies instead, and a cross-budget read comes back empty rather than populated.
Still do not introduce them on budget-scoped data: an empty result where the code expects
a row is a bug, and a connection that names no ambient budget — or no ambient user, for
`users`, `budgets`, `sessions`, `passkey_signature_counters` and `wrapped_account_keys` — fails
with `22P02` rather than answering. The build enforces this list: `BannedSymbols.txt` (referenced by
`Infrastructure` and `Api`, the only projects with an EF reference) turns each API below
into an RS0030 compile error.
- `IgnoreQueryFilters()` — never on `BudgetoidDbContext`.
- Raw SQL (`FromSqlRaw` / `FromSqlInterpolated` / `ExecuteSql...`) — bypasses the filter; if
  unavoidable, scope by budget explicitly in the SQL.
- `Find` / `FindAsync` — by-key loads bypass the filter and can even return an already-tracked
  other-budget entity with no SQL. Use a filtered LINQ lookup
  (`FirstOrDefaultAsync(t => t.Id == id)`), which returns null for non-owners.
- `Remove(new Transaction { Id = x })` and other by-id mutations on stub entities — delete by
  PK with no owner check. Load through the filtered set first, then mutate.
- `ExecuteUpdate` / `ExecuteDelete` — honor the filter only when started from
  `dbContext.Transactions` without `IgnoreQueryFilters`.
- Future budget-scoped related/owned entities must each carry their **own** query filter.
  (Required navigations can otherwise silently *under*-return via INNER JOIN — a correctness
  bug, not a leak, but worth knowing.)

Tests that lock this: `tests/IntegrationTests/RlsIsolationTests.cs` (raw SQL on the application
role, on both axes, every negative paired with the same statement against the session's own budget
or own user — and, for each exemption resting on *"this is read before any identity exists"*
(`credentials`, `passkey_public_keys`, `recovery_code_hashes`, `webauthn_challenges`), a **positive
control** proving the table
is still readable on a connection naming nobody. On `recovery_code_hashes` that control is
`Database_LetsTheAppRoleDeleteAnyRecoveryCodeHash_OnASessionNamingNobody`, which states the unbounded
half of its `DELETE` grant as well: it goes red the day somebody succeeds in policing that table. That direction needs its own test because coverage
cannot supply it: an exemption says a policy is *not required*, never that one is *forbidden*, so
adding `user_isolation` to `credentials` leaves `RlsCoverageTests` entirely green and surfaces only as
provisioning failing on every sign-in. `currencies` and `__EFMigrationsHistory` need no such control —
their exemption rests on belonging to no tenant rather than on being read before an identity exists, so
a policy landing on either fails loudly on a session that names somebody. `wrapped_account_keys` is
the newest policed table and the one whose grant depends on these tests existing: nothing in the
application reads it yet, so `Database_HidesAnotherAccountsWrappedKeys_FromASessionNamingThisUser`
and `Database_RefusesAWrappedKeyReadOnASessionNamingNobody` are the only statements that have
watched its policy decide anything, and `app-role-grants.sql` names them where it justifies granting
`SELECT` at all),
`tests/IntegrationTests/RlsCoverageTests.cs` (schema-derived, so any
new table without the policy its ownership calls for, or without a stated exemption, fails —
including one carrying neither ownership column, and one that is a view or materialized view),
`tests/IntegrationTests/DeploymentProvisioningTests.cs` (the same rule at the deploy gate,
sabotaged once per failure mode: a dropped policy, a *renamed* one, row security switched off, an
unclassifiable table, a policy narrowed to `FOR SELECT`, one with a trivial `USING`, one keyed on
the wrong session setting, one on `users` naming no ownership column, one whose `WITH CHECK` is
wider than its `USING`, a restrictive one, and a granted view over a policed table),
`tests/IntegrationTests/BudgetIsolationTests.cs` (DbContext-level two-budgets-same-process +
endpoint-level two-factory), the `BudgetId` immutability unit test in
`tests/UnitTests/TransactionTests.cs`, `tests/UnitTests/OwnershipKeyImmutabilityTests.cs` (the same
rule generalised: every `UserId` and `BudgetId` the Domain declares, derived from the assembly and
pinned, so `credentials.user_id` — the immutability the unscoped delete above rests on — is held by
a test rather than by nobody, and a new entity carrying a tenancy key cannot arrive unchecked), and
`tests/IntegrationTests/DataExportRefusalTests.cs` (the
owner-scoped `Budgets` read: a user owning a budget the request is not inside is refused rather than
answered with the part the filters can reach — see [export.md](../business-logic/export.md)).
Removing a `HasQueryFilter` line must make the DbContext-level test fail; removing — or renaming — a policy must make the RLS ones fail, and must
also refuse the next deploy.
