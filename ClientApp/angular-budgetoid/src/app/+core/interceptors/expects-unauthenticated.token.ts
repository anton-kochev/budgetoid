import { HttpContextToken } from '@angular/common/http';

// Set on the requests whose 401 is the route's own verdict on that request
// rather than a session ending. Most are anonymous by definition: a passkey that
// did not verify, a recovery code that matched nothing, a cold load asking a
// browser that holds no cookie who it is — there is no session yet to end.
//
// **Some are made by a signed-in browser, and they belong here all the same.**
// `GET /api/me/account-keys` is read by `AccountKeyCustodyService`, which never
// calls anything on `SessionService`, because a key that will not open is not a
// session that ended. Unmarked, its 401 would make that call anyway through the
// interceptor — on the sign-in path, throwing somebody back to `/welcome` over a
// cookie that had not landed yet. A session that really has ended is caught by
// the next unmarked read. See `MeApiService.getAccountKeys()`.
//
// `POST /api/me/erasure` is answered 401 when its gate declines the fresh
// assertion it carries, before the transaction opens — and also, because the
// route sits behind the fallback authorization policy, when the session had
// already ended (expired, revoked, or erased from another tab) before the gate
// ran. Both mean this request erased nothing, and the erasure dialog says so as
// `refused`. Unmarked, `sessionExpiryInterceptor` reads the gate's verdict as a
// session ending and takes the tab to `/welcome` over a sentence the dialog
// never got to say. The mark is per request, not per route family: the
// re-authentication challenge minted just before it stays unmarked, so a 401
// there is the interceptor's fact. And the flow resolves a 401 on the erasing
// request with one unmarked probe (`GET /api/me`), so a session that had
// already ended is handed to the interceptor rather than read as a refused
// passkey.
//
// Carried on the request rather than read off a list of anonymous URLs kept
// inside the interceptor. A URL list would be a second definition of the
// anonymous surface, held in the client, drifting from the server's the first
// time a route moves — and the drift is silent: a route that fell out of the
// list ends the session of somebody who mistyped a recovery code.
//
// **A module of its own, holding the token and nothing else.** Three unrelated
// services set it — `RegistrationApiService`, `SignInApiService` and
// `MeApiService` — and one interceptor reads it, so the writers outnumber the
// reader and share nothing else with it. Declaring it inside the reader made
// every writer import `session-expiry.interceptor`, and with it that file's
// whole import graph, `SessionService` included — which is built on
// `MeApiService`, so that edge closed a cycle:
// `me-api.service` → `session-expiry.interceptor` → `session.service` →
// `me-api.service`. Nothing broke, because the token is read inside a function
// body and never at module evaluation; that is a fact about when the code runs,
// not a rule anything enforces. A declaration with no dependencies of its own
// cannot close a cycle whatever imports it, which is the property this file
// exists for. `isApiRequest` stays in `api-credentials.interceptor` for the
// opposite reason: it is one interceptor's predicate, read by one other, and
// both ends of that edge are already interceptors.
export const EXPECTS_UNAUTHENTICATED = new HttpContextToken<boolean>(
  () => false,
);
