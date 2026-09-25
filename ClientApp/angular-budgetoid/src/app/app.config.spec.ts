import { HttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ApplicationInitStatus, ErrorHandler } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import {
  KeyRotationApiService,
  type KeyRotationStateDto,
} from '@app-core/api/key-rotation-api.service';
import { MeApiService } from '@app-core/api/me-api.service';
import { FailureErrorHandler } from '@app-core/logging/failure-error-handler';
import { FailureOAuthLogger } from '@app-core/logging/failure-oauth-logger';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { SessionService } from '@app-core/session/session.service';
import { OAuthLogger, OAuthService } from 'angular-oauth2-oidc';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  expectOneErrorLine,
  spyOnEveryConsoleMethod,
  type ConsoleSpies,
} from '../testing/console-spies';
import { appConfig } from './app.config';

const API_BASE_URL = 'https://api.budgetoid.app';
const API_URL = `${API_BASE_URL}/api/me`;
const CLIENT_HEADER = 'X-Budgetoid-Client';

describe('appConfig', () => {
  let httpMock: HttpTestingController;
  // How many times bootstrapping asked whether a rotation is staged. Counted
  // rather than spied, because the count is what the third test below asserts
  // and a spy would need restoring — nothing in this project configures
  // `restoreMocks`.
  let rotationStateReads: number;

  beforeEach(() => {
    rotationStateReads = 0;
    // The real providers, with only the backend swapped: everything
    // `provideHttpClient` set up — the interceptor chain included — is still the
    // one the application ships. `api-credentials.interceptor.spec.ts` calls the
    // function directly and so can never see whether anybody registered it; this
    // spec exists for exactly that half, and emptying the `withInterceptors([…])`
    // array in `app.config.ts` is what it goes red on.
    // `TestBed.inject` finalizes the test module, which runs the `APP_INITIALIZER`
    // from `core.providers.ts`, whose real dependencies fetch `app-config.json`,
    // ask `GET /api/me` who the visitor is, and — on a page the provider
    // redirected back to — fetch Google's discovery document. The probe needs
    // silencing for a second reason on top — it asks the very URL this spec
    // asserts on, so the real one leaves `expectOne` looking at two matching
    // requests.
    //
    // It is silenced at `MeApiService` rather than at `SessionService`, which
    // is what a reader will expect. `SessionService` is the application's single
    // owner of "the session ended", and the second test below watches the real
    // one make that transition; stubbing it would leave that test asserting a
    // value written by this file. Cutting the probe off at the API service
    // removes the request just as completely — `getSessionOwner()` never reaches
    // `HttpClient` — so the first test still sees exactly one request.
    //
    // `getSessionOwner` is the method `probe()` calls: the same `/api/me` route
    // as `getMe`, asked whether there is a session at all rather than for the
    // address to render, and the only one of the two that carries
    // `EXPECTS_UNAUTHENTICATED`. Stubbing the wrong one leaves the probe
    // throwing a `TypeError` that `probe()` swallows into `'unreachable'` —
    // both tests below still pass, and the silencing this comment describes is
    // no longer happening.
    const configuration: Pick<ConfigurationService, 'getConfig' | 'load'> = {
      getConfig: () => ({ apiBaseUrl: API_BASE_URL, auth: {} }),
      load: () => Promise.resolve(true),
    };
    const auth: Pick<AuthService, 'initialize' | 'isProviderReturn'> = {
      initialize: () => Promise.resolve(),
      isProviderReturn: () => false,
    };
    const me: Pick<MeApiService, 'getSessionOwner'> = {
      getSessionOwner: () =>
        of({
          budgetId: '3f5b0a91-7c24-4a1e-9d3b-6e8f0c2a5471',
          email: 'visitor@budgetoid.app',
        }),
    };
    // The initializer's second read, silenced at its API service for the reason
    // the probe is silenced at `MeApiService`: the real one reaches
    // `HttpClient`, and a request nothing flushes fails `httpMock.verify()` in
    // every case in this file. `KeyRotationService` itself stays real, so the
    // third test watches production code make the call rather than a stub this
    // file wrote reporting itself.
    const rotationApi: Pick<KeyRotationApiService, 'getRotationState'> = {
      getRotationState: () => {
        rotationStateReads += 1;

        return of<KeyRotationStateDto>({ rotation: null });
      },
    };
    const oAuth: Pick<OAuthService, 'getIdToken'> = {
      getIdToken: () => '',
    };
    // The real `Router` would run a real navigation out of a test that has no
    // application on screen. Both methods are stubbed, not just the one the
    // interceptor happens to call today: which of them takes the browser to
    // `/welcome` is a choice `session-expiry.interceptor.spec.ts` deliberately
    // leaves to the implementation, and a stub missing the other one would turn
    // that free choice into a `TypeError` here.
    const router: Pick<Router, 'navigate' | 'navigateByUrl'> = {
      navigate: () => Promise.resolve(true),
      navigateByUrl: () => Promise.resolve(true),
    };

    TestBed.configureTestingModule({
      providers: [
        ...appConfig.providers,
        provideHttpClientTesting(),
        // After the spread, so these win. The config stub is what makes the
        // assertions mean anything: the real service holds an empty `apiBaseUrl`
        // until `load()` resolves against the real network, the interceptor's
        // predicate would correctly answer "not our API", and the test would go
        // red for a reason that has nothing to do with registration.
        { provide: ConfigurationService, useValue: configuration },
        { provide: AuthService, useValue: auth },
        { provide: MeApiService, useValue: me },
        { provide: KeyRotationApiService, useValue: rotationApi },
        { provide: OAuthService, useValue: oAuth },
        { provide: Router, useValue: router },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    TestBed.resetTestingModule();
  });

  // Without the registration every request in the product loses the session
  // cookie and the CSRF header, so the server answers 403 to all of them — and
  // the build stays green, because nothing else in the suite reaches `HttpClient`
  // through the application's own providers. The bearer is deliberately not
  // asserted: it leaves when sign-in leaves the identity provider, these two do
  // not.
  it('registers the API credentials interceptor with HttpClient', () => {
    // Arrange
    const client = TestBed.inject(HttpClient);

    // Act
    client.get(API_URL).subscribe();
    const { request } = httpMock.expectOne(API_URL);
    const clientHeader = request.headers.get(CLIENT_HEADER);

    // Assert
    expect(request.withCredentials).toBe(true);
    // Both lines, because a missing header reads back as `null` and `null?.trim()`
    // is `undefined`, which is not `''` — the emptiness check alone would pass on
    // the very absence it is here to catch.
    expect(clientHeader).not.toBeNull();
    expect(clientHeader?.trim()).not.toBe('');
  });

  // The other half of the same hole. Dropping `sessionExpiryInterceptor` from
  // the `withInterceptors([…])` array costs the application its only owner of
  // "the session ended" — no 401 anywhere declares the session over or leaves
  // for `/welcome` — and nothing else notices, because both
  // `session-expiry.interceptor.spec.ts` and `session.service.spec.ts` call
  // their functions directly.
  //
  // The assertion is on the real `SessionService`'s state rather than on a
  // navigation, for two reasons. The destination and the `Router` method that
  // reaches it are the sibling spec's business, and it declines to pin the
  // method on purpose — restating either here would make a free implementation
  // choice fail this file. And the state is written by production code: a spied
  // `ended()` or a hand-rolled fake would have this file supply the value it
  // then asserts.
  it('registers the session expiry interceptor with HttpClient', () => {
    // Arrange
    const client = TestBed.inject(HttpClient);
    const session = TestBed.inject(SessionService);

    // Act
    // The interceptor re-throws, so the 401 arrives at this subscriber. Without
    // an error handler it would surface as an unhandled rejection and fail the
    // test for a reason that is not the subject.
    client.get(API_URL).subscribe({ error: () => undefined });
    httpMock
      .expectOne(API_URL)
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    // Assert
    expect(session.status()).toBe('anonymous');
  });

  // The third registration this file holds, and it is here for the same reason
  // the two above are. `core.providers.spec.ts` calls `provideAppCore()` itself,
  // so it can no more see whether the **application** registers it than an
  // interceptor's own spec can see whether it is in the chain: dropping
  // `provideAppCore()` from `app.config.ts` leaves that file green and this one
  // red.
  //
  // What it costs when it goes missing is a reload made during a key rotation
  // drawing a list of half em dashes — the three content screens read "a run is
  // in flight" from a signal nothing has written.
  it('runs the core initializer, which asks whether a rotation is staged', async () => {
    // Arrange — the initializer runs when the module is finalized, which
    // `TestBed.inject` in `beforeEach` has already done; what is outstanding is
    // the chain of promises it awaited.

    // Act
    await TestBed.inject(ApplicationInitStatus).donePromise;

    // Assert — once, for a visitor the probe found authenticated.
    expect(rotationStateReads).toBe(1);
  });
});

// The logging funnel's registrations, held here for the reason the three pins
// above are: `failure-error-handler.spec.ts` and `failure-oauth-logger.spec.ts`
// construct their classes directly and can never see whether the application
// provides them. Removing `provideFailureLogging()` from `app.config.ts`, or
// placing it before `provideOAuthClient()` so the library's console logger
// wins, is what these go red on.
//
// Its own `describe` because it needs one thing the block above does not:
// `rethrowApplicationErrors: false`. TestBed's default wraps the application's
// error handler in one that calls it and then **rethrows** — inside the
// window's `error` listener that throw lands before `preventDefault()`, so the
// event would never end prevented however correct the registration. `false`
// hands errors to the registered `ErrorHandler` and returns, which is what a
// browser running the application does. The stubs are the block above's,
// restated rather than shared so that block's `beforeEach` stays as it is.
// zone.js reads it as `Zone[__symbol__('ignoreConsoleErrorUncaughtError')]`:
// a property of the `Zone` constructor, never of `window`.
const ZONE_UNCAUGHT_FLAG = '__zone_symbol__ignoreConsoleErrorUncaughtError';

// The `Zone` constructor, or a thrown precondition: a runner without zone.js
// would leave the flag pin passing or failing for a reason that is not the
// subject.
function zoneGlobal(): object {
  const zone: unknown = Reflect.get(globalThis, 'Zone');
  if (typeof zone !== 'function') {
    throw new Error('zone.js is not loaded under this runner.');
  }

  return zone;
}

describe('appConfig failure logging', () => {
  const EMAIL = 'alice@example.test';
  let spies: ConsoleSpies;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    spies = spyOnEveryConsoleMethod();
    // Before the module is finalized below, which is when the initializers
    // run: an earlier case, or an earlier file sharing this worker, may have
    // left the flag set, and the pin has to watch this module set it.
    Reflect.deleteProperty(zoneGlobal(), ZONE_UNCAUGHT_FLAG);

    const configuration: Pick<ConfigurationService, 'getConfig' | 'load'> = {
      getConfig: () => ({ apiBaseUrl: API_BASE_URL, auth: {} }),
      load: () => Promise.resolve(true),
    };
    const auth: Pick<AuthService, 'initialize' | 'isProviderReturn'> = {
      initialize: () => Promise.resolve(),
      isProviderReturn: () => false,
    };
    const me: Pick<MeApiService, 'getSessionOwner'> = {
      getSessionOwner: () =>
        of({
          budgetId: '3f5b0a91-7c24-4a1e-9d3b-6e8f0c2a5471',
          email: 'visitor@budgetoid.app',
        }),
    };
    const rotationApi: Pick<KeyRotationApiService, 'getRotationState'> = {
      getRotationState: () => of<KeyRotationStateDto>({ rotation: null }),
    };
    const oAuth: Pick<OAuthService, 'getIdToken'> = {
      getIdToken: () => '',
    };
    const router: Pick<Router, 'navigate' | 'navigateByUrl'> = {
      navigate: () => Promise.resolve(true),
      navigateByUrl: () => Promise.resolve(true),
    };

    TestBed.configureTestingModule({
      providers: [
        ...appConfig.providers,
        provideHttpClientTesting(),
        { provide: ConfigurationService, useValue: configuration },
        { provide: AuthService, useValue: auth },
        { provide: MeApiService, useValue: me },
        { provide: KeyRotationApiService, useValue: rotationApi },
        { provide: OAuthService, useValue: oAuth },
        { provide: Router, useValue: router },
      ],
      rethrowApplicationErrors: false,
    });

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    // Destroys the environment injector, which is what removes the window
    // listeners — left installed they would answer the next file's events.
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    Reflect.deleteProperty(zoneGlobal(), ZONE_UNCAUGHT_FLAG);
  });

  it('tells zone.js not to print an uncaught error itself', async () => {
    // Arrange — zone.js prints an error escaping a zone task with
    // `console.error('Unhandled Promise rejection:', message, …, error)`
    // unless this flag is set on the `Zone` constructor, and that print
    // bypasses every handler registered here.

    // Act
    await TestBed.inject(ApplicationInitStatus).donePromise;

    // Assert
    expect(Reflect.get(zoneGlobal(), ZONE_UNCAUGHT_FLAG)).toBe(true);
  });

  it('routes an unhandled rejection through the funnel and claims it', async () => {
    // Arrange — the rejection half of `provideBrowserGlobalErrorListeners()`,
    // which listens for `unhandledrejection` and hands `event.reason` on. A
    // funnel that registered only an `error` listener leaves this one
    // unclaimed, and the browser prints the reason, message and all.
    await TestBed.inject(ApplicationInitStatus).donePromise;
    spies.error.mockClear();
    const event = new PromiseRejectionEvent('unhandledrejection', {
      cancelable: true,
      // Never settles: a rejected promise here would be a real unhandled
      // rejection in the runner rather than the synthetic one under test.
      promise: new Promise<never>(() => undefined),
      reason: new Error(EMAIL),
    });

    // Act
    window.dispatchEvent(event);

    // Assert
    expect(event.defaultPrevented).toBe(true);
    expectOneErrorLine(spies, 'Unhandled error', { kind: 'error' });
  });

  it('resolves the ErrorHandler to the failure funnel', () => {
    // Act
    const handler = TestBed.inject(ErrorHandler);

    // Assert
    expect(handler).toBeInstanceOf(FailureErrorHandler);
  });

  it('resolves the OAuth library’s logger to the failure funnel', () => {
    // Act
    const logger = TestBed.inject(OAuthLogger);

    // Assert
    expect(logger).toBeInstanceOf(FailureOAuthLogger);
  });

  it('routes an uncaught window error through the funnel and claims it', async () => {
    // Arrange — what the browser raises for an exception nobody caught. Left
    // unclaimed it is printed by the browser itself, message and all, and no
    // handler of ours ever sees it.
    await TestBed.inject(ApplicationInitStatus).donePromise;
    spies.error.mockClear();
    const event = new ErrorEvent('error', {
      cancelable: true,
      error: new Error(EMAIL),
      message: EMAIL,
    });

    // Act
    window.dispatchEvent(event);

    // Assert — claimed, so the browser prints nothing of its own, and the one
    // line is the projection.
    expect(event.defaultPrevented).toBe(true);
    expectOneErrorLine(spies, 'Unhandled error', { kind: 'error' });
  });
});
