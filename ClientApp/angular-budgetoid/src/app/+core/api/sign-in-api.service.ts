// The two calls a returning person makes, and the only two in this client that
// reach the API holding nothing at all — no session, no bearer, no account
// identifier.
//
// Both legs are **anonymous**, which is a definition rather than a relaxation:
// signing in is the exchange that runs before anybody is signed in. Everything
// below follows from that one fact.
//
// `PasskeyEndpoints.cs` argues the server's half; where a decision here is
// really that file's, the comment points at it rather than restating it.
import { HttpClient, HttpContext } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { EXPECTS_UNAUTHENTICATED } from '@app-core/interceptors/session-expiry.interceptor';
import type {
  PasskeyAssertionPayload,
  PasskeyRequestOptionsJson,
} from '@app-core/security/webauthn-encoding';
import { ConfigurationService } from '@app-core/services/configuration.service';
import type { Observable } from 'rxjs';

// `BaseApiService` is deliberately not extended, for the reason
// `registration-api.service.ts` gives at its own class: both requests have to
// carry `EXPECTS_UNAUTHENTICATED`, that token rides on an `HttpContext`, and
// that class has no parameter for one. Widening its shared `post` so one caller
// can pass a context would put a parameter on nine other services' call sites
// that none of them may ever use. What extending would buy is the getter below:
// `apiBaseUrl` is read here exactly as that class reads it — **per request**,
// never copied at construction, for the reason that class states at its own
// getter.
@Injectable({ providedIn: 'root' })
export class SignInApiService {
  private readonly http = inject(HttpClient);
  private readonly configuration = inject(ConfigurationService);

  private get baseUrl(): string {
    return this.configuration.getConfig().apiBaseUrl;
  }

  /**
   * Mints the challenge this assertion is signed over.
   *
   * `POST` and not `GET`, as the server declares it: the call persists a nonce,
   * so it is neither safe nor idempotent, and a `GET` would be cacheable and
   * prefetchable — both of which spend challenges nobody asked for. There is no
   * body, and there is nothing the caller could put in one: the options carry
   * **no `allowCredentials`**, because an endpoint that answered "here are that
   * address's passkeys" for one caller and nothing for another is an
   * account-enumeration oracle.
   */
  public getRequestOptions(): Observable<PasskeyRequestOptionsJson> {
    return this.http.post<PasskeyRequestOptionsJson>(
      `${this.baseUrl}/api/passkeys/assertion/options`,
      null,
      { context: anonymousContext() },
    );
  }

  /**
   * Sends what the authenticator signed and, on 200, the browser holds a
   * session.
   *
   * **The response is declared `void` because nothing may read it.** What comes
   * back is `{kind,expiresAtUtc}`, and the same response carries the
   * `__Host-budgetoid-session` cookie — which is what authenticates every later
   * request. Reading `kind` back would be this client re-deciding, from a body
   * it cannot verify, a fact the cookie has already settled; publishing a
   * session off a JSON member is one refactor away from publishing one the
   * server never issued. The client's own statement about the session is
   * `SessionService.established()`, made because the server answered 200 at all.
   *
   * **Every refusal is one 401 carrying no cause.** Unknown credential, bad
   * signature, untrusted origin, spent challenge, counter regression and
   * user-handle mismatch are byte-identical on purpose
   * (`PasskeyVerificationExceptionHandler.cs`): a caller able to tell them apart
   * can discover which user handles are registered without ever holding a
   * credential. Nothing above this method may invent a distinction the server
   * refuses to make.
   */
  public assert(payload: PasskeyAssertionPayload): Observable<void> {
    return this.http.post<void>(
      `${this.baseUrl}/api/passkeys/assertion`,
      payload,
      { context: anonymousContext() },
    );
  }
}

// Both legs are made by a browser that holds no session of this product's, so a
// 401 from either is this route's verdict on this request and not a session
// ending. Without the token, `sessionExpiryInterceptor` reads one as a lapse,
// declares the session over and navigates to `/welcome` — which is the screen
// the person is already standing on, wondering why pressing the button reloaded
// the page and said nothing.
//
// A fresh context per request. `HttpContext` is mutable, and a shared instance
// would be one object every sign-in request in the visit reads and writes.
function anonymousContext(): HttpContext {
  return new HttpContext().set(EXPECTS_UNAUTHENTICATED, true);
}
