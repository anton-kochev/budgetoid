import {
  HttpHeaders,
  HttpParams,
  HttpRequest,
  HttpResponse,
  type HttpHandlerFn,
} from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { OAuthService } from 'angular-oauth2-oidc';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import { apiCredentialsInterceptor } from './api-credentials.interceptor';

// The origin the config file names. Written out rather than read from
// `public/assets/app-config.json` on purpose: this spec is about the predicate,
// not about which host the predicate is pointed at, and a spec that loads the
// real config would go red the day the API moves.
const API_BASE_URL = 'https://api.budgetoid.app';
const API_URL = `${API_BASE_URL}/api/me`;

// A real other-origin request this app actually makes. `AuthService.initialize`
// calls `loadDiscoveryDocumentAndTryLogin`, which fetches exactly this URL
// through the same `HttpClient` the interceptor sits in front of. A contrived
// `https://example.com` would test the same branch while hiding what is at
// stake: `withCredentials` here attaches Google's cookies to a request this app
// makes on the user's behalf, and Google's CORS response carries no
// `Access-Control-Allow-Credentials`, so the request fails outright.
const OTHER_ORIGIN_URL =
  'https://accounts.google.com/.well-known/openid-configuration';

const ID_TOKEN = 'header.payload.signature';
const CLIENT_HEADER = 'X-Budgetoid-Client';
const AUTHORIZATION_HEADER = 'Authorization';

interface RunOptions {
  readonly apiBaseUrl?: string;
  readonly idToken?: string;
}

// A functional interceptor is a plain function, so it is called directly inside
// an injection context with a `next` that records what it was handed. That is
// the harness `guest.guard.spec.ts` already uses for the functional guard, and
// it is the right one here for two reasons beyond consistency: this repository
// provides `HttpClientTesting` nowhere — HTTP is stubbed at the service level
// with `useValue` — and the whole subject of these tests is *the request that
// reaches the next handler*, which a recording `next` hands over directly while
// `HttpTestingController` would only expose it after a round trip through the
// client's own request-building.
function forwardedRequest(
  request: HttpRequest<unknown>,
  options: RunOptions = {},
): HttpRequest<unknown> {
  const { apiBaseUrl = API_BASE_URL, idToken = ID_TOKEN } = options;

  // Reset first, so a test may run the interceptor more than once: the first
  // `runInInjectionContext` instantiates the injector, after which a second
  // `configureTestingModule` throws.
  TestBed.resetTestingModule();

  const configuration: Pick<ConfigurationService, 'getConfig'> = {
    getConfig: () => ({ apiBaseUrl, auth: {} }),
  };
  const oAuth: Pick<OAuthService, 'getIdToken'> = {
    getIdToken: () => idToken,
  };

  TestBed.configureTestingModule({
    providers: [
      { provide: ConfigurationService, useValue: configuration },
      { provide: OAuthService, useValue: oAuth },
    ],
  });

  const seen: HttpRequest<unknown>[] = [];
  const next: HttpHandlerFn = (forwarded) => {
    seen.push(forwarded);

    return of(new HttpResponse<unknown>({ status: 204, url: forwarded.url }));
  };

  // Subscribed rather than only called, because an implementation is free to
  // build its observable lazily (`defer`, `switchMap` off a signal). Without a
  // subscription such an implementation would forward nothing and every
  // assertion below would fail for the wrong reason.
  TestBed.runInInjectionContext(() =>
    apiCredentialsInterceptor(request, next),
  ).subscribe();

  const [only] = seen;

  expect(seen).toHaveLength(1);
  // Narrowing, not an assertion about behaviour: the line above already failed
  // if nothing was forwarded, and this keeps the return type non-optional
  // without a `!`.
  if (only === undefined) {
    throw new Error('The interceptor forwarded no request.');
  }

  return only;
}

function apiGet(): HttpRequest<unknown> {
  return new HttpRequest<unknown>('GET', API_URL);
}

describe('apiCredentialsInterceptor', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  // The negative control, and the reason the predicate exists at all. Every one
  // of the three effects is checked here, and an id token is deliberately
  // present: a spec that arranged no token would let an implementation pass
  // this line while attaching the bearer to every request that happens to be
  // made while signed in.
  it('sends no credentials, no client header and no bearer to another origin', () => {
    // Arrange
    const request = new HttpRequest<unknown>('GET', OTHER_ORIGIN_URL);

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.withCredentials).toBe(false);
    expect(forwarded.headers.has(CLIENT_HEADER)).toBe(false);
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
  });

  // The same danger reached from the near side. `startsWith` alone treats a
  // host that merely *extends* the configured origin as our API, and anybody
  // can register one: the cookie and the bearer would both go to it. This test
  // asks for a boundary — an origin comparison, or a base URL match that ends
  // at a path separator — and it is the one assertion in this file that the
  // interceptor being replaced does not already satisfy.
  it('sends nothing to a host that merely extends the API origin', () => {
    // Arrange
    const request = new HttpRequest<unknown>(
      'GET',
      `${API_BASE_URL}.attacker.example/api/me`,
    );

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.withCredentials).toBe(false);
    expect(forwarded.headers.has(CLIENT_HEADER)).toBe(false);
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
  });

  // Without this the `__Host-budgetoid-session` cookie is never sent — the app
  // and the API are same-site but cross-origin — and the request arrives
  // unauthenticated with nothing in the browser to say why.
  it('sends the session cookie to the API', () => {
    // Arrange
    const request = apiGet();

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.withCredentials).toBe(true);
  });

  // The value is deliberately unchecked by the server — a checked value would
  // be a shared secret shipped to every client — so presence and non-emptiness
  // are the whole contract, and pinning a particular string here would pin
  // something that is deliberately not a secret as though it were one.
  it('carries a non-empty client header on a state-changing request to the API', () => {
    // Arrange
    const request = new HttpRequest<unknown>('POST', API_URL, { amount: 1 });

    // Act
    const forwarded = forwardedRequest(request);
    const client = forwarded.headers.get(CLIENT_HEADER);

    // Assert
    expect(client).not.toBeNull();
    expect(client?.trim()).not.toBe('');
  });

  // A separate block from the one above rather than a second act inside it:
  // every route but `GET /health` answers 403 without the header, so a reader
  // is the route the rule is easiest to forget on.
  it('carries a non-empty client header on a read from the API', () => {
    // Arrange
    const request = apiGet();

    // Act
    const forwarded = forwardedRequest(request);
    const client = forwarded.headers.get(CLIENT_HEADER);

    // Assert
    expect(client).not.toBeNull();
    expect(client?.trim()).not.toBe('');
  });

  // The bearer survives this commit so every intermediate commit ships; it goes
  // when sign-in leaves the identity provider.
  it('carries the bearer to the API while there is an id token', () => {
    // Arrange
    const request = apiGet();

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.headers.get(AUTHORIZATION_HEADER)).toBe(
      `Bearer ${ID_TOKEN}`,
    );
  });

  // The mistake this catches is one line long and is exactly the shape of the
  // interceptor being replaced: `if (!idToken || !isApiRequest(...)) return
  // next(request)`. Folding the two conditions together means a signed-out
  // browser — one holding a valid session cookie but no id token, which is
  // every browser after the identity provider drops out of sign-in — sends the
  // cookie and the header nowhere.
  it('carries no bearer without an id token, while still sending the cookie and the header', () => {
    // Arrange
    const request = apiGet();

    // Act
    const forwarded = forwardedRequest(request, { idToken: '' });

    // Assert
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
    expect(forwarded.withCredentials).toBe(true);
    expect(forwarded.headers.has(CLIENT_HEADER)).toBe(true);
  });

  // Fail closed. An empty base URL is what the config holds before
  // `ConfigurationService.load()` resolves, and `''` is a prefix of every
  // string on earth — so a predicate that drops the emptiness check does not
  // merely misclassify one request, it hands credentials to all of them.
  it('treats an unconfigured apiBaseUrl as "no request is an API request"', () => {
    // Arrange
    const request = apiGet();

    // Act
    const forwarded = forwardedRequest(request, { apiBaseUrl: '' });

    // Assert
    expect(forwarded.withCredentials).toBe(false);
    expect(forwarded.headers.has(CLIENT_HEADER)).toBe(false);
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
  });

  // The interceptor clones and adds. `responseType` is in here because the
  // export writes the response bytes to disk unread, and a rebuilt request that
  // lost `'blob'` would hand an exact `numeric(14,4)` amount to `JSON.parse`
  // and turn it into a double.
  it('leaves the rest of the request untouched', () => {
    // Arrange
    const body = { amount: '12.3400' };
    const request = new HttpRequest<unknown>('POST', API_URL, body, {
      headers: new HttpHeaders({ 'Content-Type': 'application/json' }),
      params: new HttpParams().set('from', '2026-01-01'),
      responseType: 'blob',
      reportProgress: true,
    });

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.method).toBe('POST');
    expect(forwarded.url).toBe(API_URL);
    expect(forwarded.body).toBe(body);
    expect(forwarded.headers.get('Content-Type')).toBe('application/json');
    expect(forwarded.params.get('from')).toBe('2026-01-01');
    expect(forwarded.responseType).toBe('blob');
    expect(forwarded.reportProgress).toBe(true);
  });

  // Clone, not mutate. `HttpRequest` is documented as immutable and other
  // interceptors and retries may hold the instance that was handed in; an
  // implementation that reassigned onto it would leave that copy carrying
  // credentials it was never asked to carry.
  it('does not mutate the request it was handed', () => {
    // Arrange
    const request = apiGet();

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded).not.toBe(request);
    expect(request.withCredentials).toBe(false);
    expect(request.headers.has(CLIENT_HEADER)).toBe(false);
    expect(request.headers.has(AUTHORIZATION_HEADER)).toBe(false);
  });
});
