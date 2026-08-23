// The two calls that create an account, and the only two requests in this
// client that carry a whole card of recovery-code key custody.
//
// Both are made by a caller holding a provider token and no session of this
// product's, which is what shapes everything below: the requests are declared
// unauthenticated on purpose, the response of the second one is deliberately
// unread, and neither leg carries anything derived from a recovery code except
// the verifier and the two envelopes the codes step never sees.
//
// `RegistrationEndpoints.cs` argues every member of the request record this
// module builds; where a decision here is really that file's decision, the
// comment points at it rather than restating the argument.
import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import {
  REGISTRATION_OPTIONS_PATH,
  REGISTRATION_PATH,
} from '@app-core/interceptors/api-credentials.interceptor';
import { EXPECTS_UNAUTHENTICATED } from '@app-core/interceptors/session-expiry.interceptor';
import type { WrappedAccountKeys } from '@app-core/security/account-keys';
import type { RecoveryCodeVerifier } from '@app-core/security/recovery-codes';
import type {
  PasskeyCreationOptionsJson,
  PasskeyRegistrationPayload,
} from '@app-core/security/webauthn-encoding';
import { ConfigurationService } from '@app-core/services/configuration.service';
import type { Observable } from 'rxjs';

/**
 * One recovery code's whole share of the account, as it crosses the wire.
 *
 * Four members and no fifth, and **the code itself is not one of them** — see
 * `recovery-codes.ts` and ADR 0015. What the server learns about a code is the
 * verifier derived from it on one HKDF branch; the key-encryption key it also
 * derives, on an independent branch, never leaves the browser, and the two
 * envelopes below are what that key sealed.
 *
 * `WrappedAccountKeys` is **extended, never re-declared**. That interface
 * already spells `wrappedContentKey` and `wrappedIndexKey` exactly as the wire
 * does, and it is what `wrapAccountKeys` returns; a local copy of those two
 * members would be a second spelling of the two values an account is opened
 * with, able to drift from the one the sealing code hands back.
 */
export interface RecoveryCodeSubmissionBody extends WrappedAccountKeys {
  readonly verifier: RecoveryCodeVerifier;
  /**
   * The factor this code's two envelopes were sealed against, in the one
   * spelling `mintFactorId` emits. A factor is not a credential: a card of ten
   * codes is ten factors, so this value differs for every entry in the set.
   */
  readonly factorId: string;
}

/**
 * `RegistrationRequest` on the server, member for member.
 *
 * The passkey's three members are spread in from
 * {@link PasskeyRegistrationPayload}; the passkey factor's own two envelopes
 * arrive through {@link WrappedAccountKeys} for the reason the submission above
 * extends it.
 *
 * **The set member is `codes`, and `recoveryCodes` is not a synonym.** The
 * server's request-surface census refuses that token outright
 * (`RegistrationEndpoints.cs:171-179`) — it admits `recovery_code` only
 * directly in front of `hash`, so that any member able to hold key material has
 * to be looked at by a person. Renaming it here would be refused there.
 *
 * There is no `sub` and no `email`, and neither may ever be added: both are read
 * off the principal the request authenticated as, and a member a caller could
 * type would let one register an account under somebody else's provider
 * identity.
 */
export type RegistrationRequestBody = PasskeyRegistrationPayload &
  WrappedAccountKeys & {
    readonly factorId: string;
    readonly codes: readonly RecoveryCodeSubmissionBody[];
  };

// `BaseApiService` is deliberately not extended here, and the reason is the
// `HttpContext` below rather than a preference about base classes.
//
// Both requests have to carry `EXPECTS_UNAUTHENTICATED`, which rides on a
// request's context, and `BaseApiService` has no parameter for one. Adding an
// optional `context` to its `post` would widen the shared path of nine other
// services so that one caller can use it — a parameter present on every call
// site that must never be passed on any of them, which is the kind of widening
// the next caller reads as an invitation. What extending would buy is one line:
// `apiBaseUrl` is read here exactly as that class reads it, from
// `ConfigurationService`, **per request** — the getter below is that class's
// getter, and it is a getter for that class's reason. A field initializer would
// copy whatever the configuration held when this service was built, and `''` is
// what it holds until `load()` resolves; a service built before that copies the
// empty string and addresses every later request to this app's own origin,
// where the static host answers 200 with `index.html`. Nothing constructs this
// one that early today, which is a fact about today's injection graph rather
// than a property of this class. The `Content-Type` header that class attaches
// is not missed either — the options leg has no body for it to describe, which
// is the argument `getBlob` already makes in that file, and Angular sets it
// from the body on the leg that has one.
//
// **The two paths are imported, not written here.** `apiCredentialsInterceptor`
// has to recognise the same two routes to decide which requests still carry the
// provider's bearer, and a second spelling of either one is a silent failure in
// both directions: a path corrected only here loses the token and meets a 401 on
// the first call, and one corrected only there hands the token to a route that
// has moved. That file argues why the definition sits at the enforcing end —
// the same direction `EXPECTS_UNAUTHENTICATED` already travels into this one.
@Injectable({ providedIn: 'root' })
export class RegistrationApiService {
  private readonly http = inject(HttpClient);
  private readonly configuration = inject(ConfigurationService);

  private get baseUrl(): string {
    return this.configuration.getConfig().apiBaseUrl;
  }

  /**
   * Mints the challenge a registration answers, and the account identifier is
   * derived from it.
   *
   * `POST` and not `GET`, as the server declares it: this call spends a nonce,
   * so it is neither safe nor idempotent, and a `GET` would be cacheable and
   * prefetchable — both of which spend challenges nobody asked for. There is no
   * body; the address the options are built for is the one the provider
   * asserted, read off the principal on the server.
   */
  public getCreationOptions(): Observable<PasskeyCreationOptionsJson> {
    return this.http.post<PasskeyCreationOptionsJson>(
      `${this.baseUrl}${REGISTRATION_OPTIONS_PATH}`,
      null,
      { context: anonymousContext() },
    );
  }

  /**
   * Creates the account: the passkey, the card of codes, every factor's share
   * of the account keys, and the session, in one request.
   *
   * **The response is declared `void` because nothing may read it.** What comes
   * back is `{session:{kind,expiresAtUtc}}`, and the request that carries it
   * also sets the `__Host-budgetoid-session` cookie — which is what
   * authenticates every later request. Reading `kind` back would be this client
   * re-deciding, from a body it cannot verify, a fact the cookie has already
   * settled; publishing a session off a JSON member is one refactor away from
   * publishing one the server never issued. The client's own statement about
   * the session is `SessionService.established()`, made because the server
   * answered 201 at all.
   *
   * **There is no retry.** The challenge is consumed before anything is
   * verified (`RegisterAccountHandler.cs:152-165`), so re-sending a body that
   * failed meets the undifferentiated challenge refusal every time. Recovering
   * from a failure means a new challenge, which is a new call to
   * {@link getCreationOptions}.
   */
  public register(body: RegistrationRequestBody): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}${REGISTRATION_PATH}`, body, {
      context: anonymousContext(),
    });
  }
}

// Both legs are made by a browser that holds no session of this product's, so a
// 401 from either is the server's verdict on *this* request and not a session
// ending. Without the token, `sessionExpiryInterceptor` reads one as a lapse,
// declares the session over and navigates to `/welcome` — mid-flow, which on the
// second leg means walking away from a screen showing ten recovery codes
// somebody may already have written down, with no way back to them. That is
// reachable rather than theoretical: a provider id token lives an hour and a
// person can sit on the codes step for longer than that.
//
// A fresh context per request. `HttpContext` is mutable and a shared instance
// would be one object every registration request in the visit reads and writes.
function anonymousContext(): HttpContext {
  return new HttpContext().set(EXPECTS_UNAUTHENTICATED, true);
}
