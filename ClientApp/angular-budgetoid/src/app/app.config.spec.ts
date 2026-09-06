import { HttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { MeApiService } from '@app-core/api/me-api.service';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { SessionService } from '@app-core/session/session.service';
import { OAuthService } from 'angular-oauth2-oidc';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { appConfig } from './app.config';

const API_BASE_URL = 'https://api.budgetoid.app';
const API_URL = `${API_BASE_URL}/api/me`;
const CLIENT_HEADER = 'X-Budgetoid-Client';

describe('appConfig', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    // The real providers, with only the backend swapped: everything
    // `provideHttpClient` set up — the interceptor chain included — is still the
    // one the application ships. `api-credentials.interceptor.spec.ts` calls the
    // function directly and so can never see whether anybody registered it; this
    // spec exists for exactly that half, and emptying the `withInterceptors([…])`
    // array in `app.config.ts` is what it goes red on.
    // `TestBed.inject` finalizes the test module, which runs the `APP_INITIALIZER`
    // from `core.providers.ts`, whose real dependencies fetch `app-config.json`,
    // then Google's discovery document, and ask `GET /api/me` who the visitor
    // is. The probe needs silencing for a second reason on top — it asks the
    // very URL this spec asserts on, so the real one leaves `expectOne` looking
    // at two matching requests.
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
    const auth: Pick<AuthService, 'initialize'> = {
      initialize: () => Promise.resolve(),
    };
    const me: Pick<MeApiService, 'getSessionOwner'> = {
      getSessionOwner: () =>
        of({
          budgetId: '3f5b0a91-7c24-4a1e-9d3b-6e8f0c2a5471',
          email: 'visitor@budgetoid.app',
        }),
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
});
