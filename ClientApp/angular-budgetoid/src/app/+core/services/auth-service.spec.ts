import { TestBed } from '@angular/core/testing';
import { OAuthEvent, OAuthService } from 'angular-oauth2-oidc';
import { isObservable, Observable, Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { AuthService } from './auth-service';
import { ConfigurationService } from './configuration.service';

// FR-086: no image supplied by the identity provider is displayed. This pins something
// stronger than dropping the picture claim — the app reads no ID-token claim at all. A
// claim the app never holds is a claim no component can render, and reading none of them
// is what makes asking Google for only `openid email` safe to keep: a claim nobody
// consumes is a scope nobody needs.
//
// The check subscribes to every observable AuthService exposes, because a claim read
// inside a cold observable stays invisible until something subscribes.
function exposedObservables(service: AuthService): Observable<unknown>[] {
  const members = service as unknown as Record<string, unknown>;

  return Object.keys(members)
    .map((key) => members[key])
    .filter(isObservable);
}

describe('AuthService', () => {
  it('reads no claim from the ID token', () => {
    // Arrange
    const events = new Subject<OAuthEvent>();
    const getIdentityClaims = vi.fn(() => ({
      email: 'someone@example.com',
      name: 'Someone',
      picture: 'https://lh3.googleusercontent.com/a/photo',
    }));
    const oAuth = {
      events,
      getIdentityClaims,
      hasValidAccessToken: () => true,
      hasValidIdToken: () => true,
    } as unknown as OAuthService;

    TestBed.configureTestingModule({
      providers: [
        AuthService,
        { provide: OAuthService, useValue: oAuth },
        { provide: ConfigurationService, useValue: {} },
      ],
    });
    const service = TestBed.inject(AuthService);
    const subscriptions = exposedObservables(service).map((observable) =>
      observable.subscribe(),
    );

    // Act
    events.next({ type: 'discovery_document_loaded' });
    events.next({ type: 'token_received' });
    subscriptions.forEach((subscription) => {
      subscription.unsubscribe();
    });

    // Assert
    expect(getIdentityClaims).not.toHaveBeenCalled();
  });

  // `core.providers.ts:49` awaits this method inside the `APP_INITIALIZER`, so
  // a rejection here is not a degraded sign-in — it is an application that
  // never finishes bootstrapping and a browser left on a blank page. The
  // discovery document lives on `accounts.google.com`, which an outage, a
  // blocked host, a captive portal or a corporate proxy each make unreachable,
  // and none of those says anything about the first-party session cookie the
  // rest of the app runs on.
  it('finishes initializing when the provider cannot be reached', async () => {
    // Arrange
    const oAuth = {
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin: vi.fn(() =>
        Promise.reject(new Error('The discovery document is unreachable.')),
      ),
    } as unknown as OAuthService;

    TestBed.configureTestingModule({
      providers: [
        AuthService,
        { provide: OAuthService, useValue: oAuth },
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: '', auth: {} }) },
        },
      ],
    });
    const service = TestBed.inject(AuthService);

    // Act & Assert
    await expect(service.initialize()).resolves.toBeUndefined();
  });

  // **Nothing schedules a background renewal of the provider token.** That
  // token is used once, on the registration screen, and discarded at the 201;
  // every request after it authenticates from the first-party session cookie,
  // so nothing reads it again. Scheduling a renewal — which is what
  // `setupAutomaticSilentRefresh()` does — plants a hidden iframe pointed at
  // `accounts.google.com` and re-runs it on a timer for as long as the tab is
  // open: a third-party request on every page of the product, forever, to keep
  // alive a credential nobody reads, in an application whose whole point is
  // that the provider is contacted once in an account's life.
  //
  // The assertion has to sit on the **success** path. On the failure path the
  // scheduling could never have run anyway — it followed the line that throws —
  // so an assertion there discriminates nothing and would pass over a restored
  // call.
  //
  // `src/no-external-origins.spec.ts` cannot hold this. It reads the production
  // bundle for foreign origins, and `accounts.google.com` is already
  // allow-listed there as the issuer this service configures, legitimately so:
  // the sign-in redirect goes to exactly that host. A restored renewal adds no
  // origin the bundle did not already carry, so that spec stays green while a
  // timer starts hitting Google on every page.
  it('schedules no background renewal of the provider token', async () => {
    // Arrange
    const loadDiscoveryDocumentAndTryLogin = vi.fn(() => Promise.resolve(true));
    const setupAutomaticSilentRefresh = vi.fn();
    const oAuth = {
      configure: vi.fn(),
      loadDiscoveryDocumentAndTryLogin,
      setupAutomaticSilentRefresh,
    } as unknown as OAuthService;

    TestBed.configureTestingModule({
      providers: [
        AuthService,
        { provide: OAuthService, useValue: oAuth },
        {
          provide: ConfigurationService,
          useValue: { getConfig: () => ({ apiBaseUrl: '', auth: {} }) },
        },
      ],
    });
    const service = TestBed.inject(AuthService);

    // Act
    await service.initialize();

    // Assert
    // The first expectation is not decoration: without it an `initialize()`
    // that had been emptied out entirely would satisfy the second one.
    expect(loadDiscoveryDocumentAndTryLogin).toHaveBeenCalledOnce();
    expect(setupAutomaticSilentRefresh).not.toHaveBeenCalled();
  });

  // The `email` claim and nothing else. `openid email` already carries it, so
  // reading it widens no scope — and the assertion that no *other* claim is
  // read is what keeps that true: `name` costs nothing to add and `picture` is
  // an image from another origin, which this application does not load at all.
  it('reads the address the provider asserted, and nothing else', () => {
    // Arrange
    // A proxy rather than a plain object, because the question is which claims
    // were *touched*. `'email' in claims` goes through the `has` trap, so only
    // a genuine read of a member's value is recorded here.
    const read: string[] = [];
    const claims = new Proxy(
      {
        email: 'owner@budgetoid.test',
        name: 'Owner',
        picture: 'https://lh3.googleusercontent.com/a/photo',
        sub: '1234567890',
      },
      {
        get(target, property, receiver): unknown {
          if (typeof property === 'string') {
            read.push(property);
          }

          return Reflect.get(target, property, receiver);
        },
      },
    );
    const service = authServiceReading(() => claims);

    // Act
    const email = service.providerEmail();

    // Assert
    expect(email).toBe('owner@budgetoid.test');
    expect(read).toEqual(['email']);
  });

  // **The claim outlives the token, and reading it without asking is what put a
  // dead `Continue` on the registration screen.** `getIdentityClaims()` answers
  // out of storage and keeps answering for as long as the browser holds the
  // decoded token — an hour after it expired, a day after. The registration
  // screen renders "your account will be created under <address>" from this
  // method, and both legs behind that press are declared on the provider scheme
  // and nothing else: a stale claim therefore promises an account, offers a
  // control, and reaches a 401 that says nothing a person can act on.
  //
  // The address is not the only thing that goes with the token. The `sub` the
  // account is created under is read off the principal the *server* resolves, so
  // an expired token has no identity behind it at all — which is why `null` here
  // is the honest answer rather than a cautious one.
  it('reads no address from an expired id token', () => {
    // Arrange
    const service = authServiceReading(
      () => ({ email: 'owner@budgetoid.test' }),
      false,
    );

    // Act & Assert
    expect(service.providerEmail()).toBeNull();
  });

  it.each([
    // Present but blank is not an address. A screen rendering one shows a label
    // with nothing after it, which reads as a bug rather than as absence.
    { shape: 'an empty address', claims: (): object => ({ email: '' }) },
    { shape: 'a claims object with no address', claims: (): object => ({}) },
    {
      shape: 'a claims object carrying other members only',
      claims: (): object => ({ name: 'Owner', sub: '1234567890' }),
    },
    // What `getIdentityClaims()` answers before any exchange has completed.
    // Typed `object` and null at runtime, which is why every step down to a
    // `string` in the subject is checked rather than asserted.
    { shape: 'absent claims', claims: (): object => null as unknown as object },
  ])('reads no address from $shape', ({ claims }) => {
    // Arrange
    const service = authServiceReading(claims);

    // Act & Assert
    expect(service.providerEmail()).toBeNull();
  });
});

// One `AuthService` over a stubbed provider whose claims the caller decides,
// and whose token is live unless the caller says otherwise.
//
// **The validity is a parameter rather than a fixture constant**, because it is
// the one input `providerEmail` has besides the claims themselves: the claims
// outlive the token, so every case below is really a pair — these claims, this
// answer to "is the token still good?" — and defaulting it to `true` is what
// keeps each of the four shapes below a statement about the claims alone.
//
// Declared below the suite it serves rather than above it: the first test in
// this file builds its own stub on purpose — it is asserting that *nothing* is
// read — and routing it through a shared helper would put a second reader
// between it and the claim it is watching.
function authServiceReading(
  getIdentityClaims: () => object,
  hasValidIdToken = true,
): AuthService {
  const oAuth = {
    getIdentityClaims,
    hasValidIdToken: () => hasValidIdToken,
  } as unknown as OAuthService;

  TestBed.configureTestingModule({
    providers: [
      AuthService,
      { provide: OAuthService, useValue: oAuth },
      { provide: ConfigurationService, useValue: {} },
    ],
  });

  return TestBed.inject(AuthService);
}
