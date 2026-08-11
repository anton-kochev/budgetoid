# ADR 0017 — Consume a recovery code by deleting its row

- **Status:** Accepted
- **Date:** 2026-08-11
- **Area:** Persistence / Domain (grant matrix, data minimization, recovery factors)

## Context

A recovery code is single-use: redeeming one invalidates it. That sentence has two possible spellings
in a relational schema, and they are not equivalent.

One marks the row — a `redeemed_at_utc`, or a `used` flag — and every reader that cares about live
codes carries a predicate. The other removes it, so the set of rows *is* the set of live codes.

The rest of the schema does not settle this by precedent, because it already contains both patterns
and each was argued separately. `sessions` is deliberately marked rather than deleted: revocation
writes `revoked_at_utc` precisely so the row stays accountable, and the application role holds no
`DELETE` there at all. `webauthn_challenges` is deliberately deleted rather than marked: its rows are
nonces, consuming one *is* deleting it, and a row nobody can delete is a row swept by a path that does
not exist. Opposite decisions, because the rows mean opposite things.

So the question this ADR answers is which of those two a recovery code is.

## Decision

**Consuming a recovery code is deleting its row.** The application role is granted
`SELECT, INSERT, DELETE` on `recovery_code_hashes` and **no `UPDATE` of any shape**.

A recovery code is the `webauthn_challenges` case and not the `sessions` case, and the sentence that
earns the grant is that block's sentence word for word: these rows are single-use secrets. Four things
follow, and each is a property rather than a convention:

1. **"Redeeming a code invalidates that code" is literally true**, rather than being a property some
   filter has to keep remembering. There is nothing to invalidate, because there is nothing left.
2. **The remaining count is a plain `count(*)`.** Not a count of rows a predicate calls live — a count
   of rows.
3. **No behavioural timestamp lands on the schema.** A `redeemed_at_utc` records *when this person used
   a recovery code*, which is a fact about a person's behaviour in a schema whose data-minimization rule
   refuses exactly that class of column and whose `ProhibitedColumnVocabulary` already refuses its
   neighbours.
4. **There is nothing for the erasure remnant gates to find**, and nothing for an erasure to have to
   destroy beyond the row itself.

The absent `UPDATE` is the corollary worth having because it is checkable in one statement: with no
`UPDATE` of any shape, the delete is the **only** way a row can stop counting, so this decision cannot
be quietly reversed into a stamp by a later handler. `Database_RefusesEveryUpdateOnARecoveryCodeHash_…`
is that statement.

### The accepted cost, stated rather than hidden

**A redeemed code leaves no trace.** *"Was this code used, or was it never issued?"* is unanswerable —
by support, by the account holder, and by the product itself. If somebody reports that a code they
wrote down does not work, there is no record that distinguishes "you already spent it", "the set was
regenerated" and "you mistyped it".

That is a real cost and it is accepted deliberately, because the product makes the same trade
everywhere else it has come up. [erasure.md](../business-logic/erasure.md) refuses a deletion record
outright — *"a column recording the deletion is the row surviving under a different name"* — and
`ErasureLoggingTests` refuses a log line saying an erasure happened, for the same reason. A product
that keeps no behavioural log cannot make an exception for the one table where an audit trail would be
convenient to an operator, because that convenience is precisely what every one of those rules is
refusing.

## Alternatives considered

**Stamp `redeemed_at_utc` and filter every read.** The conventional design, and the one a reader will
propose. Rejected on three counts, any one of which is sufficient.

The role would need `UPDATE` on the table — the single privilege that can rewrite a secret's row in
place, on a table that is exempt from row-level security and therefore has nothing beneath the
application bounding what such a statement reaches. Every read of the set would have to carry
`where redeemed_at_utc is null`, and the first one that forgets it either counts spent codes as live —
telling a person they have ten ways back in when they have two — or, on the redemption path, matches a
spent hash and lets one code work twice. And the stamp is a behavioural record about a person, which is
the class of column the schema refuses by rule rather than by taste.

The one thing it buys is the support answer above, and the product has already decided not to buy that
anywhere.

**Keep a `used` boolean instead of a timestamp.** Strictly worse than the timestamp: it takes the same
`UPDATE` grant and the same forgettable predicate, and it discards the only piece of information that
made the timestamp version worth discussing. A boolean a bug can clear is also the exact shape
[ADR 0014](0014-scope-the-credential-delete-in-the-application.md) refused on `credentials` — *a
revoked-but-present row is a row a bug can bring back*.

**Move the row to a `redeemed_recovery_codes` table.** Deletion with the trace kept. Rejected: the row
that moves still carries the account's `user_id` and the digest of a secret, so it is a remnant under a
new name — and a table holding one row per redemption *is* the behavioural log the product does not
keep. It would also owe a policy and a grant of its own, and the classifier would rightly demand one.

**Delete the whole set on the first redemption, forcing regeneration.** It makes "single-use"
unmistakable and removes the per-code question entirely. Rejected as hostile at the exact moment a
person is least able to cope with it: somebody redeeming a code has just lost their authenticator, and
this design would leave them with zero factors until they successfully generated a new set — while
generation itself requires a fresh passkey assertion they by definition do not have. It converts the
feature's known limitation into an immediate lockout.

**Keep no codes at all and rely on the backup window.** Not a serious alternative, listed because it is
the shape of the objection *"the operator could restore a point-in-time backup and see the row"*. That
is true and it is the same physical limit [erasure.md](../business-logic/erasure.md) already records
for erasure: erased rows persist in point-in-time backups for up to seven days and in no other
location. It is not a route, a handler, a role or a grant, so it is not a path this decision has to
close.

## Consequences

- **`recovery_code_hashes` is the third identity table granted `DELETE`** — after `users`, which is the
  root the owned graph cascades from, and `webauthn_challenges`, whose rows are nonces — with
  `credentials` making a fourth since revocation. Each holds it for a reason the others do not have,
  and the grants file states each separately rather than by analogy.
- **The delete is bounded by no policy.** The table is exempt, so this is the second grant in the
  matrix whose blast radius is the whole table with the application as the only thing narrowing it.
  `Database_LetsTheAppRoleDeleteAnyRecoveryCodeHash_OnASessionNamingNobody` states that premise as an
  executable test; it goes red the day somebody succeeds in policing this table, which is the day
  [ADR 0016](0016-give-recovery-code-hashes-their-own-exempt-table.md) needs rewriting.
- **The grant has no caller yet.** Regeneration deletes the *set's* `credentials` row and these rows
  leave by the database's own cascade, which runs with the referencing table owner's privileges rather
  than this role's. The only path that will use the grant is redemption, which is not routed. A reader
  looking for a second caller will not find one and should not add one.
- **There is a failure mode this creates that nothing beneath the application can catch**, and it is
  the most dangerous line in the area. Because the role **is** granted `DELETE` here, an EF cascade into
  tracked `RecoveryCodeHash` copies would **silently succeed** — the rows would leave by the application
  instead of by the database's cascade, with no SQLSTATE to say so. On `sessions` the identical mistake
  dies loudly with `42501` precisely because that grant was withheld. So the generation path must never
  materialise the previous set's rows, and today that is held by a comment and by nothing else. See
  [recovery-codes.md](../business-logic/recovery-codes.md).
- **The remaining count needs no read of the set.** `GET /api/me/recovery-codes` counts rows for the
  user and never looks for the credential, which is also what makes zero an answer rather than a `404`.
- **Nothing sweeps this table and nothing needs to.** Rows leave when they are spent or when the set is
  replaced; there is no expiry, so there is no accumulation of dead rows the way `sessions` accumulates
  revoked and expired ones.
