import {
  HttpContext,
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
import {
  apiCredentialsInterceptor,
  EMAIL_CHANGE_PATH,
  LOCKED_SESSION_PATH,
} from './api-credentials.interceptor';
import { PROVIDER_CREDENTIAL } from './provider-credential.token';

// The origin the config file names. Written out rather than read from
// `public/assets/app-config.json` on purpose: this spec is about the predicate,
// not about which host the predicate is pointed at, and a spec that loads the
// real config would go red the day the API moves.
const API_BASE_URL = 'https://api.budgetoid.app';
const API_URL = `${API_BASE_URL}/api/me`;

// Registration's two routes, authenticated by the provider scheme and nothing
// else, with the bearer read from storage. An account may not exist without a
// completed provider exchange, and registration is the one act that runs
// before this product has an identity of its own to present. The email change
// and the locked sign-in also take a Google bearer, each from its own request
// rather than storage; their rules are below.
const REGISTRATION_OPTIONS_URL = `${API_BASE_URL}/api/registration/options`;
const REGISTRATION_URL = `${API_BASE_URL}/api/registration`;

// The anonymous assertion legs, and the reason the narrowing is a fix rather
// than tidying. `SessionService` discards the provider token once this tab
// holds a session, but a person who abandons registration keeps it until then,
// and their next act is usually a passkey sign-in — so without the narrowing
// these two would carry a provider credential to routes that neither read it
// nor could act on it. A credential travelling further than it is needed is
// the defect, whether or not anything reads it: every hop it makes is another
// log, proxy and error report it can be recorded in, and another handler that
// could start reading it later without anyone deciding to.
const ASSERTION_OPTIONS_URL = `${API_BASE_URL}/api/passkeys/assertion/options`;
const ASSERTION_URL = `${API_BASE_URL}/api/passkeys/assertion`;

// A real other-origin request this app actually makes. `AuthService.initialize`
// calls `loadDiscoveryDocument`, which fetches exactly this URL
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

  // Both legs, not one. They are separate routes with separate handlers, and an
  // implementation that matched only the finish leg would leave the flow
  // failing at its first request with a 401 nothing on screen can explain.
  it("carries the provider's token to the routes that authenticate with it", () => {
    // Arrange
    const options = new HttpRequest<unknown>(
      'POST',
      REGISTRATION_OPTIONS_URL,
      null,
    );
    const finish = new HttpRequest<unknown>('POST', REGISTRATION_URL, {
      factorId: 'f',
    });

    // Act
    const forwardedOptions = forwardedRequest(options);
    const forwardedFinish = forwardedRequest(finish);

    // Assert
    expect(forwardedOptions.headers.get(AUTHORIZATION_HEADER)).toBe(
      `Bearer ${ID_TOKEN}`,
    );
    expect(forwardedFinish.headers.get(AUTHORIZATION_HEADER)).toBe(
      `Bearer ${ID_TOKEN}`,
    );
  });

  // An id token is held throughout, which is the whole point: the browser of
  // somebody who started registering and stopped holds one for an hour, and
  // every request it makes in that hour is one of these. The two assertion
  // legs are named explicitly because they are the requests that browser
  // actually goes on to make — a provider credential presented to an anonymous
  // route that will never read it.
  //
  // The offending URLs are collected rather than asserted one by one so a
  // failure names which route still carries the token instead of reporting
  // `true !== false`.
  it('carries no provider token to any other API route', () => {
    // Arrange
    const requests = [
      apiGet(),
      new HttpRequest<unknown>('POST', ASSERTION_OPTIONS_URL, null),
      new HttpRequest<unknown>('POST', ASSERTION_URL, { id: 'c' }),
    ];

    // Act
    const forwarded = requests.map((request) => forwardedRequest(request));

    // Assert
    const carriers = forwarded
      .filter((one) => one.headers.has(AUTHORIZATION_HEADER))
      .map((one) => one.url);

    expect(carriers).toEqual([]);
  });

  // Exact, never a suffix: a registration path under another prefix — or
  // behind a doubled slash — is another route, and the stored token is the
  // one credential here an abandoned trip leaves lying around.
  it.each([
    { shape: 'the finish leg under a prefix', path: '/v1/api/registration' },
    {
      shape: 'the finish leg behind a doubled slash',
      path: '//api/registration',
    },
    {
      shape: 'the options leg under a prefix',
      path: '/v1/api/registration/options',
    },
    {
      shape: 'the options leg behind a doubled slash',
      path: '//api/registration/options',
    },
  ])('carries no provider token to $shape', ({ path }) => {
    // Arrange
    const request = new HttpRequest<unknown>(
      'POST',
      `${API_BASE_URL}${path}`,
      null,
    );

    // Act
    const forwarded = forwardedRequest(request, { idToken: ID_TOKEN });

    // Assert
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
  });

  // The narrowing touches the bearer and nothing else. Without this, an
  // implementation that narrowed the whole interceptor to the registration
  // routes would satisfy both tests above while costing every other request in
  // the product its session cookie and its client header — a 403 on every
  // route, from a change that read as a tightening.
  it('still carries the cookie and the client header everywhere', () => {
    // Arrange
    const requests = [
      new HttpRequest<unknown>('POST', REGISTRATION_OPTIONS_URL, null),
      new HttpRequest<unknown>('POST', REGISTRATION_URL, { factorId: 'f' }),
      apiGet(),
      new HttpRequest<unknown>('POST', ASSERTION_OPTIONS_URL, null),
      new HttpRequest<unknown>('POST', ASSERTION_URL, { id: 'c' }),
    ];

    // Act
    const forwarded = requests.map((request) => forwardedRequest(request));

    // Assert
    const stripped = forwarded
      .filter((one) => !one.withCredentials || !one.headers.has(CLIENT_HEADER))
      .map((one) => one.url);

    expect(stripped).toEqual([]);
  });

  // The other-origin rule, restated on a registration path because the
  // narrowing gives it a new way to fail. The two negative controls above are
  // written against `/api/me`, so an implementation that decided "is this
  // registration?" from the path alone would pass them and still hand the
  // provider's token to `api.budgetoid.app.attacker.example`, a host anybody
  // can register. Which origin a request is going to has to be settled before
  // which route it is asking for.
  it('sends nothing to another origin, registration path included', () => {
    // Arrange
    const request = new HttpRequest<unknown>(
      'POST',
      `${API_BASE_URL}.attacker.example/api/registration`,
      { factorId: 'f' },
    );

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.withCredentials).toBe(false);
    expect(forwarded.headers.has(CLIENT_HEADER)).toBe(false);
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
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

// The email change's request: the one route authenticated by the session
// cookie **and** a provider token, which arrives through the request's own
// context rather than out of the library's storage. The address is spelled out
// here rather than built from `EMAIL_CHANGE_PATH`, so a constant that drifted
// from the server's route cannot pass these cases by agreeing with itself.
const EMAIL_CHANGE_URL = `${API_BASE_URL}/api/me/email-change`;

// The token the settings screen was handed by the email-change return, and
// deliberately not `ID_TOKEN`: the library still holds that one in these cases,
// so a bearer equal to it means the interceptor read storage.
const HANDED_CREDENTIAL = 'handed.provider.credential';

function carrying(credential: string): HttpContext {
  return new HttpContext().set(PROVIDER_CREDENTIAL, credential);
}

describe('apiCredentialsInterceptor on the email change', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('names the email-change route the server declares', () => {
    // Act & Assert
    expect(EMAIL_CHANGE_PATH).toBe('/api/me/email-change');
  });

  // T9. The credential rides on the request, so it is exactly the one this
  // request was handed — never whatever the library happens to hold.
  it('sends the credential the request carries as the bearer, beside the cookie and the client header', () => {
    // Arrange
    const request = new HttpRequest<unknown>(
      'POST',
      EMAIL_CHANGE_URL,
      { credentialId: 'c' },
      { context: carrying(HANDED_CREDENTIAL) },
    );

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.headers.get(AUTHORIZATION_HEADER)).toBe(
      `Bearer ${HANDED_CREDENTIAL}`,
    );
    expect(forwarded.withCredentials).toBe(true);
    expect(forwarded.headers.has(CLIENT_HEADER)).toBe(true);
  });

  // The library's stored token is what the registration rule reads. On this
  // route it is never read: a request that was handed nothing carries nothing.
  it('sends no stored provider token to the email change when the request carries none', () => {
    // Arrange
    const request = new HttpRequest<unknown>('POST', EMAIL_CHANGE_URL, {
      credentialId: 'c',
    });

    // Act
    const forwarded = forwardedRequest(request, { idToken: ID_TOKEN });

    // Assert
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
    expect(forwarded.withCredentials).toBe(true);
  });

  // A credential on the context is not a licence to send it anywhere. Every
  // other route ignores it — including another `/api/me/*` route, which is where
  // a prefix rule would leak it first.
  it('ignores a carried credential on every other route', () => {
    // Arrange
    const context = carrying(HANDED_CREDENTIAL);
    const requests = [
      new HttpRequest<unknown>('GET', API_URL, { context }),
      new HttpRequest<unknown>(
        'POST',
        `${API_BASE_URL}/api/me/erasure`,
        { credentialId: 'c' },
        { context },
      ),
      new HttpRequest<unknown>('POST', ASSERTION_URL, { id: 'c' }, { context }),
    ];

    // Act
    const forwarded = requests.map((request) => forwardedRequest(request));

    // Assert
    const carriers = forwarded
      .filter((one) => one.headers.has(AUTHORIZATION_HEADER))
      .map((one) => one.url);

    expect(carriers).toEqual([]);
  });

  // The registration rule still decides the registration routes: the stored
  // token, whatever the context carries.
  it('leaves the registration routes to the registration rule when a credential is carried', () => {
    // Arrange
    const context = carrying(HANDED_CREDENTIAL);
    const options = new HttpRequest<unknown>(
      'POST',
      REGISTRATION_OPTIONS_URL,
      null,
      { context },
    );
    const finish = new HttpRequest<unknown>(
      'POST',
      REGISTRATION_URL,
      { factorId: 'f' },
      { context },
    );

    // Act
    const forwardedOptions = forwardedRequest(options);
    const forwardedFinish = forwardedRequest(finish);
    const withoutStoredToken = forwardedRequest(finish, { idToken: '' });

    // Assert
    expect(forwardedOptions.headers.get(AUTHORIZATION_HEADER)).toBe(
      `Bearer ${ID_TOKEN}`,
    );
    expect(forwardedFinish.headers.get(AUTHORIZATION_HEADER)).toBe(
      `Bearer ${ID_TOKEN}`,
    );
    expect(withoutStoredToken.headers.has(AUTHORIZATION_HEADER)).toBe(false);
  });

  // Origin first, path second — the order the file's header argues. A host
  // that extends the API's is somebody else's, whatever path it asks for.
  it('sends nothing to the email-change path on another origin', () => {
    // Arrange
    const request = new HttpRequest<unknown>(
      'POST',
      `${API_BASE_URL}.attacker.example/api/me/email-change`,
      { credentialId: 'c' },
      { context: carrying(HANDED_CREDENTIAL) },
    );

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.withCredentials).toBe(false);
    expect(forwarded.headers.has(CLIENT_HEADER)).toBe(false);
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
  });

  // Exact, never a prefix or a suffix: a segment below the route, a path that
  // merely starts with its spelling, and a path that merely ends with it are
  // all other routes.
  it.each([
    { shape: 'a segment below the route', path: '/api/me/email-change/x' },
    { shape: 'a path extending its spelling', path: '/api/me/email-changeX' },
    {
      shape: 'the route with a bare trailing slash',
      path: '/api/me/email-change/',
    },
    { shape: 'the route under a prefix', path: '/v1/api/me/email-change' },
    {
      shape: 'the route behind a doubled slash',
      path: '//api/me/email-change',
    },
  ])('sends no bearer to $shape', ({ path }) => {
    // Arrange
    const request = new HttpRequest<unknown>(
      'POST',
      `${API_BASE_URL}${path}`,
      { credentialId: 'c' },
      { context: carrying(HANDED_CREDENTIAL) },
    );

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
  });

  // An empty credential is no credential: `Bearer ` with nothing after it is a
  // malformed header the server answers with a refusal nothing on screen names.
  it('sends no bearer when the carried credential is empty', () => {
    // Arrange
    const request = new HttpRequest<unknown>(
      'POST',
      EMAIL_CHANGE_URL,
      { credentialId: 'c' },
      { context: carrying('') },
    );

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
    expect(forwarded.withCredentials).toBe(true);
  });
});

// The locked sign-in: `POST /api/locked-session` answers a Google sign-in on an
// account with no factors with a locked session. Like registration's two, it
// is reached carrying a provider token and no session it can use; unlike
// them, it opens a session for an account that already exists. Its
// bearer is the credential the release flow was handed by the provider return,
// carried on the request, exactly as the email change's is. Spelled out rather
// than built from `LOCKED_SESSION_PATH`, so a constant that drifted from the
// server's route cannot pass by agreeing with itself.
const LOCKED_SESSION_URL = `${API_BASE_URL}/api/locked-session`;

describe('apiCredentialsInterceptor on the locked sign-in', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('names the locked sign-in route the server declares', () => {
    // Act & Assert
    expect(LOCKED_SESSION_PATH).toBe('/api/locked-session');
  });

  it('sends the credential the request carries as the bearer, beside the cookie and the client header', () => {
    // Arrange
    const request = new HttpRequest<unknown>('POST', LOCKED_SESSION_URL, null, {
      context: carrying(HANDED_CREDENTIAL),
    });

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.headers.get(AUTHORIZATION_HEADER)).toBe(
      `Bearer ${HANDED_CREDENTIAL}`,
    );
    expect(forwarded.withCredentials).toBe(true);
    expect(forwarded.headers.has(CLIENT_HEADER)).toBe(true);
  });

  // The library still holds a token here — the harness hands one to every
  // case — and a request that was handed nothing must carry nothing. A
  // fallback to storage would send whatever an abandoned registration left.
  it('sends no stored provider token when the request carries none', () => {
    // Arrange
    const request = new HttpRequest<unknown>('POST', LOCKED_SESSION_URL, null);

    // Act
    const forwarded = forwardedRequest(request, { idToken: ID_TOKEN });

    // Assert
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
    expect(forwarded.withCredentials).toBe(true);
    expect(forwarded.headers.has(CLIENT_HEADER)).toBe(true);
  });

  // The carried credential is the bearer even when storage holds a different
  // one: equal to `ID_TOKEN` would mean storage was read first.
  it('prefers the carried credential over a stored one', () => {
    // Arrange
    const request = new HttpRequest<unknown>('POST', LOCKED_SESSION_URL, null, {
      context: carrying(HANDED_CREDENTIAL),
    });

    // Act
    const forwarded = forwardedRequest(request, { idToken: ID_TOKEN });

    // Assert
    expect(forwarded.headers.get(AUTHORIZATION_HEADER)).not.toBe(
      `Bearer ${ID_TOKEN}`,
    );
  });

  // Origin first, path second.
  it('sends nothing to the locked sign-in path on another origin', () => {
    // Arrange
    const request = new HttpRequest<unknown>(
      'POST',
      `${API_BASE_URL}.attacker.example/api/locked-session`,
      null,
      { context: carrying(HANDED_CREDENTIAL) },
    );

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.withCredentials).toBe(false);
    expect(forwarded.headers.has(CLIENT_HEADER)).toBe(false);
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
  });

  it.each([
    { shape: 'a segment below the route', path: '/api/locked-session/x' },
    { shape: 'a path extending its spelling', path: '/api/locked-sessionX' },
    {
      shape: 'the route with a bare trailing slash',
      path: '/api/locked-session/',
    },
    { shape: 'the route under /api/me', path: '/api/me/locked-session' },
    { shape: 'the route under a prefix', path: '/v1/api/locked-session' },
    { shape: 'the route behind a doubled slash', path: '//api/locked-session' },
  ])('sends no bearer to $shape', ({ path }) => {
    // Arrange
    const request = new HttpRequest<unknown>(
      'POST',
      `${API_BASE_URL}${path}`,
      null,
      { context: carrying(HANDED_CREDENTIAL) },
    );

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
  });

  it('sends no bearer when the carried credential is empty', () => {
    // Arrange
    const request = new HttpRequest<unknown>('POST', LOCKED_SESSION_URL, null, {
      context: carrying(''),
    });

    // Act
    const forwarded = forwardedRequest(request);

    // Assert
    expect(forwarded.headers.has(AUTHORIZATION_HEADER)).toBe(false);
    expect(forwarded.withCredentials).toBe(true);
  });

  // The route's own rule does not widen the email change's or registration's:
  // a carried credential still reaches the email change, and registration
  // still reads storage.
  it('leaves the other provider routes to their own rules', () => {
    // Arrange
    const context = carrying(HANDED_CREDENTIAL);
    const emailChange = new HttpRequest<unknown>(
      'POST',
      EMAIL_CHANGE_URL,
      { credentialId: 'c' },
      { context },
    );
    const registration = new HttpRequest<unknown>(
      'POST',
      REGISTRATION_URL,
      { factorId: 'f' },
      { context },
    );

    // Act
    const forwardedEmailChange = forwardedRequest(emailChange);
    const forwardedRegistration = forwardedRequest(registration);

    // Assert
    expect(forwardedEmailChange.headers.get(AUTHORIZATION_HEADER)).toBe(
      `Bearer ${HANDED_CREDENTIAL}`,
    );
    expect(forwardedRegistration.headers.get(AUTHORIZATION_HEADER)).toBe(
      `Bearer ${ID_TOKEN}`,
    );
  });
});
