// Construction order, which is the one thing nothing in this suite tested.
//
// Every other spec that touches an API service builds it against a
// `ConfigurationService` stub that already holds the base URL, so all of them
// assert what happens *after* construction and none of them can see when the
// base URL was read. That gap shipped a defect: `SessionService` injects
// `MeApiService` as a field initializer and sits in the `deps` of the
// `APP_INITIALIZER`, Angular builds everything in `deps` to call the factory,
// and the factory body is where `await config.load()` lives. So the API service
// was built while the configuration still held `''`, and a base URL read once
// in a constructor was `''` for the rest of the visit — `GET /api/me` went to
// this app's own origin on every cold load while every later call went to the
// API.
//
// This file drives the real `APP_INITIALIZER` with the real `SessionService`
// and the real `MeApiService`, and swaps only the two dependencies that would
// otherwise reach the network for something other than the subject.
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideAppCore } from '@app-core/core.providers';
import { AuthService } from '@app-core/services/auth-service';
import { ConfigurationService } from '@app-core/services/configuration.service';
import { SessionService } from '@app-core/session/session.service';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { type MeDto } from './me-api.service';

const API_BASE_URL = 'https://api.budgetoid.app';
const ME: MeDto = { email: 'owner@budgetoid.test' };

interface Boot {
  // Releases the configuration the way the real `load()` releases it: the
  // promise settles and `getConfig()` starts answering with the fetched base
  // URL. Before this call the stub answers `''`, which is exactly what the real
  // service holds — see its `config` field initializer.
  readonly configurationLoaded: () => void;
  readonly initialized: Promise<unknown>;
}

// A macrotask, so every microtask the initializer chained has had its turn.
// `await Promise.resolve()` drains one link and would leave the probe's request
// unsent.
function afterPendingWork(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

// The real provider list with two dependencies swapped. `ConfigurationService`
// is stubbed because the real one fetches `assets/app-config.json` — and
// because withholding its answer is the whole arrangement here. `AuthService`
// is stubbed because the real one reaches Google's discovery document.
// `SessionService` and `MeApiService` are the production classes: the subject
// is where `MeApiService` sends its request, so a stub of either would be this
// file supplying the answer it then asserts.
function bootstrap(): Boot {
  let apiBaseUrl = '';
  let release = (): void => undefined;
  const loaded = new Promise<boolean>((resolve) => {
    release = () => {
      apiBaseUrl = API_BASE_URL;
      resolve(true);
    };
  });

  const configuration: Pick<ConfigurationService, 'getConfig' | 'load'> = {
    getConfig: () => ({ apiBaseUrl, auth: {} }),
    load: () => loaded,
  };
  const auth: Pick<AuthService, 'initialize'> = {
    initialize: () => Promise.resolve(),
  };

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideAppCore(),
      // After `provideAppCore()`, so these win over the classes it registers
      // itself.
      { provide: ConfigurationService, useValue: configuration },
      { provide: AuthService, useValue: auth },
    ],
  });

  // `TestBed.inject` finalizes the test module, and finalizing it is what
  // resolves the initializer's `deps` and then runs it. Resolving those `deps`
  // is the moment `MeApiService` is constructed, and it happens before the
  // factory body reaches `await config.load()`.
  return {
    configurationLoaded: release,
    initialized: TestBed.inject(ApplicationInitStatus).donePromise,
  };
}

describe('BaseApiService', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    httpMock.verify();
    TestBed.resetTestingModule();
  });

  // The defect, stated as the rule it broke: where a request goes is decided by
  // the configuration in force when the request is made, never by the
  // configuration in force when the service was built. Asserted on the whole
  // list of requests rather than through `expectOne`, so a failure names the URL
  // that was actually used instead of only saying nothing matched.
  it('sends to the configured base URL though it was built before the configuration resolved', async () => {
    // Arrange
    const boot = bootstrap();
    httpMock = TestBed.inject(HttpTestingController);

    // Act
    boot.configurationLoaded();
    await afterPendingWork();
    const requests = httpMock.match(() => true);

    // Assert
    expect(requests.map(({ request }) => request.url)).toEqual([
      `${API_BASE_URL}/api/me`,
    ]);
    requests.forEach((request) => request.flush(ME));
    await boot.initialized;
  });

  // The other half, and the reason the defect was invisible for so long: a
  // mis-addressed probe does not fail loudly. `/api/me` on this app's own origin
  // is answered **200 with `index.html`** — by the dev server's history
  // fallback and by Azure Static Web Apps' `navigationFallback` alike — and
  // under `responseType: 'json'` that body fails to parse, which Angular reports
  // as an `HttpErrorResponse` still carrying status 200. `readingOf` maps it to
  // `unreachable`, both guards admit `unreachable` deliberately, and the visitor
  // gets a painted screen whose every request then 401s.
  //
  // Green before the fix and after it, on purpose: it pins the consequence, so
  // nobody closes this by teaching the probe to read an unparseable 200 as a
  // signed-in visitor.
  it('does not report the visitor as authenticated when the probe never reached the API', async () => {
    // Arrange
    const boot = bootstrap();
    httpMock = TestBed.inject(HttpTestingController);
    const session = TestBed.inject(SessionService);
    boot.configurationLoaded();
    await afterPendingWork();

    // Act
    const [probe] = httpMock.match(() => true);
    probe.error(new ProgressEvent('parse'), {
      status: 200,
      statusText: 'OK',
    });
    await boot.initialized;

    // Assert
    expect(session.status()).not.toBe('authenticated');
  });

  // The positive counterpart, without which the test above passes on a client
  // that never authenticates anybody. It is also the whole point of the fix:
  // the probe reaches the API, the API answers, and the visitor is signed in
  // before the first route activates.
  it('reports the visitor as authenticated from the API`s own answer', async () => {
    // Arrange
    const boot = bootstrap();
    httpMock = TestBed.inject(HttpTestingController);
    const session = TestBed.inject(SessionService);
    boot.configurationLoaded();
    await afterPendingWork();

    // Act
    httpMock.expectOne(`${API_BASE_URL}/api/me`).flush(ME);
    await boot.initialized;

    // Assert
    expect(session.status()).toBe('authenticated');
  });
});
