# ADR 0014 — Scope the credential delete in the application

- **Status:** Accepted
- **Date:** 2026-08-10
- **Area:** Persistence / Security (row-level security coverage, grant matrix, credential revocation)

## Context

Revoking a passkey needs `DELETE` on `credentials`. Until now the role held `SELECT, INSERT` there
and nothing else, and the grants file said so with a paragraph ending: *when a revocation path lands,
the reason written here is what has to be re-argued rather than quietly deleted.* This is that
re-argument.

The difficulty is that `credentials` is the one table that is **user-owned and exempt** from
row-level security. [ADR 0011](0011-police-the-user-owned-tables.md) granted the exemption because
`credentials` is the table read to answer *who is asking*, so a policy keyed on the identity it
resolves would refuse the query that resolves it. That same ADR recorded what it expected of erasure:

> Erasure inherits a stronger position than it expected: the `DELETE` grants it still needs will be
> policy-scoped when they arrive.

For erasure that came true — the `DELETE` it needs is on `users`, which carries `user_isolation`. It
does not come true here. Every other destructive privilege the role holds is bounded twice: the
grant says which table, and a policy says which rows. This one has only the first half, and the
second half has to be supplied by the application or by nothing.

## Decision

**Grant `DELETE ON credentials`, keep the table's exemption, and scope the statement with the
owner-bearing read that precedes it in the same transaction.**

```sql
GRANT SELECT, INSERT, DELETE ON credentials TO budgetoid_app;
```

Three things make the application-side scoping something a reviewer can check rather than something
a comment asserts:

1. **The delete takes the loaded entity, never an id — and no source of a `Credential` accepts a
   caller-chosen id.** `IPasskeyRepository.DeletePasskeyAsync` is declared over `Credential`, so a
   caller cannot name a row directly. Be precise about what that buys, because the appealing
   shorthand — "the constructor is private, so the lookup is the only source" — is **false**:
   `Credential.CreateFederated` and `Credential.CreatePasskey` are both public. What holds is
   narrower and checkable. Both factories mint their own `Guid.CreateVersion7()`, so a fabricated
   instance can never name an existing row; a detached delete of an id no row carries matches nothing
   and raises rather than removing somebody else's. And the one query that materializes a `Credential`
   from the table, `FindPasskeyCredentialAsync(credentialId, userId)`, carries id, owner and type in
   one predicate.

   So the guarantee is: **to obtain a `Credential` naming an existing row of the caller's choosing,
   someone has to add a new query to `PasskeyRepository`.** That is a rule a reviewer enforces over
   one class, not a property of the type — weaker than "unavailable by construction", and worth
   stating at its real strength. Widening that list is the thing review has to catch.
2. **`credentials.user_id` is immutable**, enforced by the absent `UPDATE` grant of any shape. That
   is what makes a delete issued by primary key alone sound: the binding between an id and its owner
   cannot move between the read that scoped it and the write that used it.
3. **The read and the write share one transaction**, so no interleaving statement can separate them.

`ExecuteDelete` with the owner predicate written into the statement would put the scope back where a
reader expects it, and it is a compile error under `BannedSymbols.txt` — and rightly, because it
would also bypass the change tracker the surrounding handler depends on.

## Alternatives considered

**Police `credentials` and let the policy scope the delete.** This is the answer the rest of the
schema would predict, and it does not work. Row-level security is enabled per **table**, not per
command: the moment `ALTER TABLE credentials ENABLE ROW LEVEL SECURITY` runs, every command with no
permissive policy is refused. A `FOR DELETE` policy alone therefore kills the discovery `SELECT` —
the query the whole exemption exists for — and leaves provisioning's `INSERT` refused as well.
Restoring both means adding `FOR SELECT USING (true)` and `FOR INSERT WITH CHECK (true)`, which puts
three policies on one table, two of which isolate nothing. `RowLevelSecurityCoverage.FindProblems`
refuses exactly that shape, and it should: a table carrying policies that admit every row is worse
than a table carrying none, because a reader scanning for protection finds some.

There is a subtler trap in the same direction, and ADR 0011 already recorded it: a policy admitting a
row *when the session names nobody or names its owner* satisfies discovery and breaks registration
under a race. It is not a way out of this either.

**Add `revoked_at_utc` to `credentials` and never delete.** Two objections, either fatal. The pinned
exemption column set refuses a new column on `credentials`, and the pin's own doctrine is *move the
column, never widen the pin* — but a revocation flag cannot move, because the discovery lookup would
then have to read it *before* the request has an identity, and the table it moved to would be policed
on exactly the identity that does not exist yet. Separately, a revoked-but-present credential is a row
a bug can bring back, which is the shape the erasure rules refuse throughout.

**Grant `DELETE` on the child tables too.** Unnecessary and strictly worse. `passkey_public_keys`,
`passkey_signature_counters` and `sessions` are reached by the database's own `ON DELETE CASCADE`,
which runs as the table owner rather than as this role. A grant on a child would widen the role's
reach without extending what revocation can do — the argument the `users` block already makes.

## Consequences

- **`credentials` becomes the first entry in the grant matrix whose blast radius is bounded by
  nothing but an application predicate.** `Database_LetsTheAppRoleDeleteAnyCredential_OnASessionNamingNobody`
  states that premise as an executable test rather than as prose. It goes red the day somebody
  succeeds in policing this table, which is the day this ADR needs rewriting.
- **One test stands between a refactor and a cross-tenant delete.**
  `Revocation_OfAnotherAccountsCredential_IsRefusedAndRemovesNeitherAccountsRows` is the only thing
  that would notice the `userId` predicate leaving the lookup. No layer below the application can.
- **The immutability of `credentials.user_id` is now load-bearing for a second, unrelated reason.**
  It was argued as an identity rule; it is now also what makes a primary-key delete correctly scoped.
  Anyone proposing an `UPDATE` grant on that column has to answer both.
- **Erasure is unaffected and stays that way.** It empties `credentials` through the cascade from
  `users`, not through this grant, and would still work if the grant were revoked tomorrow. The two
  destructive paths do not share a privilege.
