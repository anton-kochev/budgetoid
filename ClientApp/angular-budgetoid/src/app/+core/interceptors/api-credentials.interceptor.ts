import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { OAuthService } from 'angular-oauth2-oidc';

// The CSRF control. The server checks presence only, never the value — a
// checked value would be a shared secret shipped to every browser — so this
// names the client and nothing more.
const CLIENT_HEADER = 'X-Budgetoid-Client';
const CLIENT_NAME = 'budgetoid-web';

function originOf(url: string): string | null {
  try {
    return new URL(url).origin;
  } catch {
    // A relative URL, or anything else the constructor rejects. Nothing we can
    // resolve to an origin is our API.
    return null;
  }
}

// Not `url.startsWith(apiBaseUrl)`: with a base of `https://api.budgetoid.app`
// that admits `https://api.budgetoid.app.attacker.example`, a host anybody can
// register, and hands it the cookie and the bearer. Origins compare whole.
//
// Exported because `sessionExpiryInterceptor` asks the same question and must
// get the same answer. One definition with two importers rather than two
// definitions, because the drift is silent in both directions: a copy that
// widens signs the visitor out of this app over a 401 from somebody else's
// origin, and a copy that narrows leaves a lapsed session on screen with every
// read failing under it.
export function isApiRequest(url: string, apiBaseUrl: string): boolean {
  // Fail closed. `apiBaseUrl` is empty until `ConfigurationService.load()`
  // resolves, and an empty base must classify nothing as our API. `new URL('')`
  // throws, so the origin comparison below already answers false — this states
  // the rule rather than leaving it to rest on that.
  if (apiBaseUrl === '') {
    return false;
  }

  const apiOrigin = originOf(apiBaseUrl);

  return apiOrigin !== null && originOf(url) === apiOrigin;
}

// One predicate, three effects. The bearer lives here rather than in an
// interceptor of its own because a second interceptor would need a second copy
// of "is this our API?", and the two copies drift. It is also the only one of
// the three that is conditional on anything further: the cookie and the client
// header go on every API request, signed in or not. The bearer goes when
// sign-in leaves the identity provider; the other two stay.
export const apiCredentialsInterceptor: HttpInterceptorFn = (request, next) => {
  const { apiBaseUrl } = inject(ConfigurationService).getConfig();

  if (!isApiRequest(request.url, apiBaseUrl)) {
    return next(request);
  }

  const idToken = inject(OAuthService).getIdToken();
  const headers = request.headers.set(CLIENT_HEADER, CLIENT_NAME);

  return next(
    request.clone({
      withCredentials: true,
      headers: idToken
        ? headers.set('Authorization', `Bearer ${idToken}`)
        : headers,
    }),
  );
};
