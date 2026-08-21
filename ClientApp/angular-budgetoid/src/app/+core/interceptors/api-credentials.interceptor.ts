import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { OAuthService } from 'angular-oauth2-oidc';

// The CSRF control. The server checks presence only, never the value — a
// checked value would be a shared secret shipped to every browser — so this
// names the client and nothing more.
const CLIENT_HEADER = 'X-Budgetoid-Client';
const CLIENT_NAME = 'budgetoid-web';

// The two routes authenticated by the provider scheme and nothing else.
//
// Declared here and imported by `RegistrationApiService`, which builds the
// requests, rather than the other way round. Two modules have to agree about
// these two strings and a second spelling of either one fails silently in both
// directions: a path corrected only in the service loses the bearer and meets a
// 401 on the flow's first call, while a path corrected only here hands the
// provider's token to a route that has moved.
//
// This direction rather than the other for a reason beyond taste. The
// interceptor may not import the service: that file imports
// `EXPECTS_UNAUTHENTICATED` from `session-expiry.interceptor`, which imports
// `isApiRequest` from this one, so the edge would close a cycle. It is also the
// arrangement already used on both sides of that pair — `isApiRequest` is
// exported from here to the other interceptor, and `EXPECTS_UNAUTHENTICATED` is
// exported from there to `RegistrationApiService`. What an interceptor enforces
// is declared where it is enforced, and the caller imports it.
export const REGISTRATION_OPTIONS_PATH = '/api/registration/options';
export const REGISTRATION_PATH = '/api/registration';

function originOf(url: string): string | null {
  try {
    return new URL(url).origin;
  } catch {
    // A relative URL, or anything else the constructor rejects. Nothing we can
    // resolve to an origin is our API.
    return null;
  }
}

// Total, like `originOf`, though by the time the bearer is decided the URL has
// already parsed once. Neither the query string nor the fragment is part of the
// answer, which is what makes this a parse rather than a string comparison.
function pathnameOf(url: string): string | null {
  try {
    return new URL(url).pathname;
  } catch {
    return null;
  }
}

// A *path*, never a URL, and the parameter type is the argument. A predicate
// taking a whole URL would answer `true` for
// `https://api.budgetoid.app.attacker.example/api/registration` — a host anybody
// can register — and would be one refactor away from handing that host the
// provider's token. Taking a pathname makes that mistake unavailable: the caller
// has to have settled the origin to have a pathname at all.
//
// Exact matches, no prefixes. Every registration URL this client builds comes
// from the two constants above, so none of them carries a trailing slash or a
// segment underneath them, and `/api/registrations` is not a route this product
// has.
function isProviderAuthenticatedPath(path: string): boolean {
  return path === REGISTRATION_OPTIONS_PATH || path === REGISTRATION_PATH;
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
// header go on every API request, signed in or not.
//
// **The bearer did not leave with sign-in — it narrowed.** This comment used to
// say it would go when sign-in left the identity provider. Sign-in has left, and
// the token stayed, because the two registration routes are authenticated by the
// provider scheme and nothing else and always will be: an account may not exist
// without a completed provider exchange, so there is no first-party credential
// to present on the one call that creates the first-party account. Those two
// routes are now the only requests in the product that carry it.
//
// Worth narrowing rather than leaving alone, even though nothing outside
// registration reads the token: `RegisterService` discards it at the 201, but a
// browser that abandoned registration keeps it for the hour it lives, and what
// that person usually does next is sign in with a passkey — so both anonymous
// assertion legs were being handed a provider credential. Every hop a credential
// makes is another log, proxy and error report it can be recorded in, and
// another handler that could start reading it without anybody deciding to.
//
// The order of the two questions is the security property, not a style: which
// origin a request is going to is settled first, by `isApiRequest`, and only
// then which route it is asking for. Reversed — or folded into one path test —
// `https://api.budgetoid.app.attacker.example/api/registration` is a
// registration request, and a host anybody can register is handed the token.
export const apiCredentialsInterceptor: HttpInterceptorFn = (request, next) => {
  const { apiBaseUrl } = inject(ConfigurationService).getConfig();

  if (!isApiRequest(request.url, apiBaseUrl)) {
    return next(request);
  }

  const idToken = inject(OAuthService).getIdToken();
  const headers = request.headers.set(CLIENT_HEADER, CLIENT_NAME);
  const path = pathnameOf(request.url);
  const authenticatesWithProvider =
    path !== null && isProviderAuthenticatedPath(path);

  return next(
    request.clone({
      withCredentials: true,
      // Two conditions, and never folded into the guard above. A browser holding
      // a session and no id token is every browser after the provider drops out
      // of sign-in, and one that folded them would send that browser neither the
      // cookie nor the client header — a 403 on every route in the product.
      headers:
        authenticatesWithProvider && idToken
          ? headers.set('Authorization', `Bearer ${idToken}`)
          : headers,
    }),
  );
};
