# ADR 0030 — Displace the session an overwritten cookie names

- **Status:** Accepted and implemented.
- **Date:** 2026-10-05
- **Area:** API / Security (session cookie, row-level security, sessions, data minimization)

## Context

Five paths establish a session, and each one that does writes `__Host-budgetoid-session` over
whatever cookie the browser already held. Overwriting a cookie ends nothing. The browser stops
presenting the old handle, but the session it named stays live until it expires or a revocation
ends it, and no browser holds it. That row is a standing record that this browser was signed in to
that account.

[ADR 0029](0029-sweep-an-accounts-ended-sessions-when-a-session-is-established.md) does not reach
it. Its sweep takes only **ended** rows, and only rows of the account signing in. The overwritten
session can be live, and it can belong to another account, whose rows `user_isolation` hides from
the request. ADR 0029 recorded that residue as what remains rather than argued for it.

Four facts shape any fix:

- **The incoming cookie still names that session when the new one is established.** Holding it is
  the same proof `POST /api/me/session/revocation` needs to end a session.
- **The request is published as the new account.** `sessions` is policed by `user_isolation`, so a
  delete issued under that publication cannot find another account's row.
- **`AuthenticateSessionHandler` owns the three-step order** that turns a handle into an identity
  ([ADR 0019](0019-authenticate-a-request-from-a-first-party-session-cookie.md)): the exempt
  `session_tokens` lookup, the publication, the policed `sessions` read. It publishes the owner
  before it judges liveness, and returns an ended session rather than nothing.
- **Only `session_tokens` holds a foreign key into `sessions`.** Measured: the InitialCreate
  migration declares one, `FK_session_tokens_sessions`. So a delete on `sessions` cascades to the
  handle and to no other table.

### What this amends

Per this repository's convention an ADR is amended by a later ADR and never edited, so the
superseded sentences are named here rather than corrected there.

- **ADR 0029, Consequences, the "What remains" bullet.** Its sentence that live rows still record
  sessions whose cookie a later sign-in overwrote, because overwriting a cookie ends nothing and
  such a row stays live until it expires, is false. Such a session is displaced: deleted with its
  handle before the new cookie is written. The same bullet's clause that the statistics counter
  counts swept rows now holds for displaced rows too, and its opening — ended rows stand until the
  account next establishes a session — has a second way out: a browser still presenting one's
  cookie establishing a session.
- **ADR 0029, Decision, the opening statement that the role holds `DELETE` on `sessions` for that
  act.** It now holds it for two: the sweep and displacement. The grant line itself is unchanged.

One sentence is **extended, not superseded**. ADR 0029's Consequences call a swept session a second
end, beside erasure, where a dead cookie stays on the client until its `Expires`. Both still hold.
A third now stands beside them: a lost establishing response, under Consequences below.

## Decision

**Once an establishing handler has returned a session, the session the browser's incoming
`__Host-budgetoid-session` names is deleted, live or ended, on whichever account owns it, and only
then is the new cookie written. No grant changes.**

1. **One writer.** `SessionCookieWriter.WriteEstablishedAsync` in the API runs displacement and then
   calls `SessionCookie.Issue`. Each establishing endpoint calls it on the arm that established a
   session. `SessionCookieIssueCensusTests` holds the writer as the one caller of `Issue`. Measured:
   a direct `Issue` in an endpoint reddens it.

2. **After the handler, never before.** Every refusal on an establishing path leaves before the
   writer runs. Measured: displacing before the handler reddens both tests that a refused sign-in
   leaves the presented session live, a passkey assertion that does not verify in
   `PasskeyCeremonyTests` and a registration whose address is taken in `AccountRegistrationTests`.
   Run earlier, a refusal would sign the browser out of the session it had, and a stranger holding
   nothing could cause that.

3. **The delete before the cookie.** Written first, the cookie would ride a response that a failed
   delete is about to replace with an error.

4. **In a dependency scope of its own.** The writer creates a child scope and resolves
   `DisplaceSessionHandler` there. That handler hands the cookie's digest to
   `AuthenticateSessionHandler`, which publishes the cookie's account into the child scope, and then
   calls `ISessionRepository.RemoveAsync`, which names no owner and leaves the scoping to
   `user_isolation`. So displacement is a second caller of the three-step order, never a second
   owner of it. Measured: deleting in the request's scope by the cookie scheme's session id reddens
   the cross-account cases — a locked sign-in over another account's locked cookie, a registration
   from a browser holding another account's session, a passkey sign-in over another account's
   ended cookie — while the three same-account cases stay green.

5. **One reader of the cookie.** The digest comes from `SessionCookie.TryReadTokenHash`, the reader
   `SessionCookieAuthenticationHandler` uses, so a value that reader refuses to decode is not one
   displacement deletes by. Measured: a writer that decoded leniently, truncating to 32 bytes,
   deleted a live full session presented as its handle with one byte appended, on the locked
   sign-in. With the shared strict reader the scheme refuses that cookie, the `409` `full_session`
   does not fire, and the full session survives.

6. **A cookie naming no row is a no-op.** That covers a value never issued and a row already gone,
   including one the establishing path's own sweep just took. Measured: without the guard, a sign-in
   from a browser holding a cookie that named no row answered `500` after the new session committed.

7. **`RemoveAsync` loads, removes and retries.** `ExecuteDelete` is a compile error under
   `BannedSymbols.txt`. A revocation landing between the read and the delete trips the concurrency
   token on `revoked_at_utc`, and the call detaches, re-reads and removes again, three attempts at
   most. Measured: without the retry, or without the detach,
   `SessionRepositoryTests.RemoveAsync_WhenRevokedConcurrently_StillRemovesIt` fails with
   `DbUpdateConcurrencyException`. Whether a row was removed does not change the response.

8. **No transaction.** One opened before the child scope's publication would configure its
   connection with no identity, and every policed statement inside it would fail with `22P02`.

9. **Registration displaces in its endpoint, after its ladder.** `RegisterAccountHandler` is
   untouched: the account is still 31 rows in one `SaveChanges`, and displacement is a later write
   the endpoint makes through the writer. A regeneration of recovery codes displaces only when it
   re-establishes a session; without a handoff the browser goes on presenting its cookie, and
   deleting that session would sign the person out.

10. **No check that the cookie names the new session.** The new handle is minted on this request
    and the incoming cookie was issued before it.

**Where the rule sits, stated as [ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md)
requires.** Which row the cookie names is not a fact the database holds. Telling it would take a
session setting naming the row, which is the policy refused below, so the choice of row sits in the
application. Whose rows the delete can reach stays at the bottom, with `user_isolation`, applied in
the child scope.

**What gives displacement its authority.** It needs the cookie's handle, the proof sign-out needs,
and it takes the one session that handle names. That is no more than sign-out could end. An ended
row it takes is one its own account's next sweep would take.

## Alternatives considered

**Keep the residue.** The overwritten session stays a live record of a sign-in in that browser,
held by nobody. Nothing else reaches it before it expires.

**Displace before the handler.** Measured above: a refused sign-in would delete the session the
browser presented.

**`Response.OnCompleted`.** It runs after the response is sent, so a failure goes to the log rather
than to the person, and the delete races the browser's next request.

**A displaced id handed to `AddAsync`, deleted in the establishing save.** That save runs under the
new account's publication. It reaches the same account only, and across accounts it silently
deletes nothing.

**A permissive `FOR DELETE` policy keyed on an `app.displaced_session_id` setting.** It is a third
input to isolation on a table whose policy reads `user_id` alone, and one mis-published value
deletes any session.

**Republishing the cookie's account in the request's scope.** Anything the request did after it
would run as that account. Measured: resolving `DisplaceSessionHandler` from the request's scope
passes every endpoint test, because every establishing endpoint writes the cookie last, and leaves
the request published as the cookie's account. Only
`SessionCookieWriterTests.WriteEstablishedAsync_OverAnotherAccountsCookie_LeavesTheRequestPublishedAsItWas`
catches it.

**Revoking instead of deleting.** A revoked row stays until its own account next signs in. When the
cookie named another account, that sign-in may never come.

## Consequences

- **What remains as a sign-in record, after this and ADR 0029.** A live row is normally held by a
  browser. It can still be held by none: the new session of an establishment whose displacement
  failed, the new session of one whose response was lost, and the session of a browser that dropped
  its cookie without signing out — cleared site data, a closed private window. [Guessing] That last
  case is reasoned, not run: such a browser tells the server nothing. Each such row stays live until
  it expires or a revocation ends it. An ended row stands until the account next establishes a
  session, until a browser still presenting its cookie establishes one, or until its credential or
  the account is deleted. An account that never signs in again keeps its last batch. `n_tup_del` on `sessions` counts swept and displaced rows, as one total
  that names no account. [sessions.md](../business-logic/sessions.md) and
  [adversarial-properties.md](../engineering/adversarial-properties.md) carry the operator's view.
- **The failure window.** If the delete throws, the request answers `500` after the new session
  committed, and no cookie is written, because the exception handler clears the response. The new
  session stands with no browser holding it, and the old one survives in the browser still
  presenting it. What reaches this is a failure underneath — the database, or the retries running
  out. Measured: swallowing the failure turned that `500` into a `200` with the old session left
  live, so it stays loud.
- **A lost establishing response is a third end.** Displacement has already deleted the session
  the browser's old cookie names, and the response carrying the new cookie never arrives. The old
  cookie is then answered `401` on every route, the sign-out route included. Before, that browser
  kept its old session. [Guessing] The web client reads that `401` as an ended session and goes to
  `/welcome` — inferred from the code, not run.
- **A regeneration of recovery codes moves the caller's browser.** It re-establishes whenever it
  ended a session of the replaced set, including one on another device. The caller holds a full
  session to reach the route, so the new cookie displaces that session: a browser signed in with a
  passkey is moved onto a session over the new set, and its passkey session is deleted rather than
  left live beside it. Measured:
  `RecoveryCodeGenerationTests.Generation_ThatReestablishes_DeletesTheCallersPasskeySession` drives
  that case and finds the passkey session deleted and the new cookie opening a live session.
  [recovery-codes.md](../business-logic/recovery-codes.md) owns the condition.
- **Each establishment from a browser holding a cookie costs one more authentication.** The child
  scope reads `session_tokens` and `sessions`, and on a live session the owner's budget, which it
  does not use, before the delete.
- **The suite.** Measured: the integration suite passed whole with displacement in place, with
  `ApiFactory` unchanged.
