# Users & Ownership

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

This area covers **who a user is** and how that identity comes to exist. Users are not registered
through a form — they are provisioned transparently from their Google sign-in on their first
authenticated request.

A user owns **Budgets** and nothing else. Everything else — accounts, category groups, categories,
payees, transactions — belongs to a budget, so **the budget, not the user, is the unit of tenancy.**
That invariant and the isolation rules that implement it live in [budgets.md](budgets.md); this area
does not duplicate them. What it does own is the identity, its claims, and the provisioning step that
resolves a Google principal into an internal user together with the ambient budget for the request.

## Key Entities

- **User** — the account owner, identified by a `Guid Id` no external party supplies. It carries no
  identity key of its own: every way of signing in is a **Credential** row instead. It holds an
  `Email` and a creation timestamp, and that is the whole row. Of everything the identity provider
  asserts, only the address is kept — a claim the product does not use is one it does not store,
  because what is never collected never leaks and never has to be erased.
- **Credential** — one way of signing in to an account, carrying exactly one `CredentialType`:
  `Federated` (an external provider vouches for the user) or `Passkey` (the authenticator holds it,
  and no external party is involved). A federated credential names its `Provider` — drawn from a
  dictionary the database enforces, of which `google` is the only member today — and the provider's
  `Subject`, the OAuth `sub` claim, stable, non-empty and at most `Credential.MaxSubjectLength` =
  255 characters. A passkey credential carries neither. An account may hold more than one
  credential, but **at most one of type `federated`**; registering and revoking them is not built
  yet, so today every account is created with exactly one federated Google credential.
- **Email** — a value object wrapping the email string; required, trimmed, and at most
  `Email.MaxLength` = 254 characters. Two `Email` values are equal iff their strings are equal.
  Uniqueness is a **wider** comparison than that equality: `users.email` carries a unique index on
  the `case_insensitive` collation, so at most one user row holds a given address whatever its
  casing. The address is written once, when the account is provisioned, and no later request
  changes it — see the provisioning rule below.

```mermaid
erDiagram
    USER ||--o{ CREDENTIAL : "signs in with"
    USER ||--o{ BUDGET : owns
    BUDGET ||--o{ ACCOUNT : owns
    BUDGET ||--o{ CATEGORY_GROUP : owns
    BUDGET ||--o{ CATEGORY : owns
    BUDGET ||--o{ PAYEE : owns
    BUDGET ||--o{ TRANSACTION : owns
    USER {
        guid Id
        string Email
        datetime CreatedAtUtc
    }
    CREDENTIAL {
        guid Id
        guid UserId
        string Type
        string Provider
        string Subject
        datetime CreatedAtUtc
    }
```

## Constraints

### MUST

- **Data isolation is scoped to a budget, not to a user.** The MUST/MUST NOT rules that define it —
  every account, category group, category, payee and transaction belonging to exactly one budget,
  per-budget name uniqueness and ordering, no response combining budgets — are documented once, in
  [budgets.md](budgets.md#constraints). A user reaches their data only through the budget they own, so
  "a user can only see their own data" is a consequence of budget isolation rather than a separate
  rule.

- **A request must resolve to a real internal user and an ambient budget before it can touch data.**
  - **Why**: Handlers stamp and filter by `IBudgetContext.BudgetId`; without a resolved budget there
    is no tenant to scope to, and a default value would silently point at nothing.
  - **Enforced in**: `BudgetoidApp/Api/Infrastructure/UserProvisioningMiddleware.cs` populates both
    `CurrentUser.UserId` and `CurrentUser.BudgetId`; `HttpContextBudgetContext` surfaces the second as
    `IBudgetContext.ResolvedBudgetId`, and the strict `IBudgetContext.BudgetId` derived from it throws
    `"The ambient budget for the current request has not been resolved."` if the budget id is still
    null. The nullable accessor is for the paths that legitimately have none — provisioning itself,
    and infrastructure scopes such as health checks — and neither of them touches budget-owned data.
    `CurrentUser.UserId` exists because the middleware needs a request-scoped home for the identity it
    just provisioned — no query filters by it.

- **An authenticated principal must carry `sub` and `email` claims.**
  - **Why**: `sub` is the stable identity key we upsert on; `email` is a required profile field.
    Without them we cannot provision a user.
  - **Enforced in**: `UserProvisioningMiddleware` returns `401` (ProblemDetails, "missing required
    claims") when either is absent.

- **An email address belongs to at most one user, compared case-insensitively.**
  - **Why**: Two rows holding the same address are two people as far as every budget is concerned,
    and the address is the only human-readable thing that identifies a user — a support request, an
    export or a future notification has nothing else to go on. Case is not part of the address for
    this purpose: `Sam@example.com` and `sam@example.com` reaching the same mailbox but occupying two
    rows would be the same ambiguity with an extra step.
  - **Enforced in**: `UserConfiguration` maps `email` to `varchar(254)` on the `case_insensitive`
    collation with the unique index `IX_users_email`, so PostgreSQL refuses the second row whatever
    code path wrote it. `Email.Create` restates the length bound for message quality only, reading
    the same `Email.MaxLength` constant the column is generated from.

### MUST NOT

- **A request MUST NOT reach data outside its ambient budget.** Stated and enforced in
  [budgets.md](budgets.md#must-not) — another budget's row is unreachable under the `budget_isolation`
  row-level security policies, resolves to `null` through the `BudgetIsolation` filter above them,
  and surfaces as a 404 when it was the target of the request or a 400 when it was a reference inside
  one, never a 403.

## Business Rules & Invariants

- **Rule**: A user is provisioned idempotently on sign-in, keyed on the federated credential's
  `(provider, subject)`. What the provider reports on a **later** sign-in changes nothing about the
  stored account.
- **Why**: There is no registration step. The first authenticated request must create the internal
  user; subsequent requests must find the same one without ever creating duplicates. The provider's
  role ends there. It vouched for this person once, and that is not standing authority to rewrite
  what the account holds — an address the user never asked to change is not an address they can be
  reached at, and silently adopting one would move the account's only human-readable identifier
  because a token said so.
- **Enforced in**: `EnsureUserHandler` (`Application/Users/EnsureUser/EnsureUserHandler.cs`),
  invoked by `UserProvisioningMiddleware`; it returns `ProvisionedUser(UserId, BudgetId)`. The
  existing-user branch resolves the id and returns — there is no write on that path at all. The same
  handler then find-or-creates the user's default budget, because "an account exists ⇒ it has its
  budget" is one idea and splitting it would open a window where a user exists with no budget; that
  half of the step is documented in
  [budgets.md](budgets.md#business-rules--invariants) and not restated here.
- **Example**: A returning user whose Google address changed from `old@example.com` to
  `new@example.com` signs in. The handler finds her by `(provider, subject)`, returns the same
  account, and the stored address stays `old@example.com`.
  `EnsureUserHandlerTests.EnsureUser_ReturningUserWhoseProviderEmailChanged_KeepsTheRegisteredEmail`
  pins it, at both the handler and the database level.
- **Counterexample**: Keying on `email` instead of the credential would break if the user changed
  their Google email — they'd be provisioned as a brand-new user and lose access to all their data.
  Which is also why the address is not refreshed: the credential is the identity, so a changed
  address is new *information about* the account, not a new account and not a fact the account must
  adopt.
- **Consequence, accepted**: the stored address goes stale, and there is no way to update it yet.
  Changing it is its own operation, requiring its own fresh authorization exchange, and that is not
  built.
- **Source**: `[SOURCE: discussion — 2026-08-03]`

---

- **Rule**: A credential's **identity columns** — `user_id`, `type`, `provider`, `subject`,
  `created_at_utc` — are immutable. On `users`, `Email` is the only column that can change.
- **Why**: The credential is the identity anchor — repointing its subject would silently hand an
  account to a different principal, and changing its `user_id` would move a sign-in between
  accounts. That identity is written whole at registration and has no edit that means anything. The
  address is the one column on `users` an edit could ever legitimately touch — the grant is what an
  edit *may* reach, and today no code path reaches it at all.
- **Scope, stated precisely because it is about to matter**: today the identity columns *are* every
  column of `credentials`, so the table has no `UPDATE` grant at all. That is the current state of
  the list, not a property of the table. A passkey signature counter and a last-used timestamp are
  both specified; each arrives as a column that goes **on** the list while the five above stay off
  it. Anyone reading "a credential is never written" rather than "a credential's identity is never
  rewritten" will read the first counter update as a violation of a rule that was never claimed.
- **Enforced in**: **database-owned, restated in the domain.** The application role has no `UPDATE`
  grant on `credentials` of any shape — not a column list with nothing on it, but no grant at all —
  and no `DELETE` either, so every write except `INSERT` is refused with `42501` on the connection
  every request is served by. On `users` the `UPDATE` grant names `email` alone, leaving
  `created_at_utc` immutable by *omission* rather than by a `REVOKE`, which additive column
  privileges could not express. A one-column list is still a list, and must not be "simplified"
  into a table-wide grant; see
  [ADR 0004](../decisions/0004-connect-as-a-least-privilege-role.md).
  `AppRoleGrantsTests.Database_RefusesEveryUpdateOnACredentialsIdentity_WhileStillAllowingInsert`
  pins the refusals column for column against a permitted insert, and
  `Database_RefusesToChangeAUsersCreatedAt_WhileStillAllowingProfileEdits` pins that the users grant
  really is a list. Above them, neither `Domain/Users/User.cs` nor `Domain/Users/Credential.cs`
  exposes a mutator: both are written whole and never edited.
- **Example**: nothing in the application can change a stored email, so the 409 on the insert path
  is the only outcome a duplicate address can produce.
- **Gap, stated rather than hidden**: the `users` `UPDATE` grant now has no caller. It is a
  privilege the role holds and nothing exercises, which is the opposite of how the rest of this
  matrix is built. It stays because the gated email change and the erasure scheduling that need it
  are both specified and both next; if either slips, the grant should be revoked rather than left
  standing.
- **Source**: `[SOURCE: discussion — 2026-07-29]`

---

- **Rule**: An email must be present (non-blank, trimmed) and at most 254 characters. Format is
  **not** validated. A credential's `Subject` is bounded at 255 characters and its `Provider` at 50.
- **Why**: The email comes from a trusted Google ID token, which has already verified it — a regex
  check would add friction without adding trust. Presence is still required because it is the one
  channel by which the product can reach its user. The bounds are what the values are: 254 is the
  practical RFC 5321 address limit (the 256-octet path less the enclosing angle brackets) and 255 is
  Google's documented cap for the `sub` claim. The domain restatement exists so an over-long value
  is a 400 with a sentence, rather than a raw `22001` from the column surfacing to the caller as
  a 500.
- **Enforced in**: the `varchar(254)` column declared in `UserConfiguration`, `varchar(255)` and
  `varchar(50)` in `CredentialConfiguration`; the domain restates each bound in `Email.Create` and
  `Credential.CreateFederated` so the caller gets a 400 with a sentence instead of a database error.
  The two expressions of each bound cannot drift, because each configuration reads `Email.MaxLength`,
  `Credential.MaxSubjectLength` and `Credential.MaxProviderLength` rather than repeating the numbers.
  `Provider`'s 50 is now only the column width: the dictionary below rejects every value the length
  bound would have, so no branch in the domain tests it separately.
- **Example**: a 300-character `email` claim is rejected by `Email.Create` with "Email must be 254
  characters or fewer." rather than being silently cut to fit.
- **Related rule**: a federated credential's `Provider` must be a member of a **dictionary**, and its
  `Subject` must be non-empty.
- **Why**: `"Google"` and `"google"` are the same provider to a person and two identities to a
  unique index, so one human ends up with two accounts and neither can see the other's budget. The
  `Subject` half closes a phantom identity: `('federated', 'google', '')` used to be a legal row
  occupying a slot in the unique index, refused only by the domain — and the tests reach this table
  with raw SQL. Note what the fix is **not**: `Credential.CreateFederated` *rejects* a non-canonical
  spelling rather than lowercasing it. Coercing would make acceptable a value the column is about to
  refuse, which [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) rules
  out, and it would be a live bug besides — `FindByFederatedCredentialAsync` trims but does not fold
  case, so a coerced write would store a row its own lookup could never find.
- **Enforced in**: `CK_credentials_provider` (`provider is null or provider in ('google')`) and the
  `length(subject) > 0` term added to the federated arm of `CK_credentials_type_shape`; restated in
  `Credential.CreateFederated` for message quality. The two live in separate constraints on purpose:
  one defect must report one name, because `RepositoryConstraintAttributionTests` pins attribution by
  constraint name. `subject is not null` stays alongside `length(subject) > 0` and is **not**
  redundant — `length(null)` is `null`, and a CHECK evaluating to `null` is satisfied, so dropping
  the null test would silently readmit a null subject.
- **Counterexample**: adding `length(provider) > 0` "for symmetry". The dictionary already refuses an
  empty provider, and two constraints refusing the same row make the reported name nondeterministic.
- **Source**: `[SOURCE: discussion — 2026-08-03]`

---

- **Rule**: An account holds **at most one** credential of type `federated`.
- **Why**: it is what makes "which provider gates this account" a question with one answer. Nothing
  is being built that would add a second, which is the point — this guards against a **bug** on the
  credential-insert paths, and more of those are coming.
- **Enforced in**: the partial unique index `IX_credentials_user_id_federated` on `(user_id) WHERE
  type = 'federated'`, pinned as `CredentialConfiguration.FederatedPerUserIndexName`. It is a
  *different* rule from `IX_credentials_provider_subject`, which says one account per provider
  identity: that one catches two users claiming one Google identity, this one catches one user
  holding two. Both tests exist and neither is a duplicate of the other.
- **Gotcha**: declaring this index made EF's `ForeignKeyIndexConvention` stop generating the plain
  `IX_credentials_user_id`, because the convention backs off as soon as *any* index covers the
  column — uniqueness and filter irrelevant. It is therefore declared explicitly now. Removing that
  declaration would silently leave cascade delete and every read of an account's credentials with
  only a federated-rows-only index.
- **Counterexample**: assuming the column *truncates* to fit. It does not — `varchar(n)` **rejects**
  an over-long value with SQLSTATE `22001`, which is what makes it enforcement in the sense
  [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) means. Contrast
  `numeric(14,4)` on the money columns, which silently rounds an over-precise value and therefore
  enforces nothing, leaving the decimal-places rule domain-owned while separate check constraints
  carry the magnitude half. These three columns are the cleanest illustration in the schema of the
  difference.
- **Source**: `[SOURCE: discussion — 2026-07-28]`

---

- **Rule**: An email address may be held by only one user. A collision on the insert path is a
  **409**. There is no other path on which one can occur.
- **Why**: Identity is the credential row; `email` is what the provider asserted when the account
  was created. A collision that is not a lost race leaves nothing to adopt — no credential carries
  this subject, so the person behind it is new, and failing closed with a legible 409 is the honest
  answer. Nothing updates `users.email` after the insert, so a returning user cannot collide with
  anyone: the only write that could breach the index is the one that creates the account.
- **Enforced in**: three layers, deliberately. The unique index `IX_users_email` on the
  `case_insensitive` collation is what makes the rule *true*. `UserRepository` reports the rejection
  without interpreting it: `TryAddAsync` writes the user row and its first credential in one save and
  returns `false` for a unique violation of **either** the email index or the credential's
  `(provider, subject)` index, because a losing insert can breach both at once and the database names
  only one. One save is also what keeps a refused insert from leaving a user row behind: an orphan
  would hold its unique email while no credential resolved to it, and every later sign-in with that
  address would 409 with no way to heal. `EnsureUserHandler` is where the treatment is chosen — it
  re-reads by `(provider, subject)`, adopts the winning row when there is one, and otherwise throws
  `ConflictException` ("This
  email address is already linked to a different Google account."), which `ConflictExceptionHandler`
  renders as 409 ProblemDetails. **Whether a collision is a conflict or a lost race is application
  policy, sitting above the database on purpose**: the database rejects the write and cannot even
  say which of the two rules it rejected it for, so only the application can separate a person
  racing themselves from a stranger holding their address.
- **Example**: a person whose Google account was recreated signs in with a new `sub` and their old
  address. Provisioning refuses with 409 and a sentence naming the cause, rather than quietly
  handing them an empty second budget.
- **Counterexample**: deciding the outcome from the reported constraint name — email index means
  409, subject index means a lost race — looks like the precise version of the re-read and is
  unsound: two concurrent first requests from one person insert the same subject *and* the same
  email, so the loser breaches both indexes, and PostgreSQL names whichever of them it checked
  first. The user row is written before its credential, so the email index is the one that reports
  — and that person is told their own address belongs to a different Google account.
- **Source**: `[SOURCE: discussion — 2026-07-28]`

## Workflows & State Transitions

**User provisioning on an authenticated request** (`UserProvisioningMiddleware` → `EnsureUserHandler`):

```mermaid
stateDiagram-v2
    [*] --> Authenticated : request passes authentication
    Authenticated --> Rejected : missing sub or email claim
    Authenticated --> Lookup : has sub + email
    Lookup --> Existing : user found by federated credential
    Lookup --> Creating : no credential found
    Existing --> Resolved : the stored account stands as registered (no write)
    Creating --> Resolved : TryAdd wrote the user and its credential
    Creating --> InsertRejected : unique violation on the credential, the email, or both
    InsertRejected --> RaceReread : re-read by provider and subject
    RaceReread --> Resolved : a credential holds this subject → adopt its user
    RaceReread --> Conflict : no credential holds it → the email alone collided
    Resolved --> BudgetEnsured : find-or-create the user's default budget
    BudgetEnsured --> [*] : CurrentUser.UserId and CurrentUser.BudgetId set, request proceeds
    Rejected --> [*] : 401 ProblemDetails
    Conflict --> [*] : 409 ProblemDetails
```

| Transition | Triggered by | Validations |
|---|---|---|
| Authenticated → Rejected | Auth succeeds but claims missing | `sub` and `email` both required, else 401 |
| Lookup → Existing | A federated credential holds this `(provider, subject)`; its user is the account | — |
| Existing → Resolved | Always, once the credential resolves | None. The branch reads and returns; whatever the token now says about this person is not applied |
| Creating → Resolved | New user and its first credential inserted in one save | `User.Create` validates email presence and both length bounds; `Credential.CreateFederated` validates provider and subject |
| Creating → InsertRejected | A unique violation on the credential index, the email index, or both | `TryAddAsync` returns `false` without deciding which rule fired — the reported constraint name cannot say — and neither row is left behind |
| InsertRejected → RaceReread → Resolved | A concurrent request registered this credential first | The re-read finds the winning credential and the request adopts its user id |
| InsertRejected → RaceReread → Conflict | The re-read finds no credential for this subject | Only the email can have collided, so a different Google account holds it: 409 ProblemDetails |
| Resolved → BudgetEnsured | Always, on every authenticated request | Find-or-create the default budget; heals a user left without one — see [budgets.md](budgets.md#workflows--state-transitions) |

## Decision Trees

Resolving the internal user (`UserProvisioningMiddleware` → `EnsureUserHandler.EnsureUserIdAsync`):

```
IF the request is not authenticated
  THEN skip provisioning and continue                    ← public endpoints reach no budget-scoped data
ELSE IF the sub or email claim is missing or blank
  THEN 401 ProblemDetails "Authenticated principal is missing required claims."
ELSE IF a federated credential already holds that provider and sub
  THEN use its user                                      ← no write; the token's claims are not applied
ELSE                                                     ← no credential for that sub yet
  try to insert a user and its credential in one save    ← a refusal leaves neither row behind
  IF the insert succeeded
    THEN use it
  ELSE IF it lost to the credential index, the email index, or both
    re-read by provider and sub                          ← the reported name cannot separate the two
    IF a credential holds that sub now
      THEN adopt its user                                ← a concurrent request won; it is one person
    ELSE                                                 ← the subject was never duplicated
      THEN ConflictException → 409 "This email address is already linked to a different Google account."
  ELSE                                                   ← a unique rule this path does not model
    THEN let the 23505 propagate unhandled → 500 naming the constraint
```

Every repository catch that handles a PostgreSQL error filters on `PostgresException.ConstraintName`
as well as on the SQLSTATE, against a name pinned as a constant on the owning configuration — the
practice lives in [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md), under
"A violation report names one rule, not every rule that was violated". Here that is why
`UserRepository`'s catch clause filters on two pinned index names — `IX_users_email` on
`UserConfiguration` and `IX_credentials_provider_subject` on `CredentialConfiguration` — declared as
constants instead of left to EF's naming
convention. What that filter decides is whether a `23505` is a failure this path models at all: a
property rename that shifted a generated index name would leave the catch unmatched and turn an
actionable 409 into an opaque 500, and the
unmatched `23505` nobody modelled falls through on purpose, because a 500 naming an unknown
constraint is more useful than a false "someone else won the race". What a constraint name cannot
decide is *what went wrong*. It identifies the rule the database reported, not the set of rules the
row violated — an insert that duplicates a credential's subject duplicates that user's email along
with it, and the user row is written first, so the email index is the one that reports. Only the
re-read separates a person racing themselves from a stranger holding their address.

The budget branch that runs after this, on every path, is in
[budgets.md](budgets.md#decision-trees).

## Integration Points

- **Google OAuth / OIDC**: identity comes from the Google ID token, and reaches the account through a
  federated credential rather than through a column on the user. The API reads the `sub` and `email`
  claims and no others — the token carries more, and the rest is deliberately dropped rather than
  stored against the account. The frontend attaches the **ID token** (not the access token) as the
  `Authorization: Bearer` header on API calls (see the client `AuthInterceptor`) and reads no claim
  out of it at all; the authorization request asks for `openid email` and nothing more.
  `auth-service.spec.ts` pins the first half and `no-profile-scope.spec.ts`, which reads the built
  bundle, pins the second.
- **[Budgets](budgets.md)**: provisioning resolves the identity *and* the ambient budget in one step.
  Everything a user can see hangs off that budget, so all tenancy rules — stamping, filtering, name
  uniqueness, the 404 behaviour — are documented there.
- **All other domain areas**: Accounts, Transactions, Payees, Category Groups, and Categories are
  budget-scoped, not user-scoped. They carry no `UserId` at all; the only owner link in the schema is
  `Budget.UserId`.

## Edge Cases & Known Gotchas

- **Concurrent first-request race**: two simultaneous first requests for the same new user can both
  miss on lookup and race to insert. The loser catches the unique violation and re-reads by
  `(provider, subject)` rather than erroring, adopting the winner's row. Do not "simplify"
  `EnsureUserHandler` by dropping the re-read — it is what makes provisioning safe under concurrency,
  and it is also the only thing that tells this race apart from a genuine email collision. The losing
  write duplicates the subject *and* the email, so it breaches both unique indexes and the database
  names only one of them; the re-read is sound because a reported unique violation means the winning
  transaction committed, which makes its rows visible here.

- **The user row and its first credential are written in one `SaveChanges`, and that is load-bearing.**
  Splitting them would make a user with no credential reachable — a row holding its unique email that
  no sign-in can ever resolve to, so every later attempt with that address is a 409 with no repair
  path. Unlike the missing-budget case below, nothing heals it.
  `UserRepositoryTests.TryAddAsync_WhenOnlyTheCredentialCollides_LeavesNoOrphanedUserRow` is what
  fails if someone splits the save.

  Two shapes were considered and rejected, both of which a later reader is likely to propose.
  **Wrapping the two writes in `ITransactionalExecutor`** ([ADR 0003](../decisions/0003-wrap-multi-repository-writes-in-one-transaction.md))
  is the named mechanism for exactly "two writes in one handler must be atomic", and it is the
  first thing to reach for here. One save is better: it needs no execution-strategy retry loop, and
  it keeps the `23505` attribution in a single `catch` instead of splitting it across two writes
  that can each fail for a different reason. **Modelling `Credential` inside the `User` aggregate**
  would make atomicity automatic rather than argued — but the aggregate would then have to grow to
  hold sessions and passkeys too, and a root loaded on every authenticated request is the wrong
  place to accumulate them.

- **Writers take `users` before `credentials`, always, and that is what makes deadlock impossible
  here.** Two transactions inserting into both tables cannot form a cycle if neither ever takes the
  second lock first. It is a property of the write order rather than of any lock hint, so it
  survives only as long as the order does — a future path that touched `credentials` first would
  reintroduce the cycle without changing a line of the code that documents this.

- **A user row is written before its budget row, in a separate `SaveChanges`**: a user with no budget
  is therefore a reachable state, and it is the unconditional find-or-create on the next request that
  repairs it. The budget half of that story is in [budgets.md](budgets.md#edge-cases--known-gotchas).

- **A returning user's stored email is deliberately never refreshed, and it will go stale.** The
  obvious "fix" is to re-apply the token's claims on the existing-user branch, which is what the
  code did until 2026-08-03. Do not restore it: the provider gates registration and is not standing
  authority to rewrite the account afterwards, and a silent refresh both contacts the provider on
  every request and moves the account's only reachable address without anyone asking. Changing the
  address is its own operation with its own fresh authorization, and it is not built yet.
  `EnsureUserHandlerTests.EnsureUser_ReturningUserWhoseProviderEmailChanged_KeepsTheRegisteredEmail`
  is what fails if someone restores it.

- **`case_insensitive` folds case but not accents.** It is ICU `und-u-ks-level2`, so
  `josé@example.com` and `jose@example.com` are two distinct rows and both can exist at once. This is
  correct for email — the two are genuinely different addresses — and is not a bug to report.

- **The collation is nondeterministic, so `LIKE` does not work against `users.email`.** A pattern
  match on the column fails with SQLSTATE `0A000` ("nondeterministic collations are not supported for
  LIKE"). Nothing queries `users.email` today, so this is not a present defect — but the first
  search-by-email or autocomplete over it needs an explicit `COLLATE` on the expression rather than a
  plain `LIKE`, and the failure will arrive at runtime, not at compile time.

- **An over-long email from the identity provider fails the *first* sign-in with a 400, and only
  the first.** The 254-character bound is checked where the value is written, so an existing user
  never meets it again however long their provider address grows. Softening it on the insert path
  would mean truncating the address or swallowing the validation error, and
  [ADR 0002](../decisions/0002-enforce-rules-at-the-lowest-capable-layer.md) rules out both: a
  coercion that quietly changes the value is not enforcement, and a swallowed rule has no owner. It
  is an accepted consequence, not an oversight.
