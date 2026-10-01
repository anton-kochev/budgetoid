# ADR 0027 — Authenticate the email change on the session and a fresh provider token, side by side

- **Status:** Accepted. The server route is implemented, and the settings screen calls it.
- **Date:** 2026-09-30
- **Area:** API / Security (authentication schemes, route authorization, credential replacement)

## Context

An email change moves an existing account to the Google identity and address a fresh provider
sign-in asserts. It needs two facts no single credential in this product carries:

- **which account is changing** — the session cookie says that, and nothing else may, because
  [ADR 0019](0019-authenticate-a-request-from-a-first-party-session-cookie.md) made the cookie the
  only thing that names an account on a request;
- **which Google identity it is changing to, and that the caller controls it now** — only the
  provider's token says that.

The product already reads a provider token in one place: the `/api/registration` group, whose policy
names `ProviderAuthentication.SchemeName` ([ADR 0021](0021-make-registration-one-consented-act-and-derive-the-account-id-from-its-own-challenge.md)).
That shape fits registration because its caller holds no session: the provider's principal is the
only one on the request, and `HttpContext.User` is it. The email change's caller holds both, and the
two principals disagree about what `sub` means — the session's is this installation's account id,
the provider's is a Google subject. A filter reading the wrong one finds a perfectly usable `sub`
and files a credential under the account's own id.

The route also has to stay on the fallback policy. `FullSessionRequirement` rides there, and a
session a federated sign-in opened must not reach this route: the Google identity is exactly what
such a session holds.

## Decision

**The session authenticates the request on the fallback policy; the provider token is authenticated
separately, by an endpoint filter, and judged as a second proof beside it; and a fresh passkey
assertion is required as well.**

1. **The route declares no policy and no scheme.** `POST /api/me/email-change` inherits the fallback
   — the session cookie scheme, `RequireAuthenticatedUser`, `FullSessionRequirement` — like every
   route that has not argued its way out. A locked session answers `403`; a provider token with no
   cookie answers the fallback's own `401`.

2. **`ProviderAuthorizationGate`, an `IEndpointFilter`, authenticates the provider token itself.** It
   calls `HttpContext.AuthenticateAsync(ProviderAuthentication.SchemeName)` and judges **that**
   result, never `HttpContext.User`. `AuthenticateAsync` returns a result and replaces nothing, so
   the session stays the request's identity for everything else. The gate hands the route one value,
   `VouchedIdentity(Subject, Email)`, on `HttpContext.Features`, and the route reads it through
   `ProviderAuthorizationGate.IdentityOf`, which throws if the gate is absent rather than letting a
   route fall back to `HttpContext.User`. The body has no member for a subject or an address.

3. **The claim checks are one definition, `ProviderClaims.RefusalFor`**, which
   `RegistrationClaimGate` calls too: a non-blank `sub` and `email`, and `email_verified` read by
   `bool.TryParse` as `true`. The verdict is shared; the response is not. This gate answers a `401`
   with a `refusal` member — `provider_token` or `email_unverified` — because on this route a `401`
   has three causes with three next steps, and the session is not among them.

4. **`RequireProviderAuthorization()` adds the filter and a marker, `RequiresProviderAuthorizationMetadata`,
   as one act.** An endpoint filter is a delegate and leaves no trace in a route's metadata — a dump
   of the email change's `RouteEndpoint.Metadata` shows the marker and no filter type (measured).
   The marker is what lets a census read, off the route table, which routes reach the provider
   scheme this way. Its constructor is `internal`, so a route cannot carry the marker without the
   filter from outside `Api`.
   `RegistrationRouteTests.TheProviderScheme_IsReachedByExactlyTheRegistrationRoutesAndTheEmailChange`
   pins both halves against written-out sets: the routes whose policy names the scheme, and the
   routes carrying the marker.

5. **A fresh passkey assertion over a `reauthentication` challenge is required too**, verified by
   `PasskeyReauthentication` at the top of `ChangeEmailHandler`, outside the transaction, publishing
   no identity — erasure's gate, with erasure's reasons. Without it, a stolen full-session cookie
   plus the attacker's own Google account is enough to re-point the account's Google identity and
   address at the attacker: the session proves somebody holds the cookie, the provider token proves
   the caller controls the Google account they chose, and neither says the caller owns the account.

6. **The provider token is judged before the passkey.** A filter runs before the route delegate,
   and the passkey gate is inside the handler the delegate calls. The passkey gate consumes its
   nonce whatever happens next, so this order is what lets somebody whose Google sign-in lapsed
   sign in again and retry with the assertion they already made.

7. **Every passkey refusal in the product carries `refusal: "assertion"`**, written once by
   `PasskeyVerificationExceptionHandler`. It is a constant — it names the proof, never the check —
   so every passkey refusal stays byte-identical to every other and the enumeration argument that
   handler exists for is untouched.

## Alternatives considered

**A policy naming both schemes.** The obvious shape, and it breaks the one property that matters.
`AuthorizationMiddleware` authenticates every scheme a policy names and merges the principals into
`HttpContext.User`; the merged principal carries two `sub` claims, and whichever one a reader found
first decides whose identity is filed. It also takes the route off the fallback policy, so
`FullSessionRequirement` would have to be restated on it and a locked session would reach it the
day somebody forgot. And a claim check expressed as a policy requirement answers **403 with no
title**, collapsing "your token is unusable" and "your provider does not vouch for this address"
into one status — the argument `RegistrationClaimGate` already records.

**A policy naming only the provider scheme, with the session checked by hand.** The provider's
principal becomes `HttpContext.User` on a route that changes an account, and nothing in that policy
requires a session: a provider bearer alone would pass authorization, and both the session check and
the locked-session refusal would have to be restated in code the route table cannot show — the
opposite of the polarity argument [sessions.md](../business-logic/sessions.md) makes for
`FullSessionRequirement`.

**`JwtBearerEvents.OnTokenValidated`.** It runs earliest and can refuse, but the check becomes a
property of the **scheme** rather than of the route — it would run on registration too, and nothing
reading the route table would see it. A titled refusal from there needs `OnChallenge` written as
well. `RegistrationClaimGate` refuses it for the same reason.

**Judging the claims in the Application ring.** It needs a `ClaimsPrincipal` inside `Application`,
which the solution keeps out, or a verified-email member on the command, which
[users-and-ownership.md](../business-logic/users-and-ownership.md) argues against by name. The
boundary that already holds the principal is the API.

**A middleware reading the marker.** It would rebuild the deleted provisioning middleware's shape:
opt-in metadata, and silence when a route forgets it. The filter is added by the same call that adds
the marker, so the two cannot drift.

**No marker.** The census would read policies alone and report the registration routes as the only
ones reaching the provider scheme, while a third route did — a green that means the test stopped
looking.

**No passkey gate, or the passkey before the provider token.** Without it, the stolen-cookie case
above stands. Before the provider filter, every provider refusal would spend the challenge.

## Consequences

- **`JwtBearer` has two readers**: the registration group's policy, and this filter. A bearer is a
  caller's only credential on the two registration routes; on the email change it is a second proof
  beside a session; everywhere else it authenticates nothing.
  [registration.md](../business-logic/registration.md), [sessions.md](../business-logic/sessions.md)
  and ADRs 0019 and 0021 say so.
- **The census is blind to a route that calls `AuthenticateAsync` with the provider scheme itself,
  or adds `ProviderAuthorizationGate` without the marker.** Review holds that.
- **A filter runs after model binding**, so a malformed body is a framework `400` before the token is
  looked at — the cost `RegistrationClaimGate` already accepts.
- **Freshness is bounded by the token's `exp` alone.** The bearer handler's only time check is
  `ValidateLifetime`; nothing reads `iat` or `auth_time`, so "fresh" means "unexpired".
- **A refused address (`400`) and every `409` come after the passkey gate**, so the challenge is
  spent and the person runs the ceremony again. That is the cost of decision 6, which keeps only the
  provider's refusals ahead of the gate.
- **`sessionsEnded` is `0` on every real account today.** The sweep finds only sessions the retired
  federated credential opened, and nothing opens a locked session yet.
- **There is no rate limit on the route**, as on the rest of the API.
- **Replacing the federated credential is delete plus insert in one save**, because `credentials`
  holds no `UPDATE`. `users.email` has two writers: registration's insert, and this route's
  `UPDATE (email)`. Both are argued in [email-change.md](../business-logic/email-change.md).
- **The client reads the email change's answer before the session probe and carries the bearer on
  the request.** `SessionService` still discards the provider's tokens once a session begins; the
  return is read first, so that discard finds nothing, and `apiCredentialsInterceptor` attaches
  this route's bearer from the request context rather than from storage.
  [email-change.md](../business-logic/email-change.md) and
  [sessions.md](../business-logic/sessions.md) state both.
