import { HttpContextToken } from '@angular/common/http';

// Set on the requests whose own verdict is a refusal: a passkey that did not
// verify, a recovery code that matched nothing, a cold load asking a browser
// that holds no cookie who it is. None of those is a session ending, because
// there is no session yet.
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
