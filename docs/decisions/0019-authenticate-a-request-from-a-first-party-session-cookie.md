# ADR 0019 — Authenticate from a session cookie, and split its discovery key onto an exempt table

- **Status:** Accepted and implemented. The cookie is the only thing that authenticates a request to
  this API, the two registration routes aside.
- **Date:** 2026-08-16
- **Area:** Persistence / Security (row-level security coverage, grant matrix, sessions)

## Context

A session row has existed since first-party sessions shipped, and it authenticates nothing. Every
request is authenticated by the identity provider's ID token, which means the product cannot end a
sign-in without asking the provider to, and means the one credential type that can never hold the
account's keys is the only thing that proves who is asking. Sessions exist to replace that.

Replacing it needs a value the client presents and the server can look up. That is where the design
runs into a wall that is not obvious until it is stated:

**The lookup that establishes identity is itself subject to the policy that needs the identity.**
`sessions` is policed by `user_isolation`, whose predicate compares `user_id` against
`app.current_user_id`. A request arrives carrying a token and nothing else. To read the session the
token names, the connection has to already know whose request it is — and knowing that is the entire
purpose of the read. The circle does not merely fail; it fails **loudly and on every request**,
because [ADR 0008](0008-read-the-ambient-budget-inside-the-policy.md) has `SessionContextInterceptor`
write `''` for an unresolved setting rather than skipping it, so the policy meets `''::uuid` and
raises `22P02`.

Three escapes are unavailable, and each is unavailable for a reason recorded elsewhere:

- **A permissive policy** (`FOR SELECT USING (true)`) puts a vacuous policy on a policed table.
  `RowLevelSecurityCoverage.FindProblems` refuses that shape, and
  [ADR 0014](0014-scope-the-credential-delete-in-the-application.md) records the same dead end for
  `credentials`.
- **A policy admitting the row when the session names nobody** is the trap
  [ADR 0011](0011-police-the-user-owned-tables.md) names: a predicate that opens up precisely when
  the connection is anonymous is not isolation, it is a hole with a `WHERE` clause.
- **Exempting `sessions` itself** hands every application session every account's session rows, and
  reverses ADR 0011 for the table [sessions.md](../business-logic/sessions.md) argues hardest about.

## Decision

**A request authenticates from an opaque token in a first-party cookie, and the token's digest lives
on its own exempt table, `session_tokens`, separate from the policed `sessions` row it names.**

This is [ADR 0012](0012-split-a-passkeys-material-by-whether-it-is-read-before-identity.md)'s split,
applied to a second area: **material read *before* the request has an identity goes on an exempt
table; everything read *after* that answer stays on a policed one.** The line is not a convenience —
it is the same line, drawn by the same question, and it lands in a different place on each table only
because different facts are needed at different moments.

| | |
|---|---|
| `session_tokens` | `token_hash` (PK), `session_id`, `user_id`. **Exempt.** Read before an identity exists. |
| `sessions` | began, expires, revoked, kind, credential. **Policed.** Read after the answer. |

**The pinned column set is `["token_hash", "session_id", "user_id"]`**, and that pin is where this
decision is enforced rather than merely described. A column proposed here — a last-used instant, an
expiry, a device name — is a column somebody wants to read *after* the identity is known, which means
it belongs on `sessions`. When the pin goes red the fix is to **move the column**, never to append a
name to the list.

**The stored value is `SHA-256(token)`, unsalted, with no work factor.** No salt, because the lookup
arrives carrying a token and no identity, so the row must be findable by its digest alone — a per-row
salt is a value the lookup cannot know before finding the row it needs the salt to find. No work
factor, because the input is a uniform 256-bit value this server generated: there is no dictionary to
slow down, and the cost would land on the one query every authenticated request makes.

**What the hash buys here is not what it buys on `recovery_code_hashes`, and the difference matters.**
A recovery code never reaches this server at all, so its digest is a claim that the value is unknown
here. A session token *is* known here — this server mints it and reads it on every request presenting
it. The digest is a narrower claim, about what a **copy** of the table is worth: a backup, a replica,
or one unbounded read yields digests, and a digest of a uniform 256-bit value cannot be turned back
into the token a request would have to present.

**The grants are `SELECT, INSERT` and nothing else.** No `UPDATE` of any shape — every column is
written whole and no edit to one means anything, so the *absence of a grant* is a property of the
table rather than a column list somebody can widen. No `DELETE` — rows leave by the cascade from
`sessions`, which runs with the privileges of the referencing table's owner. That absence is
load-bearing in the way [ADR 0017](0017-consume-a-recovery-code-by-deleting-its-row.md) and
[ADR 0018](0018-give-the-wrapped-account-keys-a-policed-table-and-their-own-factor-identifier.md)
describe: an EF change-tracker cascade into rows it happens to be holding **succeeds silently** where
the grant exists, and dies with `42501` where it does not.

**The foreign key is composite** — `(session_id, user_id) → sessions(id, user_id)`, against a new
alternate key. A token naming a session that belongs to another user is therefore unstorable rather
than merely unwritten. This matters more here than on a policed table: the lookup runs anonymous and
**the request adopts the owner it finds**, so a row whose two columns disagreed would be a sign-in to
somebody else's account, decided by a table nothing beneath the application polices.

## Alternatives considered

**Sign the cookie — HMAC or ASP.NET Core Data Protection — so the user id is verified before it is
published.** Genuinely sound, and cheaper: one table fewer and one round trip fewer per request. It
loses on operations rather than on security. A signing key has to be stable across replicas and
across restarts, and the deployment scales to zero on Container Apps, so key persistence becomes a
provisioning story this epic does not own — and a rotated or lost key signs every session out at
once, silently. Worth revisiting the day the extra read measures.

**Put the user id in the cookie so the identity can be published before any read.** This publishes an
identity nobody has proved, for the duration of one query. The check that would make it safe *is* the
read. Refused for the reason `CompleteAssertionHandler` refuses the same shape on the assertion path:
publish after the proof, never before.

**Denormalise expiry and revocation onto the exempt table** so one read answers everything. It needs
`UPDATE` on an exempt table to revoke, which is exactly what the pinned-column doctrine exists to keep
out, and it puts the authoritative answer to "is this session still live" on the table with the
weakest guarantees.

**A surrogate primary key with a unique index on the digest.** The digest is what a request arrives
holding, so it is the row's real identity. Making it the primary key is what makes two sessions
sharing a token unstorable rather than a duplicate nobody notices.

## Consequences

**Two round trips per authenticated request**, and no transaction may wrap them. A transaction opened
before the identity is published configures the connection while `app.current_user_id` is empty, and
every policed statement inside it fails `22P02` — the trap `EnsureUserHandler`,
`CompleteAssertionHandler` and `RedeemRecoveryCodeHandler` each already carry. The cost is stated
rather than hidden.

**The exemption needs a positive control, and it is the one with the widest blast radius in the
system.** `RlsIsolationTests.Database_ReadsASessionTokenWithNoUserOnTheSession` proves the table is
still readable on a connection naming nobody. Coverage cannot supply this: an exemption says a policy
is *not required*, never that one is *forbidden*, so a policy added to this table leaves
`RlsCoverageTests` entirely green — and surfaces only as **every request in the product answering
401**, with nothing in the response naming the cause.

**`session_tokens` joins the list of tables `GenerateRecoveryCodesHandler`'s never-materialise rule
binds** — the third. Because the role holds no `DELETE`, that rule now fails loudly here rather than
silently, which is an improvement on the two tables where it does not.

**Erasure reaches it by cascade, two levels up**, so it needs no grant and no new step; the
`ErasureAtomicityTests` row-count sweep covers it like any other relation.

**Sessions still accumulate, and now so do their tokens.** Neither is swept, there is no `DELETE`
grant for a sweep to use, and adding one is the thing this decision argues against. That is the
existing accepted gap at twice the rows, recorded in [sessions.md](../business-logic/sessions.md)
rather than closed here.

**The table ships ahead of anything that uses it, and so does a fourth `webauthn_challenges` ceremony
value, deliberately.** The rebaseline window in `migrations-guard` is open
([migrations.md](../engineering/migrations.md)), so the baseline is *regenerated* rather than
extended — and regenerating it is not free: the new id has to be written by hand into
`Migrations_KeepTheBaselineFrozen`, and whoever does it resets production's `__EFMigrationsHistory`
in the same deploy. Doing that once for every schema change this area needs is the reason both arrive
before their callers. The window itself stays **open**: further schema work follows in the encryption
area, and closing it is a decision about whether the database has started holding data anyone wants
back, not a consequence of a rebaseline.

**The cookie needs a CSRF control, and the control has to start above authentication.** A browser
attaches a `SameSite=Lax` cookie to a top-level cross-site navigation, so the cookie alone does not
close request forgery. A required `X-Budgetoid-Client` header does, because no cross-site form can
add one — and it has to cover the **anonymous** routes, because those are the ones that will set a
cookie and login-CSRF is signing somebody into an account they do not own. That is a consequence of
this decision rather than a separate one: a bearer token in an `Authorization` header was never
attached by a browser on its own, so nothing before this needed the control at all.

**The reading half shipped one commit before the writing half, and the asymmetry was the point.** The
alternative was one commit moving the whole product from bearer tokens to cookies at once — the
ceremonies, the registration gate and the client all inside it. Landing the reader first meant every
intermediate commit was shippable, and the bridge scheme below is what bought that. It has since been
paid for and removed.

**One write path takes both rows, and the port's shape is what holds the pairing.**
`ISessionRepository.AddAsync` takes the session *and* its token, with no overload taking a session
alone, so a session cannot be written without its handle by construction; `ISessionTokenRepository`
stays read-only, because a second way to write a token is a way to produce one naming a session that
was never committed. The schema still permits several handles or none — closing that would need a
unique constraint for one half and a trigger for the other, which
[ADR 0002](0002-enforce-rules-at-the-lowest-capable-layer.md) refuses — so the application is where
one-to-one actually lives, and this is the sentence that says so.

**Neither the handle nor the session's id ever appears in a response body.** The cookie is `HttpOnly`
precisely so nothing else is a handle, so the establishing handlers return the raw token *beside*
their result, through a type the endpoint destructures and never serialises. A census over every type
a route returns is what keeps that true of records added later.

**The temporary default scheme has been removed, and the state it was named to reach has arrived.**
`Budgetoid.Bridge` — a policy scheme forwarding to the cookie handler when the cookie was present and
to `JwtBearer` otherwise — existed so the whole surface kept working while this decision landed one
commit at a time. **The cookie handler is now the default**, and the fallback authorization policy
names it explicitly rather than relying on the default, so a later change of default cannot silently
move every route that declares nothing onto some other handler.

`JwtBearer` stays registered and is reached by **exactly one policy**: the `/api/registration` group's,
which names the provider's scheme because an account may not exist without a completed provider
exchange. A bearer presented to any other route therefore authenticates nothing at all — the cookie
handler answers `NoResult` and the request is answered the same `401` an anonymous one gets. That is
what makes "an authenticated request can never name an account that does not exist" a **structural**
fact rather than a check: the cookie is only ever issued over a session row, and a session row is only
ever written beside the account it names. The claim gates that read a provider token did not leave with
the bridge; they moved onto the one group that still reads one.

Two things the bridge's removal closed downstream. `FullSessionRequirement` no longer has to admit a
principal that authenticated on any scheme but the cookie's — while the bridge stood, a Google bearer
carried no session and therefore no kind claim, and a requirement refusing what it did not find would
have refused the whole product. And `sub` means one thing again: this installation's own account id.
