# ADR 0029 — Sweep an account's ended sessions when a session is established

- **Status:** Accepted and implemented.
- **Date:** 2026-10-05
- **Area:** Persistence / Security (grant matrix, row-level security, sessions, data minimization)

## Context

Revocation writes `revoked_at_utc` and keeps the row, because the row is what says access ended and
when, and what tells "already revoked" from "never existed". The application role held `SELECT`,
`INSERT` and `UPDATE (revoked_at_utc)` on `sessions`, and no `DELETE`. So a session row left only by
the cascade from a deleted credential or a deleted account. Revoked and expired rows stayed for the
life of the credential that opened them, one per sign-in, each carrying its credential and its
creation, expiry and revocation instants, with its handle on `session_tokens` beside it.

The **columns** are within what [users-and-ownership.md](../business-logic/users-and-ownership.md)
lets a first-party security record carry: each is read in order to end access. The **rows** were
not. A set that grows by one per sign-in and is kept after the sessions are over is a sign-in
history, which the same rule refuses. It was an accepted gap, recorded rather than argued for, and
[ADR 0019](0019-authenticate-a-request-from-a-first-party-session-cookie.md) doubled it with the
handles.

Two facts shape any fix:

- **Revocation stays an `UPDATE`.** The sign-out route answers `204` on a second call because the
  second call still finds the row, and the row is the record of when access ended.
- **`sessions` is policed by `user_isolation`.** A statement on it reaches the account the request
  has published. A sweep reading across accounts needs a connection naming nobody, which the policy
  answers `22P02`, or a role the policy does not bind.

### What this amends

Per this repository's convention an ADR is amended by a later ADR and never edited, so the
superseded sentences are named here rather than corrected there.

- **ADR 0019, Consequences, the paragraph on accumulation.** It says sessions still accumulate and
  their tokens with them, that neither is swept, and that there is no `DELETE` grant for a sweep to
  use. All three are now false: ended rows and their handles leave when the account next
  establishes a session, and the role holds `DELETE` on `sessions` for that. The paragraph's closing
  clause still holds for the table that decision was about. What ADR 0019 refused was `DELETE` on
  `session_tokens`, and the sweep takes none.
- **ADR 0017, Context, the sentence contrasting `sessions` with `webauthn_challenges`.** Its first
  half stands: revocation still writes `revoked_at_utc` and keeps the row. Its second half, that the
  role holds no `DELETE` on `sessions` at all, is false.
- **ADR 0017, Consequences, the change-tracker bullet.** Its claim that on `sessions` the same
  mistake dies loudly with `42501` because the grant was withheld is false. With the grant, EF's own
  delete of a tracked session succeeds (measured; see Consequences). The loud failure survives for a
  tracked `SessionToken` alone.
- **ADR 0017, Consequences, the last bullet.** Its contrast with `sessions` accumulating revoked and
  expired rows now holds only up to the account's next session establishment.
- **ADR 0012, the `webauthn_challenges` section.** Its count of the seven identity tables holding
  `DELETE` — four, with `sessions` among the three that do not — is now five and two. The reasons it
  gives for keeping `passkey_public_keys` and `passkey_signature_counters` ungranted are unchanged.

[ADR 0014](0014-scope-the-credential-delete-in-the-application.md)'s rejection of `DELETE` on the
child tables of `credentials`, `sessions` among them, is **not** amended. It rejected the grant as a
way to revoke, and revocation still reaches `sessions` by the cascade from `credentials`. The grant
arrives here for a different act.

## Decision

**Establishing a session deletes, in the same save, every session of the account that has already
ended — revoked or expired — and leaves every live one. The role holds `DELETE` on `sessions` for
that act, and revocation stays an `UPDATE`.**

1. **The sweep runs inside `SessionRepository.AddAsync`.** That is the port method every
   establishing path but registration writes its session and handle through. It loads the account's
   sessions, removes each one that is not live at the new session's `CreatedAtUtc`, and saves the
   removals in the same `SaveChanges` as the new session and its handle. A sign-in that fails to
   store deletes nothing, and a delete that fails stores no sign-in. Load then remove, because
   `ExecuteDelete` is a compile error under `BannedSymbols.txt`.

2. **"Ended" has one spelling.** The sweep asks `Session.IsActiveAt` — the method that decides
   whether a request is authenticated — at the new session's `CreatedAtUtc`. It reads any revocation
   as ended, so a row revoked *after* that instant goes too; `SessionRepositoryTests` pins that case.

3. **The read names no owner, and `user_isolation` scopes it.** The policy is `FOR ALL`, so it
   bounds the delete as it bounds the read, and the sweep reaches the account the request published.
   That is the opposite of the `credentials` delete in ADR 0014, where the table is exempt and an
   application predicate is the whole bound.
   `RlsIsolationTests.Database_RefusesToDeleteAnotherUsersSession_WhileStillAllowingItsOwn` pins it.

4. **`GRANT SELECT, INSERT, DELETE ON sessions`, beside the unchanged
   `GRANT UPDATE (revoked_at_utc)`.** `session_tokens` keeps `SELECT, INSERT` and no `DELETE`. A
   swept session's handle leaves by the `ON DELETE CASCADE` from `sessions`, which runs as the
   table's owner.
   `AppRoleGrantsTests.Database_LetsTheAppRoleDeleteItsOwnSession_AndTheCascadeTakesItsHandle` and
   `Database_RefusesADeleteOnASessionToken_WhileTheCascadeFromItsSessionStillTakesIt` pin both ends.

5. **A lost race is retried, three attempts at most.** Another request on the same account can
   delete or revoke a row this one is removing first, and the save raises
   `DbUpdateConcurrencyException`. When every conflicting entry is a deleted `Session`, `AddAsync`
   detaches those entries, re-reads, removes again and saves again; any other conflict propagates.
   Measured: without the retry, all four of the sweep's race tests fail with that exception, and
   removing only the detach reddens them too. With both, they pass, including under a
   caller's open transaction, where EF's automatic savepoint lets the failed save roll back alone.

6. **The cookie scheme reads the handle untracked.** `SessionTokenRepository.FindByTokenHashAsync`
   is `AsNoTracking`. Measured: tracked, the locked sign-in over the account's own ended session
   answered `500`, `42501: permission denied for table session_tokens`, from `AddAsync`. The token
   read stayed in the change tracker, the locked path does not clear it, and removing the ended
   session made EF cascade into the tracked handle. `LockedSignIn_OverAnEndedSession_Succeeds` went
   red.

7. **Registration does not sweep.** It writes its session through `IRegistrationRepository`, and
   the account id is derived from that registration's own challenge, with the `users` row inserted
   in the same save. No session can already exist under it.

**Where the rule sits, stated as [ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md)
requires.** "Only ended rows are deleted" sits in the application, above the database, because the
database cannot judge liveness against the application's clock declaratively; the restrictive policy
below is the attempt, and why it was refused. Whose rows the delete reaches stays at the bottom,
with the policy.

## Alternatives considered

**Keep accumulating.** The rows are a sign-in history, which the behavioural-record rule refuses.

**A restrictive `FOR DELETE` policy comparing against `now()`.** It would put "only ended rows" in
the database. Refused on three counts. It is a second clock beside the one the handlers read. The
suite pins the sweep at fixed instants, which a policy reading `now()` would judge differently. And
it is a third input to isolation on a table whose policy reads `user_id` alone — the axis
[sessions.md](../business-logic/sessions.md) refuses to widen when it bars a policy from reading
`kind`.

**A scheduled sweep.** It needs a role that reaches every account, which is the elevated reach
[ADR 0004](0004-connect-as-a-least-privilege-role.md) keeps off the application role. And the API
scales to zero.

**Deleting at sign-out.** A second sign-out would present a cookie naming no row and answer `401`,
the answer the sign-out route's idempotence exists to avoid. It would remove the row saying when the
person signed out, which is why revocation is an `UPDATE`. And an expired, never-revoked row would
still accumulate.

**Sweeping at authentication.** A write on every authenticated request, on the three-step path that
no transaction may wrap (ADR 0019).

**A sweep method each establishing handler calls.** Four call sites, and an establishing path added
later that forgot it would redden nothing. Inside `AddAsync`, the port's shape carries it the way it
carries the pairing of a session with its handle.

**`DELETE` on `session_tokens`, so the sweep removes the handle by name.** The table is exempt from
row-level security, so a delete there is bounded by no policy — the cost ADR 0014 and ADR 0017
accepted for reasons this act does not have. The cascade from `sessions` already takes the handle.

## Consequences

- **What remains.** Ended rows stand until the account next establishes a session, so the rows
  standing are the sessions that were live when the most recent one was established, whether or not
  they have ended since. An account that never signs in again keeps its last batch. Live rows still
  record recent sign-ins, including sessions whose cookie a later sign-in overwrote: overwriting a
  cookie ends nothing, so such a row stays live until it expires. And PostgreSQL's statistics
  counter for deletes on `sessions` (`n_tup_del` in `pg_stat_user_tables`) counts swept rows, as a
  total for the table that names no account.
  [adversarial-properties.md](../engineering/adversarial-properties.md) states what an operator
  reads.
- **A signed-out session stays readable until the account's next sign-in, and its cookie has two
  answers.** Until then, the cookie gets the sign-out route's idempotent `204`. Once a session is
  established on the account, the row and handle are gone, and that cookie answers `401` on every
  route, the sign-out route included. That makes a swept session a second end, beside erasure, where
  a dead cookie stays on the client until its `Expires`; it names nothing. [Guessing] The web client
  reads that `401` as an ended session and goes to `/welcome`, and its sign-out flows leave on an
  error anyway — inferred from the code, with no spec run against this case.
- **The change-tracker guard on `sessions` is gone.** Measured: removing the second
  `DiscardTrackedEntities()` in `RevokePasskeyHandler` or `GenerateRecoveryCodesHandler` reddens no
  integration test, because EF's own delete of the tracked sessions now succeeds and removes the rows
  the database's cascade would have taken. Only the unit replay and placement tests notice. The
  `42501` that used to report that mistake survives only for a tracked `SessionToken`. Nothing
  confines the grant to `AddAsync`; [sessions.md](../business-logic/sessions.md) records the gotcha.
- **`sessionsEnded` depends on history on the regeneration path.** It still counts the unrevoked
  rows of the replaced credential, and credential revocation still ends only that credential's
  sessions. But an expired, never-revoked session of the old set is in the count only if no session
  was established on the account between its expiry and the regeneration. A session still live when
  the call runs is in it. [recovery-codes.md](../business-logic/recovery-codes.md) owns the
  contract.
- **Erasure is unaffected.** It still empties `sessions` by the cascade from `users`, and uses none
  of this grant.
