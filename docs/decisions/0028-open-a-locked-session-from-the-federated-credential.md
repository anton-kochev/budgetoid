# ADR 0028 — Open a locked session from the federated credential

- **Status:** Accepted. The server route is implemented, and `/release` calls it.
- **Date:** 2026-10-03
- **Area:** API / Security (authentication schemes, route authorization, sessions)

## Context

A `Locked` session is what a federated credential opens: `Session.Establish` derives the kind from
the credential's type, and `federated` is the one type that cannot reach budget content. The gate
over that kind is built. `FullSessionRequirement` refuses a locked session everywhere a route has
not opted out, and three routes have: the sign-out, `GET /api/me/session` and
`POST /api/me/erasure/schedule`. The schedule exists for somebody who has lost every passkey and
every recovery code. That person still holds a Google sign-in, and nothing else.

Something has to turn that sign-in into a session. Four constraints shape it:

- **Exactly one path creates an account**, registration
  ([ADR 0021](0021-make-registration-one-consented-act-and-derive-the-account-id-from-its-own-challenge.md)).
  Nothing in the compiler holds that, so a second creating path would redden nothing. This route
  must create none, and must not read as one that might.
- **The session must be `Locked`, so it must be opened over the federated credential.** An account
  also holds a passkey and a set of recovery codes, and a session opened over either is `Full` —
  the one thing a provider sign-in must not reach.
- **The lookup runs before the request has an identity.** `credentials` is exempt from row-level
  security and `sessions` is policed, so the order is the one
  [ADR 0019](0019-authenticate-a-request-from-a-first-party-session-cookie.md) fixed for every
  request: the exempt read before this route publishes anyone, then the identity, then every
  policed statement. Reversed, the session insert meets `''::uuid` and dies with `22P02`. (A cookie
  the browser sent may already have published its own account; item 5 says why that changes no
  answer.)
- **The provider's signature is the whole proof**, and the product cannot revoke a provider token.

## Decision

**`POST /api/locked-session` takes a Google ID token as a bearer and no body, and turns it into a
`Locked` session over the account's federated credential — or into a `404` or a `409` that writes
nothing.**

1. **The route declares its own policy, naming the provider's scheme.**
   `RequireAuthorization(policy => policy.RequireAuthenticatedUser()
   .AddAuthenticationSchemes(ProviderAuthentication.SchemeName))` — the registration group's shape,
   for that group's reason. Naming the scheme makes `AuthorizationMiddleware` authenticate the
   bearer rather than the session cookie, so a browser already holding a session, full or locked,
   cannot stand in for the provider. Declaring a policy takes the route off the fallback, which is
   right rather than worked around: the two requirements about session kinds judge the session a
   request is served under, and this route serves none — it opens one. Whether the browser already
   holds a session is a separate question, and item 3 answers it. Not `AllowAnonymous`: the
   signature is the proof.

2. **The claims are judged by `RegistrationClaimGate`, the filter registration uses.** No usable
   `sub` or `email` is a `401` titled `MissingClaimsTitle`; an address the provider does not vouch
   for is a `401` titled `UnverifiedEmailTitle`. One judgement in `ProviderClaims`, one filter
   applying it to a policy-authenticated provider principal. The filter keeps registration's name;
   a neutral one is a rename of its own.

3. **A live full session beside the token is a `409` with `conflictKind: "full_session"`, and the
   handler never runs.** The delegate's first statement authenticates the session cookie scheme by
   name and judges that result — never `HttpContext.User`, which the policy filled with the
   provider's principal. A live session whose kind is anything but `Locked`, or whose kind claim
   does not read back, is refused with a `ConflictException` whose detail names no subject, address
   or account. Nothing is written, no cookie is set, and the full session stays live. A live
   **locked** session is replaced, on the same account or another: the new cookie overwrites the old
   one, and the old row stays live until it expires, as with every sign-in that overwrites a
   cookie. An ended session authenticates as nothing on this route, so its cookie falls through.
   The check runs after the claim gate: the `409` says something true about the browser's session,
   and a caller the provider never vouched for has not earned that answer.

   **A weaker proof must never overwrite a stronger sign-in.** A provider token proves less than the
   passkey or recovery code that opened a full session. The browser has its own guard — the
   bootstrap drops a locked sign-in's hand-off when the startup probe finds a session — but it keeps
   the hand-off when the probe answers `unreachable`. Before this check, that return could post over
   a full cookie, replace it with a locked one, and show a passkey holder the screen that tells them
   their data is unrecoverable.

4. **One discovery read, on `credentials` alone.** `UserRepository.FindFederatedCredentialBySubjectAsync`
   matches type `federated`, provider and subject, never joins `users`, and returns the whole row,
   untracked. The row is needed because `Session.Establish` reads its type; untracked, because a row
   found with no owner predicate is legal for this read and for nothing written downstream of it.

5. **An unknown subject is a `404` with `refusal: "no_account"`**, and the handler publishes,
   writes and sets nothing — no session, no handle, no cookie. The body repeats neither the subject
   nor the address. The refusal word is what lets a client offer registration instead; this route
   creates no account. **"The handler publishes nothing" is not "nothing is published".** The
   session cookie is the default scheme, so on a request carrying one it has already run before the
   route: a handle that matches publishes its account, and a live session its budget too. That
   identity is the cookie's own, the discovery read ignores it because `credentials` is exempt, and
   nothing policed runs after the `404`.

6. **A found credential is published, then the session is written.** `ResolveUser(credential.UserId)`
   runs after the read and before any policed statement. Then the handle is minted, the session is
   built by `Session.Establish(credential, now, now + SessionPolicy.Lifetime)`, and one `AddAsync`
   writes the session and its handle. **No transaction and no `ITransactionalExecutor`**: there is
   one write, and a transaction opened before the publication would configure its connection with
   the identity still empty. The account's erasure schedule is read after the save. On a request
   carrying a live locked cookie, this publication replaces the cookie's account with the
   credential's owner, which is what lets a sign-in to another account replace that session.

7. **The `200` carries `kind` (always `"locked"`), `expiresAtUtc` and `erasure`** — `null`, or
   `{ "takesEffectAtUtc": … }` — the shape `GET /api/me/session` answers. The cookie is written by
   the endpoint on the established arm only, after the handler returned.

8. **The session lasts the product's one lifetime, 14 days, by decision.** Nothing in the domain
   makes a locked session's interval match a full one's; it is a product rule, and
   `EstablishedSessionLifetimeTests` drives this path beside three others so a locked-only constant
   reddens there.

**The `404` is not an enumeration oracle.** Only somebody holding a provider-verified token for that
exact subject learns that the subject holds no account. That is the argument registration's own
subject refusal and its `409`s rest on: a caller can only ever probe themselves.

## Alternatives considered

**`AllowAnonymous` and `ProviderAuthorizationGate`, the email change's filter.** It would widen the
anonymous surface `AnonymousSurfaceTests` pins with a route whose real proof sits inside a filter the
route table cannot show. And that gate is built to sit **beside** a session, authenticating the
provider scheme itself so the two principals never merge. Here there is no session to sit beside, so
the shape would carry the gate's costs and none of its reason.

**Nesting the route under `/api/registration`.** It would inherit the group's policy and filter for
free. It would also blur "exactly one path creates an account": the group is where accounts are
created, and a route there that creates none is the kind of exception a later reader folds back
into the rule. `RegistrationRouteTests` keeps the locked sign-in on a list of its own for that
reason.

**`FindUserIdByFederatedCredentialAsync`, then an owner-scoped read of "the account's federated
credential".** Two reads. An email change committing between them retires the credential the token
named and files a replacement under another subject, and the second read finds the replacement — so
the session opens over a credential the token never vouched for. One read returning the row closes
that window.

**Reading the account's credentials and choosing one.** A passkey or the recovery-code set would
open a `Full` session, which is the whole of what this route must refuse. The read is keyed on the
provider identity so that no other credential can be in its answer.

**Over a live full session, four other answers, and three other places for the check.**

- **Keep replacing.** That leaves the browser's drop as the guard, and the drop is skipped on an
  `unreachable` probe. A full session downgraded to a locked one is what this route must not cause.
- **Revoke the full session, then open a locked one.** The same downgrade, made permanent: the
  stronger sign-in is gone, not merely hidden behind a new cookie.
- **Answer `200` without replacing.** The body would say `"kind": "locked"` while the browser still
  holds a full session — a description of a session this request did not open.
- **Refuse a live locked session too.** A stale locked cookie would trap the browser: a release
  sign-in would answer `409` over a cookie the client cannot clear itself, because it is
  `HttpOnly`. Replacing a locked session costs nothing, since the new one reaches exactly as far.
- **An endpoint filter.** A type and a registration for one route, and its order against
  `RegistrationClaimGate` would hang on registration order, where the delegate's first statement
  runs after every filter by construction.
- **A flag on the command.** Application cannot see a cookie, and replacing one is transport.
- **A requirement on the route's policy.** That policy authenticates the provider scheme alone, so
  a requirement on it never sees the cookie's principal.

**`full_session` is a conflict kind and not a `refusal` word**, because every `409` in the product
carries a `conflictKind` — see [payees.md](../business-logic/payees.md).

## Consequences

- **Five paths establish a session, and this is the one whose session is `Locked`.** The other four
  open `Full`. [sessions.md](../business-logic/sessions.md) owns the count and the order rule.
- **`JwtBearer` is reached by two policies — the registration group's and this route's — and by
  `ProviderAuthorizationGate` on the email change.** `RegistrationRouteTests` reads the policy set
  off the route table against two written-out lists, registration's and this route's, and the gate's
  marker beside them. ADRs 0019, 0021 and 0027 carry amended lines saying so.
- **The locked gate is reachable from a live route.** `LockedSignInEndpointTests` follows a real
  sign-in, on the real `JwtBearer` handler and the app role, to a `403` on `GET /api/accounts`, a
  `200` reading `"kind": "locked"` on `GET /api/me/session`, and a `204` on the sign-out.
- **The email change's sweep of the federated credential now finds sessions a real sign-in opened**,
  so `sessionsEnded` can count them. See [email-change.md](../business-logic/email-change.md).
- **A race is recorded rather than handled.** An erasure committing between the discovery read and
  the save removes the credential the session names; the insert fails its foreign key with `23503`
  and the request answers `500`, and a retry finds no credential and answers `404`. Nothing in the
  suite drives that interleaving.
- **An erased account cannot come back through this route.** Its subject matches no credential, so
  a provider token outliving the erasure meets the `404` and writes nothing.
- **The handler takes no logger**, for the reason `ScheduleErasureHandler` takes none: it holds the
  id of an account that may have asked to be forgotten, and this is the sign-in such a person
  arrives on.
- **A provider token's freshness is bounded by its `exp` alone**, as on the email change. The
  session it opens can be ended here; the token cannot.
- **`full_session` joins the conflict vocabulary**, and it is the one conflict the Api ring raises:
  the fact it judges is the request's own cookie, which Application cannot see.
  `ConflictKindSpellingTests` pins the token and `ConflictKindDispositionCensusTests` pins
  `SessionEndpoints.cs` as its one site. The web client has no branch for it:
  `ReleaseFlowService` reads any `409` as `undetermined`.
- **The cookie scheme is asked twice on this route** — as the default scheme before the route, and
  by name in the delegate. Whether the second call repeats the session lookup is not measured. The
  delegate reads that result for the kind alone and never merges it with the provider's principal.
