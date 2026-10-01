// The one leg that mints a challenge for somebody who is already signed in.
//
// **Its own service rather than a method on `SignInApiService`, and the reason
// is that class's own header.** Both of that one's calls are anonymous by
// definition — signing in is the exchange that runs before anybody is signed in
// — and both carry `EXPECTS_UNAUTHENTICATED` so that `sessionExpiryInterceptor`
// reads their 401 as the route's verdict rather than as a session ending. This
// call is the opposite on both counts: it is made by a browser holding a
// session, and a 401 out of it *is* a session that ended and must reach the
// interceptor unmarked. A third method over there would be a class whose stated
// invariant had one exception in it.
//
// **`/api/passkeys/reauthentication/options` and never
// `/api/passkeys/assertion/options`.** The two mint into different nonce pools,
// and `PasskeyEndpoints.cs` puts this one in the authenticated group on purpose:
// it is the nonce that authorizes destroying an account, so an anonymous caller
// able to obtain one would make every re-authentication gate in the product
// worth nothing. A begin whose assertion was signed over an assertion-pool
// challenge is refused by the route, which is a failure no spec over the flow
// above this would otherwise see.
import { Injectable } from '@angular/core';
import type { PasskeyRequestOptionsJson } from '@app-core/security/webauthn-encoding';
import type { Observable } from 'rxjs';
import { BaseApiService } from './base-api.service';

@Injectable({ providedIn: 'root' })
export class ReauthenticationApiService extends BaseApiService {
  /**
   * Mints the challenge a re-authenticating assertion is signed over.
   *
   * `POST` and not `GET`, as the server declares it: the call persists a nonce,
   * so it is neither safe nor idempotent, and a `GET` would be cacheable and
   * prefetchable — both of which spend challenges nobody asked for. There is no
   * body and there is nothing a caller could put in one: the options carry no
   * `allowCredentials`, for the enumeration-oracle reason
   * `sign-in-api.service.ts` argues at its own options leg.
   */
  public getRequestOptions(): Observable<PasskeyRequestOptionsJson> {
    return this.post<PasskeyRequestOptionsJson>(
      'api/passkeys/reauthentication/options',
      null,
    );
  }
}
