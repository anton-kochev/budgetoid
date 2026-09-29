import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { OAuthService } from 'angular-oauth2-oidc';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { appConfig } from '../app.config';

// NFR-025, at the one moment every visitor shares: the cold load. The
// "the identity provider" describe in `core.providers.spec.ts` pins the
// initializer's decision over a stubbed `AuthService`, so it sees whether
// `initialize()` was *called* and nothing about what the real library does when
// it is built, handed storage it did not write, or asked nothing at all. This
// file boots the application's own providers — the real `AuthService`, the real
// `OAuthService` from `provideOAuthClient()`, the real `ConfigurationService`,
// `SessionService` and `KeyRotationService` — with the network swapped for
// `HttpTestingController`, then renders the first route through the real route
// table, and reads what was asked of whom.
//
// **Only the backend is swapped.** Every answer is given on the wire: the
// configuration file, `GET /api/me` (which is what decides who the visitor is),
// and the staged-rotation read. Anything else is answered as a network failure,
// so a boot that asks something new still finishes and still shows up in the
// census rather than hanging the case.
//
// What this cannot see, and what holds each instead:
// - A top-level navigation. `location.href = …` and the library's
//   `initLoginFlow()` leave by assigning the address, not by a request, and
//   jsdom does not navigate. `auth-service.spec.ts` holds the press that does
//   it, and `no-external-origins.spec.ts` refuses the argument-less `logOut()`.
// - A timer longer than the few macrotasks this waits. The library's
//   expiration timers — which is what `setupAutomaticSilentRefresh()` hangs its
//   hidden iframe on — fire at a fraction of the token's lifetime, which is
//   minutes, not ticks. `auth-service.spec.ts` ("schedules no background
//   renewal") holds that call by name.
// - A request made outside `HttpClient` — a bare `fetch`, an `<img>`, a
//   `<script>`. The library makes its requests through `HttpClient`; the
//   production bundle's origins are held by `no-external-origins.spec.ts`.
// - Any screen's work after its first render: a press, a later read.

const API_ORIGIN = 'https://api.budgetoid.app';
const DISCOVERY_URL =
  'https://accounts.google.com/.well-known/openid-configuration';

type Visitor = 'anonymous' | 'authenticated';

interface ColdBoot {
  // Every request the boot and the first render made, absolute, in order.
  readonly requested: readonly string[];
  // Whether the library itself judged the tab's provider tokens valid at the
  // moment it was built — the control on the abandoned-registration fixture.
  readonly heldValidProviderTokens: boolean;
}

// A macrotask, so every microtask a promise chain queued has had its turn.
function afterPendingWork(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

// The runner's own origin is the application's: the configuration file is
// fetched from it and the provider redirects back to it.
function appOrigin(): string {
  return document.location.origin;
}

function absolute(url: string): string {
  return new URL(url, document.baseURI).href;
}

function answer(request: TestRequest, visitor: Visitor): void {
  const url = new URL(absolute(request.request.url));

  if (
    url.origin === appOrigin() &&
    url.pathname === '/assets/app-config.local.json'
  ) {
    request.flush({
      apiBaseUrl: API_ORIGIN,
      auth: {
        google: {
          clientId: 'budgetoid-client',
          redirectUri: `${appOrigin()}/register`,
          scope: 'openid email',
        },
      },
    });

    return;
  }

  if (url.href === `${API_ORIGIN}/api/me`) {
    if (visitor === 'authenticated') {
      request.flush({
        email: 'visitor@budgetoid.app',
        budgetId: '3f5b0a91-7c24-4a1e-9d3b-6e8f0c2a5471',
      });
    } else {
      request.flush(null, { status: 401, statusText: 'Unauthorized' });
    }

    return;
  }

  if (url.href === `${API_ORIGIN}/api/me/key-rotation`) {
    request.flush({ rotation: null });

    return;
  }

  request.error(new ProgressEvent('error'));
}

// Answers everything open until `settled` holds and two rounds in a row found
// nothing new. Bounded, so a boot that never settles fails here by name rather
// than timing the case out.
async function answerUntilSettled(
  http: HttpTestingController,
  visitor: Visitor,
  requested: string[],
  settled: () => boolean,
): Promise<void> {
  let quietRounds = 0;
  for (let round = 0; round < 100; round += 1) {
    await afterPendingWork();
    const open = http.match(() => true);
    for (const request of open) {
      requested.push(absolute(request.request.url));
      // Asked and then dropped by its own caller while an earlier answer was
      // being delivered: still a request the boot made, so still counted.
      if (!request.cancelled) {
        answer(request, visitor);
      }
    }
    quietRounds = open.length === 0 ? quietRounds + 1 : 0;
    if (settled() && quietRounds >= 2) {
      return;
    }
  }

  throw new Error(
    `The boot did not settle. Asked so far: ${requested.join(', ')}`,
  );
}

// Loads the application at `path` the way a browser does: the address is set
// before anything is built, the application's providers are finalized — which
// runs the `APP_INITIALIZER` — and then the router renders the first route.
async function coldBoot(visitor: Visitor, path: string): Promise<ColdBoot> {
  history.replaceState(null, '', path);

  TestBed.configureTestingModule({
    providers: [...appConfig.providers, provideHttpClientTesting()],
  });

  // Finalizes the module, so the initializer has started by the next line.
  const http = TestBed.inject(HttpTestingController);
  const oAuth = TestBed.inject(OAuthService);
  const heldValidProviderTokens =
    oAuth.hasValidAccessToken() && oAuth.hasValidIdToken();
  const init = TestBed.inject(ApplicationInitStatus);
  const requested: string[] = [];

  await answerUntilSettled(http, visitor, requested, () => init.done);

  let rendered = false;
  const rendering = RouterTestingHarness.create(path).then(() => {
    rendered = true;
  });
  await answerUntilSettled(http, visitor, requested, () => rendered);
  await rendering;

  return { requested, heldValidProviderTokens };
}

// Every request whose origin is neither the application's nor the API's.
function foreignRequests(boot: ColdBoot): readonly string[] {
  return boot.requested.filter((href) => {
    const { origin } = new URL(href);

    return origin !== appOrigin() && origin !== API_ORIGIN;
  });
}

function framesInDocument(): readonly string[] {
  return [...document.querySelectorAll('iframe')].map(
    (frame) => frame.getAttribute('src') ?? '(no src)',
  );
}

// What an abandoned registration leaves in the tab: the provider came back,
// the library stored its answer, and the person closed the screen before
// creating anything. Valid for an hour, so the library treats it as live.
function seedAbandonedProviderTokens(): void {
  const now = Date.now();
  const inAnHour = String(now + 60 * 60 * 1000);
  const claims = {
    iss: 'https://accounts.google.com',
    aud: 'budgetoid-client',
    sub: '1234567890',
    email: 'visitor@budgetoid.app',
    iat: Math.floor(now / 1000),
    exp: Math.floor(now / 1000) + 60 * 60,
  };

  sessionStorage.setItem('access_token', 'abandoned-access-token');
  sessionStorage.setItem('expires_at', inAnHour);
  sessionStorage.setItem('access_token_stored_at', String(now));
  sessionStorage.setItem(
    'id_token',
    `${btoa('{"alg":"RS256"}')}.${btoa(JSON.stringify(claims))}.signature`,
  );
  sessionStorage.setItem('id_token_claims_obj', JSON.stringify(claims));
  sessionStorage.setItem('id_token_expires_at', inAnHour);
  sessionStorage.setItem('id_token_stored_at', String(now));
  sessionStorage.setItem('nonce', 'abandoned-nonce');
}

describe('a cold load against the real provider client', () => {
  let originalHref: string;

  beforeEach(() => {
    originalHref = document.location.href;
    sessionStorage.clear();
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    for (const frame of document.querySelectorAll('iframe')) {
      frame.remove();
    }
    sessionStorage.clear();
    history.replaceState(null, '', originalHref);
  });

  it.each<{ readonly visitor: Visitor; readonly path: string }>([
    { visitor: 'anonymous', path: '/welcome' },
    { visitor: 'authenticated', path: '/app' },
    // Somebody opening the registration screen has not been to the provider
    // yet; the press on the screen is what contacts it.
    { visitor: 'anonymous', path: '/register' },
    // Something after the path that is not the provider's answer: a campaign
    // link or an in-page anchor is still somebody opening the screen.
    { visitor: 'anonymous', path: '/register?utm_source=newsletter' },
    { visitor: 'anonymous', path: '/register#section' },
  ])(
    'reaches no provider for a visitor who is $visitor at $path',
    async ({ visitor, path }) => {
      // Arrange
      const expectedProbe = `${API_ORIGIN}/api/me`;

      // Act
      const boot = await coldBoot(visitor, path);

      // Assert — the probe first: without it a boot that asked nothing at all
      // would satisfy the census.
      expect(boot.requested).toContain(expectedProbe);
      expect(foreignRequests(boot)).toEqual([]);
      expect(framesInDocument()).toEqual([]);
    },
  );

  // An abandoned registration leaves live-looking provider tokens behind, and
  // the library reads its storage the moment it is built. Nothing it holds may
  // turn into a contact: no refresh, no session check, no discovery fetch.
  it.each<{ readonly visitor: Visitor; readonly path: string }>([
    { visitor: 'authenticated', path: '/app' },
    { visitor: 'anonymous', path: '/welcome' },
  ])(
    'reaches no provider for a $visitor visitor whose tab still holds provider tokens',
    async ({ visitor, path }) => {
      // Arrange
      seedAbandonedProviderTokens();

      // Act
      const boot = await coldBoot(visitor, path);

      // Assert — the fixture first: tokens the library did not judge valid
      // would pin nothing about what it does with valid ones.
      expect(boot.heldValidProviderTokens).toBe(true);
      expect(boot.requested).toContain(`${API_ORIGIN}/api/me`);
      expect(foreignRequests(boot)).toEqual([]);
      expect(framesInDocument()).toEqual([]);
    },
  );

  // The control. The one cold load on which the provider is contacted is the
  // provider redirecting a registration back — and this census has to be able
  // to see that contact, or every green case above is a census that sees
  // nothing.
  it('sees the discovery request when the provider redirects a registration back', async () => {
    // Arrange
    const path = '/register#access_token=a&id_token=b&state=c';

    // Act
    const boot = await coldBoot('anonymous', path);

    // Assert
    expect(foreignRequests(boot)).toEqual([DISCOVERY_URL]);
  });
});
