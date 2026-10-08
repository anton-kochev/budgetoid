import {
  HttpTestingController,
  provideHttpClientTesting,
  type TestRequest,
} from '@angular/common/http/testing';
import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { AuthService } from '@app-core/services/auth-service';
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
// configuration file, `GET /api/me/session` (which is what decides who the
// visitor is), `GET /api/me` behind it for a full session, and the
// staged-rotation read. Anything else is answered as a network failure,
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

// The mark `AuthService.signIn` leaves in the tab just before it sends the
// person to the provider. Seeded here to stand for a tab that pressed.
const EXCHANGE_MARKER = 'budgetoid-provider-exchange';

const PROVIDER_ANSWER_PATH = '/register#access_token=a&id_token=b&state=c';

// The email change's marker value, and its answer landing on the settings
// screen. The registration marker is `'started'`.
const EMAIL_CHANGE_MARKER = 'email-change';
const SETTINGS_ANSWER_PATH = '/app/settings#access_token=a&id_token=b&state=c';
const KEY_SET_URL = 'https://www.googleapis.com/oauth2/v3/certs';

// The locked sign-in's marker value, and its answer landing on the release
// screen. Spelled here rather than imported: it is module-private in
// `AuthService`, and a rename there is a change to what every open tab holds.
const LOCKED_SIGN_IN_MARKER = 'locked-sign-in';
const RELEASE_ANSWER_PATH = '/release#access_token=a&id_token=b&state=c';

// The keys angular-oauth2-oidc 17 writes to its storage for a trip or on an
// implicit-flow return.
const LIBRARY_KEYS = [
  'access_token',
  'id_token',
  'refresh_token',
  'nonce',
  'PKCE_verifier',
  'expires_at',
  'id_token_claims_obj',
  'id_token_expires_at',
  'id_token_stored_at',
  'access_token_stored_at',
  'granted_scopes',
  'session_state',
] as const;

type Visitor = 'anonymous' | 'authenticated';

interface ColdBootOptions {
  // Whether Google's discovery document and key set are answered. Off by
  // default: every other case answers them as a network failure, which is
  // what keeps a contact visible in the census without a round trip.
  readonly answerProvider?: boolean;
  // Runs once the initializer has settled and before the first route renders
  // — the moment a screen would first read what the boot left.
  readonly afterStart?: () => void;
}

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

function answer(
  request: TestRequest,
  visitor: Visitor,
  answerProvider: boolean,
): void {
  const url = new URL(absolute(request.request.url));

  if (answerProvider && url.href === DISCOVERY_URL) {
    request.flush({
      issuer: 'https://accounts.google.com',
      // The provider's own spelling.
      // eslint-disable-next-line @typescript-eslint/naming-convention
      authorization_endpoint: 'https://accounts.google.com/o/oauth2/v2/auth',
      // eslint-disable-next-line @typescript-eslint/naming-convention
      jwks_uri: KEY_SET_URL,
    });

    return;
  }

  if (answerProvider && url.href === KEY_SET_URL) {
    request.flush({ keys: [] });

    return;
  }

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
          emailChangeRedirectUri: `${appOrigin()}/app/settings`,
          lockedSignInRedirectUri: `${appOrigin()}/release`,
          scope: 'openid email',
        },
      },
    });

    return;
  }

  // The probe's first question: what kind of session, if any. Only a full
  // answer goes on to `GET /api/me` below.
  if (url.href === `${API_ORIGIN}/api/me/session`) {
    if (visitor === 'authenticated') {
      request.flush({
        kind: 'full',
        expiresAtUtc: '2026-10-17T08:00:00Z',
        erasure: null,
      });
    } else {
      request.flush(null, { status: 401, statusText: 'Unauthorized' });
    }

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
  answerProvider = false,
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
        answer(request, visitor, answerProvider);
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
async function coldBoot(
  visitor: Visitor,
  path: string,
  { answerProvider = false, afterStart }: ColdBootOptions = {},
): Promise<ColdBoot> {
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

  await answerUntilSettled(
    http,
    visitor,
    requested,
    () => init.done,
    answerProvider,
  );
  afterStart?.();

  let rendered = false;
  const rendering = RouterTestingHarness.create(path).then(() => {
    rendered = true;
  });
  await answerUntilSettled(
    http,
    visitor,
    requested,
    () => rendered,
    answerProvider,
  );
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

  it.each<{
    readonly visitor: Visitor;
    readonly path: string;
    readonly marked: boolean;
  }>([
    { visitor: 'anonymous', path: '/welcome', marked: false },
    { visitor: 'authenticated', path: '/app', marked: false },
    // Somebody opening the registration screen has not been to the provider
    // yet; the press on the screen is what contacts it.
    { visitor: 'anonymous', path: '/register', marked: false },
    // Something after the path that is not the provider's answer, in a tab
    // that did press: a campaign link, an in-page anchor, or anything in the
    // query — this client never reads an answer from there — is still
    // somebody opening the screen.
    {
      visitor: 'anonymous',
      path: '/register?utm_source=newsletter',
      marked: true,
    },
    { visitor: 'anonymous', path: '/register#section', marked: true },
    { visitor: 'anonymous', path: '/register?code=a&state=c', marked: true },
    { visitor: 'anonymous', path: '/register?error=x&state=c', marked: true },
    // The answer's exact shape in a tab that never pressed: a crafted link.
    { visitor: 'anonymous', path: PROVIDER_ANSWER_PATH, marked: false },
  ])(
    'reaches no provider for a visitor who is $visitor at $path (marked: $marked)',
    async ({ visitor, path, marked }) => {
      // Arrange
      const expectedProbe = `${API_ORIGIN}/api/me/session`;
      if (marked) {
        sessionStorage.setItem(EXCHANGE_MARKER, 'started');
      }

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
      expect(boot.requested).toContain(`${API_ORIGIN}/api/me/session`);
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
    sessionStorage.setItem(EXCHANGE_MARKER, 'started');

    // Act
    const boot = await coldBoot('anonymous', PROVIDER_ANSWER_PATH);

    // Assert
    expect(foreignRequests(boot)).toEqual([DISCOVERY_URL]);
  });

  // One exchange, one return leg. The marker is spent by the boot that read
  // the answer, whatever became of it.
  it('spends the marker on the boot that reads the answer', async () => {
    // Arrange
    sessionStorage.setItem(EXCHANGE_MARKER, 'started');

    // Act
    await coldBoot('anonymous', PROVIDER_ANSWER_PATH);

    // Assert
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).toBeNull();
  });

  // A reload of the address the provider sent the person back to — the
  // fragment still on it, from history or a bookmark — is not a second return
  // leg. Without the marker being spent, every reload of that page would ask
  // Google for the discovery document again.
  it('reaches no provider on a reload of the address the provider answered on', async () => {
    // Arrange
    sessionStorage.setItem(EXCHANGE_MARKER, 'started');
    await coldBoot('anonymous', PROVIDER_ANSWER_PATH);
    TestBed.resetTestingModule();

    // Act
    const reload = await coldBoot('anonymous', PROVIDER_ANSWER_PATH);

    // Assert — the probe first, as above.
    expect(reload.requested).toContain(`${API_ORIGIN}/api/me/session`);
    expect(foreignRequests(reload)).toEqual([]);
  });
});

function base64Url(value: object): string {
  return btoa(JSON.stringify(value))
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '');
}

// An id token the library accepts under its default `NullValidationHandler`:
// the claims it checks are right and the signature is not checked.
function idTokenFor(nonce: string): string {
  const now = Math.floor(Date.now() / 1000);

  return [
    base64Url({ alg: 'RS256', typ: 'JWT' }),
    base64Url({
      iss: 'https://accounts.google.com',
      aud: 'budgetoid-client',
      sub: 'subject-one',
      email: 'moved.owner@budgetoid.test',
      iat: now,
      exp: now + 3600,
      nonce,
      // eslint-disable-next-line @typescript-eslint/naming-convention
      at_hash: 'hash-one',
    }),
    'signature-one',
  ].join('.');
}

// The email change comes back to a tab holding a session, so the probe's
// authenticated arm — which discards the provider's tokens, nonce and marker
// included — runs on the very boot that has to read the answer.
describe('a cold load the provider answers an email change on', () => {
  let originalHref: string;
  let base: HTMLBaseElement;

  beforeEach(() => {
    originalHref = document.location.href;
    sessionStorage.clear();
    // `src/index.html` ships `<base href="/">`, which is what resolves the
    // configuration file's `./assets/…` from a two-segment path like
    // `/app/settings`. jsdom's page has none, so it would ask `/app/assets/…`.
    base = document.createElement('base');
    base.href = '/';
    document.head.append(base);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    for (const frame of document.querySelectorAll('iframe')) {
      frame.remove();
    }
    base.remove();
    sessionStorage.clear();
    history.replaceState(null, '', originalHref);
  });

  // T8's control: the census has to see this contact, or the negatives below
  // are a census that sees nothing.
  it('sees the discovery request when the provider redirects an email change back', async () => {
    // Arrange
    sessionStorage.setItem(EXCHANGE_MARKER, EMAIL_CHANGE_MARKER);

    // Act
    const boot = await coldBoot('authenticated', SETTINGS_ANSWER_PATH);

    // Assert
    expect(foreignRequests(boot)).toEqual([DISCOVERY_URL]);
  });

  it.each<{
    readonly shape: string;
    readonly visitor: Visitor;
    readonly path: string;
    readonly marker: string | null;
  }>([
    {
      shape: 'a signed-in load of the settings screen',
      visitor: 'authenticated',
      path: '/app/settings',
      marker: null,
    },
    {
      shape: 'a settings answer in a tab that never pressed',
      visitor: 'authenticated',
      path: SETTINGS_ANSWER_PATH,
      marker: null,
    },
    {
      shape: 'a settings answer in a tab that pressed to register',
      visitor: 'authenticated',
      path: SETTINGS_ANSWER_PATH,
      marker: 'started',
    },
    {
      shape: 'a registration answer in a tab that pressed to change its email',
      visitor: 'anonymous',
      path: PROVIDER_ANSWER_PATH,
      marker: EMAIL_CHANGE_MARKER,
    },
  ])('reaches no provider on $shape', async ({ visitor, path, marker }) => {
    // Arrange
    if (marker !== null) {
      sessionStorage.setItem(EXCHANGE_MARKER, marker);
    }

    // Act
    const boot = await coldBoot(visitor, path);

    // Assert — the probe first: a boot that asked nothing would pass the census.
    expect(boot.requested).toContain(`${API_ORIGIN}/api/me/session`);
    expect(foreignRequests(boot)).toEqual([]);
    expect(framesInDocument()).toEqual([]);
  });

  // The regression for the ordering bug: on a signed-in boot the answer has to
  // be read while its nonce still exists, and handed over in memory before the
  // first route draws.
  it('hands a signed-in email change the id token the provider sent back', async () => {
    // Arrange
    sessionStorage.setItem('nonce', 'trip-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, EMAIL_CHANGE_MARKER);
    const token = idTokenFor('trip-nonce');
    let handedOver: unknown = 'never read';

    // Act
    await coldBoot(
      'authenticated',
      `/app/settings#access_token=at&id_token=${token}&state=trip-nonce`,
      {
        answerProvider: true,
        afterStart: () => {
          handedOver = TestBed.inject(AuthService).takeEmailChangeReturn();
        },
      },
    );

    // Assert
    expect(handedOver).toEqual({
      kind: 'answered',
      idToken: token,
      email: 'moved.owner@budgetoid.test',
    });
  });

  it('leaves no provider token or marker behind after an email-change boot', async () => {
    // Arrange
    sessionStorage.setItem('nonce', 'trip-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, EMAIL_CHANGE_MARKER);
    const token = idTokenFor('trip-nonce');

    // Act
    const boot = await coldBoot(
      'authenticated',
      `/app/settings#access_token=at&id_token=${token}&state=trip-nonce`,
      { answerProvider: true },
    );

    // Assert — the control first: a boot that never read the answer leaves
    // nothing behind either, because the probe's discard empties storage.
    expect(foreignRequests(boot)).toEqual([DISCOVERY_URL, KEY_SET_URL]);
    const left = LIBRARY_KEYS.filter(
      (key) => sessionStorage.getItem(key) !== null,
    );
    expect(left).toEqual([]);
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).toBeNull();
  });
});

// **A provider answer leaves the address bar before the first route draws,
// whatever became of it** (docs/design/components.md, "Changing the email
// address"). A return leg removes the one it reads; the initializer's last
// step removes one nobody read — a registration answer reaching a signed-in
// visitor, whose leg is skipped, or an answer in a tab that started no trip.
// Read at `afterStart`, which is the moment the router is about to draw.
//
// Removed in place: a removal that pushed a new entry would leave the
// token-bearing one behind for Back to return to.
describe('a cold load whose address carries an answer nobody reads', () => {
  let originalHref: string;
  let base: HTMLBaseElement;

  beforeEach(() => {
    originalHref = document.location.href;
    sessionStorage.clear();
    // `/app/settings` is two segments deep; see the describe above.
    base = document.createElement('base');
    base.href = '/';
    document.head.append(base);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    for (const frame of document.querySelectorAll('iframe')) {
      frame.remove();
    }
    base.remove();
    sessionStorage.clear();
    history.replaceState(null, '', originalHref);
  });

  it('an answer nobody claimed is gone before the first route', async () => {
    // Arrange
    const entries = history.length;
    let atFirstRoute: { hash: string; entries: number } | null = null;

    // Act
    const boot = await coldBoot('authenticated', SETTINGS_ANSWER_PATH, {
      afterStart: () => {
        atFirstRoute = { hash: location.hash, entries: history.length };
      },
    });

    // Assert — the census first: an unclaimed answer is still no contact.
    expect(foreignRequests(boot)).toEqual([]);
    expect(atFirstRoute).toEqual({ hash: '', entries });
  });

  // The refusal is an answer too — the same shape the return legs recognise
  // — so a removal keyed on the tokens alone would leave it standing.
  it('a refusal nobody claimed is gone before the first route', async () => {
    // Arrange
    const entries = history.length;
    let atFirstRoute: { hash: string; entries: number } | null = null;

    // Act
    await coldBoot('authenticated', '/app/settings#error=access_denied', {
      afterStart: () => {
        atFirstRoute = { hash: location.hash, entries: history.length };
      },
    });

    // Assert
    expect(atFirstRoute).toEqual({ hash: '', entries });
  });

  // A registration answer in the tab that pressed, reaching a visitor who
  // signed in meanwhile: `guestGuard` turns them away from `/register`, so the
  // leg that would read it is skipped and the answer would stay.
  it('a registration answer reaching a signed-in visitor is gone', async () => {
    // Arrange
    sessionStorage.setItem(EXCHANGE_MARKER, 'started');
    const entries = history.length;
    let atFirstRoute: { hash: string; entries: number } | null = null;

    // Act
    const boot = await coldBoot('authenticated', PROVIDER_ANSWER_PATH, {
      afterStart: () => {
        atFirstRoute = { hash: location.hash, entries: history.length };
      },
    });

    // Assert
    expect(foreignRequests(boot)).toEqual([]);
    expect(atFirstRoute).toEqual({ hash: '', entries });
  });

  // Only an answer-shaped fragment is removed — the same shape the return
  // legs recognise. An in-page anchor is somebody's link.
  it('an in-page anchor survives boot', async () => {
    // Arrange
    let atFirstRoute: string | null = null;

    // Act
    await coldBoot('anonymous', '/register#section', {
      afterStart: () => {
        atFirstRoute = location.hash;
      },
    });

    // Assert
    expect(atFirstRoute).toBe('#section');
  });
});

// The locked sign-in comes back to `/release`, in a tab that usually holds no
// session: the account behind it has no factor left to open one. The answer is
// read before the probe and handed to the release flow in memory; nothing the
// library wrote for the trip survives the boot that read it.
describe('a cold load the provider answers a locked sign-in on', () => {
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

  // The control: the census has to see this contact, or the negatives below
  // are a census that sees nothing.
  it('sees the discovery request when the provider redirects a locked sign-in back', async () => {
    // Arrange
    sessionStorage.setItem(EXCHANGE_MARKER, LOCKED_SIGN_IN_MARKER);

    // Act
    const boot = await coldBoot('anonymous', RELEASE_ANSWER_PATH);

    // Assert
    expect(foreignRequests(boot)).toEqual([DISCOVERY_URL]);
  });

  it('hands the locked sign-in the id token the provider sent back', async () => {
    // Arrange
    sessionStorage.setItem('nonce', 'trip-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, LOCKED_SIGN_IN_MARKER);
    const token = idTokenFor('trip-nonce');
    let handedOver: unknown = 'never read';

    // Act
    await coldBoot(
      'anonymous',
      `/release#access_token=at&id_token=${token}&state=trip-nonce`,
      {
        answerProvider: true,
        afterStart: () => {
          handedOver = TestBed.inject(AuthService).takeLockedSignInReturn();
        },
      },
    );

    // Assert — the token and nothing else: no address is kept for this trip.
    expect(handedOver).toEqual({ kind: 'answered', idToken: token });
  });

  it('leaves no provider token, marker or answer behind after a locked sign-in boot', async () => {
    // Arrange
    sessionStorage.setItem('nonce', 'trip-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, LOCKED_SIGN_IN_MARKER);
    const token = idTokenFor('trip-nonce');
    let hashAtFirstRoute: string | null = null;

    // Act
    const boot = await coldBoot(
      'anonymous',
      `/release#access_token=at&id_token=${token}&state=trip-nonce`,
      {
        answerProvider: true,
        afterStart: () => {
          hashAtFirstRoute = location.hash;
        },
      },
    );

    // Assert — the control first: the answer was read, so the emptiness below
    // is the return leg's discard and not a boot that read nothing. An
    // anonymous probe discards nothing on its own.
    expect(foreignRequests(boot)).toEqual([DISCOVERY_URL, KEY_SET_URL]);
    const left = LIBRARY_KEYS.filter(
      (key) => sessionStorage.getItem(key) !== null,
    );
    expect(left).toEqual([]);
    expect(sessionStorage.getItem(EXCHANGE_MARKER)).toBeNull();
    expect(hashAtFirstRoute).toBe('');
  });

  // **Load-bearing.** Posting the answer would replace the full session's
  // cookie with a locked one, so a locked return reaching a tab that already
  // holds a full session is dropped before any screen can take it.
  it('hands nothing over when the probe finds a full session', async () => {
    // Arrange
    sessionStorage.setItem('nonce', 'trip-nonce');
    sessionStorage.setItem(EXCHANGE_MARKER, LOCKED_SIGN_IN_MARKER);
    const token = idTokenFor('trip-nonce');
    let handedOver: unknown = 'never read';

    // Act
    const boot = await coldBoot(
      'authenticated',
      `/release#access_token=at&id_token=${token}&state=trip-nonce`,
      {
        answerProvider: true,
        afterStart: () => {
          handedOver = TestBed.inject(AuthService).takeLockedSignInReturn();
        },
      },
    );

    // Assert — the control first: the answer was read, so a `null` is the
    // drop and not a return nobody recognised.
    expect(foreignRequests(boot)).toEqual([DISCOVERY_URL, KEY_SET_URL]);
    expect(handedOver).toBeNull();
  });

  // A reload of `/release` after the return — the fragment gone, or still on
  // a history entry — is not a second return leg. The marker was spent.
  it.each([
    { shape: 'the release screen', path: '/release' },
    { shape: 'the answer-shaped address', path: RELEASE_ANSWER_PATH },
  ])(
    'reaches no provider on a reload of $shape after the return',
    async ({ path }) => {
      // Arrange
      sessionStorage.setItem(EXCHANGE_MARKER, LOCKED_SIGN_IN_MARKER);
      await coldBoot('anonymous', RELEASE_ANSWER_PATH);
      TestBed.resetTestingModule();

      // Act
      const reload = await coldBoot('anonymous', path);

      // Assert — the probe first: a boot that asked nothing passes the census.
      expect(reload.requested).toContain(`${API_ORIGIN}/api/me/session`);
      expect(foreignRequests(reload)).toEqual([]);
      expect(framesInDocument()).toEqual([]);
    },
  );

  it.each<{
    readonly shape: string;
    readonly path: string;
    readonly marker: string | null;
  }>([
    {
      shape: 'a plain load of the release screen',
      path: '/release',
      marker: null,
    },
    {
      shape: 'a release answer in a tab that never pressed',
      path: RELEASE_ANSWER_PATH,
      marker: null,
    },
    {
      shape: 'a release answer in a tab that pressed to register',
      path: RELEASE_ANSWER_PATH,
      marker: 'started',
    },
    {
      shape: 'a release answer in a tab that pressed to change its email',
      path: RELEASE_ANSWER_PATH,
      marker: EMAIL_CHANGE_MARKER,
    },
    {
      shape: 'a registration answer in a tab that pressed to release',
      path: PROVIDER_ANSWER_PATH,
      marker: LOCKED_SIGN_IN_MARKER,
    },
  ])('reaches no provider on $shape', async ({ path, marker }) => {
    // Arrange
    if (marker !== null) {
      sessionStorage.setItem(EXCHANGE_MARKER, marker);
    }

    // Act
    const boot = await coldBoot('anonymous', path);

    // Assert
    expect(boot.requested).toContain(`${API_ORIGIN}/api/me/session`);
    expect(foreignRequests(boot)).toEqual([]);
    expect(framesInDocument()).toEqual([]);
  });
});
