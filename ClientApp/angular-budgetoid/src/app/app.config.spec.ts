import { HttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { OAuthService } from 'angular-oauth2-oidc';
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
    // from `core.providers.ts`. Both of its dependencies are stubbed for that
    // reason as much as for the request: the real pair fetches `app-config.json`
    // and then Google's discovery document over the network.
    const configuration: Pick<ConfigurationService, 'getConfig' | 'load'> = {
      getConfig: () => ({ apiBaseUrl: API_BASE_URL, auth: {} }),
      load: () => Promise.resolve(true),
    };
    const auth: Pick<AuthService, 'initialize'> = {
      initialize: () => Promise.resolve(),
    };
    const oAuth: Pick<OAuthService, 'getIdToken'> = {
      getIdToken: () => '',
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
        { provide: OAuthService, useValue: oAuth },
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
});
